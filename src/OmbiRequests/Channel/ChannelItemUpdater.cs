using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using OmbiRequests.Requests;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Fixes up the channel items Emby creates, in two points Emby's channel API does not cover:
    /// <list type="bullet">
    /// <item>Search: Emby creates a separate item per channel folder, so a title in "Popular movies" and
    /// "All movies" exists twice. Emby's search groups items by PresentationUniqueKey, which for channel
    /// items is the item's own id; all items of a title get the same key, so the search shows it once.
    /// Emby keeps the key of a video; a metadata refresh of a series sets the series' own id again, and
    /// the key is set back afterwards.</item>
    /// <item>Posters: Emby takes a channel item's image only when the item has none yet, so a poster that
    /// changed in Ombi (TMDb changes them, or another language) never arrived. The wanted poster comes
    /// with the item (provider id <see cref="ChannelItemIds.PosterProviderKey"/>); the one last applied is
    /// remembered per item, because Emby stores downloaded images under its own file names.</item>
    /// <item>Backgrounds: channel items can only bring a poster, so the TV apps showed it on grey on the
    /// details page. The background (provider id <see cref="ChannelItemIds.BackdropProviderKey"/>) is set
    /// once; a title keeps its background, so a later change is not followed.</item>
    /// <item>Trailers: the trailer link (provider id <see cref="ChannelItemIds.TrailerProviderKey"/>) goes
    /// into RemoteTrailers, which channel items cannot set themselves; Emby's trailer button plays it.</item>
    /// <item>Requests: after a request the new state goes straight into the title's items, so the banner
    /// changes right away instead of after the next channel refresh.</item>
    /// </list>
    /// </summary>
    public class ChannelItemUpdater : IServerEntryPoint
    {
        private const string FileName = "ombirequests-posters.json";
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(10);

        private readonly ILibraryManager libraryManager;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly string path;
        private readonly object saveLock = new object();
        private readonly Lazy<ConcurrentDictionary<long, string>> appliedPosters;
        private Timer saveTimer;

        public ChannelItemUpdater(ILibraryManager libraryManager, IApplicationPaths applicationPaths, IJsonSerializer json, ILogManager logManager)
        {
            this.libraryManager = libraryManager;
            this.json = json;
            logger = logManager.GetLogger(Plugin.PluginName);
            path = Path.Combine(applicationPaths.DataPath, FileName);
            appliedPosters = new Lazy<ConcurrentDictionary<long, string>>(Load);
        }

        public void Run()
        {
            // Added: new items. Updated: changed items (the lists are refreshed regularly with ForceUpdate).
            libraryManager.ItemAdded += OnItemChanged;
            libraryManager.ItemUpdated += OnItemChanged;
            libraryManager.ItemRemoved += OnItemRemoved;
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                plugin.RequestService.RequestChanged += OnRequestChanged;
            }
        }

        public void Dispose()
        {
            libraryManager.ItemAdded -= OnItemChanged;
            libraryManager.ItemUpdated -= OnItemChanged;
            libraryManager.ItemRemoved -= OnItemRemoved;
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                plugin.RequestService.RequestChanged -= OnRequestChanged;
            }

            lock (saveLock)
            {
                saveTimer?.Dispose();
                saveTimer = null;
            }

            if (appliedPosters.IsValueCreated)
            {
                Save();
            }
        }

        internal void OnItemChanged(object sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            if (item?.ExternalId == null || !ChannelItemIds.IsOwn(item.ExternalId))
            {
                return;
            }

            try
            {
                ItemUpdateType? update = null;
                var key = ChannelItemIds.PresentationKey(item.ExternalId);
                if (key != null && item.PresentationUniqueKey != key)
                {
                    item.PresentationUniqueKey = key;
                    update = ItemUpdateType.None;
                }

                var wanted = item.GetProviderId(ChannelItemIds.PosterProviderKey);
                var posters = appliedPosters.Value;
                if (!string.IsNullOrEmpty(wanted) && (!posters.TryGetValue(item.InternalId, out var applied) || applied != wanted))
                {
                    // New items already got it from Emby (their ImageUrl); items from before this
                    // version or with a changed poster get it here.
                    if (item.GetImageInfo(ImageType.Primary, 0)?.Path != wanted)
                    {
                        item.SetImage(new ItemImageInfo { Path = wanted, Type = ImageType.Primary, DateModified = DateTimeOffset.UtcNow }, 0);
                        update = ItemUpdateType.ImageUpdate;
                    }

                    posters[item.InternalId] = wanted;
                    ScheduleSave();
                }

                var backdrop = item.GetProviderId(ChannelItemIds.BackdropProviderKey);
                if (!string.IsNullOrEmpty(backdrop) && item.GetImageInfo(ImageType.Backdrop, 0) == null)
                {
                    item.SetImage(new ItemImageInfo { Path = backdrop, Type = ImageType.Backdrop, DateModified = DateTimeOffset.UtcNow }, 0);
                    update = ItemUpdateType.ImageUpdate;
                }

                // Trailer link for Emby's trailer button; channel items have no other remote trailers.
                var trailer = item.GetProviderId(ChannelItemIds.TrailerProviderKey);
                var trailers = string.IsNullOrEmpty(trailer) ? new string[0] : new[] { trailer };
                if (!(item.RemoteTrailers ?? new string[0]).SequenceEqual(trailers))
                {
                    item.RemoteTrailers = trailers;
                    update = update ?? ItemUpdateType.MetadataEdit;
                }

                // Raises ItemUpdated again, which then finds nothing to do.
                if (update.HasValue)
                {
                    item.UpdateToRepository(update.Value);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Updating the channel item {0} failed", ex, item.ExternalId);
            }
        }

        /// <summary>
        /// Writes the state after a request into all channel items of the title (one per folder). The banner
        /// enhancer's cache key contains the state, so Emby renders a new poster and the apps reload it. The
        /// channel refresh that follows confirms the state.
        /// </summary>
        private void OnRequestChanged(object sender, RequestChangedEventArgs e)
        {
            // A series can have hundreds of episodes: the user's answer does not wait for them.
            Task.Run(() => ApplyRequestChange(e));
        }

        internal void ApplyRequestChange(RequestChangedEventArgs e)
        {
            OmbiChannel.ForgetListed(e.MediaType, e.TmdbId);
            Plugin.Instance?.Catalog.ForgetListed(e.MediaType, e.TmdbId);
            try
            {
                // A request went through even if Ombi does not show it yet. Without details the state is
                // only known to be at least "requested": a better known one is kept. After a withdrawal
                // without details nothing is set; the channel refresh brings the state.
                var status = e.Details?.Status;
                var onlyIfNone = e.Details == null;
                if (e.Withdrawn)
                {
                    if (e.Details == null)
                    {
                        return;
                    }
                }
                else if (string.IsNullOrEmpty(status) || status == nameof(MediaStatus.None))
                {
                    status = nameof(MediaStatus.Requested);
                }

                // A query on an indexed key: Emby has no index on ExternalId, a query by it reads the whole
                // item table.
                var titleKey = ChannelItemIds.Title(e.MediaType, e.TmdbId);
                var changed = libraryManager.GetItemList(new InternalItemsQuery { PresentationUniqueKey = titleKey, IncludeItemTypes = new[] { "Movie", "Series" } })
                    .Where(i => i.ExternalId != null && ChannelItemIds.PresentationKey(i.ExternalId) == titleKey)
                    .Sum(i => SetStatus(i, status, onlyIfNone));

                logger.Debug("New state {0} of {1} {2} set on {3} channel items", status, e.MediaType, e.TmdbId, changed);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Updating the channel items of {0} {1} after the request failed", ex, e.MediaType, e.TmdbId);
            }
        }

        private static int SetStatus(BaseItem item, string status, bool onlyIfNone)
        {
            var current = item.GetProviderId(ChannelItemIds.StatusProviderKey);
            if (string.IsNullOrEmpty(status) || current == status
                || (onlyIfNone && !string.IsNullOrEmpty(current) && current != nameof(MediaStatus.None)))
            {
                return 0;
            }

            item.SetProviderId(ChannelItemIds.StatusProviderKey, status);
            item.UpdateToRepository(ItemUpdateType.ImageUpdate);
            return 1;
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            if (item?.ExternalId != null && ChannelItemIds.IsOwn(item.ExternalId) && appliedPosters.Value.TryRemove(item.InternalId, out _))
            {
                ScheduleSave();
            }
        }

        private void ScheduleSave()
        {
            lock (saveLock)
            {
                // Thousands of items change in one channel refresh: write the file once afterwards.
                saveTimer?.Dispose();
                saveTimer = new Timer(_ => Save(), null, SaveDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private ConcurrentDictionary<long, string> Load()
        {
            var result = new ConcurrentDictionary<long, string>();
            try
            {
                if (File.Exists(path))
                {
                    foreach (var pair in json.DeserializeFromFile<Dictionary<string, string>>(path) ?? new Dictionary<string, string>())
                    {
                        if (long.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        {
                            result[id] = pair.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Then every item gets its poster set once more, which is harmless.
                logger.ErrorException("Reading {0} failed", ex, path);
            }

            return result;
        }

        private void Save()
        {
            lock (saveLock)
            {
                var tempFile = path + ".tmp";
                try
                {
                    var copy = appliedPosters.Value.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value);
                    json.SerializeToFile(copy, tempFile);
                    if (File.Exists(path))
                    {
                        File.Replace(tempFile, path, null);
                    }
                    else
                    {
                        File.Move(tempFile, path);
                    }
                }
                catch (Exception ex)
                {
                    logger.ErrorException("Saving {0} failed", ex, path);
                }
            }
        }
    }
}
