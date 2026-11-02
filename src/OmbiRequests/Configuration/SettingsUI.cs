using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;

namespace OmbiRequests.Configuration
{
    /// <summary>Admin settings page, rendered by Emby's generic UI.</summary>
    public class SettingsUI : EditableOptionsBase
    {
        /// <summary>Shown instead of the stored API key, which is never sent to the browser.</summary>
        public const string ApiKeyPlaceholder = "(unchanged)";

        public override string EditorTitle => Plugin.PluginName;

        public override string EditorDescription =>
            "Connects Emby to Ombi so that users can request movies and series from within Emby: "
            + "in the web app via the 'Requests' menu entry and the search, in all other apps via the 'Requests' channel. "
            + "Users never see the name Ombi.";

        public CaptionItem CaptionServer { get; set; } = new CaptionItem("Ombi server");

        [DisplayName("Ombi URL")]
        [Description("Address of Ombi as seen from the Emby server, e.g. https://ombi.example.com or http://ombi:3579 (including a base path if Ombi uses one).")]
        [Required]
        public string OmbiUrl { get; set; }

        [DisplayName("API key")]
        [Description("Ombi > Settings > Ombi > API Key. Grants admin access to Ombi and stays on the Emby server.")]
        [IsPassword]
        public string ApiKey { get; set; }

        public ButtonItem TestButton { get; set; } = new ButtonItem("Test connection") { Icon = IconNames.network_check, Data1 = SettingsPageView.TestCommand };

        public StatusItem ConnectionStatus { get; set; } = new StatusItem("Connection", "Not tested yet", ItemStatus.Unavailable);

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        public CaptionItem CaptionUsers { get; set; } = new CaptionItem("Users");

        [DisplayName("User mapping")]
        [Description("Optional. Emby users are matched automatically: same Ombi user name, Emby Connect e-mail (Ombi's import uses it as user name) "
                     + "or an Ombi alias that only this one user has. Add one line per exception: EmbyUser=OmbiUserNameOrEmailOrAlias")]
        [EditMultiline(4)]
        public string UserMappings { get; set; }

        [DisplayName("Allow users without Ombi account")]
        [Description("Such users request with the API key and their Emby name as alias. Warning: Ombi treats these requests as admin requests (no quota, auto-approval).")]
        public bool AllowUnmappedUsers { get; set; }

        [DisplayName("Requests page (web app)")]
        [Description("Users who get the requests page in the web app: the menu entry, results under Emby's search and the buttons on library pages. "
                     + "Users created later get it too.")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(UserOptions))]
        public string PageUsers { get; set; }

        [DisplayName("Requests folder (all apps)")]
        [Description("Users who see the requests folder and can request with the heart. This is Emby's channel access of the user "
                     + "(Users > Access > Channels), the plugin changes it there. Only possible while the requests channel is switched on.")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(UserOptions))]
        public string FolderUsers { get; set; }

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> UserOptions { get; set; } = new List<EditorSelectOption>();

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        public CaptionItem CaptionIntegration { get; set; } = new CaptionItem("Integration");

        [DisplayName("Web app integration")]
        [Description("Adds a 'Requests' entry to the side menu of the Emby web app and shows Ombi results in its search. "
                     + "Requires <script src=\"/OmbiRequests/web/client.js\" defer></script> in Emby's index.html - "
                     + "either via the option below or added by the reverse proxy (see README).")]
        public bool EnableWebIntegration { get; set; }

        [DisplayName("Load the script automatically")]
        [Description("Serves Emby's web app from a copy in Emby's data folder that contains the script tag (sets the server option "
                     + "DashboardSourcePath). Needs no reverse proxy changes; the copy is rebuilt automatically after Emby updates. "
                     + "Browsers that already cached the web app need one hard reload (Ctrl+F5). Turn this off before removing the plugin.")]
        public bool InjectIntoWebApp { get; set; }

        public StatusItem WebAppStatus { get; set; } = new StatusItem("Script injection", string.Empty, ItemStatus.Unavailable);

        [DisplayName("Language")]
        [Description("Language of the request UI, the channel and notifications.")]
        public UiLanguage Language { get; set; }

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();

        public CaptionItem CaptionChannel { get; set; } = new CaptionItem("Requests channel (all Emby apps)");

        public LabelItem ChannelHint { get; set; } = new LabelItem(
            "Users see the channel as 'Requests' ('Anfragen') under 'My media'. Changes below are applied within a few seconds after saving.");

        [DisplayName("Requests channel")]
        [Description("Switches the channel on or off for the whole server and all apps. Switching it off removes it immediately; "
                     + "switching it back on needs a restart of Emby if it was off when Emby started. "
                     + "While it is off, Emby logs one 'Error creating ... OmbiChannel' entry per start - that is intended.")]
        public bool EnableChannel { get; set; }

        public StatusItem ChannelStatus { get; set; } = new StatusItem("Channel", string.Empty, ItemStatus.Unavailable);

        [DisplayName("Request via favorites")]
        [Description("Marking a movie or series in the channel as favorite requests it (a series as a whole).")]
        public bool EnableFavoriteRequests { get; set; }

        [DisplayName("Status banner on posters")]
        [Description("Shows on the posters whether a title is missing (and can be requested with the heart), requested, approved or available.")]
        public bool EnablePosterBanners { get; set; }

        [DisplayName("Keep other poster overlays off the channel")]
        [Description("Plugins such as Cover Art put frames, bars or badges on all posters, including those of the channel. "
                     + "On: they leave out the posters and folder pictures of the requests channel and keep working everywhere else.")]
        public bool HideOtherOverlays { get; set; }

        [DisplayName("Withdraw by removing the heart")]
        [Description("Removing the favorite again withdraws a request that was made with the heart, as long as it waits for approval. "
                     + "Approved, denied and available requests stay.")]
        public bool EnableWithdrawOnUnfavorite { get; set; }

        [DisplayName("Also withdraw approved requests")]
        [Description("Withdrawing (heart or 'My requests') also takes requests that are approved but not available yet. "
                     + "Ombi then forgets them, but Radarr/Sonarr keep what they already got; remove it there if needed. "
                     + "A request always stays while another user wants the title too.")]
        public bool WithdrawApprovedRequests { get; set; }

        [DisplayName("Tell users when their request is available")]
        [Description("Checks every 15 minutes and shows a message in the requester's Emby apps; users who are offline get it "
                     + "the next time they open Emby.")]
        public bool EnableAvailableNotifications { get; set; }

        [DisplayName("List: popular movies")]
        public bool ChannelPopularMovies { get; set; }

        [DisplayName("List: now in cinemas")]
        public bool ChannelNowPlayingMovies { get; set; }

        [DisplayName("List: upcoming movies")]
        public bool ChannelUpcomingMovies { get; set; }

        [DisplayName("List: trending series")]
        public bool ChannelTrendingTv { get; set; }

        [DisplayName("List: popular series")]
        public bool ChannelPopularTv { get; set; }

        [DisplayName("List: top rated movies")]
        public bool ChannelTopRatedMovies { get; set; }

        [DisplayName("List: seasonal movies")]
        [Description("Ombi's list for the time of year (e.g. Christmas). Outside such seasons it is empty, then the folder is hidden.")]
        public bool ChannelSeasonalMovies { get; set; }

        [DisplayName("List: anticipated series")]
        [Description("New and upcoming series. Some Ombi versions fail on this list; then the folder stays hidden and the diagnostics show the error.")]
        public bool ChannelAnticipatedTv { get; set; }

        [DisplayName("List: top rated series")]
        public bool ChannelTopRatedTv { get; set; }

        [DisplayName("Titles per movie list")]
        [Description("How many titles each movie list above shows. Every title is an item in Emby's database.")]
        [MinValue(10)]
        [MaxValue(500)]
        public int ChannelMovieListSize { get; set; }

        [DisplayName("Titles per series list")]
        [Description("How many titles each series list above shows. Every title is an item in Emby's database, "
                     + "and Ombi looks up every series at TMDb, so more series make a channel refresh longer.")]
        [MinValue(10)]
        [MaxValue(500)]
        public int ChannelTvListSize { get; set; }

        [DisplayName("Show titles that are not requested")]
        public bool ChannelShowNotRequested { get; set; }

        [DisplayName("Show requested titles (pending approval)")]
        public bool ChannelShowRequested { get; set; }

        [DisplayName("Show approved titles (in progress)")]
        public bool ChannelShowApproved { get; set; }

        [DisplayName("Show partially available titles")]
        [Description("Series of which only some seasons or episodes are in Emby.")]
        public bool ChannelShowPartiallyAvailable { get; set; }

        [DisplayName("Show denied titles")]
        public bool ChannelShowDenied { get; set; }

        [DisplayName("Show available titles")]
        [Description("Applies to the channel and to the 'Discover' lists of the web app (not to the search). "
                     + "Off by default: available titles can be found in the normal libraries.")]
        public bool ChannelShowAvailable { get; set; }

        public SpacerItem Spacer4 { get; set; } = new SpacerItem();

        public CaptionItem CaptionCatalog { get; set; } = new CaptionItem("All movies / All series");

        public LabelItem CatalogHint { get; set; } = new LabelItem(
            "Two more channel folders (split into A-Z), mainly so that Emby's search in all apps also finds titles that are not in Emby yet. "
            + "The titles are imported once; afterwards only titles from Ombi's current lists (cinema, upcoming, trending, popular) that are "
            + "not in the catalog yet are added. Emby's search shows a title only once even if it is in several folders. "
            + "In these folders a favorite requests the whole series. The folders appear after the first import.");

        [DisplayName("Folder 'All movies'")]
        public bool ChannelAllMovies { get; set; }

        [DisplayName("Folder 'All series'")]
        public bool ChannelAllTv { get; set; }

        [DisplayName("Movies to import")]
        [Description("No upper limit. 2/3 most popular, 1/3 best rated at TMDb, plus what is in cinemas or announced; beyond that "
                     + "(TMDb serves 10,000 titles per list) the movies are loaded year by year. Ombi looks up every title at TMDb: "
                     + "5,000 movies take roughly 15-30 minutes, and every title becomes an item in Emby's database.")]
        [MinValue(100)]
        public int CatalogMovieCount { get; set; }

        [DisplayName("Series to import")]
        [Description("No upper limit, but Ombi can only search series by list (popular, best rated, trending, upcoming): "
                     + "more than these lists deliver is not possible. Series take longer than movies.")]
        [MinValue(50)]
        public int CatalogTvCount { get; set; }

        [DisplayName("Load new titles every ... days")]
        [Description("Runs automatically after the import. 0 = only with the button below.")]
        [MinValue(0)]
        [MaxValue(90)]
        public int CatalogUpdateIntervalDays { get; set; }

        public ButtonItem ImportButton { get; set; } = new ButtonItem("Save and start import") { Icon = IconNames.cloud_download, Data1 = SettingsPageView.ImportCommand };

        public ButtonItem UpdateButton { get; set; } = new ButtonItem("Load new titles now") { Icon = IconNames.update, Data1 = SettingsPageView.UpdateCommand };

        public ButtonItem TrailerButton { get; set; } = new ButtonItem("Look up missing trailers and backgrounds now") { Icon = IconNames.movie, Data1 = SettingsPageView.TrailerCommand };

        public StatusItem CatalogStatus { get; set; } = new StatusItem("Catalog", string.Empty, ItemStatus.Unavailable);

        public SpacerItem Spacer5 { get; set; } = new SpacerItem();

        public CaptionItem CaptionDiagnostics { get; set; } = new CaptionItem("Diagnostics");

        public LabelItem DiagnosticsHint { get; set; } = new LabelItem("State when this page was opened (since Emby's start). Details are in Emby's log.");

        public StatusItem DiagnosticsIdentity { get; set; } = new StatusItem("Language of channel and catalog", string.Empty, ItemStatus.Unavailable);

        public StatusItem DiagnosticsChannel { get; set; } = new StatusItem("Last channel lists", string.Empty, ItemStatus.Unavailable);

        public StatusItem DiagnosticsTrailers { get; set; } = new StatusItem("Catalog trailers and backgrounds", string.Empty, ItemStatus.Unavailable);

        public StatusItem DiagnosticsNotifications { get; set; } = new StatusItem("Availability messages", string.Empty, ItemStatus.Unavailable);

        public StatusItem DiagnosticsIssues { get; set; } = new StatusItem("Problem reports", string.Empty, ItemStatus.Unavailable);

        public StatusItem DiagnosticsError { get; set; } = new StatusItem("Last error", string.Empty, ItemStatus.Unavailable);

        public static SettingsUI FromConfiguration(PluginConfiguration config)
        {
            return new SettingsUI
            {
                OmbiUrl = config.OmbiUrl,
                ApiKey = string.IsNullOrEmpty(config.ApiKey) ? string.Empty : ApiKeyPlaceholder,
                UserMappings = config.UserMappings,
                AllowUnmappedUsers = config.AllowUnmappedUsers,
                EnableWebIntegration = config.EnableWebIntegration,
                InjectIntoWebApp = config.InjectIntoWebApp,
                EnableFavoriteRequests = config.EnableFavoriteRequests,
                EnablePosterBanners = config.EnablePosterBanners,
                HideOtherOverlays = config.HideOtherOverlays,
                EnableWithdrawOnUnfavorite = config.EnableWithdrawOnUnfavorite,
                WithdrawApprovedRequests = config.WithdrawApprovedRequests,
                EnableAvailableNotifications = config.EnableAvailableNotifications,
                Language = config.Language,
                EnableChannel = config.EnableChannel,
                ChannelPopularMovies = config.ChannelPopularMovies,
                ChannelNowPlayingMovies = config.ChannelNowPlayingMovies,
                ChannelUpcomingMovies = config.ChannelUpcomingMovies,
                ChannelTrendingTv = config.ChannelTrendingTv,
                ChannelPopularTv = config.ChannelPopularTv,
                ChannelTopRatedMovies = config.ChannelTopRatedMovies,
                ChannelSeasonalMovies = config.ChannelSeasonalMovies,
                ChannelAnticipatedTv = config.ChannelAnticipatedTv,
                ChannelTopRatedTv = config.ChannelTopRatedTv,
                ChannelMovieListSize = config.ChannelMovieListSize,
                ChannelTvListSize = config.ChannelTvListSize,
                ChannelShowNotRequested = config.ChannelShowNotRequested,
                ChannelShowRequested = config.ChannelShowRequested,
                ChannelShowApproved = config.ChannelShowApproved,
                ChannelShowPartiallyAvailable = config.ChannelShowPartiallyAvailable,
                ChannelShowDenied = config.ChannelShowDenied,
                ChannelShowAvailable = config.ChannelShowAvailable,
                ChannelAllMovies = config.ChannelAllMovies,
                ChannelAllTv = config.ChannelAllTv,
                CatalogMovieCount = config.CatalogMovieCount,
                CatalogTvCount = config.CatalogTvCount,
                CatalogUpdateIntervalDays = config.CatalogUpdateIntervalDays
            };
        }

        /// <summary>Copies the edited values onto a configuration, keeping the stored API key if it wasn't changed.</summary>
        public void ApplyTo(PluginConfiguration config)
        {
            config.OmbiUrl = (OmbiUrl ?? string.Empty).Trim().TrimEnd('/');
            if (ApiKey != ApiKeyPlaceholder)
            {
                config.ApiKey = (ApiKey ?? string.Empty).Trim();
            }

            config.UserMappings = UserMappings ?? string.Empty;
            config.AllowUnmappedUsers = AllowUnmappedUsers;
            config.EnableWebIntegration = EnableWebIntegration;
            config.InjectIntoWebApp = InjectIntoWebApp;
            config.EnableFavoriteRequests = EnableFavoriteRequests;
            config.EnablePosterBanners = EnablePosterBanners;
            config.HideOtherOverlays = HideOtherOverlays;
            config.EnableWithdrawOnUnfavorite = EnableWithdrawOnUnfavorite;
            config.WithdrawApprovedRequests = WithdrawApprovedRequests;
            config.EnableAvailableNotifications = EnableAvailableNotifications;
            config.Language = Language;
            config.EnableChannel = EnableChannel;
            config.ChannelPopularMovies = ChannelPopularMovies;
            config.ChannelNowPlayingMovies = ChannelNowPlayingMovies;
            config.ChannelUpcomingMovies = ChannelUpcomingMovies;
            config.ChannelTrendingTv = ChannelTrendingTv;
            config.ChannelPopularTv = ChannelPopularTv;
            config.ChannelTopRatedMovies = ChannelTopRatedMovies;
            config.ChannelSeasonalMovies = ChannelSeasonalMovies;
            config.ChannelAnticipatedTv = ChannelAnticipatedTv;
            config.ChannelTopRatedTv = ChannelTopRatedTv;
            config.ChannelMovieListSize = Math.Max(10, Math.Min(500, ChannelMovieListSize));
            config.ChannelTvListSize = Math.Max(10, Math.Min(500, ChannelTvListSize));
            config.ChannelShowNotRequested = ChannelShowNotRequested;
            config.ChannelShowRequested = ChannelShowRequested;
            config.ChannelShowApproved = ChannelShowApproved;
            config.ChannelShowPartiallyAvailable = ChannelShowPartiallyAvailable;
            config.ChannelShowDenied = ChannelShowDenied;
            config.ChannelShowAvailable = ChannelShowAvailable;
            config.ChannelAllMovies = ChannelAllMovies;
            config.ChannelAllTv = ChannelAllTv;
            config.CatalogMovieCount = Math.Max(100, CatalogMovieCount);
            config.CatalogTvCount = Math.Max(50, CatalogTvCount);
            config.CatalogUpdateIntervalDays = Math.Max(0, Math.Min(90, CatalogUpdateIntervalDays));
        }

        protected override void Validate(ValidationContext context)
        {
            if (!Uri.TryCreate((OmbiUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                context.AddValidationError(nameof(OmbiUrl), "Please enter a valid http(s) URL.");
            }

            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                context.AddValidationError(nameof(ApiKey), "The API key is required.");
            }
        }
    }
}
