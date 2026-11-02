using System;
using System.Collections.Generic;
using System.Linq;

// Property names intentionally match Ombi's camelCase JSON so that Emby's
// IJsonSerializer maps them regardless of its case sensitivity settings.
// Only the fields this plugin needs are declared; see Ombi's /swagger for the full models.
#pragma warning disable IDE1006 // Naming Styles

namespace OmbiRequests.Ombi
{
    public class OmbiUser
    {
        public string id { get; set; }

        /// <summary>For Emby Connect users this is usually the e-mail address.</summary>
        public string userName { get; set; }

        /// <summary>Display name; for Emby Connect users often the Emby user name.</summary>
        public string alias { get; set; }

        public string emailAddress { get; set; }

        /// <summary>Language for titles, texts and posters, e.g. "de"; empty means Ombi's default language.</summary>
        public string language { get; set; }

        public List<OmbiClaim> claims { get; set; }

        // A method, so that a serializer does not treat it as a field.
        public bool IsAdmin() => HasRole("Admin");

        /// <summary>Ombi lets admins and power users approve, deny and delete the requests of all users.</summary>
        public bool CanManageRequests() => HasRole("Admin") || HasRole("PowerUser");

        private bool HasRole(string role) => claims?.Any(c => c.enabled && string.Equals(c.value, role, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public class TmdbVideos
    {
        public List<TmdbVideo> results { get; set; }
    }

    public class TmdbVideo
    {
        public string key { get; set; }

        /// <summary>"YouTube", "Vimeo".</summary>
        public string site { get; set; }

        /// <summary>"Trailer", "Teaser", "Clip".</summary>
        public string type { get; set; }
    }

    /// <summary>A role of an Ombi user, e.g. "Admin" or "RequestMovie".</summary>
    public class OmbiClaim
    {
        public string value { get; set; }

        public bool enabled { get; set; }
    }

    public class MultiSearchFilter
    {
        public bool movies { get; set; }

        public bool tvShows { get; set; }

        public bool music { get; set; }

        public bool people { get; set; }
    }

    public class MultiSearchResult
    {
        /// <summary>TheMovieDb id.</summary>
        public string id { get; set; }

        /// <summary>"movie", "tv" or "person".</summary>
        public string mediaType { get; set; }

        public string title { get; set; }

        public string poster { get; set; }

        public string overview { get; set; }
    }

    /// <summary>Common request/availability flags returned by Ombi's search endpoints.</summary>
    public abstract class SearchResultBase
    {
        public int id { get; set; }

        public string title { get; set; }

        public string overview { get; set; }

        public string posterPath { get; set; }

        public string backdropPath { get; set; }

        public bool approved { get; set; }

        public bool denied { get; set; }

        public string deniedReason { get; set; }

        public bool requested { get; set; }

        public int requestId { get; set; }

        public bool available { get; set; }

        public string imdbId { get; set; }

        public string theTvDbId { get; set; }

        public string theMovieDbId { get; set; }

        /// <summary>Ombi's trailer link (YouTube URL or video key), as used by Ombi's trailer button.</summary>
        public string trailer { get; set; }
    }

    /// <summary>Fields shared by the movie discover lists and the movie details.</summary>
    public class SearchMovie : SearchResultBase
    {
        public string releaseDate { get; set; }

        public double voteAverage { get; set; }

        public int voteCount { get; set; }
    }

    /// <summary>/api/v2/Search/movie/{id}.</summary>
    public class MovieDetails : SearchMovie
    {
        /// <summary>TMDb videos; used when Ombi has no trailer link of its own.</summary>
        public TmdbVideos videos { get; set; }

        public int runtime { get; set; }

        public string digitalReleaseDate { get; set; }

        /// <summary>TMDb release status, e.g. "Released".</summary>
        public string status { get; set; }

        public string tagline { get; set; }

        /// <summary>US dollars, 0 if unknown.</summary>
        public double budget { get; set; }

        /// <summary>US dollars, 0 if unknown.</summary>
        public double revenue { get; set; }

        public List<NamedItem> genres { get; set; }

        public KeywordList keywords { get; set; }

        public MovieCredits credits { get; set; }
    }

    /// <summary>Fields shared by the tv discover lists and the tv details.</summary>
    public class SearchTv : SearchResultBase
    {
        public string firstAired { get; set; }

        /// <summary>TMDb status, e.g. "Ended" or "Returning Series".</summary>
        public string status { get; set; }

        public string rating { get; set; }

        /// <summary>Minutes per episode.</summary>
        public string runtime { get; set; }

        /// <summary>Backdrop image URL.</summary>
        public string banner { get; set; }

        public bool fullyAvailable { get; set; }

        public bool partlyAvailable { get; set; }

        public List<SeasonRequest> seasonRequests { get; set; }
    }

    /// <summary>/api/v2/Search/tv/moviedb/{id}. In the lists "network" is a string, here an object.</summary>
    public class TvDetails : SearchTv
    {
        public NamedItem network { get; set; }

        public TvImages images { get; set; }

        public string certification { get; set; }

        public string tagline { get; set; }

        public List<NamedItem> genres { get; set; }

        public KeywordList keywords { get; set; }

        public List<TvCast> cast { get; set; }
    }

    public class NamedItem
    {
        public string name { get; set; }
    }

    public class KeywordList
    {
        public List<NamedItem> keywordsValue { get; set; }
    }

    public class MovieCredits
    {
        public List<MovieCast> cast { get; set; }
    }

    public class MovieCast
    {
        public string name { get; set; }

        public string character { get; set; }

        public string profile_path { get; set; }
    }

    public class TvCast
    {
        public string person { get; set; }

        public string character { get; set; }

        public string image { get; set; }
    }

    public class TvImages
    {
        public string medium { get; set; }

        public string original { get; set; }
    }

    public class SeasonRequest
    {
        public int seasonNumber { get; set; }

        public bool seasonAvailable { get; set; }

        public List<EpisodeRequest> episodes { get; set; }
    }

    public class EpisodeRequest
    {
        public int episodeNumber { get; set; }

        public string title { get; set; }

        public string airDate { get; set; }

        public bool available { get; set; }

        public bool denied { get; set; }

        public bool approved { get; set; }

        public bool requested { get; set; }
    }

    /// <summary>Body of /api/v2/Search/advancedSearch/movie (TMDb's discover, sorted by popularity).</summary>
    public class DiscoverRequest
    {
        public string type { get; set; } = "movie";

        /// <summary>Ombi only applies years after 1900.</summary>
        public int? releaseYear { get; set; }

        public List<int> genreIds { get; set; } = new List<int>();

        public List<int> keywordIds { get; set; } = new List<int>();

        public List<int> watchProviders { get; set; } = new List<int>();

        public List<int> companies { get; set; } = new List<int>();
    }

    public class MovieRequestBody
    {
        public int theMovieDbId { get; set; }

        public string languageCode { get; set; }

        public bool is4kRequest { get; set; }

        /// <summary>Ombi user id; only used when the user name cannot be sent as header.</summary>
        public string requestOnBehalf { get; set; }
    }

    public class TvRequestBody
    {
        public int theMovieDbId { get; set; }

        public string languageCode { get; set; }

        public bool requestAll { get; set; }

        public bool latestSeason { get; set; }

        public bool firstSeason { get; set; }

        public List<SeasonsBody> seasons { get; set; } = new List<SeasonsBody>();

        /// <summary>Ombi user id; only used when the user name cannot be sent as header.</summary>
        public string requestOnBehalf { get; set; }
    }

    public class SeasonsBody
    {
        public int seasonNumber { get; set; }

        public List<EpisodesBody> episodes { get; set; } = new List<EpisodesBody>();
    }

    public class EpisodesBody
    {
        public int episodeNumber { get; set; }
    }

    public class RequestEngineResult
    {
        public bool result { get; set; }

        public string message { get; set; }

        public bool isError { get; set; }

        public string errorMessage { get; set; }

        public int requestId { get; set; }
    }

    public class RequestUser
    {
        public string userName { get; set; }

        public string alias { get; set; }
    }

    public class MovieRequest
    {
        public int id { get; set; }

        public int theMovieDbId { get; set; }

        public string title { get; set; }

        public string posterPath { get; set; }

        public string releaseDate { get; set; }

        public string requestedDate { get; set; }

        public bool approved { get; set; }

        public bool available { get; set; }

        public bool denied { get; set; }

        public string deniedReason { get; set; }

        public string requestedByAlias { get; set; }

        public RequestUser requestedUser { get; set; }
    }

    public class TvRequest
    {
        public int id { get; set; }

        /// <summary>TheMovieDb id of the show (Ombi v4).</summary>
        public int externalProviderId { get; set; }

        public string title { get; set; }

        public string posterPath { get; set; }

        public string releaseDate { get; set; }

        public List<ChildRequest> childRequests { get; set; }
    }

    /// <summary>Page of the v2 request lists (api/v2/Requests/...).</summary>
    public class RequestsPage<T>
    {
        public List<T> collection { get; set; }

        public int total { get; set; }
    }

    /// <summary>A season request of a user; the show itself is in <see cref="parentRequest"/>.</summary>
    public class ChildRequest
    {
        public int id { get; set; }

        public string title { get; set; }

        public string releaseYear { get; set; }

        public TvRequest parentRequest { get; set; }

        public string requestedDate { get; set; }

        public bool approved { get; set; }

        public bool available { get; set; }

        public bool denied { get; set; }

        public string deniedReason { get; set; }

        public string requestedByAlias { get; set; }

        public RequestUser requestedUser { get; set; }

        public List<SeasonRequest> seasonRequests { get; set; }
    }

    /// <summary>/api/v1/Request/movie|tv/remaining: the user's request quota.</summary>
    public class RequestQuota
    {
        public bool hasLimit { get; set; }

        public int limit { get; set; }

        public int remaining { get; set; }

        /// <summary>When the next request is possible again (if none are left).</summary>
        public string nextRequest { get; set; }
    }

    /// <summary>Body of approve (movie: request id, series: season request id).</summary>
    public class RequestUpdateBody
    {
        public int id { get; set; }

        public bool is4K { get; set; }
    }

    public class RequestDenyBody
    {
        public int id { get; set; }

        public string reason { get; set; }

        public bool is4K { get; set; }
    }

    public class IssueCategory
    {
        public int id { get; set; }

        public string value { get; set; }
    }

    /// <summary>Body of POST /api/v1/Issues.</summary>
    public class IssueBody
    {
        public string title { get; set; }

        /// <summary>0 = series, 1 = movie.</summary>
        public int requestType { get; set; }

        /// <summary>TMDb id, which Ombi uses for the link to the title.</summary>
        public string providerId { get; set; }

        public string subject { get; set; }

        public string description { get; set; }

        public int issueCategoryId { get; set; }

        public string posterPath { get; set; }
    }
}
