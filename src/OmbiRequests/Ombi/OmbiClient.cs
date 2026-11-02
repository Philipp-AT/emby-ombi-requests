using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using OmbiRequests.Configuration;

namespace OmbiRequests.Ombi
{
    /// <summary>
    /// Who a call to Ombi is made for. Ombi treats calls with only the API key as admin calls;
    /// the UserName header makes Ombi apply that user's permissions, quotas and auto-approval.
    /// </summary>
    public sealed class OmbiIdentity
    {
        private OmbiIdentity(string userName, string alias, string displayName, string onBehalfOfUserId, bool canManageRequests = false)
        {
            UserName = userName;
            Alias = alias;
            DisplayName = displayName;
            OnBehalfOfUserId = onBehalfOfUserId;
            CanManageRequests = canManageRequests;
        }

        /// <summary>Existing Ombi user name, sent as UserName header unless <see cref="OnBehalfOfUserId"/> is set.</summary>
        public string UserName { get; }

        /// <summary>
        /// Set when the Ombi user name cannot be sent as HTTP header (headers are ASCII only, e.g. "Jürgen").
        /// Calls are then made with the API key alone and requests carry Ombi's requestOnBehalf with this id.
        /// </summary>
        public string OnBehalfOfUserId { get; }

        /// <summary>Free text name, sent as ApiAlias header (admin context).</summary>
        public string Alias { get; }

        /// <summary>For messages: "name@example.com (Name)" when the Ombi user has an alias.</summary>
        public string DisplayName { get; }

        /// <summary>Admin or power user in Ombi: may manage the requests of all users.</summary>
        public bool CanManageRequests { get; }

        public static OmbiIdentity ForUser(OmbiUser user)
        {
            var display = string.IsNullOrWhiteSpace(user.alias) || string.Equals(user.alias, user.userName, StringComparison.OrdinalIgnoreCase)
                ? user.userName
                : user.userName + " (" + user.alias + ")";
            var onBehalfOf = IsAscii(user.userName) ? null : user.id;
            return new OmbiIdentity(user.userName, null, display, onBehalfOf, user.CanManageRequests());
        }

        /// <summary>The alias goes as ASCII header, and Ombi stores it like that; it is compared in that form.</summary>
        public static OmbiIdentity ForAlias(string alias) => new OmbiIdentity(null, OmbiClient.ToAsciiHeaderValue(alias), alias, null);

        internal static bool IsAscii(string value) => value != null && value.All(c => c >= 32 && c < 127);

        public bool Matches(RequestUser user, string requestedByAlias)
        {
            if (UserName != null)
            {
                return string.Equals(user?.userName, UserName, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(requestedByAlias, Alias, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class OmbiException : Exception
    {
        public OmbiException(string message, HttpStatusCode? statusCode = null, Exception inner = null, bool isOmbiMessage = false)
            : base(message, inner)
        {
            StatusCode = statusCode;
            IsOmbiMessage = isOmbiMessage;
        }

        public HttpStatusCode? StatusCode { get; }

        /// <summary>
        /// True if the message is Ombi's own explanation (e.g. "already requested", quota exceeded),
        /// which can be shown to users; otherwise it is a technical message for logs and admins.
        /// </summary>
        public bool IsOmbiMessage { get; }
    }

    public class OmbiClient
    {
        // The timeout is set per request (DefaultTimeout, ListTimeout).
        // No automatic redirects: they would send the API key to wherever the address points (and turn POST
        // into GET). A redirect is reported instead, see SendTextAsync.
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Ombi looks up every title of a list page at TMDb, which takes long for series.</summary>
        private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(2);

        private static readonly TimeSpan UserCacheDuration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Calls to Ombi at the same time. The requests page loads five lists with several pages and
        /// looks up details; a reverse proxy in front of Ombi may limit parallel requests (nginx answers
        /// 503 then), and Ombi itself gets slow with many TMDb lookups at once. Further calls wait.
        /// </summary>
        private static readonly SemaphoreSlim Parallel = new SemaphoreSlim(4);

        /// <summary>Waits before a read is tried again after Ombi or its proxy answered "busy" (429, 502-504).</summary>
        private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

        private readonly Func<PluginConfiguration> getConfig;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly SemaphoreSlim userCacheLock = new SemaphoreSlim(1, 1);

        private List<OmbiUser> cachedUsers;
        private string cachedUsersKey;
        private DateTime cachedUsersTime;
        private Tuple<DateTime, string, OmbiException> lastUsersFailure;

        /// <summary>After a failed load of the users, callers get the result at once for this long instead of waiting again.</summary>
        private static readonly TimeSpan UserFailureCacheDuration = TimeSpan.FromSeconds(30);
        private Tuple<string, DateTime> cachedDefaultLanguage;

        public OmbiClient(Func<PluginConfiguration> getConfig, IJsonSerializer json, ILogger logger)
        {
            this.getConfig = getConfig;
            this.json = json;
            this.logger = logger;
        }

        private PluginConfiguration Config => getConfig();

        /// <param name="embyUserName">Name of the Emby user.</param>
        /// <param name="connectUserName">Emby Connect user name or e-mail of the Emby user, if linked.</param>
        public async Task<OmbiIdentity> ResolveIdentityAsync(string embyUserName, string connectUserName, CancellationToken cancellationToken)
        {
            var config = Config;
            var users = await GetUsersAsync(cancellationToken).ConfigureAwait(false);
            var match = FindUser(users, config.ParseUserMappings(), embyUserName, connectUserName);
            if (match != null)
            {
                return OmbiIdentity.ForUser(match);
            }

            logger.Debug("No Ombi user found for Emby user {0} (Emby Connect: {1})", embyUserName, connectUserName ?? "-");
            return config.AllowUnmappedUsers ? OmbiIdentity.ForAlias(embyUserName) : null;
        }

        /// <summary>
        /// Order: explicit mapping, same user name, Emby Connect name/e-mail (Ombi imports Emby Connect
        /// users with their e-mail as user name), alias. An alias only counts if exactly one Ombi user
        /// has it, so a duplicate alias can never lead to requesting as somebody else.
        /// </summary>
        public static OmbiUser FindUser(IReadOnlyList<OmbiUser> users, IDictionary<string, string> mappings, string embyUserName, string connectUserName)
        {
            if (mappings.TryGetValue(embyUserName ?? string.Empty, out var mapped))
            {
                return ByUserName(users, mapped) ?? ByEmail(users, mapped) ?? ByUniqueAlias(users, mapped);
            }

            var match = ByUserName(users, embyUserName);
            if (match == null && !string.IsNullOrWhiteSpace(connectUserName))
            {
                match = ByUserName(users, connectUserName) ?? ByEmail(users, connectUserName);
            }

            return match ?? ByUniqueAlias(users, embyUserName);
        }

        private static OmbiUser ByUserName(IEnumerable<OmbiUser> users, string name)
        {
            return users.FirstOrDefault(u => Same(u.userName, name));
        }

        private static OmbiUser ByEmail(IEnumerable<OmbiUser> users, string email)
        {
            return users.FirstOrDefault(u => Same(u.emailAddress, email));
        }

        private static OmbiUser ByUniqueAlias(IEnumerable<OmbiUser> users, string alias)
        {
            var matches = users.Where(u => Same(u.alias, alias)).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static bool Same(string a, string b)
        {
            return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
                   && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IReadOnlyList<OmbiUser>> GetUsersAsync(CancellationToken cancellationToken)
        {
            var config = Config;
            var key = config.OmbiUrl + "|" + config.ApiKey;

            await userCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var known = cachedUsers != null && cachedUsersKey == key;
                if (known && DateTime.UtcNow - cachedUsersTime <= UserCacheDuration)
                {
                    return cachedUsers;
                }

                // Every API call resolves the user: while Ombi does not answer, each one would wait for the timeout.
                var failure = lastUsersFailure;
                if (failure != null && failure.Item2 == key && DateTime.UtcNow - failure.Item1 < UserFailureCacheDuration)
                {
                    if (known)
                    {
                        return cachedUsers;
                    }

                    throw new OmbiException(failure.Item3.Message, failure.Item3.StatusCode, failure.Item3);
                }

                try
                {
                    cachedUsers = await SendAsync<List<OmbiUser>>(HttpMethod.Get, "api/v1/Identity/Users", null, null, cancellationToken).ConfigureAwait(false)
                                  ?? new List<OmbiUser>();
                    cachedUsersKey = key;
                    cachedUsersTime = DateTime.UtcNow;
                    lastUsersFailure = null;
                }
                catch (OmbiException ex)
                {
                    lastUsersFailure = Tuple.Create(DateTime.UtcNow, key, ex);
                    if (!known)
                    {
                        throw;
                    }

                    logger.Warn("Loading Ombi's users failed, the users of the last load are used: {0}", ex.Message);
                }

                return cachedUsers;
            }
            finally
            {
                userCacheLock.Release();
            }
        }

        /// <summary>Ombi's default language ("de"), used for users without own language; "en" if unknown.</summary>
        public async Task<string> GetDefaultLanguageAsync(CancellationToken cancellationToken)
        {
            var cached = cachedDefaultLanguage;
            if (cached != null && DateTime.UtcNow - cached.Item2 < UserCacheDuration)
            {
                return cached.Item1;
            }

            string language;
            try
            {
                language = (await SendAsync<string>(HttpMethod.Get, "api/v1/Settings/defaultlanguage", null, null, cancellationToken).ConfigureAwait(false))?.Trim().Trim('"');
            }
            catch (OmbiException ex)
            {
                logger.Debug("Ombi's default language is unknown: {0}", ex.Message);
                language = null;
            }

            language = string.IsNullOrWhiteSpace(language) ? "en" : language;
            cachedDefaultLanguage = Tuple.Create(language, DateTime.UtcNow);
            return language;
        }

        public Task<List<MultiSearchResult>> SearchAsync(OmbiIdentity identity, string term, bool movies, bool tv, CancellationToken cancellationToken)
        {
            // Ombi routes the term as a path segment: slashes would break the route, and "." / ".."
            // segments are removed by URL normalization (callers skip terms without letters or digits).
            var safeTerm = Uri.EscapeDataString(term.Replace('/', ' ').Replace('\\', ' ').Trim());
            var filter = new MultiSearchFilter { movies = movies, tvShows = tv };
            return SendAsync<List<MultiSearchResult>>(HttpMethod.Post, "api/v2/Search/multi/" + safeTerm, identity, filter, cancellationToken);
        }

        public Task<MovieDetails> GetMovieAsync(OmbiIdentity identity, int tmdbId, CancellationToken cancellationToken)
        {
            return SendAsync<MovieDetails>(HttpMethod.Get, "api/v2/Search/movie/" + tmdbId, identity, null, cancellationToken);
        }

        public Task<TvDetails> GetTvAsync(OmbiIdentity identity, int tmdbId, CancellationToken cancellationToken)
        {
            return SendAsync<TvDetails>(HttpMethod.Get, "api/v2/Search/tv/moviedb/" + tmdbId, identity, null, cancellationToken);
        }

        public Task<List<SearchMovie>> GetMovieListAsync(OmbiIdentity identity, string list, int start, int count, CancellationToken cancellationToken)
        {
            // list: popular, nowplaying, upcoming, toprated
            return SendAsync<List<SearchMovie>>(Config, HttpMethod.Get, $"api/v2/Search/movie/{list}/{start}/{count}", identity, null, cancellationToken, ListTimeout);
        }

        public Task<List<SearchTv>> GetTvListAsync(OmbiIdentity identity, string list, int start, int count, CancellationToken cancellationToken)
        {
            // list: popular, trending, anticipated, mostwatched (TMDb's top rated)
            return SendAsync<List<SearchTv>>(Config, HttpMethod.Get, $"api/v2/Search/tv/{list}/{start}/{count}", identity, null, cancellationToken, ListTimeout);
        }

        /// <summary>Movies of one year by popularity; reaches beyond the 10,000 titles TMDb serves per list.</summary>
        public Task<List<SearchMovie>> GetMoviesOfYearAsync(OmbiIdentity identity, int year, int start, int count, CancellationToken cancellationToken)
        {
            var body = new DiscoverRequest { releaseYear = year };
            return SendAsync<List<SearchMovie>>(Config, HttpMethod.Post, $"api/v2/Search/advancedSearch/movie/{start}/{count}", identity, body, cancellationToken, ListTimeout);
        }

        public Task<RequestEngineResult> RequestMovieAsync(OmbiIdentity identity, MovieRequestBody body, CancellationToken cancellationToken)
        {
            return SendAsync<RequestEngineResult>(HttpMethod.Post, "api/v1/Request/movie", identity, body, cancellationToken);
        }

        public Task<RequestEngineResult> RequestTvAsync(OmbiIdentity identity, TvRequestBody body, CancellationToken cancellationToken)
        {
            return SendAsync<RequestEngineResult>(HttpMethod.Post, "api/v2/Requests/tv", identity, body, cancellationToken);
        }

        /// <summary>Series by TheTVDB id, for library series that have no TMDb id.</summary>
        public Task<TvDetails> GetTvByTvdbAsync(OmbiIdentity identity, string tvdbId, CancellationToken cancellationToken)
        {
            return SendAsync<TvDetails>(HttpMethod.Get, "api/v2/Search/tv/" + Uri.EscapeDataString(tvdbId), identity, null, cancellationToken);
        }

        /// <param name="mediaType">"movie" or "tv".</param>
        public Task<RequestQuota> GetQuotaAsync(OmbiIdentity identity, string mediaType, CancellationToken cancellationToken)
        {
            return SendAsync<RequestQuota>(HttpMethod.Get, $"api/v1/Request/{mediaType}/remaining", identity, null, cancellationToken);
        }

        // Approving, denying and deleting run with the API key alone (admin); the plugin checks who may do it.
        public Task<RequestEngineResult> ApproveMovieAsync(int requestId, CancellationToken cancellationToken)
        {
            return SendAsync<RequestEngineResult>(HttpMethod.Post, "api/v1/Request/movie/approve", null, new RequestUpdateBody { id = requestId }, cancellationToken);
        }

        public Task<RequestEngineResult> DenyMovieAsync(int requestId, string reason, CancellationToken cancellationToken)
        {
            return SendAsync<RequestEngineResult>(HttpMethod.Put, "api/v1/Request/movie/deny", null, new RequestDenyBody { id = requestId, reason = reason }, cancellationToken);
        }

        /// <param name="childRequestId">Id of the season request.</param>
        public Task<RequestEngineResult> ApproveTvAsync(int childRequestId, CancellationToken cancellationToken)
        {
            return SendAsync<RequestEngineResult>(HttpMethod.Post, "api/v1/Request/tv/approve", null, new RequestUpdateBody { id = childRequestId }, cancellationToken);
        }

        public Task<RequestEngineResult> DenyTvAsync(int childRequestId, string reason, CancellationToken cancellationToken)
        {
            return SendAsync<RequestEngineResult>(HttpMethod.Put, "api/v1/Request/tv/deny", null, new RequestDenyBody { id = childRequestId, reason = reason }, cancellationToken);
        }

        public async Task<RequestEngineResult> DeleteMovieRequestAsync(int requestId, CancellationToken cancellationToken)
        {
            var text = await SendTextAsync(Config, HttpMethod.Delete, "api/v1/Request/movie/" + requestId.ToString(CultureInfo.InvariantCulture), null, null, cancellationToken).ConfigureAwait(false);
            return ParseEngineResult(text);
        }

        public async Task<RequestEngineResult> DeleteTvChildRequestAsync(int childRequestId, CancellationToken cancellationToken)
        {
            var text = await SendTextAsync(Config, HttpMethod.Delete, "api/v1/Request/tv/child/" + childRequestId.ToString(CultureInfo.InvariantCulture), null, null, cancellationToken).ConfigureAwait(false);
            return ParseEngineResult(text);
        }

        /// <summary>Ombi answers some calls with a result object, others (deleting a season request) with a bare true/false.</summary>
        private RequestEngineResult ParseEngineResult(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (bool.TryParse(trimmed, out var done))
            {
                return new RequestEngineResult { result = done, isError = !done };
            }

            try
            {
                return json.DeserializeFromString<RequestEngineResult>(trimmed);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Unexpected response from Ombi: {0}", ex, Truncate(trimmed));
                throw new OmbiException("Unexpected response from Ombi.", null, ex);
            }
        }

        /// <summary>Whether issue reports are switched on in Ombi.</summary>
        public Task<bool> GetIssuesEnabledAsync(CancellationToken cancellationToken)
        {
            return SendAsync<bool>(HttpMethod.Get, "api/v1/Settings/issuesenabled", null, null, cancellationToken);
        }

        public Task<List<IssueCategory>> GetIssueCategoriesAsync(CancellationToken cancellationToken)
        {
            return SendAsync<List<IssueCategory>>(HttpMethod.Get, "api/v1/Issues/categories", null, null, cancellationToken);
        }

        /// <summary>Creates an issue as the given user (Ombi takes the reporter from the identity).</summary>
        public Task<int> CreateIssueAsync(OmbiIdentity identity, IssueBody body, CancellationToken cancellationToken)
        {
            return SendAsync<int>(HttpMethod.Post, "api/v1/Issues", identity, body, cancellationToken);
        }

        // The v2 lists are what Ombi's own UI uses; the v1 lists fail with HTTP 500 on current Ombi versions.
        // Without identity they contain the requests of all users (the catalog needs them for its states).
        public Task<RequestsPage<MovieRequest>> GetMovieRequestsAsync(OmbiIdentity identity, int count, CancellationToken cancellationToken)
        {
            return SendAsync<RequestsPage<MovieRequest>>(Config, HttpMethod.Get, $"api/v2/Requests/movie/{count}/0/requestedDate/desc", identity, null, cancellationToken, ListTimeout);
        }

        public Task<RequestsPage<ChildRequest>> GetTvRequestsAsync(OmbiIdentity identity, int count, CancellationToken cancellationToken)
        {
            return SendAsync<RequestsPage<ChildRequest>>(Config, HttpMethod.Get, $"api/v2/Requests/tv/{count}/0/requestedDate/desc", identity, null, cancellationToken, ListTimeout);
        }

        /// <summary>Checks URL and API key. Returns the number of Ombi users.</summary>
        public async Task<int> TestConnectionAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            var users = await SendAsync<List<OmbiUser>>(config, HttpMethod.Get, "api/v1/Identity/Users", null, null, cancellationToken).ConfigureAwait(false);
            return users?.Count ?? 0;
        }

        private Task<T> SendAsync<T>(HttpMethod method, string path, OmbiIdentity identity, object body, CancellationToken cancellationToken)
        {
            return SendAsync<T>(Config, method, path, identity, body, cancellationToken);
        }

        private async Task<T> SendAsync<T>(PluginConfiguration config, HttpMethod method, string path, OmbiIdentity identity, object body, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            var text = await SendTextAsync(config, method, path, identity, body, cancellationToken, timeout).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            try
            {
                return json.DeserializeFromString<T>(text);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Unexpected response from Ombi for {0}: {1}", ex, path, Truncate(text));
                throw new OmbiException("Unexpected response from Ombi.", null, ex);
            }
        }

        /// <returns>The body of Ombi's answer.</returns>
        private async Task<string> SendTextAsync(PluginConfiguration config, HttpMethod method, string path, OmbiIdentity identity, object body, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            if (!config.IsConfigured)
            {
                throw new OmbiException("Ombi is not configured.");
            }

            var url = config.OmbiUrl.TrimEnd('/') + "/" + path;
            var payload = body == null ? null : json.SerializeToString(body);

            // Reads only: a request, approval or deletion is never sent twice.
            var mayRetry = method == HttpMethod.Get || (method == HttpMethod.Post && path.StartsWith("api/v2/Search/", StringComparison.OrdinalIgnoreCase));
            for (var attempt = 0; ; attempt++)
            {
                HttpResponseMessage response;
                var limit = timeout ?? DefaultTimeout;
                await Parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using (var request = CreateRequest(config, method, url, identity, payload))
                    using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutSource.CancelAfter(limit);

                        // Reads the whole body, so the timeout covers it too.
                        response = await Http.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.Error("Ombi request {0} {1} got no answer within {2} seconds", method, path, limit.TotalSeconds);
                    Diagnostics.Error($"{method} {path}: no answer within {limit.TotalSeconds} seconds");
                    throw new OmbiException($"Ombi did not answer within {limit.TotalSeconds} seconds.", null, ex);
                }
                catch (HttpRequestException ex)
                {
                    logger.Error("Ombi request {0} {1} failed: {2}", method, path, ex.Message);
                    Diagnostics.Error($"{method} {path}: {ex.Message}");
                    throw new OmbiException("Ombi is not reachable: " + ex.Message, null, ex);
                }
                catch (Exception ex) when (ex is UriFormatException || ex is InvalidOperationException || ex is NotSupportedException || ex is ArgumentException)
                {
                    // A mistyped address in the settings (no scheme, unsupported scheme, ...).
                    Diagnostics.Error($"{method} {path}: {ex.Message}");
                    throw new OmbiException($"The Ombi address '{config.OmbiUrl}' is not valid: {ex.Message}", null, ex);
                }
                finally
                {
                    Parallel.Release();
                }

                using (response)
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var code = (int)response.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        var target = response.Headers.Location?.ToString() ?? "?";
                        Diagnostics.Error($"{method} {path}: redirected to {target}");
                        throw new OmbiException($"The Ombi address redirects to {target}. Enter that address in the plugin settings.", response.StatusCode);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        if (mayRetry && attempt < RetryDelays.Length && IsBusy(response.StatusCode))
                        {
                            var delay = RetryDelay(response, attempt);
                            logger.Debug("Ombi request {0} {1} returned {2}, trying again in {3} s", method, path, (int)response.StatusCode, delay.TotalSeconds);
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        logger.Warn("Ombi request {0} {1} returned {2}: {3}", method, path, (int)response.StatusCode, Truncate(text));
                        Diagnostics.Error($"{method} {path}: HTTP {(int)response.StatusCode}");
                        var ombiMessage = TryGetErrorMessage(text);
                        throw new OmbiException(ombiMessage ?? DescribeError(response.StatusCode, text), response.StatusCode, null, ombiMessage != null);
                    }

                    return text;
                }
            }
        }

        private static HttpRequestMessage CreateRequest(PluginConfiguration config, HttpMethod method, string url, OmbiIdentity identity, string payload)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("ApiKey", config.ApiKey.Trim());
            if (identity?.UserName != null)
            {
                if (identity.OnBehalfOfUserId == null)
                {
                    request.Headers.TryAddWithoutValidation("UserName", identity.UserName);
                }
            }
            else if (identity?.Alias != null)
            {
                request.Headers.TryAddWithoutValidation("ApiAlias", ToAsciiHeaderValue(identity.Alias));
            }

            if (payload != null)
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            return request;
        }

        private static bool IsBusy(HttpStatusCode status)
        {
            var code = (int)status;
            return code == 429 || code == 502 || code == 503 || code == 504;
        }

        /// <summary>The proxy's Retry-After if it sends one (at most 10 seconds), otherwise <see cref="RetryDelays"/>.</summary>
        private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            return retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Min(10, retryAfter.Value.TotalSeconds))
                : RetryDelays[attempt];
        }

        /// <summary>"Jürgen Größ" -> "Juergen Groess"; the alias is only a display name in Ombi.</summary>
        internal static string ToAsciiHeaderValue(string value)
        {
            var text = value.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
                .Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue").Replace("ß", "ss")
                .Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c >= 32 && c < 127)
                {
                    builder.Append(c);
                }
                else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append('?');
                }
            }

            return builder.ToString();
        }

        /// <summary>Ombi answers rejected requests with a RequestEngineResult-like body.</summary>
        private string TryGetErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                var result = json.DeserializeFromString<RequestEngineResult>(body);
                if (result == null || (!result.isError && string.IsNullOrWhiteSpace(result.errorMessage)))
                {
                    return null; // Not Ombi's answer, e.g. a reverse proxy's error page.
                }

                var message = result.errorMessage ?? result.message;
                return string.IsNullOrWhiteSpace(message) ? null : message;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string DescribeError(HttpStatusCode statusCode, string body)
        {
            switch (statusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return "Ombi rejected the request (invalid API key or unknown user).";
                case HttpStatusCode.NotFound:
                    return "Ombi endpoint not found - check the Ombi URL (including any base path).";
                default:
                    return $"Ombi returned HTTP {(int)statusCode}: {Truncate(body, 200)}";
            }
        }

        private static string Truncate(string text, int length = 500)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= length)
            {
                return text;
            }

            return text.Substring(0, length) + "...";
        }
    }
}
