using System;
using System.Collections.Generic;
using OmbiRequests.Configuration;

namespace OmbiRequests
{
    /// <summary>Server-side texts (channel, notifications, API messages). The web UI has its own strings in client.js.</summary>
    public static class Localization
    {
        private static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            ["ChannelName"] = "Requests",
            ["ChannelDescription"] = "Discover movies and series that are not in Emby yet. Mark a title as favorite to request it.",
            ["FolderPopularMovies"] = "Popular movies",
            ["FolderUpcomingMovies"] = "Upcoming movies",
            ["FolderNowPlayingMovies"] = "Now in cinemas",
            ["FolderTrendingTv"] = "Trending series",
            ["FolderPopularTv"] = "Popular series",
            ["FolderAllMovies"] = "All movies",
            ["FolderTopRatedMovies"] = "Top rated movies",
            ["FolderSeasonalMovies"] = "Seasonal movies",
            ["FolderAnticipatedTv"] = "Anticipated series",
            ["FolderTopRatedTv"] = "Top rated series",
            ["FolderAllTv"] = "All series",
            ["NotConfigured"] = "Requests have not been set up yet.",
            ["NoOmbiAccount"] = "Requests are not enabled for your account \"{0}\" yet. Please ask the administrator.",
            ["RequestSent"] = "Request sent",
            ["RequestFailed"] = "Request failed",
            ["RequestedTitle"] = "\"{0}\" has been requested.",
            ["NothingToRequest"] = "The selected episodes are already available.",
            ["ServiceUnavailable"] = "The request service cannot be reached at the moment. Please try again later.",
            ["ServiceError"] = "The request service reported an error (HTTP {0}). Please try again later.",
            ["UnexpectedError"] = "An unexpected error occurred. Details are in the server log.",
            ["MovieRequestsLeft"] = "{0} movie requests left.",
            ["SeriesRequestsLeft"] = "{0} series requests left.",
            ["RequestWithdrawn"] = "The request for \"{0}\" was withdrawn.",
            ["WithdrawFailed"] = "Request not withdrawn",
            ["RequestWithdrawnHeader"] = "Request withdrawn",
            ["NothingToWithdraw"] = "There is no request of yours for this title.",
            ["WithdrawAlreadyApproved"] = "The request for \"{0}\" is already approved and can no longer be withdrawn.",
            ["WithdrawAlreadyAvailable"] = "\"{0}\" is already available in Emby.",
            ["WithdrawKeptForOthers"] = "\"{0}\" stays requested because someone else wants it too.",
            ["WishRemoved"] = "Your wish for \"{0}\" was removed; the request of another user stays.",
            ["RequestApproved"] = "Request approved.",
            ["RequestDenied"] = "Request denied.",
            ["RequestDeleted"] = "Request deleted. The title can be requested again.",
            ["RequestsPageDisabled"] = "Requests are not enabled for your account.",
            ["IssueReported"] = "Thank you, the problem was reported.",
            ["IssueNotPossible"] = "Problems cannot be reported for this title.",
            ["NowAvailableTitle"] = "Now available",
            ["NowAvailable"] = "\"{0}\" is now available in Emby.",
            ["NowAvailableSeveral"] = "Now available in Emby: {0}",
            ["SeasonShort"] = "season {0}",
        };

        private static readonly Dictionary<string, string> German = new Dictionary<string, string>
        {
            ["ChannelName"] = "Anfragen",
            ["ChannelDescription"] = "Filme und Serien entdecken, die noch nicht in Emby sind. Titel als Favorit markieren, um ihn anzufragen.",
            ["FolderPopularMovies"] = "Beliebte Filme",
            ["FolderUpcomingMovies"] = "Demnächst erscheinende Filme",
            ["FolderNowPlayingMovies"] = "Aktuell im Kino",
            ["FolderTrendingTv"] = "Angesagte Serien",
            ["FolderPopularTv"] = "Beliebte Serien",
            ["FolderAllMovies"] = "Alle Filme",
            ["FolderTopRatedMovies"] = "Bestbewertete Filme",
            ["FolderSeasonalMovies"] = "Saisonale Filme",
            ["FolderAnticipatedTv"] = "Erwartete Serien",
            ["FolderTopRatedTv"] = "Bestbewertete Serien",
            ["FolderAllTv"] = "Alle Serien",
            ["NotConfigured"] = "Anfragen sind noch nicht eingerichtet.",
            ["NoOmbiAccount"] = "Für dein Konto \"{0}\" sind Anfragen noch nicht freigeschaltet. Bitte wende dich an den Administrator.",
            ["RequestSent"] = "Anfrage gesendet",
            ["RequestFailed"] = "Anfrage fehlgeschlagen",
            ["RequestedTitle"] = "\"{0}\" wurde angefragt.",
            ["NothingToRequest"] = "Die ausgewählten Folgen sind bereits verfügbar.",
            ["ServiceUnavailable"] = "Der Anfrage-Dienst ist gerade nicht erreichbar. Bitte versuche es später noch einmal.",
            ["ServiceError"] = "Der Anfrage-Dienst hat einen Fehler gemeldet (HTTP {0}). Bitte versuche es später noch einmal.",
            ["UnexpectedError"] = "Ein unerwarteter Fehler ist aufgetreten. Details stehen im Server-Log.",
            ["MovieRequestsLeft"] = "Noch {0} Filmanfragen übrig.",
            ["SeriesRequestsLeft"] = "Noch {0} Serienanfragen übrig.",
            ["RequestWithdrawn"] = "Die Anfrage für \"{0}\" wurde zurückgezogen.",
            ["WithdrawFailed"] = "Anfrage nicht zurückgezogen",
            ["RequestWithdrawnHeader"] = "Anfrage zurückgezogen",
            ["NothingToWithdraw"] = "Es gibt keine Anfrage von dir für diesen Titel.",
            ["WithdrawAlreadyApproved"] = "Die Anfrage für \"{0}\" ist schon genehmigt und kann nicht mehr zurückgezogen werden.",
            ["WithdrawAlreadyAvailable"] = "\"{0}\" ist schon in Emby verfügbar.",
            ["WithdrawKeptForOthers"] = "\"{0}\" bleibt angefragt, weil ihn sich noch jemand anderes wünscht.",
            ["WishRemoved"] = "Dein Wunsch nach \"{0}\" ist entfernt, die Anfrage eines anderen Benutzers bleibt bestehen.",
            ["RequestApproved"] = "Anfrage genehmigt.",
            ["RequestDenied"] = "Anfrage abgelehnt.",
            ["RequestDeleted"] = "Anfrage gelöscht. Der Titel kann wieder angefragt werden.",
            ["RequestsPageDisabled"] = "Anfragen sind für dein Konto nicht freigeschaltet.",
            ["IssueReported"] = "Danke, das Problem wurde gemeldet.",
            ["IssueNotPossible"] = "Für diesen Titel können keine Probleme gemeldet werden.",
            ["NowAvailableTitle"] = "Jetzt verfügbar",
            ["NowAvailable"] = "\"{0}\" ist jetzt in Emby verfügbar.",
            ["NowAvailableSeveral"] = "Jetzt in Emby verfügbar: {0}",
            ["SeasonShort"] = "Staffel {0}",
        };

        public static bool IsGerman(UiLanguage language, string serverCulture)
        {
            switch (language)
            {
                case UiLanguage.German:
                    return true;
                case UiLanguage.English:
                    return false;
                default:
                    return (serverCulture ?? string.Empty).StartsWith("de", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string Get(bool german, string key, params object[] args)
        {
            var table = german ? German : English;
            if (!table.TryGetValue(key, out var text) && !English.TryGetValue(key, out text))
            {
                text = key;
            }

            return args.Length == 0 ? text : string.Format(text, args);
        }
    }
}
