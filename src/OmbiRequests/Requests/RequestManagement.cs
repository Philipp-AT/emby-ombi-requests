using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using OmbiRequests.Ombi;

namespace OmbiRequests.Requests
{
    /// <summary>
    /// Request management beyond requesting: quotas, withdrawing, managing all requests as Emby admin,
    /// and the actions on library pages (request missing seasons, report an issue).
    /// </summary>
    public partial class RequestService
    {
        /// <summary>Requests of all users loaded for withdrawals and approvals; effectively all of them.</summary>
        private const int MaxAllRequests = 100000;

        private static readonly TimeSpan IssueSettingsCacheDuration = TimeSpan.FromMinutes(10);

        private Tuple<bool, List<IssueCategory>, DateTime> issueSettings;

        // ---------- quotas ----------

        /// <summary>The user's remaining requests, only for media types Ombi has a limit for.</summary>
        public Task<List<QuotaDto>> GetQuotasAsync(OmbiIdentity identity, CancellationToken cancellationToken)
        {
            return GetQuotasAsync(identity, new[] { Movie, Tv }, cancellationToken);
        }

        private async Task<List<QuotaDto>> GetQuotasAsync(OmbiIdentity identity, string[] mediaTypes, CancellationToken cancellationToken)
        {
            var result = new List<QuotaDto>();
            foreach (var mediaType in mediaTypes)
            {
                try
                {
                    var quota = await ombi.GetQuotaAsync(identity, mediaType, cancellationToken).ConfigureAwait(false);
                    if (quota?.hasLimit == true)
                    {
                        result.Add(new QuotaDto { MediaType = mediaType, Remaining = quota.remaining, Limit = quota.limit, NextRequest = quota.nextRequest });
                    }
                }
                catch (OmbiException ex)
                {
                    logger.Debug("The {0} quota of {1} is unknown: {2}", mediaType, identity?.DisplayName, ex.Message);
                }
            }

            return result;
        }

        /// <summary>" 3 movie requests left." for the confirmation, empty without limit.</summary>
        private async Task<string> QuotaSuffixAsync(OmbiIdentity identity, string mediaType, CancellationToken cancellationToken)
        {
            var quota = (await GetQuotasAsync(identity, new[] { mediaType }, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            return quota == null ? string.Empty : " " + Text(mediaType == Movie ? "MovieRequestsLeft" : "SeriesRequestsLeft", quota.Remaining);
        }

        // ---------- withdrawing ----------

        public Task<RequestOutcomeDto> WithdrawAsync(User user, string mediaType, int tmdbId, IList<int> seasons, CancellationToken cancellationToken)
        {
            return WithdrawAsync(user, mediaType, tmdbId, seasons, null, cancellationToken);
        }

        /// <summary>
        /// Withdraws the user's own requests of a title: the movie request, or the season requests of a
        /// series (with seasons or episodes: only those that consist of them). Only requests that wait for
        /// approval, with the setting also approved ones that are not available yet. A request of which
        /// another user still wants a part (<see cref="RequestInterest"/>) stays and is noted as abandoned;
        /// it goes once nobody wants it any more, whoever it belongs to. Runs with the API key; the plugin
        /// checks the owner.
        /// </summary>
        /// <param name="episodes">Single episodes as "season:episode".</param>
        public async Task<RequestOutcomeDto> WithdrawAsync(User user, string mediaType, int tmdbId, IList<int> seasons, IList<string> episodes, CancellationToken cancellationToken)
        {
            var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
            using (await LockTitleAsync(mediaType, tmdbId, cancellationToken).ConfigureAwait(false))
            {
                return await WithdrawLockedAsync(user, identity, mediaType, tmdbId, seasons, episodes, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<RequestOutcomeDto> WithdrawLockedAsync(User user, OmbiIdentity identity, string mediaType, int tmdbId, IList<int> seasons, IList<string> episodes, CancellationToken cancellationToken)
        {
            var withdrawApproved = getConfig().WithdrawApprovedRequests;
            var requests = await LoadTitleRequestsAsync(identity, mediaType, tmdbId, seasons, episodes, cancellationToken).ConfigureAwait(false);
            var hadWish = interest.Remove(mediaType, tmdbId, user.InternalId, seasons, episodes);

            var own = requests.Where(r => r.Own).ToList();
            var title = requests.Select(r => r.Title).FirstOrDefault(t => !string.IsNullOrEmpty(t))
                        ?? (await TryGetDetailsAsync(user, identity, mediaType, tmdbId, cancellationToken).ConfigureAwait(false))?.Title
                        ?? tmdbId.ToString(CultureInfo.InvariantCulture);

            // Own requests of which somebody else still wants a part stay; the others go.
            var ownWithdrawable = own.Where(r => r.CanWithdraw(withdrawApproved)).ToList();
            var kept = ownWithdrawable.Where(r => interest.IsWanted(mediaType, tmdbId, r.Seasons, r.Episodes)).ToList();
            var toDelete = ownWithdrawable.Except(kept).ToList();
            if (kept.Count > 0)
            {
                interest.Abandon(mediaType, tmdbId, user.InternalId, kept.Select(r => r.Id));
                logger.Info("{0} withdrew {1} {2}, but others want {3} of the requests: they stay", user.Name, mediaType, tmdbId, kept.Count);
            }

            // Requests whose owners withdrew before go once nobody wants any part of them.
            var abandoned = interest.AbandonedIds(mediaType, tmdbId);
            var gone = abandoned.Where(id => requests.All(r => r.Id != id)).ToList();
            if (hadWish || own.Count > 0)
            {
                toDelete.AddRange(requests.Where(r => !r.Own && abandoned.Contains(r.Id) && r.CanWithdraw(withdrawApproved)
                                                      && !interest.IsWanted(mediaType, tmdbId, r.Seasons, r.Episodes)));
            }

            // One by one: a failing deletion must not keep the others from being reported.
            var deleted = new List<int>();
            string failure = null;
            foreach (var request in toDelete)
            {
                try
                {
                    var result = mediaType == Movie
                        ? await ombi.DeleteMovieRequestAsync(request.Id, cancellationToken).ConfigureAwait(false)
                        : await ombi.DeleteTvChildRequestAsync(request.Id, cancellationToken).ConfigureAwait(false);
                    if (Succeeded(result))
                    {
                        deleted.Add(request.Id);
                    }
                    else
                    {
                        failure = result?.errorMessage ?? result?.message ?? Text("ServiceUnavailable");
                    }
                }
                catch (OmbiException ex)
                {
                    logger.Warn("Deleting the {0} request {1} failed: {2}", mediaType, request.Id, ex.Message);
                    failure = DescribeForUser(ex);
                }
            }

            interest.ForgetAbandoned(mediaType, tmdbId, deleted.Concat(gone));
            if (deleted.Count > 0)
            {
                ForgetDetails(mediaType, tmdbId);
                var details = await TryGetDetailsAsync(user, identity, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
                logger.Info("{0} withdrew {1} request(s) for {2} {3}", user.Name, deleted.Count, mediaType, tmdbId);
                RequestChanged?.Invoke(this, new RequestChangedEventArgs(mediaType, tmdbId, details, withdrawn: true));
                return new RequestOutcomeDto
                {
                    Success = true,
                    Message = Text("RequestWithdrawn", details?.Title ?? title),
                    Status = details?.Status
                };
            }

            if (failure != null)
            {
                return new RequestOutcomeDto { Success = false, Message = failure };
            }

            if (kept.Count > 0)
            {
                return new RequestOutcomeDto { Success = false, Message = Text("WithdrawKeptForOthers", title) };
            }

            string message;
            if (own.Any(r => r.Available))
            {
                message = Text("WithdrawAlreadyAvailable", title);
            }
            else if (own.Any(r => r.Approved && !r.Denied))
            {
                message = Text("WithdrawAlreadyApproved", title);
            }
            else if (hadWish)
            {
                // Somebody else's request: only this user's wish goes.
                return new RequestOutcomeDto { Success = true, Message = Text("WishRemoved", title) };
            }
            else
            {
                message = Text("NothingToWithdraw");
            }

            return new RequestOutcomeDto { Success = false, Message = message };
        }

        /// <summary>The movie request or the season requests of a title, with what they consist of.</summary>
        private async Task<List<TitleRequest>> LoadTitleRequestsAsync(OmbiIdentity identity, string mediaType, int tmdbId, IList<int> seasons, IList<string> episodes, CancellationToken cancellationToken)
        {
            if (mediaType == Movie)
            {
                var movies = (await ombi.GetMovieRequestsAsync(null, MaxAllRequests, cancellationToken).ConfigureAwait(false))?.collection ?? new List<MovieRequest>();
                return movies
                    .Where(r => r.theMovieDbId == tmdbId)
                    .Select(r => new TitleRequest(r.id, r.title, r.approved, r.denied, r.available, identity.Matches(r.requestedUser, r.requestedByAlias), null))
                    .ToList();
            }

            var children = (await ombi.GetTvRequestsAsync(null, MaxAllRequests, cancellationToken).ConfigureAwait(false))?.collection ?? new List<ChildRequest>();
            return children
                .Where(c => c.parentRequest?.externalProviderId == tmdbId)
                .Select(c => new TitleRequest(c.id, c.parentRequest.title ?? c.title, c.approved, c.denied, c.available,
                    identity.Matches(c.requestedUser, c.requestedByAlias) && ConsistsOf(c, seasons, episodes), c.seasonRequests))
                .ToList();
        }

        private readonly ConcurrentDictionary<string, SemaphoreSlim> titleLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// Requests, withdrawals and deletions of one title run one after another: each decides from
        /// Ombi's requests and the users' wishes, which the other one changes meanwhile.
        /// </summary>
        private async Task<IDisposable> LockTitleAsync(string mediaType, int tmdbId, CancellationToken cancellationToken)
        {
            var gate = titleLocks.GetOrAdd(mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture), _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new TitleLock(gate);
        }

        private sealed class TitleLock : IDisposable
        {
            private SemaphoreSlim gate;

            public TitleLock(SemaphoreSlim gate)
            {
                this.gate = gate;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref gate, null)?.Release();
            }
        }

        /// <summary>A movie request or season request of the title being withdrawn.</summary>
        private sealed class TitleRequest
        {
            public TitleRequest(int id, string title, bool approved, bool denied, bool available, bool own, IList<SeasonRequest> parts)
            {
                Id = id;
                Title = title;
                Approved = approved;
                Denied = denied;
                Available = available;
                Own = own;
                var seasonParts = parts ?? new List<SeasonRequest>();
                Seasons = seasonParts.Where(p => p.episodes == null || p.episodes.Count == 0).Select(p => p.seasonNumber).ToList();
                Episodes = seasonParts
                    .Where(p => p.episodes != null)
                    .SelectMany(p => p.episodes.Select(e => p.seasonNumber.ToString(CultureInfo.InvariantCulture) + ":" + e.episodeNumber.ToString(CultureInfo.InvariantCulture)))
                    .ToList();
            }

            /// <summary>Series: seasons requested without single episodes.</summary>
            public List<int> Seasons { get; }

            /// <summary>Series: requested episodes as "season:episode".</summary>
            public List<string> Episodes { get; }

            public int Id { get; }

            public string Title { get; }

            public bool Approved { get; }

            public bool Denied { get; }

            public bool Available { get; }

            /// <summary>The withdrawing user's request (series: of the given seasons).</summary>
            public bool Own { get; }

            public bool CanWithdraw(bool withdrawApproved) => !Available && !Denied && (!Approved || withdrawApproved);
        }

        private static bool ConsistsOf(ChildRequest child, IList<int> seasons, IList<string> episodes)
        {
            var parts = child.seasonRequests ?? new List<SeasonRequest>();
            if (episodes != null && episodes.Count > 0)
            {
                return parts.Count > 0 && parts.All(s => s.episodes != null && s.episodes.Count > 0
                    && s.episodes.All(e => episodes.Contains(s.seasonNumber.ToString(CultureInfo.InvariantCulture) + ":" + e.episodeNumber.ToString(CultureInfo.InvariantCulture))));
            }

            return seasons == null || seasons.Count == 0 || (parts.Count > 0 && parts.All(s => seasons.Contains(s.seasonNumber)));
        }

        /// <summary>Ombi answers deletions and approvals with a result; an empty answer counts as done.</summary>
        private static bool Succeeded(RequestEngineResult result) => result == null || (result.result && !result.isError);

        // ---------- all requests (Emby admins) ----------

        /// <summary>
        /// Requests of all users, newest first, with the Emby users who also want the title
        /// (<see cref="RequestInterest"/>). Available ones only on demand: there are usually many, and
        /// nothing is left to decide about them.
        /// </summary>
        public async Task<List<AdminRequestDto>> GetAllRequestsAsync(bool includeAvailable, CancellationToken cancellationToken)
        {
            var movies = (await ombi.GetMovieRequestsAsync(null, MaxAllRequests, cancellationToken).ConfigureAwait(false))?.collection ?? new List<MovieRequest>();
            var seasons = (await ombi.GetTvRequestsAsync(null, MaxAllRequests, cancellationToken).ConfigureAwait(false))?.collection ?? new List<ChildRequest>();

            // (request, Ombi user and alias of the requester)
            var rows = movies
                .Where(m => includeAvailable || !m.available)
                .Select(m => Tuple.Create(new AdminRequestDto
                {
                    MediaType = Movie,
                    RequestId = m.id,
                    TmdbId = m.theMovieDbId,
                    Title = m.title,
                    Year = ParseYear(m.releaseDate),
                    PosterUrl = ImageUrl(m.posterPath, "w342"),
                    Status = RequestStatus(m.approved, m.denied, m.available),
                    DeniedReason = m.denied ? m.deniedReason : null,
                    RequestedBy = RequesterName(m.requestedUser, m.requestedByAlias),
                    RequestedDate = m.requestedDate
                }, m.requestedUser, m.requestedByAlias))
                .Concat(seasons
                    .Where(c => c.parentRequest != null && (includeAvailable || !c.available))
                    .Select(c => Tuple.Create(new AdminRequestDto
                    {
                        MediaType = Tv,
                        RequestId = c.id,
                        TmdbId = c.parentRequest.externalProviderId,
                        Title = c.parentRequest.title ?? c.title,
                        Year = ParseYear(c.parentRequest.releaseDate) ?? ParseYear(c.releaseYear),
                        PosterUrl = ImageUrl(c.parentRequest.posterPath, "w342"),
                        Status = RequestStatus(c.approved, c.denied, c.available),
                        DeniedReason = c.denied ? c.deniedReason : null,
                        RequestedBy = RequesterName(c.requestedUser, c.requestedByAlias),
                        RequestedDate = c.requestedDate,
                        Seasons = c.seasonRequests?.Select(s => s.seasonNumber).OrderBy(n => n).ToList()
                    }, c.requestedUser, c.requestedByAlias)))
                .OrderByDescending(r => r.Item1.RequestedDate, StringComparer.Ordinal)
                .ToList();

            await FillWantedByAsync(rows, cancellationToken).ConfigureAwait(false);
            var result = rows.Select(r => r.Item1).ToList();
            await FillMissingPostersAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private static string RequestStatus(bool approved, bool denied, bool available)
        {
            var status = available ? MediaStatus.Available : denied ? MediaStatus.Denied : approved ? MediaStatus.Approved : MediaStatus.Requested;
            return status.ToString();
        }

        /// <summary>Names of the Emby users who want a title, except the one whose request it is.</summary>
        private async Task FillWantedByAsync(List<Tuple<AdminRequestDto, RequestUser, string>> rows, CancellationToken cancellationToken)
        {
            var wanting = rows.SelectMany(r => interest.Users(r.Item1.MediaType, r.Item1.TmdbId)).Distinct().ToList();
            var manager = wanting.Count == 0 ? null : userManager?.Value;
            var users = manager == null
                ? new Dictionary<long, User>()
                : manager.GetUserList(new UserQuery()).Where(u => wanting.Contains(u.InternalId)).ToDictionary(u => u.InternalId);

            var identities = new Dictionary<long, OmbiIdentity>();
            foreach (var user in users.Values)
            {
                try
                {
                    identities[user.InternalId] = await ombi.ResolveIdentityAsync(user.Name, user.ConnectUserName, cancellationToken).ConfigureAwait(false);
                }
                catch (OmbiException ex)
                {
                    logger.Debug("No Ombi user for {0}: {1}", user.Name, ex.Message);
                }
            }

            foreach (var row in rows)
            {
                row.Item1.AlsoWantedBy = interest.Users(row.Item1.MediaType, row.Item1.TmdbId)
                    .Where(id => users.ContainsKey(id) && !(identities.TryGetValue(id, out var identity) && identity != null && identity.Matches(row.Item2, row.Item3)))
                    .Select(id => users[id].Name)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        private async Task FillMissingPostersAsync(List<AdminRequestDto> requests, CancellationToken cancellationToken)
        {
            var missing = requests.Where(r => r.MediaType == Tv && r.TmdbId > 0 && !IsTmdbImage(r.PosterUrl)).GroupBy(r => r.TmdbId).ToList();
            using (var throttle = new SemaphoreSlim(DetailConcurrency))
            {
                await Task.WhenAll(missing.Select(async show =>
                {
                    var poster = await GetTvPosterAsync(null, show.Key, throttle, cancellationToken).ConfigureAwait(false);
                    foreach (var request in show.Where(r => poster != null))
                    {
                        request.PosterUrl = poster;
                    }
                })).ConfigureAwait(false);
            }
        }

        private static string RequesterName(RequestUser user, string alias)
        {
            return !string.IsNullOrWhiteSpace(user?.alias) ? user.alias : user?.userName ?? alias;
        }

        /// <summary>Approves or denies a request, also one decided before (an approved one can still be denied, a denied one approved).</summary>
        /// <param name="requestId">Movie request id or season request id (from <see cref="GetAllRequestsAsync"/>).</param>
        /// <param name="reason">Null approves, otherwise denies with this reason.</param>
        public async Task<RequestOutcomeDto> DecideAsync(User admin, string mediaType, int requestId, int tmdbId, string reason, CancellationToken cancellationToken)
        {
            // The title comes from Ombi, not from the browser: caches and banners of the right title change.
            tmdbId = await FindRequestTitleAsync(mediaType, requestId, cancellationToken).ConfigureAwait(false) ?? 0;
            var approve = reason == null;
            RequestEngineResult result;
            if (mediaType == Movie)
            {
                result = approve
                    ? await ombi.ApproveMovieAsync(requestId, cancellationToken).ConfigureAwait(false)
                    : await ombi.DenyMovieAsync(requestId, reason, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = approve
                    ? await ombi.ApproveTvAsync(requestId, cancellationToken).ConfigureAwait(false)
                    : await ombi.DenyTvAsync(requestId, reason, cancellationToken).ConfigureAwait(false);
            }

            var success = Succeeded(result);
            logger.Info("{0} {1} the {2} request {3}: {4}", admin.Name, approve ? "approved" : "denied", mediaType, requestId, success ? "ok" : result?.errorMessage ?? result?.message);
            if (!success)
            {
                return new RequestOutcomeDto { Success = false, Message = result?.errorMessage ?? result?.message ?? Text("ServiceError", 0) };
            }

            var details = await AnnounceChangeAsync(mediaType, tmdbId, false, cancellationToken).ConfigureAwait(false);
            return new RequestOutcomeDto { Success = true, Message = Text(approve ? "RequestApproved" : "RequestDenied"), Status = details?.Status };
        }

        /// <summary>
        /// Deletes a request of any user and status; the title can then be requested again. What Radarr or
        /// Sonarr already got stays there.
        /// </summary>
        public async Task<RequestOutcomeDto> DeleteRequestAsync(User admin, string mediaType, int requestId, int tmdbId, CancellationToken cancellationToken)
        {
            // The title comes from Ombi, not from the browser: the wishes of exactly that title are forgotten.
            var title = await FindRequestTitleAsync(mediaType, requestId, cancellationToken).ConfigureAwait(false);
            if (title == null)
            {
                return new RequestOutcomeDto { Success = false, Message = Text("NothingToWithdraw") };
            }

            using (await LockTitleAsync(mediaType, title.Value, cancellationToken).ConfigureAwait(false))
            {
                return await DeleteLockedAsync(admin, mediaType, requestId, title.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<RequestOutcomeDto> DeleteLockedAsync(User admin, string mediaType, int requestId, int tmdbId, CancellationToken cancellationToken)
        {
            var result = mediaType == Movie
                ? await ombi.DeleteMovieRequestAsync(requestId, cancellationToken).ConfigureAwait(false)
                : await ombi.DeleteTvChildRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
            var success = Succeeded(result);
            logger.Info("{0} deleted the {1} request {2}: {3}", admin.Name, mediaType, requestId, success ? "ok" : result?.errorMessage ?? result?.message);
            if (!success)
            {
                return new RequestOutcomeDto { Success = false, Message = result?.errorMessage ?? result?.message ?? Text("ServiceUnavailable") };
            }

            // Without any request left nobody's wish counts any more: a new request starts over.
            interest.ForgetAbandoned(mediaType, tmdbId, new[] { requestId });
            if (tmdbId > 0 && (mediaType == Movie || !await HasTvRequestsAsync(tmdbId, cancellationToken).ConfigureAwait(false)))
            {
                interest.Clear(mediaType, tmdbId);
            }

            var details = await AnnounceChangeAsync(mediaType, tmdbId, true, cancellationToken).ConfigureAwait(false);
            return new RequestOutcomeDto { Success = true, Message = Text("RequestDeleted"), Status = details?.Status };
        }

        /// <returns>The TMDb id of a movie request or season request, null if Ombi has no such request.</returns>
        private async Task<int?> FindRequestTitleAsync(string mediaType, int requestId, CancellationToken cancellationToken)
        {
            if (mediaType == Movie)
            {
                var movies = (await ombi.GetMovieRequestsAsync(null, MaxAllRequests, cancellationToken).ConfigureAwait(false))?.collection ?? new List<MovieRequest>();
                return movies.FirstOrDefault(m => m.id == requestId)?.theMovieDbId;
            }

            var children = (await ombi.GetTvRequestsAsync(null, MaxAllRequests, cancellationToken).ConfigureAwait(false))?.collection ?? new List<ChildRequest>();
            return children.FirstOrDefault(c => c.id == requestId)?.parentRequest?.externalProviderId;
        }

        private async Task<bool> HasTvRequestsAsync(int tmdbId, CancellationToken cancellationToken)
        {
            var children = (await ombi.GetTvRequestsAsync(null, MaxAllRequests, cancellationToken).ConfigureAwait(false))?.collection ?? new List<ChildRequest>();
            return children.Any(c => c.parentRequest?.externalProviderId == tmdbId);
        }

        /// <summary>After an admin's decision: the title's new state for the banners, loaded without user (there is no requester here).</summary>
        private async Task<MediaItemDto> AnnounceChangeAsync(string mediaType, int tmdbId, bool withdrawn, CancellationToken cancellationToken)
        {
            if (tmdbId <= 0)
            {
                return null;
            }

            ForgetDetails(mediaType, tmdbId);
            var identity = await GetContentIdentityAsync(cancellationToken).ConfigureAwait(false);
            var details = await TryGetDetailsAsync(null, identity, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
            if (details != null)
            {
                RequestChanged?.Invoke(this, new RequestChangedEventArgs(mediaType, tmdbId, details, withdrawn));
            }

            return details;
        }

        // ---------- library pages ----------

        /// <summary>
        /// What the plugin offers on the page of a movie, series, season or episode in the library:
        /// missing episodes of a series to request, and issue reports. Null for other items or items the
        /// user cannot see.
        /// </summary>
        public async Task<LibraryItemDto> GetLibraryItemAsync(User user, string itemId, CancellationToken cancellationToken)
        {
            var target = await ResolveLibraryTitleAsync(user, itemId, cancellationToken).ConfigureAwait(false);
            if (target == null)
            {
                return null;
            }

            var dto = new LibraryItemDto { MediaType = target.MediaType, TmdbId = target.TmdbId, Title = target.Title };
            if (target.MediaType == Tv && target.TmdbId > 0)
            {
                var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
                var details = await TryGetDetailsAsync(user, identity, Tv, target.TmdbId, cancellationToken).ConfigureAwait(false);
                dto.MissingEpisodes = details?.Seasons?
                    .SelectMany(s => s.Episodes ?? new List<EpisodeDto>())
                    .Count(e => e.Status == nameof(MediaStatus.None)) ?? 0;
            }

            var issues = await GetIssueSettingsAsync(cancellationToken).ConfigureAwait(false);
            dto.CanReportIssue = issues.Item1 && target.TmdbId > 0 && issues.Item2.Count > 0;
            dto.IssueCategories = dto.CanReportIssue
                ? issues.Item2.Select(c => new IssueCategoryDto { Id = c.id, Name = c.value }).ToList()
                : new List<IssueCategoryDto>();
            return dto;
        }

        /// <summary>Reports a problem with a title in the library to Ombi, as the user.</summary>
        public async Task<RequestOutcomeDto> ReportIssueAsync(User user, string itemId, int categoryId, string subject, string description, CancellationToken cancellationToken)
        {
            var target = await ResolveLibraryTitleAsync(user, itemId, cancellationToken).ConfigureAwait(false);
            if (target == null || target.TmdbId <= 0)
            {
                return new RequestOutcomeDto { Success = false, Message = Text("IssueNotPossible") };
            }

            var issues = await GetIssueSettingsAsync(cancellationToken).ConfigureAwait(false);
            var category = issues.Item2.FirstOrDefault(c => c.id == categoryId);
            if (!issues.Item1 || category == null)
            {
                return new RequestOutcomeDto { Success = false, Message = Text("IssueNotPossible") };
            }

            var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
            subject = string.IsNullOrWhiteSpace(subject) ? category.value : subject.Trim();
            await ombi.CreateIssueAsync(identity, new IssueBody
            {
                title = target.Title,
                requestType = target.MediaType == Movie ? 1 : 0,
                providerId = target.TmdbId.ToString(CultureInfo.InvariantCulture),
                subject = target.EpisodePrefix + subject,
                description = (description ?? string.Empty).Trim(),
                issueCategoryId = categoryId
            }, cancellationToken).ConfigureAwait(false);

            logger.Info("{0} reported an issue ({1}) for {2} {3}", user.Name, category.value, target.MediaType, target.TmdbId);
            return new RequestOutcomeDto { Success = true, Message = Text("IssueReported") };
        }

        /// <summary>For the diagnostics: whether Ombi takes issue reports and how many categories it has (asked fresh).</summary>
        public async Task<Tuple<bool, int>> GetIssueStatusAsync(CancellationToken cancellationToken)
        {
            issueSettings = null;
            var settings = await GetIssueSettingsAsync(cancellationToken).ConfigureAwait(false);
            return Tuple.Create(settings.Item1, settings.Item2.Count);
        }

        /// <summary>Whether Ombi takes issue reports, and its categories; kept a few minutes.</summary>
        private async Task<Tuple<bool, List<IssueCategory>, DateTime>> GetIssueSettingsAsync(CancellationToken cancellationToken)
        {
            var cached = issueSettings;
            if (cached != null && DateTime.UtcNow - cached.Item3 < IssueSettingsCacheDuration)
            {
                return cached;
            }

            try
            {
                var enabled = await ombi.GetIssuesEnabledAsync(cancellationToken).ConfigureAwait(false);
                var categories = enabled ? await ombi.GetIssueCategoriesAsync(cancellationToken).ConfigureAwait(false) : null;
                cached = Tuple.Create(enabled, categories ?? new List<IssueCategory>(), DateTime.UtcNow);
            }
            catch (OmbiException ex)
            {
                logger.Debug("Ombi's issue settings are unknown: {0}", ex.Message);
                cached = Tuple.Create(false, new List<IssueCategory>(), DateTime.UtcNow);
            }

            issueSettings = cached;
            return cached;
        }

        /// <summary>The movie or series of a library item (episodes and seasons lead to their series).</summary>
        private async Task<LibraryTitle> ResolveLibraryTitleAsync(User user, string itemId, CancellationToken cancellationToken)
        {
            BaseItem item = null;
            if (long.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId))
            {
                item = libraryManager.Value.GetItemById(internalId);
            }
            else if (Guid.TryParse(itemId, out var guid))
            {
                item = libraryManager.Value.GetItemById(guid);
            }

            if (item == null || !item.IsVisible(user) || (item.ExternalId != null && Channel.ChannelItemIds.IsOwn(item.ExternalId)))
            {
                return null;
            }

            var prefix = string.Empty;
            if (item is Episode episode)
            {
                prefix = string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}: ", episode.ParentIndexNumber ?? 0, episode.IndexNumber ?? 0);
                item = episode.Series;
            }
            else if (item is Season season)
            {
                prefix = string.Format(CultureInfo.InvariantCulture, "S{0:00}: ", season.IndexNumber ?? 0);
                item = season.Series;
            }

            string mediaType = item is Movie ? Movie : item is Series ? Tv : null;
            if (mediaType == null)
            {
                return null;
            }

            var title = new LibraryTitle { MediaType = mediaType, Title = item.Name, EpisodePrefix = prefix };
            if (int.TryParse(item.GetProviderId("Tmdb"), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId))
            {
                title.TmdbId = tmdbId;
            }
            else if (mediaType == Tv && !string.IsNullOrEmpty(item.GetProviderId("Tvdb")))
            {
                // Series scraped from TheTVDB only: Ombi finds the TMDb id.
                try
                {
                    var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
                    var show = await ombi.GetTvByTvdbAsync(identity, item.GetProviderId("Tvdb"), cancellationToken).ConfigureAwait(false);
                    title.TmdbId = ParseId(show?.theMovieDbId) ?? show?.id ?? 0;
                }
                catch (OmbiException ex)
                {
                    logger.Debug("No TMDb id for {0}: {1}", item.Name, ex.Message);
                }
            }

            return title;
        }

        private sealed class LibraryTitle
        {
            public string MediaType { get; set; }

            public int TmdbId { get; set; }

            public string Title { get; set; }

            /// <summary>"S01E02: " for episodes, "S01: " for seasons.</summary>
            public string EpisodePrefix { get; set; }
        }
    }
}
