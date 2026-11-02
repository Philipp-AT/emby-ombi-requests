using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using OmbiRequests.Channel;

namespace OmbiRequests.Requests
{
    /// <summary>
    /// Provider ids ("Tmdb=603") of all movies and series in Emby's libraries, to tell whether a title
    /// is already there without one library query per title.
    /// </summary>
    internal sealed class LibraryIndex
    {
        private static readonly string[] ProviderKeys = { "Tmdb", "Imdb", "Tvdb" };
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        private readonly Lazy<ILibraryManager> libraryManager;
        private readonly ILogger logger;
        private Tuple<HashSet<string>, HashSet<string>, DateTime> cached;

        public LibraryIndex(Lazy<ILibraryManager> libraryManager, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.logger = logger;
        }

        /// <summary>Whether the movie or series is in the library, from ids read at most 10 minutes ago.</summary>
        public bool Contains(string mediaType, int tmdbId, string imdbId, string tvdbId)
        {
            var current = cached;
            if (current == null || DateTime.UtcNow - current.Item3 >= CacheDuration)
            {
                var movies = Load("Movie");
                var series = Load("Series");
                if (movies == null || series == null)
                {
                    return false;
                }

                cached = current = Tuple.Create(movies, series, DateTime.UtcNow);
            }

            return Matches(mediaType == RequestService.Movie ? current.Item1 : current.Item2, tmdbId, imdbId, tvdbId);
        }

        public static bool Matches(HashSet<string> ids, int tmdbId, string imdbId, string tvdbId)
        {
            return ids.Contains("Tmdb=" + tmdbId.ToString(CultureInfo.InvariantCulture))
                   || (!string.IsNullOrEmpty(imdbId) && ids.Contains("Imdb=" + imdbId))
                   || (!string.IsNullOrEmpty(tvdbId) && tvdbId != "0" && ids.Contains("Tvdb=" + tvdbId));
        }

        /// <param name="itemType">"Movie" or "Series".</param>
        /// <returns>Fresh ids of all items of the type; null if the library cannot be read.</returns>
        public HashSet<string> Load(string itemType)
        {
            try
            {
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var items = libraryManager.Value.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { itemType },
                    Recursive = true,

                    // Only what is compared: the whole library otherwise comes with all fields.
                    DtoOptions = new DtoOptions(false)
                    {
                        Fields = new[] { ItemFields.ProviderIds, ItemFields.ExternalId },
                        EnableImages = false,
                        EnableUserData = false
                    }
                });

                foreach (var item in items)
                {
                    // Entries of older plugin versions were created as movies/series with TMDb ids.
                    if (item.ExternalId != null && ChannelItemIds.IsOwn(item.ExternalId))
                    {
                        continue;
                    }

                    foreach (var key in ProviderKeys)
                    {
                        var value = item.GetProviderId(key);
                        if (!string.IsNullOrEmpty(value))
                        {
                            ids.Add(key + "=" + value);
                        }
                    }
                }

                return ids;
            }
            catch (Exception ex)
            {
                logger.ErrorException("Reading the {0} items of the library failed", ex, itemType);
                return null;
            }
        }
    }
}
