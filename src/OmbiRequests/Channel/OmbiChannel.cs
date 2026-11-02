using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using OmbiRequests.Catalog;
using OmbiRequests.Configuration;
using OmbiRequests.Ombi;
using OmbiRequests.Requests;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Shows Ombi's discover lists natively in every Emby app, named "Requests" for the users, plus
    /// "All movies" / "All series" from the <see cref="TitleCatalog"/>. Emby queries channels without
    /// user context, so the lists are the same for everybody; requesting happens through favorites
    /// (see <see cref="FavoriteRequestHandler"/>), where the user is known. Emby only reads channel
    /// content when its daily task runs or the channel raises ContentChanged, see <see cref="RequestRefresh"/>.
    /// </summary>
    public class OmbiChannel : IChannel, IHasChangeEvent, IHasChannelFeatures
    {

        private static readonly MetadataFields[] OwnFields = { MetadataFields.Name, MetadataFields.Overview };
        private static readonly object RefreshLock = new object();

        private const string AllMovies = "movie:all";
        private const string AllTv = "tv:all";

        /// <summary>Safety net only: every refresh starts at the root, which rebuilds the lists.</summary>
        private static readonly TimeSpan ListsMaxAge = TimeSpan.FromHours(6);
        private static readonly SemaphoreSlim ListsLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromSeconds(30);

        /// <summary>Emby reads the folders one after another; while it does, a refresh is still running.</summary>
        private static readonly TimeSpan RefreshQuietTime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ListedSaveDelay = TimeSpan.FromSeconds(30);
        private const string ListedFileName = "ombirequests-channel-listed.json";
        private const string ListsFileName = "ombirequests-channel-lists.json";

        /// <summary>A refresh that failed at the start is tried again after this.</summary>
        private static readonly TimeSpan FailedRefreshRetryDelay = TimeSpan.FromMinutes(5);

        private static OmbiChannel current;
        private static Timer refreshTimer;
        private static Dictionary<string, List<ChannelItemInfo>> cachedLists;
        private static DateTime cachedListsTime;

        /// <summary>Catalog titles per list, marked as handed to Emby once Emby fetched the folder.</summary>
        private static Dictionary<string, List<CatalogEntry>> unlistedEntries = new Dictionary<string, List<CatalogEntry>>();

        /// <summary>
        /// Titles of the last successful load per list, used when Ombi does not answer; kept in a file, so
        /// also right after a restart (Ombi may start later than Emby).
        /// </summary>
        private static readonly Dictionary<string, List<MediaItemDto>> lastLoaded = new Dictionary<string, List<MediaItemDto>>();
        private static DataFile<Dictionary<string, List<MediaItemDto>>> listsFile;
        private static DateTime refreshDue = DateTime.MaxValue;

        /// <summary>
        /// What Emby last got for each title of the lists ("list|id": a hash of state, name, pictures,
        /// texts). Emby writes an existing item again only with ForceUpdate, and every write is a database
        /// write plus a queued metadata refresh. So only changed ones get it, also after a restart (kept in
        /// a file).
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> listed = new ConcurrentDictionary<string, string>();
        private static readonly object ListedLock = new object();
        private static DataFile<Dictionary<string, string>> listedFile;
        private static Timer listedSaveTimer;

        /// <summary>Titles of a list with what they show, marked as listed once Emby fetched the folder.</summary>
        private static Dictionary<string, List<KeyValuePair<string, string>>> unlistedTitles = new Dictionary<string, List<KeyValuePair<string, string>>>();

        /// <summary>Last time Emby asked the channel for a folder, and how many it is reading right now.</summary>
        private static DateTime lastQuery;
        private static int readsInProgress;

        private readonly ILogger logger;
        private readonly Lazy<string> name;

        public OmbiChannel(ILogManager logManager, IJsonSerializer json)
        {
            // Emby has no way to disable a plugin's channel. Failing here makes Emby skip it
            // (ApplicationHost.CreateInstanceSafe), and its daily channel task removes the channel entry.
            if (Plugin.Instance?.Configuration.EnableChannel == false)
            {
                throw new ChannelDisabledException();
            }

            logger = logManager.GetLogger(Plugin.PluginName);
            LoadListed(json, logger);

            name = new Lazy<string>(StableName);
            current = this;
        }

        public event EventHandler ContentChanged;

        /// <summary>
        /// Emby derives the channel's id from its name. Another name (e.g. after switching the language)
        /// would make a second channel and drop all items and hearts of the first one, so the name used
        /// the first time stays.
        /// </summary>
        private static string StableName()
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (config == null)
            {
                return Service?.Text("ChannelName") ?? "Requests";
            }

            if (string.IsNullOrEmpty(config.ChannelRegisteredName))
            {
                config.ChannelRegisteredName = plugin.RequestService.Text("ChannelName");
                try
                {
                    plugin.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    // Kept for this run anyway; the next start tries again.
                    plugin.Logger.ErrorException("Saving the name of the requests channel failed", ex);
                }
            }

            return config.ChannelRegisteredName;
        }

        /// <summary>
        /// Library options of the channel's items, without any metadata or image fetcher. As series Emby
        /// would otherwise look the titles up at TMDb by name after every refresh and add ids, pictures
        /// and collections (Emby uses these options instead of its defaults, see
        /// ChannelManager.GetLibraryOptions).
        /// </summary>
        public ChannelFeatures Features { get; } = new ChannelFeatures { LibraryOptions = new LibraryOptions() };

        public string Name => name.Value;

        public string Description => Service?.Text("ChannelDescription") ?? string.Empty;

        /// <summary>False if the channel was switched off when Emby started; switching it on then needs a restart.</summary>
        public static bool IsRegistered => current != null;

        /// <summary>Name of the channel as Emby registered it ("Anfragen"), null if it is not registered.</summary>
        public static string RegisteredName => current?.Name;

        private static bool IsEnabled => Plugin.Instance?.Configuration.EnableChannel != false;

        /// <summary>Asks Emby to reload the channel; several calls within the delay are merged into one.</summary>
        public static void RequestRefresh(TimeSpan delay)
        {
            var channel = current;
            if (channel == null || !IsEnabled)
            {
                return;
            }

            ScheduleFire(channel, delay, keepEarlier: true);
        }

        /// <param name="keepEarlier">A refresh that is due sooner stays (e.g. 5 s after an import, not 10 min after a request).</param>
        private static void ScheduleFire(OmbiChannel channel, TimeSpan delay, bool keepEarlier)
        {
            lock (RefreshLock)
            {
                var due = DateTime.UtcNow + delay;
                if (keepEarlier && refreshTimer != null && refreshDue <= due)
                {
                    return;
                }

                refreshTimer?.Dispose();
                refreshDue = due;
                refreshTimer = new Timer(_ =>
                {
                    lock (RefreshLock)
                    {
                        refreshDue = DateTime.MaxValue;
                    }

                    Fire(channel);
                }, null, delay, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>Stops the timers and writes what is pending; called when Emby shuts down.</summary>
        public static void Shutdown()
        {
            lock (RefreshLock)
            {
                refreshTimer?.Dispose();
                refreshTimer = null;
                refreshDue = DateTime.MaxValue;
            }

            lock (ListedLock)
            {
                listedSaveTimer?.Dispose();
                listedSaveTimer = null;
                listedFile?.Save(new Dictionary<string, string>(listed));
            }
        }

        /// <summary>
        /// While Emby refreshes the channel, another change event would queue a second full pass right
        /// behind it (thousands of folder reads): wait until the running one is done.
        /// </summary>
        private static void Fire(OmbiChannel channel)
        {
            // A refresh started through ContentChanged is no scheduled task; Emby reading the folders shows it.
            if (IsEmbyRefreshRunning() || Volatile.Read(ref readsInProgress) > 0 || DateTime.UtcNow - lastQuery < RefreshQuietTime)
            {
                ScheduleFire(channel, RefreshRetryDelay, keepEarlier: false);
                return;
            }

            channel.ContentChanged?.Invoke(channel, EventArgs.Empty);
        }

        private static bool IsEmbyRefreshRunning()
        {
            try
            {
                return Plugin.Instance?.TaskManager.ScheduledTasks
                    .Any(t => t.State != TaskState.Idle && t.ScheduledTask.GetType().Name == "RefreshChannelsScheduledTask") == true;
            }
            catch (Exception)
            {
                // Without the task manager the request simply goes out.
                return false;
            }
        }

        /// <summary>
        /// All lists of the channel. A title can be in several of them (e.g. "Popular movies" and
        /// "All movies"); Emby creates an item per folder, and <see cref="ChannelItemUpdater"/>
        /// makes the search show it once. Emby asks for the lists one after another during a refresh,
        /// so they are built once per refresh (see the root folder in <see cref="GetChannelItems"/>).
        /// </summary>
        private async Task<Dictionary<string, List<ChannelItemInfo>>> GetListsAsync(RequestService service, PluginConfiguration config, CancellationToken cancellationToken)
        {
            await ListsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cached = cachedLists;
                if (cached != null && DateTime.UtcNow - cachedListsTime < ListsMaxAge)
                {
                    return cached;
                }

                var started = DateTime.UtcNow;
                var lists = new Dictionary<string, List<ChannelItemInfo>>();
                var titles = new Dictionary<string, List<KeyValuePair<string, string>>>();
                var catalogLists = new Dictionary<string, List<CatalogEntry>>();
                var loaded = new Dictionary<string, int>();
                List<CatalogEntry> catalogEntries = null;

                // Same language (and so the same posters) as the requests page, see GetContentIdentityAsync.
                var identity = await service.GetContentIdentityAsync(cancellationToken).ConfigureAwait(false);
                foreach (var category in config.ChannelCategories())
                {
                    if (category == AllMovies || category == AllTv)
                    {
                        catalogEntries = catalogEntries ?? await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
                        var mediaType = category == AllMovies ? RequestService.Movie : RequestService.Tv;
                        var entries = catalogEntries
                            .Where(e => e.MediaType == mediaType && e.TmdbId > 0 && config.ShowInChannel(e.Status))
                            .ToList();
                        AddLetterFolders(lists, catalogLists, category, entries);
                        loaded[category] = catalogEntries.Count(e => e.MediaType == mediaType);
                        logger.Info("Requests channel: {0} has {1} of {2} catalog titles", category, entries.Count, catalogEntries.Count(e => e.MediaType == mediaType));
                        continue;
                    }

                    var listSize = Math.Max(10, Math.Min(500, category.StartsWith(RequestService.Movie + ":", StringComparison.Ordinal)
                        ? config.ChannelMovieListSize
                        : config.ChannelTvListSize));
                    List<MediaItemDto> items;
                    try
                    {
                        // Fetch more than shown: the status filter removes some.
                        items = await service.DiscoverAsync(null, identity, category, FetchCount(listSize), cancellationToken, allowStale: false).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // The titles of the last refresh: an empty list would hide the folder, and Emby would
                        // delete its items (with the users' hearts) only to create them again next time.
                        logger.ErrorException("Loading the list {0} failed, keeping the titles of the last refresh", ex, category);
                        items = lastLoaded.TryGetValue(category, out var previous) ? previous : new List<MediaItemDto>();
                    }

                    if (items.Count > 0)
                    {
                        lastLoaded[category] = items;
                    }

                    var shown = items
                        .Where(i => i.TmdbId > 0 && config.ShowInChannel(i.Status))
                        .Take(listSize)
                        .ToList();
                    await FillTrailersAsync(service, shown, cancellationToken).ConfigureAwait(false);
                    lists[category] = shown.Select(i => ToChannelItem(i, category)).ToList();
                    titles[category] = lists[category].Select(i => SetForceUpdate(i, category + "|" + i.Id)).ToList();
                    loaded[category] = items.Count;
                    logger.Info("Requests channel: {0} has {1} titles ({2} loaded from Ombi)", category, lists[category].Count, items.Count);
                }

                cachedLists = lists;
                cachedListsTime = DateTime.UtcNow;
                PruneListed(titles);
                if (loaded.Count > 0)
                {
                    listsFile?.Save(new Dictionary<string, List<MediaItemDto>>(lastLoaded));
                }
                Diagnostics.ChannelLists(started, cachedListsTime - started, config.ChannelCategories()
                    .Select(c => new ChannelListInfo
                    {
                        Name = service.Text(FolderNameKey(c)),
                        Shown = c == AllMovies || c == AllTv
                            ? catalogLists.Where(p => p.Key.StartsWith(c + ":", StringComparison.Ordinal)).Sum(p => p.Value.Count)
                            : (lists.TryGetValue(c, out var l) ? l.Count : 0),
                        Loaded = loaded.TryGetValue(c, out var n) ? n : 0
                    })
                    .ToList());
                unlistedEntries = catalogLists;
                unlistedTitles = titles;
                return lists;
            }
            finally
            {
                ListsLock.Release();
            }
        }

        /// <summary>
        /// "All movies" can hold tens of thousands of titles, so it is split into A-Z: on every refresh
        /// Emby compares each item of a channel folder with every other one, which gets slow for very
        /// big folders, and endless lists are tiresome on a TV anyway.
        /// </summary>
        private static void AddLetterFolders(Dictionary<string, List<ChannelItemInfo>> lists, Dictionary<string, List<CatalogEntry>> catalogLists, string category, List<CatalogEntry> entries)
        {
            var folders = new List<ChannelItemInfo>();
            foreach (var group in entries.GroupBy(e => LetterOf(e.Title)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var letterCategory = category + ":" + group.Key;
                var sorted = group.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                lists[letterCategory] = sorted.Select(ToCatalogItem).ToList();
                catalogLists[letterCategory] = sorted;
                folders.Add(NewFolder(letterCategory, group.Key, FolderImages.ForLetter(group.Key)));
            }

            lists[category] = folders;
        }

        /// <summary>A list folder or an A-Z folder, with its own picture (otherwise Emby shows a poster from inside).</summary>
        private static ChannelItemInfo NewFolder(string category, string name, string image)
        {
            var info = new ChannelItemInfo
            {
                Id = ChannelItemIds.List(category),
                Name = name,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = image,

                // Emby only processes existing items again with ForceUpdate (a few folders, so cheap):
                // folders from before this version get their picture, a changed one is swapped.
                ForceUpdate = true
            };

            // Marks the folder for the web app, which hides Emby's play buttons in the channel.
            info.ProviderIds[ChannelItemIds.FolderProviderKey] = category;
            if (!string.IsNullOrEmpty(image))
            {
                info.ProviderIds[ChannelItemIds.PosterProviderKey] = image;
            }

            return info;
        }

        /// <summary>"Ärger" -> "A", "the Matrix" -> "T"; digits, symbols and other scripts -> "#".</summary>
        internal static string LetterOf(string title)
        {
            foreach (var c in (title ?? string.Empty).Normalize(NormalizationForm.FormD))
            {
                if (char.IsLetter(c))
                {
                    var upper = char.ToUpperInvariant(c);
                    return upper >= 'A' && upper <= 'Z' ? upper.ToString() : "#";
                }

                if (char.IsDigit(c))
                {
                    return "#";
                }
            }

            return "#";
        }

        /// <summary>Ombi's movie lists have no trailer links; the few titles of a list get them from the details.</summary>
        private async Task FillTrailersAsync(RequestService service, List<MediaItemDto> items, CancellationToken cancellationToken)
        {
            foreach (var item in items.Where(i => string.IsNullOrEmpty(i.TrailerUrl)))
            {
                try
                {
                    item.TrailerUrl = await service.GetTrailerAsync(item.MediaType, item.TmdbId, cancellationToken).ConfigureAwait(false);
                }
                catch (OmbiException ex)
                {
                    logger.Debug("No trailer for {0} {1}: {2}", item.MediaType, item.TmdbId, ex.Message);
                }
            }
        }

        /// <summary>Titles to load for a list: more than shown, because the status filter hides some (30 shown: 50 loaded).</summary>
        private static int FetchCount(int listSize) => listSize + Math.Max(20, listSize / 2);

        private async Task<List<CatalogEntry>> LoadCatalogAsync(CancellationToken cancellationToken)
        {
            var catalog = Plugin.Instance?.Catalog;
            if (catalog == null)
            {
                return new List<CatalogEntry>();
            }

            // A failure is passed on: without the catalog the folders would be empty and Emby would delete them.
            return await catalog.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Emby's item id of the channel ("N" format, as in the users' channel access), null if it is not registered.</summary>
        public static string ItemId(ILibraryManager libraryManager)
        {
            var channel = current;
            return channel == null
                ? null
                : libraryManager.GetNewItemId("Channel " + channel.Name, typeof(MediaBrowser.Controller.Channels.Channel)).ToString("N");
        }

        /// <summary>
        /// Removes the channel and its items from the library right away when it is switched off while
        /// Emby runs; from the next start on Emby does not register it any more.
        /// </summary>
        public static void RemoveFromLibrary(ILibraryManager libraryManager, ILogger logger)
        {
            var channel = current;
            if (channel == null)
            {
                return;
            }

            // Same id as Emby's ChannelManager.GetChannelGuid.
            var id = libraryManager.GetNewItemId("Channel " + channel.Name, typeof(MediaBrowser.Controller.Channels.Channel));
            var entity = libraryManager.GetItemById(id);
            if (entity == null)
            {
                return;
            }

            libraryManager.DeleteItem(entity, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false });
            (libraryManager.GetUserRootFolder() as UserRootFolder)?.ClearCachedChildren();
            logger.Info("Requests channel switched off and removed from the library.");
        }

        /// <summary>
        /// Older versions created the movies as plain videos, for which the TV apps show the poster instead
        /// of the background on the details page. Emby never changes the type of an item, so the old ones are
        /// removed before Emby reads the lists, and this refresh creates them as movies (without their hearts).
        /// </summary>
        private void RemoveOldMovieItems()
        {
            var libraryManager = Plugin.Instance?.LibraryManager;
            var channel = libraryManager?.GetItemById(libraryManager.GetNewItemId("Channel " + Name, typeof(MediaBrowser.Controller.Channels.Channel)));
            if (channel == null)
            {
                return;
            }

            var old = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Video) },
                    AncestorIds = new[] { channel.InternalId },
                    DtoOptions = new DtoOptions(false) { Fields = new[] { ItemFields.ExternalId }, EnableImages = false, EnableUserData = false }
                })
                .Where(i => i.ExternalId != null && ChannelItemIds.IsOwn(i.ExternalId))
                .ToList();
            foreach (var item in old)
            {
                libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false });
            }

            if (old.Count > 0)
            {
                logger.Info("Requests channel: {0} movies of an older version removed, this refresh creates them again as movies", old.Count);
            }
        }

        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        private static RequestService Service => Plugin.Instance?.RequestService;

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref readsInProgress);
            lastQuery = DateTime.UtcNow;
            try
            {
                return await ReadFolderAsync(query, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lastQuery = DateTime.UtcNow;
                Interlocked.Decrement(ref readsInProgress);
            }
        }

        private async Task<ChannelItemResult> ReadFolderAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var result = new ChannelItemResult();
            var service = Service;
            var config = Plugin.Instance?.Configuration;
            if (service == null || config?.IsConfigured != true || !config.EnableChannel)
            {
                return result;
            }

            try
            {
                if (string.IsNullOrEmpty(query.FolderId))
                {
                    // Every refresh starts here: rebuild the lists with current request states, right
                    // away, so that a list without titles to show (e.g. all of them already in Emby, or
                    // no catalog imported yet) is left out until it has some again.
                    cachedLists = null;
                    var lists = await GetListsAsync(service, config, cancellationToken).ConfigureAwait(false);
                    RemoveOldMovieItems();
                    result.Items.AddRange(config.ChannelCategories()
                        .Where(c => lists.TryGetValue(c, out var items) && items.Count > 0)
                        .Select(c => NewFolder(c, service.Text(FolderNameKey(c)), FolderImages.ForList(c, service.IsGerman))));
                }
                else if (ChannelItemIds.TryGetList(query.FolderId, out var category))
                {
                    var lists = await GetListsAsync(service, config, cancellationToken).ConfigureAwait(false);
                    if (lists.TryGetValue(category, out var items))
                    {
                        result.Items.AddRange(items);
                    }

                    // Not before: if a refresh is cut off, the next one still sends the changed states.
                    if (unlistedEntries.TryGetValue(category, out var entries))
                    {
                        Plugin.Instance?.Catalog.MarkListed(entries);
                        unlistedEntries.Remove(category);
                    }

                    if (unlistedTitles.TryGetValue(category, out var shownTitles))
                    {
                        MarkListed(shownTitles);
                        unlistedTitles.Remove(category);
                    }
                }

                // Series folders stay empty (Emby removes seasons and episodes of older versions). Daily shows
                // alone have thousands of episodes, and every item costs database writes and a metadata
                // refresh. The heart on a series requests all of it; single seasons and episodes are
                // requested on the requests page.
            }
            catch (Exception ex)
            {
                // Passed on: Emby then skips the folder and keeps its items. An empty result would make Emby
                // delete everything below it (with the users' hearts) and create it all again next time.
                logger.ErrorException("Loading channel folder {0} failed, Emby keeps its items", ex, query.FolderId ?? "(root)");
                if (string.IsNullOrEmpty(query.FolderId) && !(ex is OperationCanceledException))
                {
                    RequestRefresh(FailedRefreshRetryDelay);
                }

                throw;
            }

            result.TotalRecordCount = result.Items.Count;
            return result;
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var language = Service?.IsGerman == true ? "de" : "en";
            var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Images.channel-{language}.png");
            return Task.FromResult(new DynamicImageResponse
            {
                Format = ImageFormat.Png,
                Stream = stream
            });
        }

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return new[] { ImageType.Primary, ImageType.Thumb };
        }

        /*
         * Movies and series are real movies and series, so that every app shows their details page with
         * background and heart (for plain videos the TV apps show the poster instead). They carry no TMDb
         * id: with one, Emby refreshed movies from TMDb including collection ids, and collection features
         * (Emby's automatic collections, the Auto Box Sets plugin) or other plugins then treated titles that
         * are not in the library as real movies. Features keeps Emby from looking them up by name.
         * Series have no seasons.
         */
        private static ChannelItemInfo ToChannelItem(MediaItemDto item, string category)
        {
            var info = NewTitleItem(item);
            if (item.MediaType == RequestService.Movie)
            {
                SetMovie(info);
            }
            else
            {
                SetSeries(info, item.TmdbId, category);
            }

            return info;
        }

        private static ChannelItemInfo ToCatalogItem(CatalogEntry entry)
        {
            var info = NewTitleItem(entry.ToDto());

            // Emby sets the creation date on every forced update otherwise, and sorts "latest" by it.
            info.DateCreated = new DateTimeOffset(DateTime.SpecifyKind(entry.AddedUtc, DateTimeKind.Utc));
            if (entry.MediaType == RequestService.Movie)
            {
                SetMovie(info);
            }
            else
            {
                SetSeries(info, entry.TmdbId, AllTv);
            }

            // Emby only takes over changed states, pictures and trailers of existing items with ForceUpdate,
            // which would rewrite thousands of items on every refresh. New items get everything anyway.
            info.ForceUpdate = entry.HasUnlistedChanges();
            return info;
        }

        private static void SetMovie(ChannelItemInfo info)
        {
            info.Type = ChannelItemType.Media;
            info.MediaType = ChannelMediaType.Video;
            info.ContentType = ChannelMediaContentType.Movie;
        }

        private static void SetSeries(ChannelItemInfo info, int tmdbId, string category)
        {
            info.Id = ChannelItemIds.Series(tmdbId, category);
            info.Type = ChannelItemType.Folder;
            info.FolderType = ChannelFolderType.Series;
        }

        private static ChannelItemInfo NewTitleItem(MediaItemDto item)
        {
            var info = new ChannelItemInfo
            {
                Id = ChannelItemIds.Title(item.MediaType, item.TmdbId),
                Name = item.Title,
                // Only the plot: the request state is shown by the banner on the poster.
                Overview = item.Overview,
                ImageUrl = LargePoster(item.PosterUrl),
                ProductionYear = item.Year,
                // One decimal, as Emby shows it: TMDb's average moves a little every day, and every change
                // would write the item again.
                CommunityRating = item.Rating.HasValue ? (float)Math.Round(item.Rating.Value, 1) : (float?)null,
                LockedFields = OwnFields
            };

            SetIds(info, item.Status);

            // Emby's trailer button (all apps) plays it; ChannelItemUpdater puts it into RemoteTrailers.
            if (!string.IsNullOrEmpty(item.TrailerUrl))
            {
                info.ProviderIds[ChannelItemIds.TrailerProviderKey] = item.TrailerUrl;
            }

            // Background of the details page (the TV apps show the poster on grey without one). Channel
            // items can only bring a poster; ChannelItemUpdater sets it.
            if (!string.IsNullOrEmpty(item.BackdropUrl))
            {
                info.ProviderIds[ChannelItemIds.BackdropProviderKey] = item.BackdropUrl;
            }

            return info;
        }

        /// <summary>
        /// ForceUpdate only if Emby got something else last time (see <see cref="listed"/>). The title is
        /// recorded once Emby fetched its folder (<see cref="MarkListed"/>).
        /// </summary>
        private static KeyValuePair<string, string> SetForceUpdate(ChannelItemInfo info, string key)
        {
            info.ProviderIds.TryGetValue(ChannelItemIds.StatusProviderKey, out var status);
            info.ProviderIds.TryGetValue(ChannelItemIds.TrailerProviderKey, out var trailer);
            info.ProviderIds.TryGetValue(ChannelItemIds.BackdropProviderKey, out var backdrop);
            var shows = StableHash(string.Join("|", status, info.Name, info.ImageUrl, trailer, backdrop, info.Overview, info.ProductionYear, info.CommunityRating));
            info.ForceUpdate = !listed.TryGetValue(key, out var previous) || previous != shows;
            return new KeyValuePair<string, string>(key, shows);
        }

        private static void MarkListed(IEnumerable<KeyValuePair<string, string>> entries)
        {
            var changed = false;
            foreach (var entry in entries)
            {
                if (!listed.TryGetValue(entry.Key, out var previous) || previous != entry.Value)
                {
                    listed[entry.Key] = entry.Value;
                    changed = true;
                }
            }

            if (changed)
            {
                ScheduleListedSave();
            }
        }

        /// <summary>
        /// After a request the plugin writes the new state into the items itself (ChannelItemUpdater), so
        /// what Emby has no longer matches the record: the next refresh sends the title again.
        /// </summary>
        public static void ForgetListed(string mediaType, int tmdbId)
        {
            var keys = listed.Keys.Where(key =>
            {
                // Titles are recorded as "list|ombirequests|...".
                var start = key.IndexOf("ombirequests|", StringComparison.Ordinal);
                return start >= 0 && ChannelItemIds.TryGetTarget(key.Substring(start), out var target) && target.MediaType == mediaType && target.TmdbId == tmdbId;
            }).ToList();
            foreach (var key in keys)
            {
                listed.TryRemove(key, out _);
            }

            if (keys.Count > 0)
            {
                ScheduleListedSave();
            }
        }

        /// <summary>FNV-1a: the same text gives the same value after a restart (string.GetHashCode does not).</summary>
        private static string StableHash(string text)
        {
            var hash = 14695981039346656037UL;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 1099511628211UL;
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>Drops records of titles that left the lists (and of seasons and episodes of older versions).</summary>
        private static void PruneListed(Dictionary<string, List<KeyValuePair<string, string>>> titles)
        {
            var current = new HashSet<string>(titles.Values.SelectMany(l => l.Select(e => e.Key)));
            var stale = listed.Keys.Where(key => !current.Contains(key)).ToList();
            foreach (var key in stale)
            {
                listed.TryRemove(key, out _);
            }

            if (stale.Count > 0)
            {
                ScheduleListedSave();
            }
        }

        private static void LoadListed(IJsonSerializer json, ILogger logger)
        {
            lock (ListedLock)
            {
                var dataPath = Plugin.Instance?.DataPath;
                if (listedFile != null || json == null || string.IsNullOrEmpty(dataPath))
                {
                    return;
                }

                listedFile = new DataFile<Dictionary<string, string>>(dataPath, ListedFileName, json, logger);
                foreach (var pair in listedFile.Load())
                {
                    listed[pair.Key] = pair.Value;
                }

                listsFile = new DataFile<Dictionary<string, List<MediaItemDto>>>(dataPath, ListsFileName, json, logger);
                foreach (var pair in listsFile.Load())
                {
                    lastLoaded[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>One write after a refresh, not one per item.</summary>
        private static void ScheduleListedSave()
        {
            lock (ListedLock)
            {
                if (listedFile == null)
                {
                    return;
                }

                if (listedSaveTimer == null)
                {
                    listedSaveTimer = new Timer(_ =>
                    {
                        lock (ListedLock)
                        {
                            listedFile?.Save(new Dictionary<string, string>(listed));
                        }
                    }, null, ListedSaveDelay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    listedSaveTimer.Change(ListedSaveDelay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private static void SetIds(ChannelItemInfo info, string status)
        {
            info.ProviderIds[ChannelItemIds.StatusProviderKey] = status;

            // Emby keeps an item's first image; ChannelItemUpdater swaps it when the poster changes.
            if (!string.IsNullOrEmpty(info.ImageUrl))
            {
                info.ProviderIds[ChannelItemIds.PosterProviderKey] = info.ImageUrl;
            }
        }

        /// <summary>The web app uses w342 posters; the channel takes w780 so the banner text stays sharp.</summary>
        private static string LargePoster(string url)
        {
            return url?.Replace("/t/p/w342/", "/t/p/w780/");
        }

        private static string FolderNameKey(string category)
        {
            switch (category)
            {
                case "movie:popular":
                    return "FolderPopularMovies";
                case "movie:nowplaying":
                    return "FolderNowPlayingMovies";
                case "movie:upcoming":
                    return "FolderUpcomingMovies";
                case "tv:trending":
                    return "FolderTrendingTv";
                case "tv:popular":
                    return "FolderPopularTv";
                case "movie:toprated":
                    return "FolderTopRatedMovies";
                case "movie:seasonal":
                    return "FolderSeasonalMovies";
                case "tv:anticipated":
                    return "FolderAnticipatedTv";
                case "tv:mostwatched":
                    return "FolderTopRatedTv";
                case AllMovies:
                    return "FolderAllMovies";
                case AllTv:
                    return "FolderAllTv";
                default:
                    // Unknown keys are shown as is (Localization.Get returns the key).
                    return category;
            }
        }
    }
}
