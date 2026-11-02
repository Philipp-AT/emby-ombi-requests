using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using OmbiRequests.Configuration;
using OmbiRequests.Ombi;

namespace OmbiRequests.Requests
{
    /// <summary>An error whose message can be shown to the user as is.</summary>
    public class UserFacingException : Exception
    {
        public UserFacingException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Everything the web UI, the channel and the favorite handler do with Ombi, mapped to plugin DTOs.
    /// </summary>
    /// <summary>Part of a list loaded by <see cref="RequestService.LoadListRangeAsync"/>.</summary>
    public class ListRange
    {
        public List<MediaItemDto> Items { get; } = new List<MediaItemDto>();

        /// <summary>True if the list has no more titles (or failed after some), so loading further is pointless.</summary>
        public bool Ended { get; set; }
    }

    public partial class RequestService
    {
        public const string Movie = "movie";
        public const string Tv = "tv";

        private const string TmdbImageBase = "https://image.tmdb.org/t/p/";
        private const int MaxSearchResults = 20;
        private const int MaxRequestsPerList = 250;
        private const int MaxCast = 20;
        private const int ListPageSize = 20;

        private static readonly TimeSpan PosterCacheDuration = TimeSpan.FromHours(12);
        private static readonly TimeSpan DetailsCacheDuration = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ChannelShowCacheDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan TrailerCacheDuration = TimeSpan.FromHours(24);

        /// <summary>
        /// Raised by every request, withdrawal or decision. A load that started before must not put the old
        /// state into a cache afterwards; it compares the number before storing.
        /// </summary>
        private long cacheGeneration;

        private readonly ConcurrentDictionary<string, Tuple<object, DateTime>> detailsCache = new ConcurrentDictionary<string, Tuple<object, DateTime>>();
        private readonly ConcurrentDictionary<int, Tuple<MediaItemDto, DateTime>> channelShowCache = new ConcurrentDictionary<int, Tuple<MediaItemDto, DateTime>>();
        /// <summary>"movie:603" → trailer, background, time of the lookup.</summary>
        private readonly ConcurrentDictionary<string, Tuple<string, string, DateTime>> trailerCache = new ConcurrentDictionary<string, Tuple<string, string, DateTime>>();

        private readonly ConcurrentDictionary<int, Tuple<string, DateTime>> tvPosterCache = new ConcurrentDictionary<int, Tuple<string, DateTime>>();

        /// <summary>Pages of Ombi's discover lists, see <see cref="CachedPageAsync"/>.</summary>
        private readonly ConcurrentDictionary<string, CachedPage> listPages = new ConcurrentDictionary<string, CachedPage>();
        private static readonly TimeSpan ListPageFresh = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ListPageMaxAge = TimeSpan.FromHours(6);
        private const int DetailConcurrency = 5;

        private readonly OmbiClient ombi;
        private readonly Func<PluginConfiguration> getConfig;
        private readonly IServerConfigurationManager serverConfig;
        private readonly Lazy<ILibraryManager> libraryManager;
        private readonly LibraryIndex libraryIndex;
        private readonly RequestInterest interest;
        private readonly Lazy<IUserManager> userManager;
        private readonly ILogger logger;

        public RequestService(
            OmbiClient ombi,
            Func<PluginConfiguration> getConfig,
            IServerConfigurationManager serverConfig,
            Lazy<ILibraryManager> libraryManager,
            ILogger logger,
            RequestInterest interest = null,
            Lazy<IUserManager> userManager = null)
        {
            this.ombi = ombi;
            this.interest = interest ?? new RequestInterest(null, null, logger);
            this.userManager = userManager;
            this.getConfig = getConfig;
            this.serverConfig = serverConfig;
            this.libraryManager = libraryManager;
            libraryIndex = new LibraryIndex(libraryManager, logger);
            this.logger = logger;
        }

        /// <summary>Raised after Ombi accepted a request (e.g. to refresh the channel's status banners).</summary>
        /// <summary>Raised after a request was made, withdrawn, approved or denied, with the title's state right after it.</summary>
        public event EventHandler<RequestChangedEventArgs> RequestChanged;

        public bool IsGerman => Localization.IsGerman(getConfig().Language, serverConfig.Configuration.UICulture);

        /// <summary>Two letter code Ombi uses for metadata of new requests.</summary>
        private string LanguageCode => getConfig().Language == UiLanguage.English ? "en"
            : IsGerman ? "de"
            : PrimaryLanguage(serverConfig.Configuration.UICulture ?? "en");

        public string Text(string key, params object[] args) => Localization.Get(IsGerman, key, args);

        /// <summary>Message for users: Ombi's own explanations as is, technical errors without naming Ombi (details are logged).</summary>
        public string DescribeForUser(Exception ex)
        {
            if (ex is OmbiException ombiException && !ombiException.IsOmbiMessage)
            {
                return ombiException.StatusCode == null
                    ? Text("ServiceUnavailable")
                    : Text("ServiceError", (int)ombiException.StatusCode.Value);
            }

            return ex.Message;
        }

        public async Task<OmbiIdentity> GetIdentityAsync(User user, CancellationToken cancellationToken)
        {
            if (!getConfig().IsConfigured)
            {
                throw new UserFacingException(Text("NotConfigured"));
            }

            var identity = await ombi.ResolveIdentityAsync(user.Name, user.ConnectUserName, cancellationToken).ConfigureAwait(false);
            if (identity == null)
            {
                throw new UserFacingException(Text("NoOmbiAccount", user.Name));
            }

            return identity;
        }

        /// <summary>Language of titles, texts and posters, e.g. "de".</summary>
        public string ContentLanguage => LanguageCode;

        /// <summary>
        /// Identity for reads without Emby user (channel lists, catalog). Ombi returns titles, texts and
        /// posters in the language of the requesting user, and in English for the API key alone, so the
        /// channel showed other posters than the requests page (which runs as the user). These reads
        /// therefore run as an Ombi user with the configured language, preferably an admin. Null if no
        /// user has that language; Ombi then answers in English.
        /// </summary>
        private OmbiIdentity lastContentIdentity;

        public async Task<OmbiIdentity> GetContentIdentityAsync(CancellationToken cancellationToken)
        {
            try
            {
                var users = await ombi.GetUsersAsync(cancellationToken).ConfigureAwait(false);
                var defaultLanguage = await ombi.GetDefaultLanguageAsync(cancellationToken).ConfigureAwait(false);
                var wanted = LanguageCode;
                bool SpeaksWanted(OmbiUser u) =>
                    string.Equals(PrimaryLanguage(string.IsNullOrWhiteSpace(u.language) ? defaultLanguage : u.language), wanted, StringComparison.OrdinalIgnoreCase);

                // The user name is sent as header, which only allows ASCII (see OmbiIdentity.ForUser).
                var user = users
                    .Where(u => !string.IsNullOrEmpty(u.userName) && OmbiIdentity.ForUser(u).OnBehalfOfUserId == null && SpeaksWanted(u))
                    .OrderByDescending(u => u.IsAdmin())
                    .ThenBy(u => u.userName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (user == null)
                {
                    logger.Debug("No Ombi user with language {0}, channel and catalog are loaded in Ombi's API language", wanted);
                    Diagnostics.ContentIdentity = $"API key only, Ombi answers in English (no Ombi user has the language '{wanted}')";
                    return null;
                }

                Diagnostics.ContentIdentity = $"Ombi user '{user.userName}' ({wanted})";
                lastContentIdentity = OmbiIdentity.ForUser(user);
                return lastContentIdentity;
            }
            catch (OmbiException ex)
            {
                // The last one that worked: in another language every title would be written again, and back afterwards.
                logger.Warn("Ombi users could not be loaded, the last Ombi user for channel and catalog is used: {0}", ex.Message);
                return lastContentIdentity;
            }
        }

        private static string PrimaryLanguage(string code) => (code ?? string.Empty).Trim().Split('-', '_')[0];

        /// <summary>
        /// Whether the user may manage the requests of all users. Ombi decides, not Emby: the linked Ombi
        /// user needs the role Admin or PowerUser (like in Ombi itself).
        /// </summary>
        public async Task<bool> CanManageRequestsAsync(User user, CancellationToken cancellationToken)
        {
            if (!getConfig().IsConfigured)
            {
                return false;
            }

            var identity = await ombi.ResolveIdentityAsync(user.Name, user.ConnectUserName, cancellationToken).ConfigureAwait(false);
            return identity?.CanManageRequests == true;
        }

        public async Task<StatusDto> GetStatusAsync(User user, CancellationToken cancellationToken)
        {
            var config = getConfig();
            var status = new StatusDto
            {
                Configured = config.IsConfigured,
                WebIntegration = config.EnableWebIntegration,
                Language = IsGerman ? "de" : "en",
                ChannelName = Channel.OmbiChannel.RegisteredName,
                PageEnabled = config.IsRequestsPageEnabled(user.InternalId),
                Quotas = new List<QuotaDto>()
            };

            try
            {
                var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
                status.OmbiUser = identity.DisplayName;
                status.CanRequest = true;
                status.IsAdmin = identity.CanManageRequests;
                status.Quotas = await GetQuotasAsync(identity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UserFacingException || ex is OmbiException)
            {
                status.Message = DescribeForUser(ex);
            }

            return status;
        }

        public async Task<List<MediaItemDto>> SearchAsync(User user, string query, string type, CancellationToken cancellationToken)
        {
            // Terms like "/" or ".." would turn into an invalid Ombi route.
            if (string.IsNullOrWhiteSpace(query) || !query.Any(char.IsLetterOrDigit))
            {
                return new List<MediaItemDto>();
            }

            var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
            var results = await ombi.SearchAsync(identity, query, type != Tv, type != Movie, cancellationToken).ConfigureAwait(false)
                          ?? new List<MultiSearchResult>();

            var candidates = results
                .Where(r => (r.mediaType == Movie || r.mediaType == Tv) && int.TryParse(r.id, out _))
                .Take(MaxSearchResults)
                .ToList();

            // The multi search has no request status, so each hit is enriched with its details.
            using (var throttle = new SemaphoreSlim(DetailConcurrency))
            {
                var tasks = candidates.Select(async hit =>
                {
                    await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var tmdbId = int.Parse(hit.id, CultureInfo.InvariantCulture);
                        var item = await TryGetDetailsAsync(user, identity, hit.mediaType, tmdbId, cancellationToken).ConfigureAwait(false)
                                   ?? FromSearchHit(hit);

                        // Seasons, cast and keywords are loaded with the details view; keep the result list small.
                        item.Seasons = null;
                        item.Cast = null;
                        item.Keywords = null;
                        return item;
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });

                return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
            }
        }

        public async Task<MediaItemDto> GetDetailsAsync(User user, string mediaType, int tmdbId, CancellationToken cancellationToken)
        {
            var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
            return await GetDetailsAsync(user, identity, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
        }

        /// <param name="user">Used for the library lookup; may be null (channel).</param>
        /// <param name="identity">Null calls Ombi in admin context, which is fine for read-only lists.</param>
        public async Task<MediaItemDto> GetDetailsAsync(User user, OmbiIdentity identity, string mediaType, int tmdbId, CancellationToken cancellationToken)
        {
            if (mediaType == Movie)
            {
                var movie = await GetCachedAsync(Movie, tmdbId, identity, () => ombi.GetMovieAsync(identity, tmdbId, cancellationToken)).ConfigureAwait(false);
                return movie == null ? null : ToDto(movie, user, tmdbId);
            }

            var show = await GetCachedAsync(Tv, tmdbId, identity, () => ombi.GetTvAsync(identity, tmdbId, cancellationToken)).ConfigureAwait(false);
            return show == null ? null : ToDto(show, user, tmdbId, includeSeasons: true);
        }

        /// <summary>
        /// Short cache for Ombi's details: a search enriches up to 20 hits, and typing a longer term
        /// repeats most of them. Request states are the same for all users; a request clears the entry.
        /// </summary>
        private async Task<T> GetCachedAsync<T>(string mediaType, int tmdbId, OmbiIdentity identity, Func<Task<T>> load)
            where T : class
        {
            // Per Ombi user: Ombi answers in the language of the user.
            var key = mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture) + ":" + IdentityKey(identity);
            if (detailsCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Item2 < DetailsCacheDuration && cached.Item1 is T value)
            {
                return value;
            }

            var generation = Interlocked.Read(ref cacheGeneration);
            var loaded = await load().ConfigureAwait(false);
            if (loaded != null && generation == Interlocked.Read(ref cacheGeneration))
            {
                detailsCache[key] = Tuple.Create((object)loaded, DateTime.UtcNow);
                if (detailsCache.Count > 100)
                {
                    foreach (var stale in detailsCache.Where(p => DateTime.UtcNow - p.Value.Item2 >= DetailsCacheDuration).Select(p => p.Key).ToList())
                    {
                        detailsCache.TryRemove(stale, out _);
                    }
                }
            }

            return loaded;
        }

        private void ForgetDetails(string mediaType, int tmdbId)
        {
            // Loads that are running now saw the old state: they must not store it (see cacheGeneration).
            Interlocked.Increment(ref cacheGeneration);
            var prefix = mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture) + ":";
            foreach (var key in detailsCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                detailsCache.TryRemove(key, out _);
            }

            foreach (var page in listPages.Where(p => p.Value.Items.Any(i => i.MediaType == mediaType && i.TmdbId == tmdbId)).Select(p => p.Key).ToList())
            {
                listPages.TryRemove(page, out _);
            }

            if (mediaType == Tv)
            {
                channelShowCache.TryRemove(tmdbId, out _);
            }
        }

        /// <summary>
        /// A series with its seasons, to tell whether a listed series in Emby is complete (see
        /// <see cref="RefineSeriesAvailabilityAsync"/>) and for its trailer. Ombi fetches all seasons and
        /// episodes from TMDb for it, so it is kept for some hours; a request through the plugin clears it.
        /// </summary>
        public async Task<MediaItemDto> GetChannelShowAsync(int tmdbId, CancellationToken cancellationToken)
        {
            if (channelShowCache.TryGetValue(tmdbId, out var cached) && DateTime.UtcNow - cached.Item2 < ChannelShowCacheDuration)
            {
                return cached.Item1;
            }

            var identity = await GetContentIdentityAsync(cancellationToken).ConfigureAwait(false);
            var generation = Interlocked.Read(ref cacheGeneration);
            var show = await GetDetailsAsync(null, identity, Tv, tmdbId, cancellationToken).ConfigureAwait(false);
            if (show != null && generation == Interlocked.Read(ref cacheGeneration))
            {
                // Only seasons and episodes are needed; ToDto created a new object, so it can be trimmed.
                show.Cast = null;
                show.Keywords = null;

                channelShowCache[tmdbId] = Tuple.Create(show, DateTime.UtcNow);
                if (channelShowCache.Count > 300)
                {
                    foreach (var stale in channelShowCache.Where(p => DateTime.UtcNow - p.Value.Item2 >= ChannelShowCacheDuration).Select(p => p.Key).ToList())
                    {
                        channelShowCache.TryRemove(stale, out _);
                    }
                }
            }

            return show;
        }

        /// <summary>Trailer of a title whose list entry has none (Ombi's movie lists leave it out), see <see cref="GetTrailerAndBackdropAsync"/>.</summary>
        public async Task<string> GetTrailerAsync(string mediaType, int tmdbId, CancellationToken cancellationToken)
        {
            return (await GetTrailerAndBackdropAsync(mediaType, tmdbId, cancellationToken).ConfigureAwait(false)).Item1;
        }

        /// <summary>
        /// Trailer and background of a title from Ombi's details, for list entries without them. Kept for a
        /// day, also when there is none.
        /// </summary>
        /// <returns>Trailer link and background URL, each null if Ombi has none.</returns>
        public async Task<Tuple<string, string>> GetTrailerAndBackdropAsync(string mediaType, int tmdbId, CancellationToken cancellationToken)
        {
            var key = mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture);
            if (trailerCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Item3 < TrailerCacheDuration)
            {
                return Tuple.Create(cached.Item1, cached.Item2);
            }

            // A series of the lists is usually in the channel's cache already. Others (thousands in the
            // catalog) are not put there: with all seasons and episodes they would fill the memory.
            var details = mediaType == Tv && channelShowCache.TryGetValue(tmdbId, out var show) && DateTime.UtcNow - show.Item2 < ChannelShowCacheDuration
                ? show.Item1
                : await GetDetailsAsync(null, await GetContentIdentityAsync(cancellationToken).ConfigureAwait(false), mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
            trailerCache[key] = Tuple.Create(details?.TrailerUrl, details?.BackdropUrl, DateTime.UtcNow);
            if (trailerCache.Count > 20000)
            {
                foreach (var stale in trailerCache.Where(p => DateTime.UtcNow - p.Value.Item3 >= TrailerCacheDuration).Select(p => p.Key).ToList())
                {
                    trailerCache.TryRemove(stale, out _);
                }
            }

            return Tuple.Create(details?.TrailerUrl, details?.BackdropUrl);
        }

        /// <param name="allowStale">
        /// The requests page takes a list up to some hours old (and loads a new one in the background), so it
        /// shows up at once; the channel wants it at most 15 minutes old.
        /// </param>
        public async Task<List<MediaItemDto>> DiscoverAsync(User user, OmbiIdentity identity, string category, int count, CancellationToken cancellationToken, bool allowStale = true)
        {
            // category: "movie:popular", "movie:upcoming", "movie:nowplaying", "tv:trending", "tv:popular", ...
            var parts = (category ?? string.Empty).Split(':');
            if (parts.Length != 2 || (parts[0] != Movie && parts[0] != Tv))
            {
                throw new ArgumentException("Unknown category " + category);
            }

            var mediaType = parts[0];
            var list = parts[1];
            var range = await LoadPagedAsync($"the {mediaType} list {list}", 0, count, null,
                (start, amount) => CachedPageAsync(PageKey(identity, mediaType, list, start, amount), allowStale,
                    token => FetchListPageAsync(null, identity, mediaType, list, start, amount, token), cancellationToken),
                parallel: true).ConfigureAwait(false);

            // The cached titles are shared: every caller gets copies with its own link into the library.
            var items = range.Items.Select(i => i.Copy()).ToList();
            foreach (var item in items.Where(i => user != null))
            {
                item.EmbyItemId = FindLibraryItemId(user, item.MediaType, item.TmdbId, item.ImdbId, item.TvdbId);
            }

            if (mediaType == Tv)
            {
                await RefineSeriesAvailabilityAsync(items, cancellationToken).ConfigureAwait(false);
            }

            return items;
        }

        /// <summary>
        /// Ombi's series lists have no seasons, so "available" there only means "in the Emby library" (see
        /// <see cref="SeriesStatus"/>). For these series the details (kept a few hours, see
        /// <see cref="GetChannelShowAsync"/>) tell whether all episodes are there; if not, the series is in
        /// Emby but incomplete.
        /// </summary>
        private async Task RefineSeriesAvailabilityAsync(List<MediaItemDto> items, CancellationToken cancellationToken)
        {
            var available = items.Where(i => i.MediaType == Tv && i.TmdbId > 0 && i.Status == nameof(MediaStatus.Available)).ToList();
            using (var throttle = new SemaphoreSlim(DetailConcurrency))
            {
                await Task.WhenAll(available.Select(async item =>
                {
                    await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var show = await GetChannelShowAsync(item.TmdbId, cancellationToken).ConfigureAwait(false);
                        if (show?.Seasons != null && show.Seasons.Count > 0 && show.Status != nameof(MediaStatus.Available))
                        {
                            item.Status = nameof(MediaStatus.PartiallyAvailable);
                        }
                    }
                    catch (Exception ex) when (ex is OmbiException || ex is UserFacingException)
                    {
                        logger.Debug("Availability of the series {0} stays as listed: {1}", item.TmdbId, ex.Message);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                })).ConfigureAwait(false);
            }
        }

        public async Task<List<MediaItemDto>> LoadListAsync(User user, OmbiIdentity identity, string mediaType, string list, int count, CancellationToken cancellationToken, Action<int> onPage = null)
        {
            return (await LoadListRangeAsync(user, identity, mediaType, list, 0, count, cancellationToken, onPage).ConfigureAwait(false)).Items;
        }

        /// <summary>Positions first .. first + count - 1 of one of Ombi's lists.</summary>
        public Task<ListRange> LoadListRangeAsync(User user, OmbiIdentity identity, string mediaType, string list, int first, int count, CancellationToken cancellationToken, Action<int> onPage = null)
        {
            return LoadPagedAsync($"the {mediaType} list {list}", first, count, onPage,
                (start, amount) => FetchListPageAsync(user, identity, mediaType, list, start, amount, cancellationToken));
        }

        private async Task<List<MediaItemDto>> FetchListPageAsync(User user, OmbiIdentity identity, string mediaType, string list, int start, int amount, CancellationToken cancellationToken)
        {
            return mediaType == Movie
                ? ((await ombi.GetMovieListAsync(identity, list, start, amount, cancellationToken).ConfigureAwait(false)) ?? new List<SearchMovie>())
                    .Select(m => ToDto(m, user, 0)).ToList()
                : ((await ombi.GetTvListAsync(identity, list, start, amount, cancellationToken).ConfigureAwait(false)) ?? new List<SearchTv>())
                    .Select(s => ToDto(s, user, 0, includeSeasons: false)).ToList();
        }

        /// <summary>Ombi answers in the language of the user, so pages are kept per Ombi user.</summary>
        private static string PageKey(OmbiIdentity identity, string mediaType, string list, int start, int amount)
        {
            return string.Join("|", IdentityKey(identity), mediaType, list, start.ToString(CultureInfo.InvariantCulture), amount.ToString(CultureInfo.InvariantCulture));
        }

        private static string IdentityKey(OmbiIdentity identity) => identity == null ? string.Empty : identity.UserName ?? "alias:" + identity.Alias;

        /// <summary>
        /// A page of one of Ombi's lists. Ombi looks up every title of a page at TMDb, which takes seconds
        /// (more for series), and opening the requests page asks for five lists. A page is fresh for 15
        /// minutes; after that, with <paramref name="allowStale"/>, the old one is still used (up to 6 hours)
        /// while a new one loads in the background. A request through the plugin drops the pages with the
        /// title, see <see cref="ForgetDetails"/>.
        /// </summary>
        private async Task<List<MediaItemDto>> CachedPageAsync(string key, bool allowStale, Func<CancellationToken, Task<List<MediaItemDto>>> load, CancellationToken cancellationToken)
        {
            if (listPages.TryGetValue(key, out var cached))
            {
                var age = DateTime.UtcNow - cached.Loaded;
                if (age < ListPageFresh)
                {
                    return cached.Items;
                }

                if (allowStale && age < ListPageMaxAge)
                {
                    RefreshPageInBackground(key, cached, load);
                    return cached.Items;
                }
            }

            var generation = Interlocked.Read(ref cacheGeneration);
            var items = await load(cancellationToken).ConfigureAwait(false);
            StorePage(key, items, generation);
            return items;
        }

        private void RefreshPageInBackground(string key, CachedPage cached, Func<CancellationToken, Task<List<MediaItemDto>>> load)
        {
            if (Interlocked.Exchange(ref cached.Refreshing, 1) == 1)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    var generation = Interlocked.Read(ref cacheGeneration);
                    StorePage(key, await load(CancellationToken.None).ConfigureAwait(false), generation);
                }
                catch (Exception ex)
                {
                    logger.Debug("Reloading the list page {0} failed, the old one stays: {1}", key, ex.Message);
                    Interlocked.Exchange(ref cached.Refreshing, 0);
                }
            });
        }

        private void StorePage(string key, List<MediaItemDto> items, long generation)
        {
            if (generation != Interlocked.Read(ref cacheGeneration))
            {
                return; // A request changed states while this page loaded.
            }

            listPages[key] = new CachedPage(items);
            if (listPages.Count > 500)
            {
                foreach (var stale in listPages.Where(p => DateTime.UtcNow - p.Value.Loaded >= ListPageMaxAge).Select(p => p.Key).ToList())
                {
                    listPages.TryRemove(stale, out _);
                }
            }
        }

        private sealed class CachedPage
        {
            public CachedPage(List<MediaItemDto> items)
            {
                Items = items;
                Loaded = DateTime.UtcNow;
            }

            public List<MediaItemDto> Items { get; }

            public DateTime Loaded { get; }

            /// <summary>1 while a new copy loads in the background.</summary>
            public int Refreshing;
        }

        /// <summary>Movies released in one year, most popular first (catalog import beyond the size of the lists).</summary>
        public Task<ListRange> LoadMoviesOfYearAsync(OmbiIdentity identity, int year, int first, int count, CancellationToken cancellationToken, Action<int> onPage = null)
        {
            return LoadPagedAsync("the movies of " + year.ToString(CultureInfo.InvariantCulture), first, count, onPage, async (start, amount) =>
                ((await ombi.GetMoviesOfYearAsync(identity, year, start, amount, cancellationToken).ConfigureAwait(false)) ?? new List<SearchMovie>())
                    .Select(m => ToDto(m, null, 0)).ToList());
        }

        /// <summary>
        /// Loads in pages, like Ombi's own UI: Ombi looks up every title of a page at TMDb, so a big
        /// page of series can take minutes. If a later page fails, the titles so far are kept; if the
        /// first one fails, the exception is passed on.
        /// </summary>
        /// <param name="onPage">Called with the number of positions loaded so far (progress of the catalog import).</param>
        /// <param name="fetchPage">Loads "amount" titles from position "start".</param>
        /// <param name="parallel">Loads all pages at once (for the few pages of a discover list, not for imports).</param>
        private async Task<ListRange> LoadPagedAsync(string label, int first, int count, Action<int> onPage, Func<int, int, Task<List<MediaItemDto>>> fetchPage, bool parallel = false)
        {
            var result = new ListRange();
            var ids = new HashSet<int>();
            var started = new Dictionary<int, Task<List<MediaItemDto>>>();
            if (parallel)
            {
                for (var offset = 0; offset < count; offset += ListPageSize)
                {
                    started[offset] = fetchPage(first + offset, Math.Min(ListPageSize, count - offset));
                }
            }

            try
            {
                for (var offset = 0; offset < count; offset += ListPageSize)
                {
                    var amount = Math.Min(ListPageSize, count - offset);
                    List<MediaItemDto> page;
                    try
                    {
                        page = await (started.TryGetValue(offset, out var running) ? running : fetchPage(first + offset, amount)).ConfigureAwait(false);
                    }
                    catch (OmbiException ex) when (result.Items.Count > 0)
                    {
                        logger.Warn("Loading {0} stopped after {1} titles: {2}", label, result.Items.Count, ex.Message);
                        result.Ended = true;
                        break;
                    }

                    // Pages can overlap when TMDb's ranking changes between two calls.
                    result.Items.AddRange(page.Where(i => i.TmdbId <= 0 || ids.Add(i.TmdbId)));
                    onPage?.Invoke(offset + amount);
                    if (page.Count < amount)
                    {
                        result.Ended = true;
                        break;
                    }
                }
            }
            finally
            {
                // Pages after a short or failed one are not needed; their errors must not go unobserved.
                foreach (var task in started.Values)
                {
                    _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            return result;
        }

        /// <param name="seasons">Season numbers for <see cref="TvRequestScope.Seasons"/>.</param>
        /// <param name="episodes">"season:episode" values for <see cref="TvRequestScope.Episodes"/>.</param>
        public async Task<RequestOutcomeDto> RequestAsync(
            User user,
            string mediaType,
            int tmdbId,
            TvRequestScope scope,
            IList<int> seasons,
            IList<string> episodes,
            CancellationToken cancellationToken)
        {
            CheckSelection(tmdbId, seasons, episodes);
            if (mediaType == Movie)
            {
                scope = TvRequestScope.All;
                seasons = null;
                episodes = null;
            }

            var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
            using (await LockTitleAsync(mediaType, tmdbId, cancellationToken).ConfigureAwait(false))
            {
                return await RequestLockedAsync(user, identity, mediaType, tmdbId, scope, seasons, episodes, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Most seasons and episodes in one request or withdrawal; anything above is not from the web app.</summary>
        private const int MaxSelectedSeasons = 100;
        private const int MaxSelectedEpisodes = 5000;

        internal static void CheckSelection(int tmdbId, IList<int> seasons, IList<string> episodes)
        {
            if (tmdbId <= 0)
            {
                throw new ArgumentException("TmdbId must be positive");
            }

            if ((seasons?.Count ?? 0) > MaxSelectedSeasons || (episodes?.Count ?? 0) > MaxSelectedEpisodes)
            {
                throw new ArgumentException("Too many seasons or episodes");
            }
        }

        private async Task<RequestOutcomeDto> RequestLockedAsync(User user, OmbiIdentity identity, string mediaType, int tmdbId, TvRequestScope scope,
            IList<int> seasons, IList<string> episodes, CancellationToken cancellationToken)
        {
            RequestEngineResult result = null;
            var nothingToRequest = false;

            // What the user wants: the whole title, or the existing seasons/episodes of the selection.
            IList<int> wishedSeasons = null;
            IList<string> wishedEpisodes = null;
            if (mediaType == Movie)
            {
                var body = new MovieRequestBody { theMovieDbId = tmdbId, languageCode = LanguageCode, requestOnBehalf = identity.OnBehalfOfUserId };
                result = await ombi.RequestMovieAsync(identity, body, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var body = new TvRequestBody
                {
                    theMovieDbId = tmdbId,
                    languageCode = LanguageCode,
                    requestAll = scope == TvRequestScope.All,
                    firstSeason = scope == TvRequestScope.First,
                    latestSeason = scope == TvRequestScope.Latest,
                    requestOnBehalf = identity.OnBehalfOfUserId
                };

                if (scope == TvRequestScope.Seasons || scope == TvRequestScope.Episodes)
                {
                    // Ombi needs explicit episode numbers. Only episodes that are still open are sent,
                    // otherwise Ombi rejects the whole request as "already requested".
                    var wanted = scope == TvRequestScope.Seasons
                        ? (seasons ?? new List<int>()).Distinct().ToDictionary(s => s, s => (HashSet<int>)null)
                        : ParseEpisodeSelection(episodes);

                    // Usually just loaded for the details page (cached two minutes; a request clears it).
                    var show = await GetCachedAsync(Tv, tmdbId, identity, () => ombi.GetTvAsync(identity, tmdbId, cancellationToken)).ConfigureAwait(false);
                    var existing = (show?.seasonRequests ?? new List<SeasonRequest>()).Where(s => wanted.ContainsKey(s.seasonNumber)).ToList();
                    if (scope == TvRequestScope.Seasons)
                    {
                        wishedSeasons = existing.Select(s => s.seasonNumber).ToList();
                    }
                    else
                    {
                        wishedEpisodes = existing
                            .SelectMany(s => (s.episodes ?? new List<EpisodeRequest>())
                                .Where(e => wanted[s.seasonNumber].Contains(e.episodeNumber))
                                .Select(e => s.seasonNumber.ToString(CultureInfo.InvariantCulture) + ":" + e.episodeNumber.ToString(CultureInfo.InvariantCulture)))
                            .ToList();
                    }

                    foreach (var season in existing.OrderBy(s => s.seasonNumber))
                    {
                        var selection = wanted[season.seasonNumber];
                        var selected = (season.episodes ?? new List<EpisodeRequest>())
                            .Where(e => !e.available && !e.requested && !e.approved && !e.denied)
                            .Where(e => selection == null || selection.Contains(e.episodeNumber))
                            .Select(e => new EpisodesBody { episodeNumber = e.episodeNumber })
                            .ToList();
                        if (selected.Count > 0)
                        {
                            body.seasons.Add(new SeasonsBody { seasonNumber = season.seasonNumber, episodes = selected });
                        }
                    }

                    if ((wishedSeasons?.Count ?? 0) == 0 && (wishedEpisodes?.Count ?? 0) == 0)
                    {
                        // None of the selected seasons or episodes exists.
                        return new RequestOutcomeDto { Success = false, Message = Text("NothingToRequest") };
                    }

                    nothingToRequest = body.seasons.Count == 0;
                }

                if (!nothingToRequest)
                {
                    result = await ombi.RequestTvAsync(identity, body, cancellationToken).ConfigureAwait(false);
                }
            }

            var success = result != null && result.result && !result.isError;
            ForgetDetails(mediaType, tmdbId);
            var quotaTask = success ? QuotaSuffixAsync(identity, mediaType, cancellationToken) : Task.FromResult(string.Empty);
            var details = await TryGetDetailsAsync(user, identity, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);

            // The user wants it also when somebody else requested it already ("already requested", see
            // RequestInterest), but not when Ombi refused it (e.g. quota exceeded).
            if (success || AlreadyRequested(details, wishedSeasons, wishedEpisodes))
            {
                interest.Add(mediaType, tmdbId, user.InternalId, wishedSeasons, wishedEpisodes);
            }

            if (nothingToRequest)
            {
                return new RequestOutcomeDto { Success = false, Message = Text("NothingToRequest"), Status = details?.Status };
            }

            logger.Info("{0} requested {1} {2} as {3}: {4}", user.Name, mediaType, tmdbId, identity.DisplayName, success ? "ok" : result?.errorMessage ?? result?.message);
            if (success)
            {
                RequestChanged?.Invoke(this, new RequestChangedEventArgs(mediaType, tmdbId, details));
            }

            return new RequestOutcomeDto
            {
                Success = success,
                Message = success
                    ? Text("RequestedTitle", details?.Title ?? tmdbId.ToString(CultureInfo.InvariantCulture)) + await quotaTask.ConfigureAwait(false)
                    : result?.errorMessage ?? result?.message ?? Text("RequestFailed"),
                Status = details?.Status
            };
        }

        private static bool IsTaken(string status)
        {
            return status == nameof(MediaStatus.Requested) || status == nameof(MediaStatus.Approved)
                   || status == nameof(MediaStatus.PartiallyAvailable) || status == nameof(MediaStatus.Available);
        }

        /// <summary>Whether the title, or the wished seasons/episodes, are requested or there already.</summary>
        private static bool AlreadyRequested(MediaItemDto details, IList<int> seasons, IList<string> episodes)
        {
            if (details == null)
            {
                return false;
            }

            var parts = details.Seasons ?? new List<SeasonDto>();
            if (episodes != null && episodes.Count > 0)
            {
                return parts.Any(s => (s.Episodes ?? new List<EpisodeDto>()).Any(e =>
                    IsTaken(e.Status) && episodes.Contains(s.SeasonNumber.ToString(CultureInfo.InvariantCulture) + ":" + e.EpisodeNumber.ToString(CultureInfo.InvariantCulture))));
            }

            if (seasons != null && seasons.Count > 0)
            {
                return parts.Any(s => seasons.Contains(s.SeasonNumber) && (IsTaken(s.Status) || (s.Episodes ?? new List<EpisodeDto>()).Any(e => IsTaken(e.Status))));
            }

            return IsTaken(details.Status);
        }

        /// <summary>Same rule as <see cref="WithdrawAsync(User, string, int, IList{int}, IList{string}, CancellationToken)"/>.</summary>
        private bool CanWithdraw(bool approved, bool denied, bool available) => !available && !denied && (!approved || getConfig().WithdrawApprovedRequests);

        public async Task<List<UserRequestDto>> GetMyRequestsAsync(User user, CancellationToken cancellationToken)
        {
            var identity = await GetIdentityAsync(user, cancellationToken).ConfigureAwait(false);
            var moviesTask = ombi.GetMovieRequestsAsync(identity, MaxRequestsPerList, cancellationToken);
            var showsTask = ombi.GetTvRequestsAsync(identity, MaxRequestsPerList, cancellationToken);

            // One failing list (movies or series) should not hide the other one.
            var movies = await TryGetRequestsAsync(moviesTask, "movie").ConfigureAwait(false);
            var seasons = await TryGetRequestsAsync(showsTask, "tv").ConfigureAwait(false);
            if (movies == null && seasons == null)
            {
                await moviesTask.ConfigureAwait(false);
            }

            var list = new List<UserRequestDto>();

            // Depending on Ombi's settings users may see everybody's requests, so filter to their own.
            foreach (var movie in movies ?? new List<MovieRequest>())
            {
                if (!identity.Matches(movie.requestedUser, movie.requestedByAlias))
                {
                    continue;
                }

                list.Add(new UserRequestDto
                {
                    MediaType = Movie,
                    TmdbId = movie.theMovieDbId,
                    Title = movie.title,
                    Year = ParseYear(movie.releaseDate),
                    PosterUrl = ImageUrl(movie.posterPath, "w342"),
                    RequestedDate = movie.requestedDate,
                    Status = GetStatus(movie.available, false, movie.denied, movie.approved, true).ToString(),
                    DeniedReason = movie.deniedReason,
                    CanWithdraw = CanWithdraw(movie.approved, movie.denied, movie.available)
                });
            }

            foreach (var child in seasons ?? new List<ChildRequest>())
            {
                if (!identity.Matches(child.requestedUser, child.requestedByAlias))
                {
                    continue;
                }

                var show = child.parentRequest;
                list.Add(new UserRequestDto
                {
                    MediaType = Tv,
                    TmdbId = show?.externalProviderId ?? 0,
                    Title = show?.title ?? child.title,
                    Year = ParseYear(show?.releaseDate) ?? ParseYear(child.releaseYear),
                    PosterUrl = ImageUrl(show?.posterPath, "w342"),
                    RequestedDate = child.requestedDate,
                    Status = GetStatus(child.available, false, child.denied, child.approved, true).ToString(),
                    DeniedReason = child.deniedReason,
                    CanWithdraw = CanWithdraw(child.approved, child.denied, child.available),
                    Seasons = child.seasonRequests?.Select(s => s.seasonNumber).OrderBy(n => n).ToList()
                });
            }

            // ISO dates sort correctly as strings.
            var result = list.OrderByDescending(r => r.RequestedDate, StringComparer.Ordinal).Take(100).ToList();
            await FillMissingTvPostersAsync(identity, result, cancellationToken).ConfigureAwait(false);

            // Available titles open their Emby page directly in the web app.
            foreach (var request in result.Where(r => r.Status == nameof(MediaStatus.Available) && r.TmdbId > 0))
            {
                request.EmbyItemId = FindLibraryItemId(user, request.MediaType, request.TmdbId, null, null);
            }

            return result;
        }

        /// <summary>
        /// Series requests often have no poster or an outdated TheTVDB link (Ombi used TVDB before v4),
        /// while the details always have a TMDb poster. Missing ones are taken from there, cached per show.
        /// </summary>
        private async Task FillMissingTvPostersAsync(OmbiIdentity identity, List<UserRequestDto> requests, CancellationToken cancellationToken)
        {
            var missing = requests
                .Where(r => r.MediaType == Tv && r.TmdbId > 0 && !IsTmdbImage(r.PosterUrl))
                .GroupBy(r => r.TmdbId)
                .ToList();
            if (missing.Count == 0)
            {
                return;
            }

            using (var throttle = new SemaphoreSlim(DetailConcurrency))
            {
                await Task.WhenAll(missing.Select(async show =>
                {
                    var poster = await GetTvPosterAsync(identity, show.Key, throttle, cancellationToken).ConfigureAwait(false);
                    if (poster != null)
                    {
                        foreach (var request in show)
                        {
                            request.PosterUrl = poster;
                        }
                    }
                })).ConfigureAwait(false);
            }
        }

        private async Task<string> GetTvPosterAsync(OmbiIdentity identity, int tmdbId, SemaphoreSlim throttle, CancellationToken cancellationToken)
        {
            if (tvPosterCache.TryGetValue(tmdbId, out var cached) && DateTime.UtcNow - cached.Item2 < PosterCacheDuration)
            {
                return cached.Item1;
            }

            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var show = await ombi.GetTvAsync(identity, tmdbId, cancellationToken).ConfigureAwait(false);
                var poster = ImageUrl(show?.posterPath ?? show?.images?.medium ?? show?.images?.original, "w342");
                tvPosterCache[tmdbId] = Tuple.Create(poster, DateTime.UtcNow);
                return poster;
            }
            catch (OmbiException ex)
            {
                logger.Debug("No poster for show {0}: {1}", tmdbId, ex.Message);

                // Not again on every load of the lists (the admin list can have hundreds of shows).
                tvPosterCache[tmdbId] = Tuple.Create((string)null, DateTime.UtcNow);
                return null;
            }
            finally
            {
                throttle.Release();
            }
        }

        private static bool IsTmdbImage(string url)
        {
            return url != null && url.IndexOf("image.tmdb.org", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<List<T>> TryGetRequestsAsync<T>(Task<RequestsPage<T>> request, string kind)
        {
            try
            {
                return (await request.ConfigureAwait(false))?.collection ?? new List<T>();
            }
            catch (OmbiException ex)
            {
                logger.Warn("Loading the {0} requests from Ombi failed: {1}", kind, ex.Message);
                return null;
            }
        }

        private async Task<MediaItemDto> TryGetDetailsAsync(User user, OmbiIdentity identity, string mediaType, int tmdbId, CancellationToken cancellationToken)
        {
            try
            {
                return await GetDetailsAsync(user, identity, mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
            }
            catch (OmbiException ex)
            {
                logger.Debug("No Ombi details for {0} {1}: {2}", mediaType, tmdbId, ex.Message);
                return null;
            }
        }

        private MediaItemDto ToDto(SearchMovie movie, User user, int fallbackTmdbId)
        {
            var tmdbId = ParseId(movie.theMovieDbId) ?? (movie.id != 0 ? movie.id : fallbackTmdbId);
            var dto = new MediaItemDto
            {
                MediaType = Movie,
                TmdbId = tmdbId,
                Title = movie.title,
                Year = ParseYear(movie.releaseDate),
                Overview = movie.overview,
                PosterUrl = ImageUrl(movie.posterPath, "w342"),
                BackdropUrl = ImageUrl(movie.backdropPath, "w1280"),
                Status = GetStatus(movie.available, false, movie.denied, movie.approved, movie.requested).ToString(),
                DeniedReason = movie.deniedReason,
                EmbyItemId = FindLibraryItemId(user, Movie, tmdbId, movie.imdbId, null),
                ImdbId = movie.imdbId,
                TrailerUrl = TrailerUrl(movie.trailer, (movie as MovieDetails)?.videos?.results),
                Rating = movie.voteAverage > 0 ? movie.voteAverage : (double?)null,
                VoteCount = movie.voteCount > 0 ? movie.voteCount : (int?)null,
                ReleaseDate = movie.releaseDate
            };

            if (movie is MovieDetails details)
            {
                dto.Runtime = details.runtime > 0 ? details.runtime : (int?)null;
                dto.DigitalReleaseDate = details.digitalReleaseDate;
                dto.ReleaseStatus = details.status;
                dto.Tagline = details.tagline;
                dto.Budget = details.budget > 0 ? details.budget : (double?)null;
                dto.Revenue = details.revenue > 0 ? details.revenue : (double?)null;
                dto.Genres = Names(details.genres);
                dto.Keywords = Names(details.keywords?.keywordsValue);
                dto.Cast = details.credits?.cast?
                    .Where(c => !string.IsNullOrEmpty(c.name))
                    .Take(MaxCast)
                    .Select(c => new CastDto { Name = c.name, Character = c.character, ImageUrl = ImageUrl(c.profile_path, "w185") })
                    .ToList();
            }

            return dto;
        }

        private MediaItemDto ToDto(SearchTv show, User user, int fallbackTmdbId, bool includeSeasons)
        {
            var tmdbId = ParseId(show.theMovieDbId) ?? (show.id != 0 ? show.id : fallbackTmdbId);
            var details = show as TvDetails;
            var dto = new MediaItemDto
            {
                MediaType = Tv,
                TmdbId = tmdbId,
                Title = show.title,
                Year = ParseYear(show.firstAired),
                Overview = show.overview,
                // The tv details have no posterPath/backdropPath, Ombi puts poster and backdrop into images/banner.
                PosterUrl = ImageUrl(show.posterPath ?? details?.images?.medium ?? details?.images?.original, "w342"),
                BackdropUrl = ImageUrl(show.backdropPath ?? show.banner, "w1280"),
                Status = SeriesStatus(show, tmdbId).ToString(),
                DeniedReason = show.deniedReason,
                EmbyItemId = FindLibraryItemId(user, Tv, tmdbId, show.imdbId, show.theTvDbId),
                ImdbId = show.imdbId,
                TvdbId = show.theTvDbId,
                TrailerUrl = TrailerUrl(show.trailer, null),
                Rating = double.TryParse(show.rating, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating) && rating > 0 ? rating : (double?)null,
                Runtime = int.TryParse(show.runtime, NumberStyles.None, CultureInfo.InvariantCulture, out var runtime) && runtime > 0 ? runtime : (int?)null,
                ReleaseDate = show.firstAired,
                ReleaseStatus = show.status
            };

            if (details != null)
            {
                dto.Network = details.network?.name;
                dto.Certification = details.certification;
                dto.Tagline = details.tagline;
                dto.Genres = Names(details.genres);
                dto.Keywords = Names(details.keywords?.keywordsValue);
                dto.Cast = details.cast?
                    .Where(c => !string.IsNullOrEmpty(c.person))
                    .Take(MaxCast)
                    .Select(c => new CastDto { Name = c.person, Character = c.character, ImageUrl = ImageUrl(c.image, "w185") })
                    .ToList();
            }

            if (includeSeasons && show.seasonRequests != null)
            {
                dto.Seasons = show.seasonRequests
                    .Where(s => s.seasonNumber > 0)
                    .OrderBy(s => s.seasonNumber)
                    .Select(s => new SeasonDto
                    {
                        SeasonNumber = s.seasonNumber,
                        EpisodeCount = s.episodes?.Count ?? 0,
                        Status = GetSeasonStatus(s).ToString(),
                        Episodes = (s.episodes ?? new List<EpisodeRequest>())
                            .OrderBy(e => e.episodeNumber)
                            .Select(e => new EpisodeDto
                            {
                                EpisodeNumber = e.episodeNumber,
                                Title = e.title,
                                AirDate = e.airDate,
                                Status = GetStatus(e.available, false, e.denied, e.approved, e.requested).ToString()
                            })
                            .ToList()
                    })
                    .ToList();
            }

            return dto;
        }

        /// <summary>
        /// Ombi's series lists come without seasons, and Ombi then reports every series as fully
        /// available (all of its zero seasons are). Without season data, "available" therefore comes
        /// from the Emby library; requested, approved and denied still come from Ombi.
        /// </summary>
        private MediaStatus SeriesStatus(SearchTv show, int tmdbId)
        {
            var episodes = (show.seasonRequests ?? new List<SeasonRequest>())
                .Where(s => s.seasonNumber > 0)
                .SelectMany(s => s.episodes ?? new List<EpisodeRequest>())
                .ToList();
            if (episodes.Count > 0)
            {
                // Ombi's "available" on a series only says that it is in Emby at all, and "fullyAvailable"
                // also wants specials and episodes that have not aired yet. Counted here: the aired
                // episodes of the regular seasons (all of them if Ombi has no air dates).
                var aired = episodes.Where(HasAired).ToList();
                if (aired.Count == 0)
                {
                    aired = episodes;
                }

                return GetStatus(aired.All(e => e.available), aired.Any(e => e.available), show.denied, show.approved, show.requested);
            }

            if (show.seasonRequests != null && show.seasonRequests.Count > 0)
            {
                return GetStatus(show.fullyAvailable, show.partlyAvailable, show.denied, show.approved, show.requested);
            }

            if (libraryIndex.Contains(Tv, tmdbId, show.imdbId, show.theTvDbId))
            {
                return show.partlyAvailable ? MediaStatus.PartiallyAvailable : MediaStatus.Available;
            }

            return GetStatus(false, false, show.denied, show.approved, show.requested);
        }

        /// <summary>Ombi writes "0001-01-01" for episodes without a date; those count as not aired.</summary>
        private static bool HasAired(EpisodeRequest episode)
        {
            return DateTime.TryParse(episode.airDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
                   && date.Year > 1
                   && date.Date <= DateTime.UtcNow.Date;
        }

        private MediaItemDto FromSearchHit(MultiSearchResult hit)
        {
            return new MediaItemDto
            {
                MediaType = hit.mediaType,
                TmdbId = int.Parse(hit.id, CultureInfo.InvariantCulture),
                Title = hit.title,
                Overview = hit.overview,
                PosterUrl = ImageUrl(hit.poster, "w342"),
                Status = MediaStatus.None.ToString()
            };
        }

        /// <summary>
        /// Available for watching: Ombi says so, or a movie is already in the Emby library (Ombi's sync
        /// may lag behind). Partly available series do not count, missing seasons can still be requested.
        /// </summary>
        public static bool IsInLibrary(MediaItemDto item)
        {
            return item.Status == nameof(MediaStatus.Available) || (item.MediaType == Movie && item.EmbyItemId != null);
        }

        public static MediaStatus GetStatus(bool available, bool partlyAvailable, bool denied, bool approved, bool requested)
        {
            if (available)
            {
                return MediaStatus.Available;
            }

            if (partlyAvailable)
            {
                return MediaStatus.PartiallyAvailable;
            }

            if (denied)
            {
                return MediaStatus.Denied;
            }

            if (approved)
            {
                return MediaStatus.Approved;
            }

            return requested ? MediaStatus.Requested : MediaStatus.None;
        }

        private static List<string> Names(IEnumerable<NamedItem> items)
        {
            return items?.Select(i => i?.name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        }

        /// <summary>"1:3", "1:4", "2:1" -> {1: [3, 4], 2: [1]}. Invalid values are ignored.</summary>
        private static Dictionary<int, HashSet<int>> ParseEpisodeSelection(IList<string> episodes)
        {
            var result = new Dictionary<int, HashSet<int>>();
            foreach (var value in episodes ?? new List<string>())
            {
                var parts = (value ?? string.Empty).Split(':');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var season)
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var episode))
                {
                    continue;
                }

                if (!result.TryGetValue(season, out var set))
                {
                    result[season] = set = new HashSet<int>();
                }

                set.Add(episode);
            }

            return result;
        }

        private static MediaStatus GetSeasonStatus(SeasonRequest season)
        {
            var episodes = season.episodes ?? new List<EpisodeRequest>();
            if (season.seasonAvailable || (episodes.Count > 0 && episodes.All(e => e.available)))
            {
                return MediaStatus.Available;
            }

            if (episodes.Any(e => e.available))
            {
                return MediaStatus.PartiallyAvailable;
            }

            if (episodes.Count > 0 && episodes.All(e => e.denied))
            {
                return MediaStatus.Denied;
            }

            if (episodes.Any(e => e.approved))
            {
                return MediaStatus.Approved;
            }

            return episodes.Any(e => e.requested) ? MediaStatus.Requested : MediaStatus.None;
        }

        private string FindLibraryItemId(User user, string mediaType, int tmdbId, string imdbId, string tvdbId)
        {
            if (user == null || tmdbId == 0)
            {
                return null;
            }

            var providerIds = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Tmdb", tmdbId.ToString(CultureInfo.InvariantCulture))
            };
            if (!string.IsNullOrEmpty(imdbId))
            {
                providerIds.Add(new KeyValuePair<string, string>("Imdb", imdbId));
            }

            if (!string.IsNullOrEmpty(tvdbId) && tvdbId != "0")
            {
                providerIds.Add(new KeyValuePair<string, string>("Tvdb", tvdbId));
            }

            try
            {
                var items = libraryManager.Value.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { mediaType == Movie ? "Movie" : "Series" },
                    AnyProviderIdEquals = providerIds,
                    Recursive = true,
                    Limit = 5
                });

                // Entries of the requests channel are not in the library; older plugin versions created
                // them as movies/series with TMDb ids, which would otherwise match here.
                return items.FirstOrDefault(i => i.ExternalId == null || !Channel.ChannelItemIds.IsOwn(i.ExternalId))?.GetClientId();
            }
            catch (Exception ex)
            {
                logger.ErrorException("Library lookup for {0} {1} failed", ex, mediaType, tmdbId);
                return null;
            }
        }

        /// <summary>
        /// Ombi's trailer link as a YouTube URL. Ombi stores either a full link or only the video key;
        /// movie details without link fall back to TMDb's first YouTube trailer.
        /// </summary>
        internal static string TrailerUrl(string trailer, IEnumerable<TmdbVideo> videos)
        {
            var value = (trailer ?? string.Empty).Trim();
            if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (value.Length > 0)
            {
                return "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(value);
            }

            var key = videos?
                .Where(v => !string.IsNullOrEmpty(v?.key) && string.Equals(v.site, "YouTube", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => string.Equals(v.type, "Trailer", StringComparison.OrdinalIgnoreCase))
                .Select(v => v.key)
                .FirstOrDefault();
            return key == null ? null : "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(key);
        }

        public static string ImageUrl(string path, string size)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return TmdbImageBase + size + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);
        }

        private static int? ParseYear(string date)
        {
            if (!string.IsNullOrEmpty(date) && date.Length >= 4 && int.TryParse(date.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year > 1800)
            {
                return year;
            }

            return null;
        }

        private static int? ParseId(string value)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : (int?)null;
        }
    }
}
