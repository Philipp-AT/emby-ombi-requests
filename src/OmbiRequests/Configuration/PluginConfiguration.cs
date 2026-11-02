using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace OmbiRequests.Configuration
{
    public enum UiLanguage
    {
        [Description("Automatic (server language)")]
        Auto,

        [Description("English")]
        English,

        [Description("Deutsch")]
        German
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public string OmbiUrl { get; set; } = string.Empty;

        public string ApiKey { get; set; } = string.Empty;

        /// <summary>One mapping per line: EmbyUserName=OmbiUserName.</summary>
        public string UserMappings { get; set; } = string.Empty;

        /// <summary>
        /// Lets Emby users without an Ombi account request via the API key with an alias.
        /// Such requests run with admin rights in Ombi (no quota, auto approval).
        /// </summary>
        public bool AllowUnmappedUsers { get; set; }

        /// <summary>Emby users (internal ids, comma separated) without the requests page; users created later have it.</summary>
        public string RequestsPageDisabledUsers { get; set; } = string.Empty;

        public bool IsRequestsPageEnabled(long userId) => !ParseUserIds(RequestsPageDisabledUsers).Contains(userId);

        internal static HashSet<long> ParseUserIds(string text)
        {
            return new HashSet<long>((text ?? string.Empty)
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1)
                .Where(id => id >= 0));
        }

        public bool EnableWebIntegration { get; set; } = true;

        /// <summary>Serve Emby's web app from a copy with the script tag (sets DashboardSourcePath).</summary>
        public bool InjectIntoWebApp { get; set; }

        public bool EnableFavoriteRequests { get; set; } = true;

        public bool EnablePosterBanners { get; set; } = true;

        /// <summary>Keeps other plugins' poster overlays (e.g. Cover Art) off the channel, see <see cref="Channel.ForeignOverlayFilter"/>.</summary>
        public bool HideOtherOverlays { get; set; } = true;

        /// <summary>Removing the heart withdraws a request made with it while it waits for approval.</summary>
        public bool EnableWithdrawOnUnfavorite { get; set; } = true;

        /// <summary>Withdrawing also takes approved requests that are not available yet (Radarr/Sonarr keep them).</summary>
        public bool WithdrawApprovedRequests { get; set; }

        /// <summary>Emby message to the requester when a request becomes available.</summary>
        public bool EnableAvailableNotifications { get; set; } = true;

        /// <summary>
        /// Whether the requests channel exists at all. Off means Emby does not register it at startup;
        /// see <see cref="Channel.OmbiChannel"/>.
        /// </summary>
        public bool EnableChannel { get; set; } = true;

        /// <summary>Name the channel was registered with the first time; it stays (Emby derives the channel's id from it).</summary>
        public string ChannelRegisteredName { get; set; }

        // Lists in the requests channel.
        public bool ChannelPopularMovies { get; set; } = true;

        public bool ChannelNowPlayingMovies { get; set; } = true;

        public bool ChannelUpcomingMovies { get; set; } = true;

        public bool ChannelTrendingTv { get; set; } = true;

        public bool ChannelPopularTv { get; set; } = true;

        public bool ChannelTopRatedMovies { get; set; } = true;

        /// <summary>Ombi's seasonal list (e.g. Christmas); empty outside such seasons, then the folder is hidden.</summary>
        public bool ChannelSeasonalMovies { get; set; } = true;

        public bool ChannelAnticipatedTv { get; set; } = true;

        public bool ChannelTopRatedTv { get; set; } = true;

        /// <summary>Titles per movie list folder (popular, in cinemas, upcoming).</summary>
        public int ChannelMovieListSize { get; set; } = 30;

        /// <summary>Titles per series list folder (trending, popular).</summary>
        public int ChannelTvListSize { get; set; } = 30;

        /// <summary>"All movies" / "All series", filled by the catalog import, see <see cref="Catalog.TitleCatalog"/>.</summary>
        public bool ChannelAllMovies { get; set; } = true;

        public bool ChannelAllTv { get; set; } = true;

        public int CatalogMovieCount { get; set; } = 5000;

        public int CatalogTvCount { get; set; } = 1500;

        /// <summary>0 = new titles are only loaded when started manually.</summary>
        public int CatalogUpdateIntervalDays { get; set; } = 7;

        // Request states shown in the requests channel. Available titles are in the libraries anyway.
        public bool ChannelShowNotRequested { get; set; } = true;

        public bool ChannelShowRequested { get; set; } = true;

        public bool ChannelShowApproved { get; set; } = true;

        public bool ChannelShowPartiallyAvailable { get; set; } = true;

        public bool ChannelShowDenied { get; set; } = true;

        public bool ChannelShowAvailable { get; set; }

        /// <summary>Discover lists of the channel, in display order.</summary>
        public IEnumerable<string> ChannelCategories()
        {
            if (ChannelPopularMovies)
            {
                yield return "movie:popular";
            }

            if (ChannelNowPlayingMovies)
            {
                yield return "movie:nowplaying";
            }

            if (ChannelUpcomingMovies)
            {
                yield return "movie:upcoming";
            }

            if (ChannelTopRatedMovies)
            {
                yield return "movie:toprated";
            }

            if (ChannelSeasonalMovies)
            {
                yield return "movie:seasonal";
            }

            if (ChannelAllMovies)
            {
                yield return "movie:all";
            }

            if (ChannelTrendingTv)
            {
                yield return "tv:trending";
            }

            if (ChannelPopularTv)
            {
                yield return "tv:popular";
            }

            if (ChannelAnticipatedTv)
            {
                yield return "tv:anticipated";
            }

            // Ombi calls TMDb's list of the best rated series "mostwatched".
            if (ChannelTopRatedTv)
            {
                yield return "tv:mostwatched";
            }

            if (ChannelAllTv)
            {
                yield return "tv:all";
            }
        }

        /// <param name="status">A <see cref="Requests.MediaStatus"/> name.</param>
        public bool ShowInChannel(string status)
        {
            switch (status)
            {
                case "Requested":
                    return ChannelShowRequested;
                case "Approved":
                    return ChannelShowApproved;
                case "PartiallyAvailable":
                    return ChannelShowPartiallyAvailable;
                case "Denied":
                    return ChannelShowDenied;
                case "Available":
                    return ChannelShowAvailable;
                default:
                    return ChannelShowNotRequested;
            }
        }

        public UiLanguage Language { get; set; } = UiLanguage.Auto;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(OmbiUrl) && !string.IsNullOrWhiteSpace(ApiKey);

        public Dictionary<string, string> ParseUserMappings()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(UserMappings))
            {
                return result;
            }

            foreach (var line in UserMappings.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var embyName = line.Substring(0, separator).Trim();
                var ombiName = line.Substring(separator + 1).Trim();
                if (embyName.Length > 0 && ombiName.Length > 0)
                {
                    result[embyName] = ombiName;
                }
            }

            return result;
        }
    }
}
