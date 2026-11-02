using System;
using System.Collections.Generic;

namespace OmbiRequests.Requests
{
    public enum MediaStatus
    {
        None,
        Requested,
        Approved,
        PartiallyAvailable,
        Available,
        Denied
    }

    public enum TvRequestScope
    {
        All,
        First,
        Latest,

        /// <summary>All missing episodes of the given seasons.</summary>
        Seasons,

        /// <summary>Single episodes, given as "season:episode".</summary>
        Episodes
    }

    public class MediaItemDto
    {
        /// <summary>A copy for one caller (cached lists are shared); lists inside are shared too, they are not changed.</summary>
        internal MediaItemDto Copy() => (MediaItemDto)MemberwiseClone();

        /// <summary>"movie" or "tv".</summary>
        public string MediaType { get; set; }

        public int TmdbId { get; set; }

        public string Title { get; set; }

        public int? Year { get; set; }

        public string Overview { get; set; }

        public string PosterUrl { get; set; }

        public string BackdropUrl { get; set; }

        /// <summary><see cref="Requests.MediaStatus"/> as string.</summary>
        public string Status { get; set; }

        public string DeniedReason { get; set; }

        /// <summary>Set when the title is already in the Emby library and visible to the user.</summary>
        public string EmbyItemId { get; set; }

        /// <summary>Other ids for matching the title with the Emby library, if Ombi knows them.</summary>
        public string ImdbId { get; set; }

        public string TvdbId { get; set; }

        /// <summary>TV only, filled by the details endpoint.</summary>
        public List<SeasonDto> Seasons { get; set; }

        // The fields below are filled by the details endpoint only.

        public string Tagline { get; set; }

        /// <summary>TMDb rating 0-10.</summary>
        public double? Rating { get; set; }

        public int? VoteCount { get; set; }

        /// <summary>Minutes; per episode for series.</summary>
        public int? Runtime { get; set; }

        /// <summary>Cinema release (movies) or first air date (series), ISO.</summary>
        public string ReleaseDate { get; set; }

        /// <summary>YouTube link of the trailer, from Ombi.</summary>
        public string TrailerUrl { get; set; }

        public string DigitalReleaseDate { get; set; }

        /// <summary>TMDb status, e.g. "Released", "Ended", "Returning Series".</summary>
        public string ReleaseStatus { get; set; }

        public string Network { get; set; }

        public string Certification { get; set; }

        /// <summary>Movies only, US dollars.</summary>
        public double? Budget { get; set; }

        /// <summary>Movies only, US dollars.</summary>
        public double? Revenue { get; set; }

        public List<string> Genres { get; set; }

        public List<string> Keywords { get; set; }

        public List<CastDto> Cast { get; set; }
    }

    public class CastDto
    {
        public string Name { get; set; }

        public string Character { get; set; }

        public string ImageUrl { get; set; }
    }

    public class SeasonDto
    {
        public int SeasonNumber { get; set; }

        public int EpisodeCount { get; set; }

        public string Status { get; set; }

        public List<EpisodeDto> Episodes { get; set; }
    }

    public class EpisodeDto
    {
        public int EpisodeNumber { get; set; }

        public string Title { get; set; }

        /// <summary>ISO date of the first broadcast, if known.</summary>
        public string AirDate { get; set; }

        public string Status { get; set; }
    }

    /// <summary>A request was made, withdrawn, approved or denied.</summary>
    public class RequestChangedEventArgs : EventArgs
    {
        public RequestChangedEventArgs(string mediaType, int tmdbId, MediaItemDto details, bool withdrawn = false)
        {
            MediaType = mediaType;
            TmdbId = tmdbId;
            Details = details;
            Withdrawn = withdrawn;
        }

        /// <summary>True if requests were withdrawn: the state may go back to "not requested".</summary>
        public bool Withdrawn { get; }

        public string MediaType { get; }

        public int TmdbId { get; }

        /// <summary>The title loaded right after the change (series with seasons); null if that failed.</summary>
        public MediaItemDto Details { get; }
    }

    public class RequestOutcomeDto
    {
        public bool Success { get; set; }

        public string Message { get; set; }

        /// <summary>Status of the title after the request, if it could be determined.</summary>
        public string Status { get; set; }
    }

    public class UserRequestDto
    {
        public string MediaType { get; set; }

        public int TmdbId { get; set; }

        public string Title { get; set; }

        public int? Year { get; set; }

        public string PosterUrl { get; set; }

        public string RequestedDate { get; set; }

        public string Status { get; set; }

        public string DeniedReason { get; set; }

        /// <summary>Waiting for approval; with the setting WithdrawApprovedRequests also approved, not yet available ones.</summary>
        public bool CanWithdraw { get; set; }

        /// <summary>TV only: requested season numbers.</summary>
        public List<int> Seasons { get; set; }

        /// <summary>Set for available titles that are in the Emby library.</summary>
        public string EmbyItemId { get; set; }
    }

    /// <summary>Remaining requests of a user, only where Ombi has a limit.</summary>
    public class QuotaDto
    {
        /// <summary>"movie" or "tv".</summary>
        public string MediaType { get; set; }

        public int Remaining { get; set; }

        public int Limit { get; set; }

        /// <summary>When requests are possible again, if none are left.</summary>
        public string NextRequest { get; set; }
    }

    /// <summary>A request of any user (admin view "all requests").</summary>
    public class AdminRequestDto
    {
        public string MediaType { get; set; }

        /// <summary>Ombi's id of the movie request or the season request.</summary>
        public int RequestId { get; set; }

        public int TmdbId { get; set; }

        public string Title { get; set; }

        public int? Year { get; set; }

        public string PosterUrl { get; set; }

        /// <summary>Requested (waiting for approval), Approved, Denied or Available.</summary>
        public string Status { get; set; }

        public string DeniedReason { get; set; }

        public string RequestedBy { get; set; }

        public string RequestedDate { get; set; }

        /// <summary>Emby users who also want the title (heart or "already requested"), besides <see cref="RequestedBy"/>.</summary>
        public List<string> AlsoWantedBy { get; set; }

        public List<int> Seasons { get; set; }
    }

    /// <summary>What the plugin offers on the page of a title in the library.</summary>
    public class LibraryItemDto
    {
        public string MediaType { get; set; }

        public int TmdbId { get; set; }

        public string Title { get; set; }

        /// <summary>Series only: episodes that are neither in Emby nor requested.</summary>
        public int MissingEpisodes { get; set; }

        public bool CanReportIssue { get; set; }

        public List<IssueCategoryDto> IssueCategories { get; set; }
    }

    public class IssueCategoryDto
    {
        public int Id { get; set; }

        public string Name { get; set; }
    }

    public class StatusDto
    {
        public bool Configured { get; set; }

        public bool WebIntegration { get; set; }

        /// <summary>Ombi user the current Emby user acts as, null if none.</summary>
        public string OmbiUser { get; set; }

        public bool CanRequest { get; set; }

        public string Message { get; set; }

        /// <summary>"de" or "en".</summary>
        public string Language { get; set; }

        /// <summary>Name of the requests channel, so the web app can recognize its page; null if it is off.</summary>
        public string ChannelName { get; set; }

        /// <summary>Admin or power user in Ombi: sees "all requests" and may approve, deny and delete them.</summary>
        public bool IsAdmin { get; set; }

        /// <summary>False if an admin switched the requests page off for this user (the channel's pages are still marked).</summary>
        public bool PageEnabled { get; set; } = true;

        /// <summary>Only the media types with a limit in Ombi.</summary>
        public List<QuotaDto> Quotas { get; set; }
    }
}
