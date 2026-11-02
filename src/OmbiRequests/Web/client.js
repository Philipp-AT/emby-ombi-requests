/*
 * Ombi Requests for Emby - web app integration.
 * Loaded by a script tag in Emby's index.html: the plugin's setting "Load the script automatically"
 * adds it to a copy of the web app, or a reverse proxy adds it (see README). Adds a "Requests" menu entry,
 * a requests page (discover, search, my requests, title details) and Ombi results
 * below Emby's own search. All Ombi calls go through the plugin's API, which acts as the logged-in user.
 */
(function () {
    'use strict';

    if (window.__ombiRequestsLoaded) {
        return;
    }
    window.__ombiRequestsLoaded = true;

    // The stylesheet lives next to this script, so a single script tag is enough.
    (function loadStylesheet() {
        var script = document.currentScript;
        var href = script && script.src
            ? script.src.replace(/client\.js(\?.*)?$/, function (match, query) { return 'client.css' + (query || ''); })
            : '/OmbiRequests/web/client.css';
        var link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        document.head.appendChild(link);
    })();

    var STRINGS = {
        en: {
            Requests: 'Requests',
            Discover: 'Discover',
            MyRequests: 'My requests',
            SearchPlaceholder: 'Search movies and series…',
            SearchType: 'Type',
            SortLabel: 'Order',
            ShowLabel: 'Show',
            All: 'All',
            Movies: 'Movies',
            Series: 'Series',
            PopularMovies: 'Popular movies',
            NowPlaying: 'Now in cinemas',
            UpcomingMovies: 'Upcoming movies',
            TrendingSeries: 'Trending series',
            PopularSeries: 'Popular series',
            NoResults: 'Nothing found.',
            NoRequests: 'You have not requested anything yet.',
            Loading: 'Loading…',
            Request: 'Request',
            RequestAll: 'Request all seasons',
            RequestSelection: 'Request selection ({0})',
            Requesting: 'Requesting …',
            SelectSeason: 'Select all missing episodes of this season',
            EpisodeFallback: 'Episode {0}',
            OpenInEmby: 'Watch in Emby',
            Trailer: 'Trailer',
            TrailerOf: 'Trailer: {0}',
            Close: 'Close',
            Season: 'Season {0}',
            Episodes: '{0} episodes',
            Status_None: 'Not requested',
            Status_Requested: 'Requested',
            Status_Approved: 'Approved',
            Status_PartiallyAvailable: 'Partially available',
            Status_Available: 'Available',
            Status_Denied: 'Denied',
            DeniedReason: 'Reason: {0}',
            RequestedOn: 'Requested on {0}',
            SearchSectionTitle: 'Not in Emby yet – request',
            Back: 'Back',
            Error: 'Something went wrong: {0}',
            Overview: 'Overview',
            Keywords: 'Keywords',
            SeasonsTitle: 'Seasons',
            Cast: 'Cast',
            Minutes: '{0} minutes',
            MinutesPerEpisode: '{0} min per episode',
            Fact_Rating: 'Rating',
            Fact_Status: 'Status',
            Fact_Availability: 'Availability',
            Fact_Release: 'Release',
            Fact_DigitalRelease: 'Digital release',
            Fact_Runtime: 'Runtime',
            Fact_FirstAired: 'First aired',
            Fact_Network: 'Network',
            Fact_Seasons: 'Seasons',
            Fact_Certification: 'Age rating',
            Fact_DeniedReason: 'Reason for denial',
            Fact_Revenue: 'Revenue',
            Fact_Budget: 'Budget',
            AllRequests: 'All requests',
            NoAdminRequests: 'No requests match this filter.',
            FilterRequests: 'Filter by title or user',
            ShowOpen: 'All except available',
            ShowRequested: 'Waiting for approval',
            ShowAll: 'All incl. available',
            SortNewest: 'Newest first',
            SortOldest: 'Oldest first',
            SortTitle: 'Title A–Z',
            SortUser: 'Requested by',
            SortStatus: 'Status',
            RequestCount: 'Requests: {0}',
            AlsoWantedBy: 'Also wanted by {0}',
            Delete: 'Delete',
            ConfirmDelete: 'Really delete?',
            DeleteHint: 'Removes the request. The title can then be requested again.',
            Approve: 'Approve',
            Deny: 'Deny',
            DenyReason: 'Reason (optional)',
            ConfirmDeny: 'Deny request',
            RequestedByOn: '{0} on {1}',
            Withdraw: 'Withdraw',
            QuotaMovies: '{0} of {1} movie requests left',
            QuotaSeries: '{0} of {1} series requests left',
            QuotaNext: '(again from {0})',
            RequestMissing: 'Request missing episodes ({0})',
            ReportIssue: 'Report a problem',
            IssueTitle: 'Report a problem with "{0}"',
            IssueCategory: 'Category',
            IssueSubject: 'Subject',
            IssueDescription: 'Description',
            Send: 'Send',
            Cancel: 'Cancel'
        },
        de: {
            Requests: 'Anfragen',
            Discover: 'Entdecken',
            MyRequests: 'Meine Anfragen',
            SearchPlaceholder: 'Filme und Serien suchen…',
            SearchType: 'Art',
            SortLabel: 'Sortierung',
            ShowLabel: 'Anzeigen',
            All: 'Alle',
            Movies: 'Filme',
            Series: 'Serien',
            PopularMovies: 'Beliebte Filme',
            NowPlaying: 'Aktuell im Kino',
            UpcomingMovies: 'Demnächst',
            TrendingSeries: 'Angesagte Serien',
            PopularSeries: 'Beliebte Serien',
            NoResults: 'Nichts gefunden.',
            NoRequests: 'Du hast noch nichts angefragt.',
            Loading: 'Wird geladen…',
            Request: 'Anfragen',
            RequestAll: 'Alle Staffeln anfragen',
            RequestSelection: 'Auswahl anfragen ({0})',
            Requesting: 'Wird angefragt …',
            SelectSeason: 'Alle fehlenden Folgen dieser Staffel auswählen',
            EpisodeFallback: 'Folge {0}',
            OpenInEmby: 'In Emby ansehen',
            Trailer: 'Trailer',
            TrailerOf: 'Trailer: {0}',
            Close: 'Schließen',
            Season: 'Staffel {0}',
            Episodes: '{0} Folgen',
            Status_None: 'Nicht angefragt',
            Status_Requested: 'Angefragt',
            Status_Approved: 'Genehmigt',
            Status_PartiallyAvailable: 'Teilweise verfügbar',
            Status_Available: 'Verfügbar',
            Status_Denied: 'Abgelehnt',
            DeniedReason: 'Grund: {0}',
            RequestedOn: 'Angefragt am {0}',
            SearchSectionTitle: 'Noch nicht in Emby – anfragen',
            Back: 'Zurück',
            Error: 'Etwas ist schiefgelaufen: {0}',
            Overview: 'Handlung',
            Keywords: 'Schlagwörter',
            SeasonsTitle: 'Staffeln',
            Cast: 'Besetzung',
            Minutes: '{0} Minuten',
            MinutesPerEpisode: '{0} Min. pro Folge',
            Fact_Rating: 'Bewertung',
            Fact_Status: 'Status',
            Fact_Availability: 'Verfügbarkeit',
            Fact_Release: 'Kinostart',
            Fact_DigitalRelease: 'Digitale Veröffentlichung',
            Fact_Runtime: 'Laufzeit',
            Fact_FirstAired: 'Erstausstrahlung',
            Fact_Network: 'Sender',
            Fact_Seasons: 'Staffeln',
            Fact_Certification: 'Altersfreigabe',
            Fact_DeniedReason: 'Ablehnungsgrund',
            Fact_Revenue: 'Einspielergebnis',
            Fact_Budget: 'Budget',
            AllRequests: 'Alle Anfragen',
            NoAdminRequests: 'Keine Anfragen für diesen Filter.',
            FilterRequests: 'Nach Titel oder Benutzer filtern',
            ShowOpen: 'Alle außer verfügbare',
            ShowRequested: 'Warten auf Freigabe',
            ShowAll: 'Alle inkl. verfügbare',
            SortNewest: 'Neueste zuerst',
            SortOldest: 'Älteste zuerst',
            SortTitle: 'Titel A–Z',
            SortUser: 'Angefragt von',
            SortStatus: 'Status',
            RequestCount: 'Anfragen: {0}',
            AlsoWantedBy: 'Auch gewünscht von {0}',
            Delete: 'Löschen',
            ConfirmDelete: 'Wirklich löschen?',
            DeleteHint: 'Entfernt die Anfrage. Der Titel kann danach neu angefragt werden.',
            Approve: 'Genehmigen',
            Deny: 'Ablehnen',
            DenyReason: 'Grund (optional)',
            ConfirmDeny: 'Anfrage ablehnen',
            RequestedByOn: '{0} am {1}',
            Withdraw: 'Zurückziehen',
            QuotaMovies: 'Noch {0} von {1} Filmanfragen',
            QuotaSeries: 'Noch {0} von {1} Serienanfragen',
            QuotaNext: '(wieder ab {0})',
            RequestMissing: 'Fehlende Folgen anfragen ({0})',
            ReportIssue: 'Problem melden',
            IssueTitle: 'Problem mit „{0}“ melden',
            IssueCategory: 'Kategorie',
            IssueSubject: 'Betreff',
            IssueDescription: 'Beschreibung',
            Send: 'Senden',
            Cancel: 'Abbrechen'
        }
    };

    // TMDb release states as delivered by Ombi; English is shown as is.
    var RELEASE_STATUS = {
        de: {
            'Released': 'Veröffentlicht',
            'Post Production': 'In Postproduktion',
            'In Production': 'In Produktion',
            'Planned': 'Geplant',
            'Rumored': 'Angekündigt',
            'Canceled': 'Abgesetzt',
            'Ended': 'Beendet',
            'Returning Series': 'Wird fortgesetzt',
            'Pilot': 'Pilotfolge'
        }
    };

    var DISCOVER_SECTIONS = [
        { category: 'movie:popular', title: 'PopularMovies' },
        { category: 'tv:trending', title: 'TrendingSeries' },
        { category: 'movie:nowplaying', title: 'NowPlaying' },
        { category: 'movie:upcoming', title: 'UpcomingMovies' },
        { category: 'tv:popular', title: 'PopularSeries' }
    ];

    var state = {
        userId: null,
        status: null,
        statusRetryAt: 0,
        lang: 'en'
    };

    // ---------- helpers ----------

    function t(key) {
        var text = (STRINGS[state.lang] || STRINGS.en)[key] || STRINGS.en[key] || key;
        for (var i = 1; i < arguments.length; i++) {
            // A function as replacement, so "$&" or "$'" in messages are inserted literally.
            var value = String(arguments[i]);
            text = text.replace('{' + (i - 1) + '}', function () { return value; });
        }
        return text;
    }

    function esc(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function importModule(path) {
        if (window.Emby && typeof window.Emby.importModule === 'function') {
            return window.Emby.importModule(path).then(function (m) {
                return m && m.default ? m.default : m;
            });
        }
        return Promise.reject(new Error('Emby.importModule not available'));
    }

    function toast(text) {
        importModule('./modules/toast/toast.js').then(function (toastFn) {
            // Emby's toast puts the text into innerHTML, and messages contain titles from Ombi.
            toastFn(esc(text));
        }, function () {
            window.alert(text);
        });
    }

    function api(path, params, body) {
        var client = window.ApiClient;
        var url = client.getUrl('OmbiRequests/' + path, params || {});
        var promise = body
            ? client.ajax({ type: 'POST', url: url, data: JSON.stringify(body), contentType: 'application/json', dataType: 'json' })
            : client.getJSON(url);

        return Promise.resolve(promise).then(function (response) {
            if (!response || !response.Success) {
                throw new Error((response && response.Message) || 'Unknown error');
            }
            return response.Data;
        }, function (err) {
            var message = err && (err.statusText || err.message);
            throw new Error(message || 'Request failed');
        });
    }

    function canRequest(item) {
        if (item.MediaType === 'tv') {
            return item.Status !== 'Available' && item.Status !== 'Denied';
        }
        return item.Status === 'None';
    }

    function statusBadge(status) {
        if (!status || status === 'None') {
            return '';
        }
        return '<span class="ombi-badge ombi-badge-' + esc(status) + '">' + esc(t('Status_' + status)) + '</span>';
    }

    function formatDate(value) {
        if (!value) {
            return '';
        }
        var date = new Date(value);
        return isNaN(date.getTime()) ? '' : date.toLocaleDateString(state.lang === 'de' ? 'de-DE' : undefined);
    }

    function showItemInEmby(itemId) {
        // The item may be the view under the page (e.g. "missing episodes" of the series below): Emby's
        // router then does not navigate, so the page is closed first.
        closeRequestsPage();
        importModule('./modules/approuter.js').then(function (router) {
            router.showItem(itemId, window.ApiClient.serverId());
        }, function () {
            window.location.hash = '#!/item?id=' + encodeURIComponent(itemId) + '&serverId=' + encodeURIComponent(window.ApiClient.serverId());
        });
    }

    // ---------- cards ----------

    function cardHtml(item) {
        var poster = item.PosterUrl
            ? '<img class="ombi-card-img" loading="lazy" alt="" src="' + esc(item.PosterUrl) + '">'
            : '<div class="ombi-card-img ombi-card-noimg"><i class="md-icon">' + (item.MediaType === 'tv' ? 'tv' : 'movie') + '</i></div>';

        return '<button type="button" class="ombi-card" data-type="' + esc(item.MediaType) + '" data-id="' + esc(item.TmdbId) + '">' +
            '<div class="ombi-card-imgbox">' + poster + statusBadge(item.Status) + '</div>' +
            '<div class="ombi-card-title">' + esc(item.Title) + '</div>' +
            '<div class="ombi-card-sub secondaryText">' + esc(item.Year || '') + ' · ' + esc(item.MediaType === 'tv' ? t('Series') : t('Movies')) + '</div>' +
            '</button>';
    }

    function renderCards(container, items, layout) {
        container.className = 'ombi-cards ombi-cards-' + layout;
        container.innerHTML = items.map(cardHtml).join('');
        container.__ombiItems = items;
    }

    function bindCardClicks(container) {
        // Rows of "my requests" and "all requests" are not buttons (they contain buttons): Enter opens them.
        container.addEventListener('keydown', function (e) {
            if ((e.key === 'Enter' || e.key === ' ') && e.target.classList && e.target.classList.contains('ombi-card') && e.target.tagName !== 'BUTTON') {
                e.preventDefault();
                e.target.click();
            }
        });
        container.addEventListener('click', function (e) {
            var card = e.target.closest('.ombi-card');
            if (!card || e.target.closest('.ombi-row-action, .ombi-deny-box')) {
                return;
            }
            var list = card.parentNode.__ombiItems || [];
            var type = card.getAttribute('data-type');
            var id = parseInt(card.getAttribute('data-id'), 10);
            var item = list.filter(function (i) {
                return i.MediaType === type && i.TmdbId === id;
            })[0] || { MediaType: type, TmdbId: id };

            if (opensInEmby(item)) {
                showItemInEmby(item.EmbyItemId);
            } else {
                showDetails(item);
            }
        });
    }

    /**
     * Titles that are in the library open their Emby page instead of the request details:
     * movies as soon as they are in the library, series only when complete (a partly available
     * series keeps the details page, where missing seasons can be requested).
     */
    function opensInEmby(item) {
        if (!item.EmbyItemId) {
            return false;
        }
        return item.MediaType === 'movie' || item.Status === 'Available';
    }

    // Images that fail to load (e.g. outdated TheTVDB poster links) become a neutral placeholder.
    document.addEventListener('error', function (e) {
        var img = e.target;
        if (!img || img.tagName !== 'IMG' || !/(^|\s)ombi-/.test(img.className)) {
            return;
        }
        var placeholder = document.createElement('div');
        placeholder.className = img.className + ' ombi-img-missing';
        placeholder.innerHTML = '<i class="md-icon">' + (img.classList.contains('ombi-person-img') ? 'person' : 'image') + '</i>';
        img.replaceWith(placeholder);
    }, true);

    // Keeps status badges in every visible list up to date after a request.
    document.addEventListener('ombi-status-changed', function (e) {
        var detail = e.detail;
        document.querySelectorAll('.ombi-card[data-type="' + detail.MediaType + '"][data-id="' + detail.TmdbId + '"]').forEach(function (card) {
            var items = card.parentNode.__ombiItems || [];
            items.forEach(function (i) {
                if (i.MediaType === detail.MediaType && i.TmdbId === detail.TmdbId) {
                    i.Status = detail.Status;
                }
            });
            // Rows of "my requests" and "all requests" have no poster box; their badge comes with the next load.
            var box = card.querySelector('.ombi-card-imgbox');
            if (!box) {
                return;
            }
            var old = box.querySelector('.ombi-badge');
            if (old) {
                old.remove();
            }
            box.insertAdjacentHTML('beforeend', statusBadge(detail.Status));
        });
    });

    // ---------- details ----------

    /** Shows a title on the requests page; from Emby's search the page is opened just for it. */
    function showDetails(item) {
        openPage().then(function (current) {
            if (!current) {
                return;
            }
            if (!current.main.classList.contains('hide')) {
                current.mainScroll = current.element.scrollTop;
            }
            current.main.classList.add('hide');
            current.details.classList.remove('hide');
            current.element.scrollTop = 0;

            var request = current.detailsRequest = {};
            var view = { body: current.details, expandedSeasons: {}, request: request };
            current.details.innerHTML = backButtonHtml() + '<div class="ombi-loading">' + esc(t('Loading')) + '</div>';
            bindBackButton(current.details);

            api('Details', { MediaType: item.MediaType, TmdbId: item.TmdbId }).then(function (details) {
                if (page === current && current.detailsRequest === request) {
                    renderDetails(view, details);
                }
            }, function (err) {
                if (page === current && current.detailsRequest === request) {
                    current.details.innerHTML = backButtonHtml() + '<div class="ombi-message ombi-error">' + esc(t('Error', err.message)) + '</div>';
                    bindBackButton(current.details);
                }
            });
        });
    }

    /** Back to the discover/search list, or out of the page if it was opened for this title only. */
    function backFromDetails() {
        var current = page;
        if (!current) {
            return;
        }
        if (!current.mainRendered) {
            closeRequestsPage();
            return;
        }
        current.detailsRequest = null;
        current.details.classList.add('hide');
        current.details.innerHTML = '';
        current.main.classList.remove('hide');
        current.element.scrollTop = current.mainScroll || 0;
    }

    function backButtonHtml() {
        return '<button type="button" class="ombi-back"><i class="md-icon">arrow_back</i><span>' + esc(t('Back')) + '</span></button>';
    }

    function bindBackButton(root) {
        var button = root.querySelector('.ombi-back');
        if (button) {
            button.addEventListener('click', backFromDetails);
        }
    }

    function formatLongDate(value) {
        if (!value) {
            return '';
        }
        var date = new Date(value);
        if (isNaN(date.getTime()) || date.getFullYear() < 1800) {
            return '';
        }
        return date.toLocaleDateString(state.lang === 'de' ? 'de-DE' : undefined, { day: 'numeric', month: 'short', year: 'numeric' });
    }

    function releaseStatusText(value) {
        var map = RELEASE_STATUS[state.lang] || {};
        return map[value] || value || '';
    }

    /** TMDb amounts are US dollars. */
    function formatMoney(value) {
        return value.toLocaleString(state.lang === 'de' ? 'de-DE' : 'en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });
    }

    function formatRating(rating) {
        return (Math.round(rating * 10) / 10).toLocaleString(state.lang === 'de' ? 'de-DE' : undefined) + '/10';
    }

    function chipHtml(icon, text) {
        return '<span class="ombi-chip"><i class="md-icon">' + icon + '</i><span>' + esc(text) + '</span></span>';
    }

    function factHtml(label, valueHtml) {
        return '<div class="ombi-fact"><div class="ombi-fact-label">' + esc(label) + '</div><div class="ombi-fact-value">' + valueHtml + '</div></div>';
    }

    function sectionHtml(title, content) {
        return '<section class="ombi-detail-section"><h2 class="ombi-section-heading">' + esc(title) + '</h2>' + content + '</section>';
    }

    function renderDetails(view, item) {
        var isTv = item.MediaType === 'tv';
        var seasons = isTv && item.Seasons ? item.Seasons : [];
        var releaseDate = formatLongDate(item.ReleaseDate);
        var html = '<div class="ombi-detail">';

        // Hero: backdrop, poster, title, key facts, genres and actions (layout modelled on Ombi).
        var chips = '';
        if (item.Rating) {
            chips += chipHtml('star', formatRating(item.Rating));
        }
        if (item.Runtime) {
            chips += chipHtml('schedule', t(isTv ? 'MinutesPerEpisode' : 'Minutes', item.Runtime));
        }
        if (releaseDate) {
            chips += chipHtml('event', releaseDate);
        }
        if (item.ReleaseStatus) {
            chips += chipHtml('info', releaseStatusText(item.ReleaseStatus));
        }

        var actions = '';
        if (canRequest(item)) {
            actions += '<button type="button" is="emby-button" class="raised button-submit emby-button ombi-btn-request" data-scope="All">' +
                '<i class="md-icon button-icon button-icon-left">add</i><span>' + esc(t(isTv ? 'RequestAll' : 'Request')) + '</span></button>';
        }
        if (item.EmbyItemId) {
            actions += '<button type="button" is="emby-button" class="raised emby-button ombi-btn-open">' +
                '<i class="md-icon button-icon button-icon-left">play_arrow</i><span>' + esc(t('OpenInEmby')) + '</span></button>';
        }
        if (item.TrailerUrl) {
            actions += '<button type="button" is="emby-button" class="raised emby-button ombi-btn-trailer">' +
                '<i class="md-icon button-icon button-icon-left">theaters</i><span>' + esc(t('Trailer')) + '</span></button>';
        }

        html += '<div class="ombi-hero">' +
            (item.BackdropUrl ? '<div class="ombi-hero-backdrop"></div>' : '') +
            '<div class="ombi-hero-content">' + backButtonHtml() +
            '<div class="ombi-hero-main">' +
            (item.PosterUrl
                ? '<img class="ombi-hero-poster" alt="" src="' + esc(item.PosterUrl) + '">'
                : '<div class="ombi-hero-poster ombi-card-noimg"><i class="md-icon">' + (isTv ? 'tv' : 'movie') + '</i></div>') +
            '<div class="ombi-hero-info">' +
            '<h1 class="ombi-hero-title">' + esc(item.Title) + (item.Year ? ' <span class="ombi-hero-year">(' + esc(item.Year) + ')</span>' : '') + '</h1>' +
            '<div class="ombi-chiplist">' + chips + statusBadge(item.Status || 'None') +
            (item.Status === 'None' ? '<span class="ombi-badge ombi-badge-None">' + esc(t('Status_None')) + '</span>' : '') + '</div>' +
            (item.Genres && item.Genres.length
                ? '<div class="ombi-chiplist">' + item.Genres.map(function (g) { return '<span class="ombi-genre">' + esc(g) + '</span>'; }).join('') + '</div>'
                : '') +
            (item.Tagline ? '<p class="ombi-tagline">' + esc(item.Tagline) + '</p>' : '') +
            (actions ? '<div class="ombi-actions">' + actions + '</div>' : '') +
            '</div></div></div></div>';

        if (item.Overview) {
            html += sectionHtml(t('Overview'), '<p class="ombi-overview">' + esc(item.Overview) + '</p>');
        }

        // Fact tiles.
        var facts = '';
        if (item.Rating) {
            facts += factHtml(t('Fact_Rating'), '<i class="md-icon ombi-fact-star">star</i>' + esc(formatRating(item.Rating)) +
                (item.VoteCount ? ' <span class="secondaryText">(' + esc(item.VoteCount.toLocaleString()) + ')</span>' : ''));
        }
        if (item.ReleaseStatus) {
            facts += factHtml(t('Fact_Status'), esc(releaseStatusText(item.ReleaseStatus)));
        }
        facts += factHtml(t('Fact_Availability'), esc(t('Status_' + (item.Status || 'None'))));
        if (releaseDate) {
            facts += factHtml(t(isTv ? 'Fact_FirstAired' : 'Fact_Release'), esc(releaseDate));
        }
        if (!isTv && formatLongDate(item.DigitalReleaseDate)) {
            facts += factHtml(t('Fact_DigitalRelease'), esc(formatLongDate(item.DigitalReleaseDate)));
        }
        if (item.Runtime) {
            facts += factHtml(t('Fact_Runtime'), esc(t(isTv ? 'MinutesPerEpisode' : 'Minutes', item.Runtime)));
        }
        if (item.Revenue) {
            facts += factHtml(t('Fact_Revenue'), esc(formatMoney(item.Revenue)));
        }
        if (item.Budget) {
            facts += factHtml(t('Fact_Budget'), esc(formatMoney(item.Budget)));
        }
        if (isTv && item.Network) {
            facts += factHtml(t('Fact_Network'), esc(item.Network));
        }
        if (isTv && seasons.length) {
            facts += factHtml(t('Fact_Seasons'), esc(seasons.length));
        }
        if (item.Certification) {
            facts += factHtml(t('Fact_Certification'), esc(item.Certification));
        }
        if (item.Status === 'Denied' && item.DeniedReason) {
            facts += factHtml(t('Fact_DeniedReason'), '<span class="ombi-denied">' + esc(item.DeniedReason) + '</span>');
        }
        html += '<div class="ombi-facts">' + facts + '</div>';

        if (item.Keywords && item.Keywords.length) {
            html += '<div class="ombi-keywords"><div class="ombi-fact-label">' + esc(t('Keywords')) + '</div><div class="ombi-chiplist">' +
                item.Keywords.map(function (k) { return '<span class="ombi-tag">' + esc(k) + '</span>'; }).join('') + '</div></div>';
        }

        if (seasons.length) {
            // Also for series that count as available or denied as a whole but miss single episodes.
            var openEpisodes = seasons.some(function (season) {
                return (season.Episodes || []).some(function (episode) {
                    return isRequestable(episode.Status);
                });
            });
            html += sectionHtml(t('SeasonsTitle'),
                '<div class="ombi-seasons">' + seasons.map(function (season) {
                    return seasonHtml(season, !!view.expandedSeasons[season.SeasonNumber]);
                }).join('') + '</div>' +
                (canRequest(item) || openEpisodes
                    ? '<div class="ombi-actions"><button type="button" is="emby-button" class="raised emby-button ombi-btn-request ombi-btn-selection" data-scope="Episodes" disabled><span>' + esc(t('RequestSelection', 0)) + '</span></button></div>'
                    : ''));
        }

        if (item.Cast && item.Cast.length) {
            html += sectionHtml(t('Cast'), '<div class="ombi-cast">' + item.Cast.map(function (person) {
                return '<div class="ombi-person">' +
                    (person.ImageUrl
                        ? '<img class="ombi-person-img" loading="lazy" alt="" src="' + esc(person.ImageUrl) + '">'
                        : '<div class="ombi-person-img ombi-person-noimg"><i class="md-icon">person</i></div>') +
                    '<div class="ombi-person-name">' + esc(person.Name) + '</div>' +
                    (person.Character ? '<div class="ombi-person-role secondaryText">' + esc(person.Character) + '</div>' : '') +
                    '</div>';
            }).join('') + '</div>');
        }

        html += '</div>';
        view.body.innerHTML = html;

        // Set as a property, not in the markup: a quote in the URL could otherwise break out of url().
        var heroBackdrop = view.body.querySelector('.ombi-hero-backdrop');
        if (heroBackdrop) {
            heroBackdrop.style.backgroundImage = 'url(' + JSON.stringify(item.BackdropUrl) + ')';
        }

        bindBackButton(view.body);
        if (seasons.length) {
            bindSeasonSelection(view);
        }

        view.body.querySelectorAll('.ombi-btn-request').forEach(function (button) {
            button.addEventListener('click', function () {
                var scope = button.getAttribute('data-scope');
                var episodes = scope === 'Episodes' ? selectedEpisodes(view) : [];
                if (scope === 'Episodes' && !episodes.length) {
                    return;
                }
                sendRequest(view, item, scope, episodes, button);
            });
        });

        var trailerButton = view.body.querySelector('.ombi-btn-trailer');
        if (trailerButton) {
            trailerButton.addEventListener('click', function () {
                openTrailer(item.TrailerUrl, item.Title);
            });
        }

        var openButton = view.body.querySelector('.ombi-btn-open');
        if (openButton) {
            openButton.addEventListener('click', function () {
                showItemInEmby(item.EmbyItemId);
            });
        }
    }

    /** An episode can be selected if it is neither available nor already requested. */
    function isRequestable(status) {
        return status === 'None';
    }

    function seasonHtml(season, expanded) {
        var episodes = season.Episodes || [];
        var requestable = episodes.filter(function (e) {
            return isRequestable(e.Status);
        }).length;

        var html = '<div class="ombi-season' + (expanded ? ' ombi-season-expanded' : '') + '" data-season="' + esc(season.SeasonNumber) + '">' +
            '<div class="ombi-season-head">' +
            '<input type="checkbox" class="ombi-season-box" title="' + esc(t('SelectSeason')) + '"' + (requestable ? '' : ' disabled') + '>' +
            '<button type="button" class="ombi-season-toggle" aria-expanded="' + (expanded ? 'true' : 'false') + '">' +
            '<span class="ombi-season-name">' + esc(t('Season', season.SeasonNumber)) + '</span>' +
            '<span class="ombi-season-count secondaryText">' + esc(t('Episodes', season.EpisodeCount)) + '</span>' +
            '<span class="ombi-season-badge">' + statusBadge(season.Status) + '</span>' +
            '<i class="md-icon ombi-season-arrow">expand_more</i>' +
            '</button>' +
            '</div>' +
            '<div class="ombi-episodes">';

        episodes.forEach(function (episode) {
            var enabled = isRequestable(episode.Status);
            var airDate = formatDate(episode.AirDate);
            html += '<label class="ombi-episode' + (enabled ? '' : ' ombi-episode-disabled') + '">' +
                '<input type="checkbox" class="ombi-episode-box" value="' + esc(season.SeasonNumber + ':' + episode.EpisodeNumber) + '"' + (enabled ? '' : ' disabled') + '>' +
                '<span class="ombi-episode-number">' + esc(episode.EpisodeNumber) + '</span>' +
                '<span class="ombi-episode-title">' + esc(episode.Title || t('EpisodeFallback', episode.EpisodeNumber)) + '</span>' +
                '<span class="ombi-episode-date secondaryText">' + esc(airDate) + '</span>' +
                '<span class="ombi-episode-badge">' + statusBadge(episode.Status) + '</span>' +
                '</label>';
        });

        return html + '</div></div>';
    }

    function bindSeasonSelection(view) {
        var root = view.body;
        var selectionButton = root.querySelector('.ombi-btn-selection');

        function updateSelection() {
            root.querySelectorAll('.ombi-season').forEach(function (season) {
                var boxes = season.querySelectorAll('.ombi-episode-box:not(:disabled)');
                var checked = season.querySelectorAll('.ombi-episode-box:checked').length;
                var seasonBox = season.querySelector('.ombi-season-box');
                seasonBox.checked = boxes.length > 0 && checked === boxes.length;
                seasonBox.indeterminate = checked > 0 && checked < boxes.length;
            });
            var count = selectedEpisodes(view).length;
            if (selectionButton) {
                selectionButton.disabled = count === 0;
                selectionButton.querySelector('span').textContent = t('RequestSelection', count);
            }
        }

        root.querySelectorAll('.ombi-season').forEach(function (season) {
            var number = season.getAttribute('data-season');
            season.querySelector('.ombi-season-toggle').addEventListener('click', function (e) {
                var expanded = season.classList.toggle('ombi-season-expanded');
                e.currentTarget.setAttribute('aria-expanded', expanded ? 'true' : 'false');
                view.expandedSeasons[number] = expanded;
            });
            season.querySelector('.ombi-season-box').addEventListener('change', function (e) {
                season.querySelectorAll('.ombi-episode-box:not(:disabled)').forEach(function (box) {
                    box.checked = e.target.checked;
                });
                updateSelection();
            });
        });
        root.querySelectorAll('.ombi-episode-box').forEach(function (box) {
            box.addEventListener('change', updateSelection);
        });

        updateSelection();
    }

    function selectedEpisodes(view) {
        var values = [];
        view.body.querySelectorAll('.ombi-episode-box:checked').forEach(function (box) {
            values.push(box.value);
        });
        return values;
    }

    function sendRequest(view, item, scope, episodes, button) {
        view.body.querySelectorAll('.ombi-actions button').forEach(function (b) {
            b.disabled = true;
        });
        // Ombi can take a while (for many episodes, or when it hands the request to Sonarr/Radarr right away).
        button.classList.add('ombi-busy');
        button.innerHTML = '<i class="md-icon button-icon button-icon-left ombi-spin">autorenew</i><span>' + esc(t('Requesting')) + '</span>';

        // The user may have gone back or opened another title in the meantime.
        function stillShown() {
            return page && page.detailsRequest === view.request;
        }

        api('Request', null, { MediaType: item.MediaType, TmdbId: item.TmdbId, Scope: scope, Episodes: episodes }).then(function (outcome) {
            toast(outcome.Message);
            if (!outcome.Success) {
                if (stillShown()) {
                    renderDetails(view, item);
                }
                return;
            }

            var status = outcome.Status || 'Requested';
            document.dispatchEvent(new CustomEvent('ombi-status-changed', {
                detail: { MediaType: item.MediaType, TmdbId: item.TmdbId, Status: status }
            }));
            refreshStatus();

            // The request went through; if reloading the details fails, show the known new status.
            api('Details', { MediaType: item.MediaType, TmdbId: item.TmdbId }).then(function (details) {
                if (stillShown()) {
                    renderDetails(view, details);
                }
            }, function () {
                if (stillShown()) {
                    renderDetails(view, Object.assign({}, item, { Status: status }));
                }
            });
        }, function (err) {
            toast(t('Error', err.message));
            if (stillShown()) {
                renderDetails(view, item);
            }
        });
    }

    // ---------- quota ----------

    function quotaText(quota) {
        var text = t(quota.MediaType === 'tv' ? 'QuotaSeries' : 'QuotaMovies', quota.Remaining, quota.Limit);
        if (quota.Remaining <= 0 && quota.NextRequest) {
            text += ' ' + t('QuotaNext', formatDate(quota.NextRequest));
        }
        return text;
    }

    /** Remaining requests, only shown when Ombi has a limit for the user. */
    function renderQuota(root) {
        var box = root.querySelector('.ombi-quota');
        var quotas = (state.status && state.status.Quotas) || [];
        if (!box) {
            return;
        }
        box.classList.toggle('hide', !quotas.length);
        box.innerHTML = quotas.length ? '<i class="md-icon">info_outline</i><span>' + esc(quotas.map(quotaText).join(' · ')) + '</span>' : '';
    }

    /** Reloads the status after a request, for the quota. */
    function refreshStatus() {
        api('Status').then(function (status) {
            state.status = status;
            document.dispatchEvent(new CustomEvent('ombi-quota-changed'));
        }, function () { });
    }

    // ---------- requests page content ----------

    /** Discover / search / my requests UI, rendered into the given element. */
    function renderRequestsUi(root) {
        root.innerHTML =
            '<div class="ombi-toolbar">' +
            '<div class="ombi-tabs" role="tablist">' +
            '<button type="button" role="tab" aria-selected="true" class="ombi-tab ombi-tab-active" data-tab="discover">' + esc(t('Discover')) + '</button>' +
            '<button type="button" role="tab" aria-selected="false" class="ombi-tab" data-tab="mine">' + esc(t('MyRequests')) + '</button>' +
            (state.status && state.status.IsAdmin ? '<button type="button" role="tab" aria-selected="false" class="ombi-tab" data-tab="all">' + esc(t('AllRequests')) + '</button>' : '') +
            '</div>' +
            '<div class="ombi-searchbar">' +
            '<i class="md-icon" aria-hidden="true">search</i>' +
            '<input type="search" class="ombi-search-input" autocomplete="off" placeholder="' + esc(t('SearchPlaceholder')) + '" aria-label="' + esc(t('SearchPlaceholder')) + '">' +
            '<select class="ombi-search-type" aria-label="' + esc(t('SearchType')) + '">' +
            '<option value="all">' + esc(t('All')) + '</option>' +
            '<option value="movie">' + esc(t('Movies')) + '</option>' +
            '<option value="tv">' + esc(t('Series')) + '</option>' +
            '</select>' +
            '</div>' +
            '</div>' +
            '<div class="ombi-quota hide"></div>' +
            '<div class="ombi-content"></div>';

        var content = root.querySelector('.ombi-content');
        var input = root.querySelector('.ombi-search-input');
        var typeSelect = root.querySelector('.ombi-search-type');
        var tabs = root.querySelectorAll('.ombi-tab');
        var searchTimer = null;

        // Bumped whenever the content switches (tab, search): late answers for the old one are dropped.
        var contentVersion = 0;

        bindCardClicks(content);
        renderQuota(root);
        // Removes itself once the page is gone (closing drops it, the next opening renders a new one).
        document.addEventListener('ombi-quota-changed', function onQuotaChanged() {
            if (!root.isConnected) {
                document.removeEventListener('ombi-quota-changed', onQuotaChanged);
                return;
            }
            renderQuota(root);
        });

        // Buttons inside the rows: withdraw (my requests), approve, deny and delete (all requests).
        content.addEventListener('click', function (e) {
            var action = e.target.closest('.ombi-row-action');
            var row = action && action.closest('.ombi-request-row');
            if (!row) {
                return;
            }
            var request = (row.parentNode.__ombiRows || [])[parseInt(row.getAttribute('data-index'), 10)];
            if (!request) {
                return;
            }
            if (action.classList.contains('ombi-withdraw')) {
                withdraw(row, request);
            } else if (action.classList.contains('ombi-approve')) {
                decide(row, request, true, null);
            } else if (action.classList.contains('ombi-deny')) {
                var box = row.querySelector('.ombi-deny-box');
                box.classList.toggle('hide');
                if (!box.classList.contains('hide')) {
                    box.querySelector('input').focus();
                }
            } else if (action.classList.contains('ombi-deny-confirm')) {
                decide(row, request, false, row.querySelector('.ombi-deny-reason').value);
            } else if (action.classList.contains('ombi-delete')) {
                // Two clicks: the first one asks "really delete?" for a few seconds.
                if (action.classList.contains('ombi-delete-armed')) {
                    removeRequest(row, request);
                    return;
                }
                action.classList.add('ombi-delete-armed');
                action.textContent = t('ConfirmDelete');
                setTimeout(function () {
                    action.classList.remove('ombi-delete-armed');
                    action.textContent = t('Delete');
                }, 4000);
            }
        });

        function setRowBusy(row, busy) {
            row.querySelectorAll('.ombi-row-action').forEach(function (b) {
                b.disabled = busy;
            });
        }

        function withdraw(row, request) {
            setRowBusy(row, true);
            api('Withdraw', null, { MediaType: request.MediaType, TmdbId: request.TmdbId, Seasons: request.Seasons }).then(function (outcome) {
                toast(outcome.Message);
                if (outcome.Success) {
                    document.dispatchEvent(new CustomEvent('ombi-status-changed', {
                        detail: { MediaType: request.MediaType, TmdbId: request.TmdbId, Status: outcome.Status || 'None' }
                    }));
                    refreshStatus();
                    showMine();
                } else {
                    setRowBusy(row, false);
                }
            }, function (err) {
                toast(t('Error', err.message));
                setRowBusy(row, false);
            });
        }

        /** Admin actions on a row of "all requests"; the list keeps its filter and order. */
        function adminAction(row, path, body, applied) {
            setRowBusy(row, true);
            api(path, null, body).then(function (outcome) {
                toast(outcome.Message);
                if (!outcome.Success) {
                    setRowBusy(row, false);
                    return;
                }
                if (outcome.Status) {
                    document.dispatchEvent(new CustomEvent('ombi-status-changed', {
                        detail: { MediaType: body.MediaType, TmdbId: body.TmdbId, Status: outcome.Status }
                    }));
                }
                applied();
                renderAllRequests();
            }, function (err) {
                toast(t('Error', err.message));
                setRowBusy(row, false);
            });
        }

        function decide(row, request, approve, reason) {
            var body = { MediaType: request.MediaType, RequestId: request.RequestId, TmdbId: request.TmdbId, Approve: approve, Reason: reason };
            adminAction(row, 'Admin/Decide', body, function () {
                request.Status = approve ? 'Approved' : 'Denied';
                request.DeniedReason = approve ? null : reason;
            });
        }

        function removeRequest(row, request) {
            var body = { MediaType: request.MediaType, RequestId: request.RequestId, TmdbId: request.TmdbId };
            adminAction(row, 'Admin/Delete', body, function () {
                adminView.requests = adminView.requests.filter(function (r) {
                    return r !== request;
                });
            });
        }

        function setTab(name) {
            contentVersion++;
            tabs.forEach(function (tab) {
                var active = tab.getAttribute('data-tab') === name;
                tab.classList.toggle('ombi-tab-active', active);
                tab.setAttribute('aria-selected', active ? 'true' : 'false');
            });
            return contentVersion;
        }

        function showMessage(text, isError) {
            content.innerHTML = '<div class="ombi-message' + (isError ? ' ombi-error' : '') + '">' + esc(text) + '</div>';
        }

        function runSearch() {
            var query = input.value.trim();
            if (query.length < 2) {
                showDiscover();
                return;
            }
            var version = setTab(null);
            showMessage(t('Loading'));
            api('Search', { Query: query, Type: typeSelect.value }).then(function (items) {
                if (version !== contentVersion) {
                    return;
                }
                if (!items.length) {
                    showMessage(t('NoResults'));
                    return;
                }
                var grid = document.createElement('div');
                content.innerHTML = '';
                content.appendChild(grid);
                renderCards(grid, items, 'grid');
            }, function (err) {
                if (version === contentVersion) {
                    showMessage(t('Error', err.message), true);
                }
            });
        }

        function showDiscover() {
            setTab('discover');
            content.innerHTML = DISCOVER_SECTIONS.map(function (section) {
                return '<div class="ombi-section">' +
                    '<h2 class="sectionTitle ombi-section-title">' + esc(t(section.title)) + '</h2>' +
                    '<div class="ombi-cards ombi-cards-row" data-category="' + esc(section.category) + '"><div class="ombi-loading">' + esc(t('Loading')) + '</div></div>' +
                    '</div>';
            }).join('');

            content.querySelectorAll('[data-category]').forEach(function (row) {
                api('Discover', { Category: row.getAttribute('data-category'), Count: 20 }).then(function (items) {
                    renderCards(row, items, 'row');
                }, function (err) {
                    row.innerHTML = '<div class="ombi-message ombi-error">' + esc(t('Error', err.message)) + '</div>';
                });
            });
        }

        function showMine() {
            var version = setTab('mine');
            showMessage(t('Loading'));
            api('MyRequests').then(function (requests) {
                if (version !== contentVersion) {
                    return;
                }
                if (!requests.length) {
                    showMessage(t('NoRequests'));
                    return;
                }
                content.innerHTML = '<div class="ombi-requests">' + requests.map(function (r, index) {
                    var seasons = r.Seasons && r.Seasons.length
                        ? ' · ' + r.Seasons.map(function (n) { return 'S' + n; }).join(', ')
                        : '';
                    return '<div role="button" tabindex="0" class="ombi-request-row ombi-card" data-index="' + index + '" data-type="' + esc(r.MediaType) + '" data-id="' + esc(r.TmdbId) + '">' +
                        (r.PosterUrl ? '<img class="ombi-request-img" loading="lazy" alt="" src="' + esc(r.PosterUrl) + '">' : '<div class="ombi-request-img"></div>') +
                        '<div class="ombi-request-text">' +
                        '<div class="ombi-request-title">' + esc(r.Title) + (r.Year ? ' (' + esc(r.Year) + ')' : '') + '</div>' +
                        '<div class="secondaryText">' + esc(r.MediaType === 'tv' ? t('Series') : t('Movies')) + esc(seasons) + ' · ' + esc(t('RequestedOn', formatDate(r.RequestedDate))) + '</div>' +
                        (r.Status === 'Denied' && r.DeniedReason ? '<div class="ombi-denied">' + esc(t('DeniedReason', r.DeniedReason)) + '</div>' : '') +
                        '</div>' +
                        statusBadge(r.Status) +
                        // Waiting for approval, with the setting also approved ones (the plugin decides).
                        (r.CanWithdraw ? '<button type="button" class="ombi-row-action ombi-withdraw">' + esc(t('Withdraw')) + '</button>' : '') +
                        '</div>';
                }).join('') + '</div>';
                content.querySelector('.ombi-requests').__ombiItems = requests;
                content.querySelector('.ombi-requests').__ombiRows = requests;
            }, function (err) {
                if (version === contentVersion) {
                    showMessage(t('Error', err.message), true);
                }
            });
        }

        // ---------- all requests (admins) ----------

        var STATUS_ORDER = { Requested: 0, Approved: 1, Denied: 2, Available: 3 };
        var SHOW_OPTIONS = [['open', 'ShowOpen'], ['Requested', 'ShowRequested'], ['Approved', 'Status_Approved'], ['Denied', 'Status_Denied'], ['Available', 'Status_Available'], ['all', 'ShowAll']];
        var SORT_OPTIONS = [['newest', 'SortNewest'], ['oldest', 'SortOldest'], ['title', 'SortTitle'], ['user', 'SortUser'], ['status', 'SortStatus']];

        // Filter and order stay while the page is open; available requests are only loaded when shown.
        var adminView = { show: 'open', sort: 'newest', filter: '', requests: null, withAvailable: false, version: 0 };

        function options(list, selected) {
            return list.map(function (o) {
                return '<option value="' + o[0] + '"' + (o[0] === selected ? ' selected' : '') + '>' + esc(t(o[1])) + '</option>';
            }).join('');
        }

        function showAllRequests() {
            setTab('all');
            content.innerHTML =
                '<div class="ombi-admin-bar">' +
                '<input type="search" class="ombi-admin-filter" autocomplete="off" placeholder="' + esc(t('FilterRequests')) + '" aria-label="' + esc(t('FilterRequests')) + '" value="' + esc(adminView.filter) + '">' +
                '<select class="ombi-admin-show" aria-label="' + esc(t('ShowLabel')) + '">' + options(SHOW_OPTIONS, adminView.show) + '</select>' +
                '<select class="ombi-admin-sort" aria-label="' + esc(t('SortLabel')) + '">' + options(SORT_OPTIONS, adminView.sort) + '</select>' +
                '<span class="ombi-admin-count secondaryText"></span>' +
                '</div>' +
                '<div class="ombi-admin-list"></div>';

            var filterTimer = null;
            content.querySelector('.ombi-admin-filter').addEventListener('input', function (e) {
                adminView.filter = e.target.value;
                clearTimeout(filterTimer);
                filterTimer = setTimeout(renderAllRequests, 200);
            });
            content.querySelector('.ombi-admin-show').addEventListener('change', function (e) {
                adminView.show = e.target.value;
                if (showsAvailable() && !adminView.withAvailable) {
                    loadAllRequests();
                } else {
                    renderAllRequests();
                }
            });
            content.querySelector('.ombi-admin-sort').addEventListener('change', function (e) {
                adminView.sort = e.target.value;
                renderAllRequests();
            });

            loadAllRequests();
        }

        function showsAvailable() {
            return adminView.show === 'Available' || adminView.show === 'all';
        }

        function loadAllRequests() {
            var list = content.querySelector('.ombi-admin-list');
            var withAvailable = showsAvailable();
            var version = ++adminView.version;
            adminView.requests = null;
            list.innerHTML = '<div class="ombi-message">' + esc(t('Loading')) + '</div>';
            api('Admin/Requests', { IncludeAvailable: withAvailable }).then(function (requests) {
                if (version !== adminView.version) {
                    return;
                }
                adminView.requests = requests;
                adminView.withAvailable = withAvailable;
                renderAllRequests();
            }, function (err) {
                if (version === adminView.version && content.contains(list)) {
                    list.innerHTML = '<div class="ombi-message ombi-error">' + esc(t('Error', err.message)) + '</div>';
                }
            });
        }

        function renderAllRequests() {
            var list = content.querySelector('.ombi-admin-list');
            if (!list || !adminView.requests) {
                return;
            }
            var words = adminView.filter.toLowerCase().split(/\s+/).filter(Boolean);
            var rows = adminView.requests.filter(function (r) {
                var shown = adminView.show === 'all'
                    || (adminView.show === 'open' ? r.Status !== 'Available' : r.Status === adminView.show);
                var text = [r.Title, r.Year, r.RequestedBy].concat(r.AlsoWantedBy || []).join(' ').toLowerCase();
                return shown && words.every(function (w) {
                    return text.indexOf(w) >= 0;
                });
            });
            rows.sort(compareRequests);
            content.querySelector('.ombi-admin-count').textContent = t('RequestCount', rows.length);
            if (!rows.length) {
                list.innerHTML = '<div class="ombi-message">' + esc(t('NoAdminRequests')) + '</div>';
                return;
            }
            list.innerHTML = '<div class="ombi-requests">' + rows.map(adminRow).join('') + '</div>';
            var box = list.querySelector('.ombi-requests');
            box.__ombiItems = rows;
            box.__ombiRows = rows;
        }

        function compareText(a, b) {
            return (a || '').localeCompare(b || '', undefined, { sensitivity: 'base' });
        }

        function compareRequests(a, b) {
            var dateA = a.RequestedDate || '';
            var dateB = b.RequestedDate || '';
            var newestFirst = dateA < dateB ? 1 : dateA > dateB ? -1 : 0;
            switch (adminView.sort) {
                case 'oldest':
                    return -newestFirst;
                case 'title':
                    return compareText(a.Title, b.Title) || newestFirst;
                case 'user':
                    return compareText(a.RequestedBy, b.RequestedBy) || newestFirst;
                case 'status':
                    return (STATUS_ORDER[a.Status] - STATUS_ORDER[b.Status]) || newestFirst;
                default:
                    return newestFirst;
            }
        }

        function adminRow(r, index) {
            var seasons = r.Seasons && r.Seasons.length
                ? ' · ' + r.Seasons.map(function (n) { return 'S' + n; }).join(', ')
                : '';
            // Approved requests can still be denied, denied ones approved; every request can be deleted.
            var actions = '';
            if (r.Status === 'Requested' || r.Status === 'Denied') {
                actions += '<button type="button" class="ombi-row-action ombi-approve">' + esc(t('Approve')) + '</button>';
            }
            if (r.Status === 'Requested' || r.Status === 'Approved') {
                actions += '<button type="button" class="ombi-row-action ombi-deny">' + esc(t('Deny')) + '</button>';
            }
            actions += '<button type="button" class="ombi-row-action ombi-delete" title="' + esc(t('DeleteHint')) + '">' + esc(t('Delete')) + '</button>';

            return '<div role="button" tabindex="0" class="ombi-request-row ombi-card ombi-admin-request" data-index="' + index + '" data-type="' + esc(r.MediaType) + '" data-id="' + esc(r.TmdbId) + '">' +
                (r.PosterUrl ? '<img class="ombi-request-img" loading="lazy" alt="" src="' + esc(r.PosterUrl) + '">' : '<div class="ombi-request-img"></div>') +
                '<div class="ombi-request-text">' +
                '<div class="ombi-request-title">' + esc(r.Title) + (r.Year ? ' (' + esc(r.Year) + ')' : '') + '</div>' +
                '<div class="secondaryText">' + esc(r.MediaType === 'tv' ? t('Series') : t('Movies')) + esc(seasons) + ' · ' + esc(t('RequestedByOn', r.RequestedBy || '?', formatDate(r.RequestedDate))) + '</div>' +
                (r.AlsoWantedBy && r.AlsoWantedBy.length ? '<div class="secondaryText ombi-wanted">' + esc(t('AlsoWantedBy', r.AlsoWantedBy.join(', '))) + '</div>' : '') +
                (r.Status === 'Denied' && r.DeniedReason ? '<div class="ombi-denied">' + esc(t('DeniedReason', r.DeniedReason)) + '</div>' : '') +
                '<div class="ombi-deny-box hide">' +
                '<input type="text" class="ombi-deny-reason" maxlength="200" placeholder="' + esc(t('DenyReason')) + '">' +
                '<button type="button" class="ombi-row-action ombi-deny-confirm">' + esc(t('ConfirmDeny')) + '</button>' +
                '</div>' +
                '</div>' +
                statusBadge(r.Status) +
                '<div class="ombi-row-actions">' + actions + '</div>' +
                '</div>';
        }

        tabs.forEach(function (tab) {
            tab.addEventListener('click', function () {
                // A search typed just before must not replace the tab half a second later.
                clearTimeout(searchTimer);
                input.value = '';
                var name = tab.getAttribute('data-tab');
                if (name === 'mine') {
                    showMine();
                } else if (name === 'all') {
                    showAllRequests();
                } else {
                    showDiscover();
                }
            });
        });

        input.addEventListener('input', function () {
            clearTimeout(searchTimer);
            searchTimer = setTimeout(runSearch, 500);
        });
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                clearTimeout(searchTimer);
                runSearch();
            }
        });
        typeSelect.addEventListener('change', function () {
            if (input.value.trim().length >= 2) {
                runSearch();
            }
        });

        if (state.status && !state.status.CanRequest) {
            showMessage(state.status.Message || t('Error', ''), true);
            return;
        }

        showDiscover();
    }

    // ---------- requests page ----------

    /*
     * Emby gives plugins no route for normal users, so the page is an element laid exactly over the
     * content area of the current Emby view (right of the docked menu, below the header). Header and
     * menu keep working; any navigation removes the page again.
     */
    var page = null;

    function getCurrentView() {
        return importModule('./modules/viewmanager/viewmanager.js').then(function (viewManager) {
            return viewManager && typeof viewManager.currentView === 'function' ? viewManager.currentView() : null;
        }, function () {
            return null;
        }).then(function (view) {
            if (view) {
                return view;
            }
            var views = document.querySelectorAll('.view:not(.hide)');
            return views.length ? views[views.length - 1] : null;
        });
    }

    var pageOpening = null;

    /** Counts navigations, so a page that is still opening knows the user went elsewhere. */
    var navigationCount = 0;

    /** Opens the (empty) page over the current Emby view and resolves with it. */
    function openPage() {
        if (page) {
            return Promise.resolve(page);
        }
        if (pageOpening) {
            return pageOpening;
        }
        var navigation = navigationCount;
        pageOpening = getCurrentView().then(function (view) {
            pageOpening = null;
            if (page) {
                return page;
            }
            if (navigation !== navigationCount) {
                return null; // The user went elsewhere meanwhile.
            }

            var element = document.createElement('div');
            element.className = 'ombi-page';
            var parent = view && view.parentNode ? view.parentNode : document.body;
            parent.insertBefore(element, view ? view.nextSibling : null);

            var current = { element: element, view: view };
            current.layout = function () {
                layoutPage(current);
            };
            window.addEventListener('resize', current.layout);
            if (window.ResizeObserver) {
                // Follows docking/undocking of the menu and header changes.
                current.observer = new ResizeObserver(current.layout);
                [view, document.querySelector('.mainDrawer'), document.querySelector('.skinHeader')].forEach(function (el) {
                    if (el) {
                        current.observer.observe(el);
                    }
                });
            }
            if (view) {
                view.style.visibility = 'hidden';
            }

            current.previousSelection = document.querySelector('.mainDrawer .navMenuOption-selected');
            if (current.previousSelection) {
                current.previousSelection.classList.remove('navMenuOption-selected');
            }
            var entry = document.querySelector('.ombi-drawer-entry');
            if (entry) {
                entry.classList.add('navMenuOption-selected');
            }
            current.title = setHeaderTitle(t('Requests'));

            // Over a series or movie page, its background picture and see-through header would stay
            // (client.css hides the picture while this class is set). Both come back on closing.
            document.body.classList.add('ombi-page-open');
            var header = document.querySelector('.skinHeader');
            current.transparentHeader = !!header && header.classList.contains('semiTransparent');
            if (current.transparentHeader) {
                header.classList.remove('semiTransparent', 'darkContentContainer');
            }

            // With a picture Emby also darkens its background (withBackdrop); without the picture that is
            // just black. The page gets the plain background, as when opened from the menu. Emby sets the
            // class again when it changes pictures (every 20 seconds), so it is taken off again right away.
            var background = document.querySelector('.backgroundContainer');
            if (background) {
                current.background = background;
                current.darkenedBackground = background.classList.contains('withBackdrop');
                background.classList.remove('withBackdrop');
                if (window.MutationObserver) {
                    current.backgroundObserver = new MutationObserver(function () {
                        if (background.classList.contains('withBackdrop')) {
                            current.darkenedBackground = true;
                            background.classList.remove('withBackdrop');
                        }
                    });
                    current.backgroundObserver.observe(background, { attributes: true, attributeFilter: ['class'] });
                }
            }

            element.innerHTML = '<div class="ombi-page-main"></div><div class="ombi-page-details hide"></div>';
            current.main = element.querySelector('.ombi-page-main');
            current.details = element.querySelector('.ombi-page-details');

            page = current;
            current.layout();
            return current;
        });
        return pageOpening;
    }

    /** The discover/search/my requests list (menu entry). */
    function showRequestsPage() {
        openPage().then(function (current) {
            if (!current) {
                return;
            }
            if (!current.mainRendered) {
                renderRequestsUi(current.main);
                current.mainRendered = true;
            }
            current.detailsRequest = null;
            current.details.classList.add('hide');
            current.details.innerHTML = '';
            current.main.classList.remove('hide');
        });
    }

    function layoutPage(current) {
        var rect = current.view ? current.view.getBoundingClientRect() : null;
        if (!rect || !rect.width || !rect.height) {
            rect = { top: 0, left: 0, right: window.innerWidth, height: window.innerHeight };
        }

        var left = rect.left;
        var dockedDrawer = document.querySelector('.mainDrawer.drawer-docked');
        if (dockedDrawer && dockedDrawer.offsetWidth) {
            left = Math.max(left, dockedDrawer.getBoundingClientRect().right);
        }

        // Start below the header: overlapping it (even transparently) would swallow clicks on its buttons.
        var header = document.querySelector('.skinHeader');
        var headerBottom = header && header.offsetHeight ? header.getBoundingClientRect().bottom : 0;
        var top = Math.max(rect.top, headerBottom);

        var style = current.element.style;
        style.top = top + 'px';
        style.left = left + 'px';
        style.width = Math.max(0, rect.right - left) + 'px';
        style.height = Math.max(0, rect.top + rect.height - top) + 'px';
    }

    function closeRequestsPage() {
        if (!page) {
            return;
        }
        var current = page;
        page = null;
        closeOverlay();

        window.removeEventListener('resize', current.layout);
        if (current.observer) {
            current.observer.disconnect();
        }
        current.element.remove();
        if (current.view) {
            current.view.style.visibility = '';
        }

        var entry = document.querySelector('.ombi-drawer-entry');
        if (entry) {
            entry.classList.remove('navMenuOption-selected');
        }
        if (current.previousSelection && current.previousSelection.isConnected) {
            current.previousSelection.classList.add('navMenuOption-selected');
        }
        restoreHeaderTitle(current.title);

        document.body.classList.remove('ombi-page-open');
        var header = document.querySelector('.skinHeader');
        if (current.transparentHeader && header) {
            header.classList.add('semiTransparent', 'darkContentContainer');
        }
        if (current.backgroundObserver) {
            current.backgroundObserver.disconnect();
        }
        if (current.darkenedBackground && current.background) {
            current.background.classList.add('withBackdrop');
        }
    }

    function setHeaderTitle(text) {
        var element = document.querySelector('.skinHeader .pageTitle');
        if (!element) {
            return null;
        }
        var saved = { element: element, text: element.textContent, className: element.className, style: element.getAttribute('style') };
        element.classList.remove('pageTitleWithLogo', 'pageTitleWithDefaultLogo');
        element.style.backgroundImage = '';
        element.textContent = text;
        return saved;
    }

    function restoreHeaderTitle(saved) {
        // Only if Emby has not set a new title in the meantime.
        if (!saved || saved.element.textContent !== t('Requests')) {
            return;
        }
        saved.element.textContent = saved.text;
        saved.element.className = saved.className;
        if (saved.style == null) {
            saved.element.removeAttribute('style');
        } else {
            saved.element.setAttribute('style', saved.style);
        }
    }

    // Another Emby view is shown (navigation). The covered view itself is "shown" again when a
    // dialog on top of it closes, which must not close the page.
    document.addEventListener('viewbeforeshow', function (e) {
        if (page && e.target === page.view) {
            return;
        }
        navigationCount++;
        closeOverlay();
        closeRequestsPage();
    }, true);

    // ---------- the requests channel in Emby's own pages ----------

    // Titles of the channel cannot be played, but Emby's web app shows play buttons for every video
    // and folder. Views of the channel (its folders and the detail pages of its titles) get a class
    // that hides them, see client.css. The other Emby apps decide this themselves.
    var channelViews = {};
    var viewItems = {};

    /**
     * The item a view shows. Emby passes the route's parameters with the event, and its own pages read
     * them there; the address bar can still show the previous page at that moment, so it is only the
     * fallback.
     */
    function viewItemId(e) {
        var params = e && e.detail && e.detail.params;
        if (params && (params.id || params.parentId)) {
            return String(params.id || params.parentId);
        }
        var hash = window.location.hash || '';
        var query = hash.indexOf('?') !== -1 ? hash.substring(hash.indexOf('?') + 1) : (window.location.search || '').replace(/^\?/, '');
        var match = /(?:^|&)(?:id|parentId)=([^&]+)/i.exec(query);
        return match ? decodeURIComponent(match[1]) : null;
    }

    function isChannelItem(item) {
        if (!item) {
            return false;
        }
        // Set by the plugin on titles and seasons (OmbiStatus) and on list folders (OmbiFolder).
        var keys = Object.keys(item.ProviderIds || {}).map(function (key) { return key.toLowerCase(); });
        return keys.indexOf('ombistatus') !== -1 || keys.indexOf('ombifolder') !== -1
            || (item.Type === 'Channel' && !!state.status && item.Name === state.status.ChannelName);
    }

    document.addEventListener('viewshow', function (e) {
        var view = e.target;
        if (!view || !view.classList || (page && view === page.view)) {
            return;
        }

        var id = viewItemId(e);
        view.__ombiItemId = id;
        // Emby reuses views: without a known answer the class goes, the buttons of other titles stay.
        view.classList.toggle('ombi-channel-view', !!id && channelViews[id] === true);
        view.querySelectorAll('.ombi-library-button').forEach(function (button) {
            button.remove();
        });
        if (id) {
            markView(view, id, 0);
        }
    }, true);

    /** Marks a view of the channel, or adds the plugin's buttons to a title of the library. */
    function markView(view, id, attempt) {
        var client = window.ApiClient;
        if (view.__ombiItemId !== id) {
            return; // Shows another item by now.
        }
        if (!client || !state.status) {
            // Right after the web app loaded, the plugin's status may still be on its way.
            if (attempt < 30) {
                setTimeout(function () {
                    markView(view, id, attempt + 1);
                }, 500);
            }
            return;
        }
        if (!state.status.WebIntegration) {
            return;
        }
        if (channelViews.hasOwnProperty(id)) {
            view.classList.toggle('ombi-channel-view', channelViews[id]);
            if (!channelViews[id] && pageEnabled()) {
                addLibraryButtons(view, viewItems[id]);
            }
            return;
        }

        client.getItem(client.getCurrentUserId(), id).then(function (item) {
            channelViews[id] = isChannelItem(item);
            viewItems[id] = item;
            markView(view, id, attempt);
        }, function () {
            // Not our business then.
        });
    }

    // ---------- titles in the library: missing episodes, problems ----------

    var LIBRARY_TYPES = ['Movie', 'Series', 'Season', 'Episode'];

    /** "Request missing episodes" (series) and "Report a problem" next to Emby's own buttons. */
    function addLibraryButtons(view, item) {
        if (!item || LIBRARY_TYPES.indexOf(item.Type) === -1 || !state.status.CanRequest) {
            return;
        }
        var id = item.Id;
        api('LibraryItem', { ItemId: id }).then(function (info) {
            var container = view.querySelector('.mainDetailButtons');
            // The view may show another item by now (Emby reuses views).
            if (!info || !container || view.__ombiItemId !== id || container.querySelector('.ombi-library-button')) {
                return;
            }
            if (info.MissingEpisodes > 0) {
                container.appendChild(libraryButton('playlist_add', t('RequestMissing', info.MissingEpisodes), function () {
                    showDetails({ MediaType: 'tv', TmdbId: info.TmdbId });
                }));
            }
            if (info.CanReportIssue) {
                container.appendChild(libraryButton('report_problem', t('ReportIssue'), function () {
                    openIssueDialog(item, info);
                }));
            }
        }, function () {
            // No buttons then; the page itself works as usual.
        });
    }

    function libraryButton(icon, text, onClick) {
        var button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('is', 'emby-button');
        button.className = 'fab detailButton detailButton-autotext ombi-library-button';
        button.title = text;
        button.setAttribute('aria-label', text);
        button.innerHTML = '<i class="md-icon detailButton-autotext-icon fab-icon">' + icon + '</i>' +
            '<div class="detailButton-autotext-text secondaryText">' + esc(text) + '</div>';
        button.addEventListener('click', onClick);
        return button;
    }

    var activeOverlay = null;

    /** Closes an open dialog (trailer, problem report), e.g. when the user navigates elsewhere. */
    function closeOverlay() {
        if (activeOverlay) {
            activeOverlay.close();
        }
    }

    /**
     * Shows a dialog over the page; Escape, a click beside it and the returned function close it.
     * Focus goes back to where it was.
     */
    function openOverlay(html, className) {
        closeOverlay();
        var previousFocus = document.activeElement;
        var backdrop = document.createElement('div');
        backdrop.className = 'ombi-dialog-backdrop' + (className ? ' ' + className : '');
        backdrop.innerHTML = html;
        document.body.appendChild(backdrop);

        var overlay = { element: backdrop, close: close };
        activeOverlay = overlay;

        function close() {
            document.removeEventListener('keydown', onKey, true);
            backdrop.remove();
            if (activeOverlay === overlay) {
                activeOverlay = null;
            }
            if (previousFocus && previousFocus.isConnected && typeof previousFocus.focus === 'function') {
                previousFocus.focus();
            }
        }
        function onKey(e) {
            if (e.key === 'Escape') {
                e.stopPropagation();
                close();
            }
        }
        document.addEventListener('keydown', onKey, true);
        backdrop.addEventListener('click', function (e) {
            if (e.target === backdrop) {
                close();
            }
        });
        return overlay;
    }

    /** YouTube video id of a trailer link ("https://www.youtube.com/watch?v=abc"), null for other sites. */
    function youTubeId(url) {
        var match = /(?:youtube(?:-nocookie)?\.com\/(?:watch\?(?:[^#]*&)?v=|embed\/)|youtu\.be\/)([\w-]{6,})/i.exec(url || '');
        return match ? match[1] : null;
    }

    /** Plays a trailer in a dialog over the page (like Emby's trailer button); other sites open in a new tab. */
    function openTrailer(url, title) {
        var id = youTubeId(url);
        if (!id) {
            window.open(url, '_blank', 'noopener');
            return;
        }
        var label = esc(t('TrailerOf', title || ''));
        var overlay = openOverlay(
            '<button type="button" class="ombi-trailer-close" title="' + esc(t('Close')) + '" aria-label="' + esc(t('Close')) + '"><i class="md-icon">close</i></button>' +
            '<div class="ombi-trailer" role="dialog" aria-modal="true" aria-label="' + label + '">' +
            '<iframe class="ombi-trailer-frame" title="' + label + '" src="https://www.youtube.com/embed/' + encodeURIComponent(id) + '?autoplay=1&rel=0"' +
            ' allow="autoplay; encrypted-media; fullscreen; picture-in-picture" allowfullscreen></iframe>' +
            '</div>', 'ombi-trailer-backdrop');
        var closeButton = overlay.element.querySelector('.ombi-trailer-close');
        closeButton.addEventListener('click', overlay.close);
        closeButton.focus();
    }

    function openIssueDialog(item, info) {
        var name = item.Type === 'Episode' && item.SeriesName ? item.SeriesName + ' – ' + item.Name : item.Name;
        var overlay = openOverlay('<div class="ombi-dialog" role="dialog" aria-modal="true" aria-labelledby="ombi-issue-title">' +
            '<h2 class="ombi-dialog-title" id="ombi-issue-title">' + esc(t('IssueTitle', name)) + '</h2>' +
            '<label class="ombi-field"><span>' + esc(t('IssueCategory')) + '</span><select class="ombi-issue-category">' +
            info.IssueCategories.map(function (c) { return '<option value="' + esc(c.Id) + '">' + esc(c.Name) + '</option>'; }).join('') +
            '</select></label>' +
            '<label class="ombi-field"><span>' + esc(t('IssueSubject')) + '</span><input type="text" class="ombi-issue-subject" maxlength="100"></label>' +
            '<label class="ombi-field"><span>' + esc(t('IssueDescription')) + '</span><textarea class="ombi-issue-description" rows="5" maxlength="2000"></textarea></label>' +
            '<div class="ombi-dialog-buttons">' +
            '<button type="button" class="emby-button ombi-dialog-cancel">' + esc(t('Cancel')) + '</button>' +
            '<button type="button" class="emby-button button-submit ombi-dialog-send">' + esc(t('Send')) + '</button>' +
            '</div></div>');
        var backdrop = overlay.element;
        var close = overlay.close;
        backdrop.querySelector('.ombi-dialog-cancel').addEventListener('click', close);
        backdrop.querySelector('.ombi-issue-subject').focus();

        var send = backdrop.querySelector('.ombi-dialog-send');
        send.addEventListener('click', function () {
            send.disabled = true;
            api('Issue', null, {
                ItemId: item.Id,
                CategoryId: parseInt(backdrop.querySelector('.ombi-issue-category').value, 10),
                Subject: backdrop.querySelector('.ombi-issue-subject').value,
                Description: backdrop.querySelector('.ombi-issue-description').value
            }).then(function (outcome) {
                toast(outcome.Message);
                if (outcome.Success) {
                    close();
                } else {
                    send.disabled = false;
                }
            }, function (err) {
                toast(t('Error', err.message));
                send.disabled = false;
            });
        });
    }

    // Menu entries and Emby's header buttons, also when they lead to the page that is currently covered.
    document.addEventListener('click', function (e) {
        if (!page || !e.target.closest) {
            return;
        }
        var target = e.target.closest('.mainDrawer .navMenuOption, .mainDrawer a, .headerHomeButton, .headerBackButton, .headerSearchButton, .btnNavDrawerLogo');
        if (target && !target.closest('.ombi-drawer-entry')) {
            closeRequestsPage();
        }
    }, true);

    // ---------- integration into Emby's UI ----------

    var HOME_ENTRY_SELECTOR = '.drawer-home, .navMenuOption[data-navmenuid="home"], .navMenuOption[data-navmenuid="/home"]';

    /** Puts "Requests" right below "Home" in the side menu, looking like Emby's own entries. */
    function ensureDrawerEntry() {
        var slider = document.querySelector('.mainDrawer .mainDrawerScrollSlider');
        if (!slider || !slider.children.length) {
            return;
        }

        var home = slider.querySelector(HOME_ENTRY_SELECTOR);
        var entry = slider.querySelector('.ombi-drawer-entry');

        // Built before "Home" existed: rebuild in Emby's style now.
        if (entry && home && !entry.__ombiNative) {
            entry.remove();
            entry = null;
        }
        if (!entry) {
            entry = createDrawerEntry(home);
        }

        if (home) {
            if (home.nextElementSibling !== entry) {
                home.parentNode.insertBefore(entry, home.nextSibling);
            }
        } else if (!entry.parentNode) {
            slider.appendChild(entry);
        }
    }

    var watchedDrawer = null;
    var drawerObserver = null;

    /**
     * Emby rebuilds its menu now and then (e.g. when it reloads the entries from the server), which
     * drops ours. The observer puts it back before the browser draws the menu again, so it does not
     * flicker; the timer of tick() would only bring it back up to a second later.
     */
    function watchDrawer() {
        var drawer = document.querySelector('.mainDrawer');
        if (!drawer || drawer === watchedDrawer || typeof MutationObserver === 'undefined') {
            return;
        }
        if (drawerObserver) {
            drawerObserver.disconnect();
        }
        watchedDrawer = drawer;
        drawerObserver = new MutationObserver(function () {
            if (pageEnabled()) {
                ensureDrawerEntry();
            }
        });
        drawerObserver.observe(drawer, { childList: true, subtree: true });
    }

    /**
     * Like Emby's own menu entries: only an overlay menu (state Open = 1) closes after a click.
     * A docked (2) or mini-docked (3) menu stays as the user set it.
     */
    function closeDrawerIfOverlay() {
        importModule('./modules/navdrawer/navdrawer.js').then(function (drawer) {
            if (drawer && typeof drawer.getDrawerState === 'function' && drawer.getDrawerState() === 1 && typeof drawer.close === 'function') {
                drawer.close();
            }
        }, function () { });
    }

    function createDrawerEntry(home) {
        var entry = document.createElement('button');
        entry.type = 'button';

        if (home) {
            // Same classes and inner markup as "Home", but a plain button: Emby's item container
            // and link handling must not treat it as one of its items.
            entry.className = home.className;
            entry.classList.remove('drawer-home', 'navMenuOption-selected');
            entry.innerHTML = home.innerHTML;
            var icon = entry.querySelector('.md-icon');
            if (icon) {
                icon.textContent = 'playlist_add';
            }
            var text = entry.querySelector('.listItemBodyText') || entry.querySelector('.navMenuOptionText');
            if (text) {
                text.textContent = t('Requests');
            }
            entry.querySelectorAll('[data-index],[data-action],[data-id]').forEach(function (el) {
                el.removeAttribute('data-index');
                el.removeAttribute('data-action');
                el.removeAttribute('data-id');
            });
            entry.__ombiNative = true;
        } else {
            entry.innerHTML = '<i class="md-icon">playlist_add</i><span>' + esc(t('Requests')) + '</span>';
        }

        entry.classList.add('ombi-drawer-entry');
        entry.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            closeDrawerIfOverlay();
            showRequestsPage();
        });
        return entry;
    }

    function visibleSearchInput() {
        var inputs = document.querySelectorAll('.searchfields-txtSearch');
        for (var i = 0; i < inputs.length; i++) {
            if (inputs[i].offsetParent !== null) {
                return inputs[i];
            }
        }
        return null;
    }

    /**
     * Adds a "Request via Ombi" row between Emby's search field and its results. Works with both
     * search pages: list/list.html?type=search (desktop, has .searchContainer) and search/search.js
     * (TV layout, .searchFields followed by .sections).
     */
    function ensureSearchHook() {
        var input = visibleSearchInput();
        if (!input) {
            return;
        }
        var hook = input.__ombiHook;
        if (!hook || !hook.container.isConnected) {
            var anchor = input.closest('.searchContainer') || input.closest('.searchFields');
            if (!anchor || !anchor.parentNode) {
                return;
            }
            hook = createSearchHook(input, anchor);
            input.__ombiHook = hook;
        }
        // Polled as well, because the menu's search box sets the term without an input event.
        hook.check();
    }

    function createSearchHook(input, anchor) {
        var container = document.createElement('div');
        container.className = 'ombi-search-section verticalSection hide';
        if (anchor.classList.contains('padded-left')) {
            container.classList.add('padded-left', 'padded-right');
        }
        container.innerHTML = '<h2 class="sectionTitle">' + esc(t('SearchSectionTitle')) + '</h2>' +
            '<div class="ombi-search-results"></div>' +
            '<div class="ombi-search-message"></div>';
        anchor.parentNode.insertBefore(container, anchor.nextSibling);

        var results = container.querySelector('.ombi-search-results');
        var message = container.querySelector('.ombi-search-message');
        bindCardClicks(results);

        var lastQuery = null;
        var timer = null;
        var version = 0;

        function update(query) {
            var current = ++version;
            results.innerHTML = '';
            message.textContent = '';
            if (query.length < 2) {
                container.classList.add('hide');
                return;
            }
            container.classList.remove('hide');
            message.className = 'ombi-search-message ombi-loading';
            message.textContent = t('Loading');

            api('Search', { Query: query, Type: 'all' }).then(function (items) {
                if (current !== version) {
                    return;
                }
                // Titles already in the library show up in Emby's own results.
                var missing = items.filter(function (i) {
                    return !i.EmbyItemId;
                });
                message.textContent = '';
                container.classList.toggle('hide', !missing.length);
                renderCards(results, missing, 'row');
            }, function (err) {
                if (current !== version) {
                    return;
                }
                console.warn('[Ombi Requests] search failed:', err.message);
                message.className = 'ombi-search-message ombi-message ombi-error';
                message.textContent = t('Error', err.message);
            });
        }

        function check() {
            var query = input.value.trim();
            if (query === lastQuery) {
                return;
            }
            lastQuery = query;
            clearTimeout(timer);
            timer = setTimeout(function () {
                update(query);
            }, 500);
        }

        input.addEventListener('input', check);
        return { container: container, check: check };
    }

    /** An admin can switch the requests page off for single users; the channel's pages are still marked. */
    function pageEnabled() {
        return !!state.status && state.status.Configured && state.status.WebIntegration && state.status.PageEnabled !== false;
    }

    function installUi() {
        if (!pageEnabled()) {
            document.querySelectorAll('.ombi-drawer-entry').forEach(function (entry) {
                entry.remove();
            });
            return;
        }
        ensureDrawerEntry();
        watchDrawer();
        // Users who cannot request (no account) still get the page, which explains why, but no
        // error row under every Emby search.
        if (state.status.CanRequest) {
            ensureSearchHook();
        }
    }

    // ---------- bootstrap ----------

    function loadStatus(userId) {
        state.statusRetryAt = Date.now() + 30000;
        api('Status').then(function (status) {
            if (state.userId !== userId) {
                return;
            }
            state.status = status;
            state.lang = status.Language === 'de' ? 'de' : 'en';
            if (!status.Configured || !status.WebIntegration) {
                console.info('[Ombi Requests] web integration is disabled in the plugin settings.');
            } else if (status.CanRequest) {
                console.info('[Ombi Requests] ready, requesting as Ombi user ' + status.OmbiUser);
            } else {
                console.warn('[Ombi Requests] ' + status.Message);
            }
        }, function (err) {
            console.warn('[Ombi Requests] status check failed, retrying in 30s:', err.message);
        });
    }

    function tick() {
        var client = window.ApiClient;
        var userId = client && typeof client.getCurrentUserId === 'function' ? client.getCurrentUserId() : null;

        if (userId !== state.userId) {
            state.userId = userId;
            state.status = null;
            channelViews = {};
            viewItems = {};
            if (userId) {
                loadStatus(userId);
            }
        } else if (userId && !state.status && Date.now() > state.statusRetryAt) {
            loadStatus(userId);
        }

        if (state.status && state.status.Configured && state.status.WebIntegration) {
            installUi();
        }
    }

    setInterval(tick, 1000);
    tick();
})();
