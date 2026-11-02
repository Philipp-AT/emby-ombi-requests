using System;
using System.Collections.Generic;
using OmbiRequests.Requests;

namespace OmbiRequests.Catalog
{
    /// <summary>Content of the catalog file in Emby's data folder.</summary>
    public class CatalogData
    {
        public DateTime? ImportedUtc { get; set; }

        /// <summary>Last time new titles were added (set by the import as well).</summary>
        public DateTime? UpdatedUtc { get; set; }

        /// <summary>Language of titles, texts and posters at the import ("de"); null for catalogs of older versions (English).</summary>
        public string Language { get; set; }

        public List<CatalogEntry> Entries { get; set; } = new List<CatalogEntry>();
    }

    public class CatalogEntry
    {
        /// <summary>"movie" or "tv".</summary>
        public string MediaType { get; set; }

        public int TmdbId { get; set; }

        public string ImdbId { get; set; }

        public string TvdbId { get; set; }

        public string Title { get; set; }

        public int? Year { get; set; }

        public string Overview { get; set; }

        public string PosterUrl { get; set; }

        /// <summary>Background picture; catalogs of older versions get it later, like trailers.</summary>
        public string BackdropUrl { get; set; }

        /// <summary>True once the background was looked up (also when there is none), see <see cref="TrailerChecked"/>.</summary>
        public bool BackdropChecked { get; set; }

        public double? Rating { get; set; }

        public string ReleaseDate { get; set; }

        /// <summary>YouTube link from Ombi; movies get it later, see <see cref="TitleCatalog.FillTrailersAsync"/>.</summary>
        public string TrailerUrl { get; set; }

        /// <summary>True once the trailer was looked up (also when there is none).</summary>
        public bool TrailerChecked { get; set; }

        /// <summary>
        /// Ombi's state when the title was loaded. Only used for series in the library: Ombi knows
        /// whether seasons are missing, the library lookup does not.
        /// </summary>
        public string LoadedStatus { get; set; }

        /// <summary>Current <see cref="MediaStatus"/>, recomputed on every channel refresh.</summary>
        public string Status { get; set; }

        /// <summary>
        /// The state last handed to Emby. Emby only takes over changed states of existing channel
        /// items when they are marked for update, which is too expensive for thousands of titles.
        /// </summary>
        public string ListedStatus { get; set; }

        /// <summary>The poster last handed to Emby, see <see cref="ListedStatus"/>.</summary>
        public string ListedPoster { get; set; }

        /// <summary>The trailer last handed to Emby, see <see cref="ListedStatus"/>.</summary>
        public string ListedTrailer { get; set; }

        /// <summary>The background last handed to Emby, see <see cref="ListedStatus"/>.</summary>
        public string ListedBackdrop { get; set; }

        public DateTime AddedUtc { get; set; }

        // Methods, so that they do not end up in the file.
        public string GetKey() => MediaType + ":" + TmdbId;

        /// <summary>Trailer or background still to be looked up in Ombi's details.</summary>
        public bool NeedsDetails() => !TrailerChecked || !BackdropChecked;

        /// <summary>Whether Emby got another state, poster, trailer or background last time.</summary>
        public bool HasUnlistedChanges() =>
            ListedStatus != Status || ListedPoster != PosterUrl || ListedTrailer != TrailerUrl || ListedBackdrop != BackdropUrl;

        /// <summary>Notes what Emby got, see <see cref="HasUnlistedChanges"/>.</summary>
        public void MarkListed()
        {
            ListedStatus = Status;
            ListedPoster = PosterUrl;
            ListedTrailer = TrailerUrl;
            ListedBackdrop = BackdropUrl;
        }

        public static CatalogEntry From(MediaItemDto item, DateTime now)
        {
            return new CatalogEntry
            {
                MediaType = item.MediaType,
                TmdbId = item.TmdbId,
                ImdbId = item.ImdbId,
                TvdbId = item.TvdbId,
                Title = item.Title,
                Year = item.Year,
                Overview = item.Overview,
                PosterUrl = item.PosterUrl,
                BackdropUrl = item.BackdropUrl,
                BackdropChecked = !string.IsNullOrEmpty(item.BackdropUrl),
                Rating = item.Rating,
                ReleaseDate = item.ReleaseDate,
                TrailerUrl = item.TrailerUrl,
                TrailerChecked = !string.IsNullOrEmpty(item.TrailerUrl),
                LoadedStatus = item.Status,
                Status = item.Status,
                AddedUtc = now
            };
        }

        public MediaItemDto ToDto()
        {
            return new MediaItemDto
            {
                MediaType = MediaType,
                TmdbId = TmdbId,
                ImdbId = ImdbId,
                TvdbId = TvdbId,
                Title = Title,
                Year = Year,
                Overview = Overview,
                PosterUrl = PosterUrl,
                BackdropUrl = BackdropUrl,
                Rating = Rating,
                ReleaseDate = ReleaseDate,
                TrailerUrl = TrailerUrl,
                Status = Status
            };
        }
    }
}
