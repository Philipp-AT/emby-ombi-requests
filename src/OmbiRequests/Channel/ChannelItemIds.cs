using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OmbiRequests.Requests;

namespace OmbiRequests.Channel
{
    /// <summary>The movie or series a channel item stands for, encoded in its external id.</summary>
    public sealed class ChannelTarget
    {
        public string MediaType { get; set; }

        public int TmdbId { get; set; }
    }

    /// <summary>
    /// Ids look like "ombirequests|list|movie:popular", "ombirequests|movie|603" and
    /// "ombirequests|tv|1399|tv:popular" (a series in one list).
    /// </summary>
    public static class ChannelItemIds
    {
        /// <summary>Provider id key holding the Ombi status of a channel item (read by the banner enhancer).</summary>
        public const string StatusProviderKey = "OmbiStatus";

        /// <summary>Provider id key holding the poster URL a channel item should have (read by <see cref="ChannelItemUpdater"/>).</summary>
        public const string PosterProviderKey = "OmbiPoster";

        /// <summary>Provider id key holding the background picture URL of a title (read by <see cref="ChannelItemUpdater"/>).</summary>
        public const string BackdropProviderKey = "OmbiBackdrop";

        /// <summary>Provider id key marking the list and A-Z folders of the channel (read by the web app).</summary>
        public const string FolderProviderKey = "OmbiFolder";

        /// <summary>Provider id key holding the trailer link of a title (read by <see cref="ChannelItemUpdater"/>).</summary>
        public const string TrailerProviderKey = "OmbiTrailer";

        private const string Prefix = "ombirequests|";

        /// <summary>The lists a series can be in, see <see cref="Series"/>.</summary>
        public static readonly string[] TvCategories = { "tv:trending", "tv:popular", "tv:anticipated", "tv:mostwatched", "tv:all" };

        public static bool IsOwn(string id) => id.StartsWith(Prefix, System.StringComparison.Ordinal);

        public static string List(string category) => Prefix + "list|" + category;

        /// <summary>A movie; the same id in every list (movies have no children).</summary>
        public static string Title(string mediaType, int tmdbId) => Prefix + mediaType + "|" + Number(tmdbId);

        /// <summary>
        /// A series in one list. Series are folders, and older versions gave the copy in one list its
        /// seasons, so every list has its own id; it stays, so that the items and their hearts stay too.
        /// </summary>
        public static string Series(int tmdbId, string category) => Title(RequestService.Tv, tmdbId) + "|" + category;

        /// <summary>All ids the channel can have for a movie or series (one per list for series).</summary>
        public static IEnumerable<string> TitleIds(string mediaType, int tmdbId)
        {
            return mediaType == RequestService.Tv
                ? new[] { Title(mediaType, tmdbId) }.Concat(TvCategories.Select(c => Series(tmdbId, c)))
                : new[] { Title(mediaType, tmdbId) };
        }

        /// <summary>
        /// The PresentationUniqueKey shared by all channel items of a movie or series (so the search shows
        /// it once), null for list folders. See <see cref="ChannelItemUpdater"/>.
        /// </summary>
        public static string PresentationKey(string id)
        {
            return TryGetTarget(id, out var target) ? Title(target.MediaType, target.TmdbId) : null;
        }

        public static bool TryGetList(string id, out string category)
        {
            category = null;
            if (id == null || !id.StartsWith(Prefix + "list|", System.StringComparison.Ordinal))
            {
                return false;
            }

            category = id.Substring((Prefix + "list|").Length);
            return true;
        }

        /// <summary>
        /// Parses ids of requestable items (movies and series). Seasons and episodes of older versions are
        /// not requestable any more; Emby removes them with the next channel refresh.
        /// </summary>
        public static bool TryGetTarget(string id, out ChannelTarget target)
        {
            target = null;
            var parts = Parts(id);
            if (parts == null || parts.Length < 2 || (parts[0] != RequestService.Movie && parts[0] != RequestService.Tv) || !TryNumber(parts[1], out var tmdbId))
            {
                return false;
            }

            target = new ChannelTarget { MediaType = parts[0], TmdbId = tmdbId };
            return true;
        }

        private static string[] Parts(string id)
        {
            return id == null || !id.StartsWith(Prefix, System.StringComparison.Ordinal) ? null : id.Substring(Prefix.Length).Split('|');
        }

        private static bool TryNumber(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
