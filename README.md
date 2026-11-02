# Ombi Requests for Emby

Ombi Requests is an Emby Server plugin that lets users request movies and series through [Ombi](https://ombi.io) without leaving Emby. In the Emby web app it adds a "Requests" page to the side menu (discover lists, search, title details, "My requests" and, for Ombi admins, "All requests") and a request section below Emby's search field. In all other Emby apps (TV, mobile, Theater) it adds a "Requests" channel in which a movie or series is requested by marking it as a favorite. Every request is made as the user's own Ombi account, so Ombi's permissions, quotas and auto-approval apply. Users never see the name "Ombi".

Version 0.9.0.

## Contents

- [Requirements](#requirements)
- [Installation](#installation)
- [Disclaimer](#disclaimer)
- [Features](#features)
- [How it works](#how-it-works)
- [Settings](#settings)
- [Data files](#data-files)
- [Plugin API](#plugin-api)
- [Security notes](#security-notes)
- [Known limitations](#known-limitations)
- [Project structure](#project-structure)
- [Building](#building)
- [Tests](#tests)
- [License](#license)

## Requirements

- **Emby Server 4.9.1 or newer.** The plugin is built against the Emby SDK 4.9.1.90 (`MediaBrowser.Server.Core`) and tested on Emby 4.10.2.
- **Ombi v4**, reachable from the Emby server, and its API key.
- For building from source: a .NET SDK (the plugin targets .NET Standard 2.0). The tests need the **.NET 10 SDK**.

## Installation

1. Download `OmbiRequests.dll` from the [releases page](https://github.com/Philipp-AT/emby-ombi-requests/releases).
2. Copy it into Emby's plugins folder. In the official Docker image (`emby/embyserver`) that is `/config/plugins`, i.e. inside the mounted config volume. On other installations it is the `plugins` folder in Emby's program data folder.
3. Restart Emby (in Docker: restart the container).
4. Configure the plugin under *Dashboard → Server → Ombi Requests* (also reachable from Emby's plugin list).

### First-time setup

1. Enter the **Ombi URL** as the Emby server reaches it, e.g. `https://ombi.example.com`, or `http://ombi:3579` when both containers share a Docker network. Include a base path if Ombi uses one.
2. Enter the **API key** (*Ombi → Settings → Ombi → API Key*) and click **Test connection**.
3. Check that your users are matched to Ombi users (see [Who requests](#who-requests-user-mapping)). Most are matched automatically.
4. For the web app, turn on **Load the script automatically** and save, or let your reverse proxy add the script tag (see below). Reload Emby in the browser once with **Ctrl+F5**. "Requests" now appears in the side menu right below "Home".
5. Optional: to let Emby's search find titles that are not in Emby yet, in all apps, set **Movies to import** / **Series to import** and click **Save and start import** (see ["All movies" and "All series"](#all-movies-and-all-series)).

The channel, the poster banners and requesting with the heart work as soon as steps 1 and 2 are done, even without the script tag.

### Adding the script to the web app

The web app needs a script tag before `</body>` in Emby's `/web/index.html`:

```html
<script src="/OmbiRequests/web/client.js" defer></script>
```

A plugin can neither add `/web/...` routes nor rewrite Emby's responses (see [Why not a normal plugin page?](#why-not-a-normal-plugin-page)), but Emby can serve the whole web app from another folder. The plugin uses that with one setting; a reverse proxy is the alternative.

**Normally: Load the script automatically.** Turn the setting on and save. The plugin copies Emby's web client to `ombirequests-webapp` in Emby's data folder (`/config/data/ombirequests-webapp` in Docker, about 35 MB), adds the script tag to the copy's `index.html` and points Emby's server option `DashboardSourcePath` to the copy. Emby then serves the web app from the copy; "Script injection" below the setting shows whether it is active.

- The copy belongs to one Emby version and one plugin build, so the setting survives updates: after an Emby or plugin update, the plugin switches Emby back to its original web app at startup, before Emby serves anything, and rebuilds the copy in the background (a few seconds).
- Emby normally serves a few scripts from `Emby.Web.dll` instead of from disk; with `DashboardSourcePath` it reads everything from disk, so the plugin writes them into the copy.
- Browsers that already cached the web app need **one Ctrl+F5**, because Emby's ETag for `index.html` only depends on the Emby version and language.
- If `DashboardSourcePath` is already set to something else, the plugin changes nothing and says so under "Script injection". If building the copy fails, Emby keeps its original web app and the error is in Emby's log.
- Turning the setting off points Emby back to its original web app and deletes the copy. Uninstalling the plugin from Emby's plugin list does the same.

> **Do not just delete the DLL while the setting is on.** Turn off "Load the script automatically" and save first, or uninstall the plugin from Emby's plugin list. If the DLL is already gone, clear the `<DashboardSourcePath>` entry in `config/system.xml` (Docker: `/config/config/system.xml`), restart Emby and delete the `ombirequests-webapp` folder. Otherwise Emby keeps serving the copy, which is outdated after the next Emby update.

**Alternative: the reverse proxy.** If you prefer not to change `DashboardSourcePath`, or it is already used for something else, leave **Load the script automatically** off and let the proxy rewrite `index.html` on the way to the browser.

nginx / Nginx Proxy Manager (in NPM: *Proxy Host → Advanced → Custom Nginx Configuration*):

```nginx
location = /web/index.html {
    proxy_pass $forward_scheme://$server:$port;   # plain nginx: e.g. proxy_pass http://emby:8096;
    proxy_set_header Host $host;
    proxy_set_header Accept-Encoding "";          # uncompressed, otherwise sub_filter does not apply
    sub_filter '</body>' '<script src="/OmbiRequests/web/client.js" defer></script></body>';
    sub_filter_once on;
}
```

- **Traefik** needs a body rewrite plugin, e.g. [traefik/plugin-rewritebody](https://github.com/traefik/plugin-rewritebody). As a middleware on `/web/index.html` it replaces `</body>` with the line above.
- **Caddy** works with the module [caddyserver/replace-response](https://github.com/caddyserver/replace-response): `replace </body> "<script src=\"/OmbiRequests/web/client.js\" defer></script></body>"` for the path `/web/index.html`.

To uninstall with this option, delete the DLL and restart Emby. A script tag left in the proxy does no harm: the script is not found and simply does not load.

**Quick test without any configuration:** open Emby in the browser, sign in, open the developer console (F12) and run:

```js
var s = document.createElement('script'); s.src = '/OmbiRequests/web/client.js'; document.head.appendChild(s);
```

The "Requests" menu entry then stays until the next page reload.

## Disclaimer

This is an independent community project. It is not affiliated with, endorsed by or supported by Emby or Ombi. "Emby" and "Ombi" are the names of their respective projects and owners.

## Features

| Where | What |
|---|---|
| **Web app (browser)** | A "Requests" entry in the side menu, right below "Home", opens the requests page in the content area; menu and header stay. Tabs *Discover* (Popular movies, Trending series, Now in cinemas, Upcoming movies, Popular series) and *My requests*, for Ombi admins also *All requests*, plus a search field for movies and series. A title opens a details page modelled on Ombi's: backdrop, poster, rating, runtime, dates, status, genres, overview, info tiles, keywords, cast, and for series the seasons and episodes with their status. Whole series ("Request all seasons"), seasons or single episodes ("Request selection (n)") can be requested. If Ombi has a trailer, *Trailer* plays it in a dialog. Titles already in the library open their Emby page directly: movies always, series when they are complete. Partly available series show the details page with "Watch in Emby". |
| **Emby's search (browser)** | Below Emby's search field a section "Not in Emby yet – request" lists matching titles that are not in the library yet. |
| **All Emby apps** (TV, mobile, Theater, web) | A channel "Requests" under *My media* with Ombi's discover lists as folders. The admin chooses which lists and which request states it shows (e.g. without titles that are already available). A title may be in several folders; Emby's search still shows it once. Folders without visible titles (e.g. everything is in Emby already) are hidden until there is something to request again. Movies and series open Emby's usual details page with background picture and, where Ombi has one, a trailer. Series have no seasons or episodes there: the heart requests the whole series, single seasons and episodes are requested in the web app. A movie or series is requested by marking it as **favorite (heart)**; Emby then shows a message with the result. A banner on the poster shows the state ("Not in Emby yet / Favorite it to request", "Requested", "Approved", ...); the description contains only the plot. The folders **"All movies"** and **"All series"** let Emby's search in all apps find titles that are not in Emby yet. |
| **Library pages (browser)** | On a library series: **"Request missing episodes (n)"** when Ombi knows episodes that are neither in Emby nor requested. It opens the details page with season and episode selection. On movies, series, seasons and episodes: **"Report a problem"** with category, subject and description. The report goes to Ombi's issues as the user and is only offered when issues are enabled in Ombi. |
| **"Now available" messages** | When a request becomes available, the requester gets an Emby message ("Now available": *"Dune" is now available in Emby.*). Users who are offline get it the next time they open an Emby app. |
| **Quota** | If Ombi limits the user's requests, the requests page shows e.g. "3 of 10 movie requests left", and the confirmation after a request names the remaining ones. Without limits nothing is shown. |
| **Withdrawing** | *My requests* offers *Withdraw* for requests that wait for approval, and also for approved ones that are not available yet when **Also withdraw approved requests** is on (the server decides this per request, field `CanWithdraw`). In the channel, removing the heart withdraws a request that was made with the heart. See [Several users want the same title](#several-users-want-the-same-title). |
| **All requests (Ombi admins)** | For users whose mapped Ombi user has the role Admin or PowerUser in Ombi: the requests of all users, with a text filter (title, user), a status filter (All except available, Waiting for approval, Approved, Denied, Available, All incl. available) and a sort order (Newest first, Oldest first, Title A–Z, Requested by, Status). Each row shows who requested the title and who else wants it. *Approve* for waiting and denied requests, *Deny* (optional reason) for waiting and also for already approved ones, *Delete* (asks "Really delete?") for all. Available requests are only loaded when selected. |
| **Settings** | Admin page under *Dashboard → Server → Ombi Requests*, with a diagnostics section (language of channel and catalog, last channel lists, catalog trailers and backgrounds, availability messages, problem reports, last error). |

### Who requests: user mapping

Requests always run as the user's Ombi account: the plugin sends the API key plus Ombi's `UserName` header, so Ombi applies that user's permissions, quotas and auto-approval. Emby users are matched to Ombi users in this order:

1. the manual mapping in the settings (**User mapping**),
2. the same user name,
3. the Emby Connect name or e-mail (Ombi's import creates Emby Connect users with their e-mail as user name),
4. an Ombi alias equal to the Emby name, but only if exactly one Ombi user has that alias.

Users without a match cannot request ("Requests are not enabled for your account ... yet") unless **Allow users without Ombi account** is on.

### Admin rights come from Ombi

*All requests* (approve, deny, delete) is available to users whose mapped Ombi user has the role **Admin** or **PowerUser** in Ombi, as in Ombi itself. Whether someone is an Emby administrator does not matter. The role is checked on every call; changes in Ombi take effect within about 5 minutes (the plugin keeps Ombi's user list that long). Only the plugin's settings page is reserved for Emby administrators, because Emby manages it.

Because these rights follow the mapping, keep it correct: an Emby user mapped to an Ombi admin can manage all requests.

Per user, the admin decides in the plugin settings who gets what:

- **Requests page (web app)**: the menu entry, the section below Emby's search and the buttons on library pages.
- **Requests folder (all apps)**: the channel. This setting is Emby's channel access in the user's policy (*Users → Access → Channels*); the plugin changes it there.

### Several users want the same title

In Ombi a request belongs to exactly one user; whoever requests the same title later only gets "already requested". So that one user withdrawing does not take the title away from the others, the plugin keeps its own list of every Emby user who wanted a title, with the heart or on the requests page, also when Ombi answered "already requested" (`ombirequests-interest.json`).

- Wishes are tracked per title, season and episode, so wanting season 2 does not keep somebody else's season 1.
- A request stays while somebody else still wants a part of it (one of its seasons or episodes); the user who withdrew is told that it "stays requested because someone else wants it too".
- It goes once nobody wants any part of it any more, no matter who owns it in Ombi and in which order the users withdrew (as long as it can still be withdrawn: waiting for approval, or approved but not available with **Also withdraw approved requests**).
- If the original requester wants the title again after withdrawing, the request stays theirs.
- Withdrawing from someone else's request only removes your own wish; their request stays.
- Requests made directly in Ombi are never deleted by someone else's withdrawal.
- Requests, withdrawals and deletions of one title run one after another, because each one decides from Ombi's requests and the wishes that the others change.
- When an admin deletes the last request of a title, the plugin forgets all wishes for it, so a new request starts over.

## How it works

### Why not a normal plugin page?

Emby (checked with 4.10.2 and the 4.11 beta) shows plugin pages only to administrators:

- The web client's route `/configurationpage` requires `roles: "admin"`.
- The GenericUI service is protected with `[Authenticated(Roles = "admin")]`. `EnableInUserMenu` shows a menu entry, but the page does not work for normal users.
- Emby silently drops `/web/...` routes from plugins (`ServiceController.RegisterRestPaths` accepts them only from `Emby.Web.dll`), and plugins get no request or response filters. A plugin therefore cannot change the `index.html` that Emby serves from its own web folder. What it can do is point Emby's server option `DashboardSourcePath` to another folder.

So the plugin takes two routes:

1. **Web app:** the plugin serves `/OmbiRequests/web/client.js` (and `client.css`). One `<script>` tag in Emby's `index.html` loads it. The plugin adds it to its own copy of the web app and lets Emby serve that copy (**Load the script automatically**); alternatively a reverse proxy adds it (see [Installation](#adding-the-script-to-the-web-app)).
2. **Apps:** a channel plus the favorite event. Both are official plugin interfaces and work in every client. Emby lists channel content without a user (it passes none), so the lists are the same for everybody; the favorite event knows the user, so that is where requests happen. The poster banners come from an `IImageEnhancer` that lays pre-rendered banners (`src/OmbiRequests/Images`, generated with `tools/generate-images.ps1`) over the poster with Emby's own SkiaSharp. No text is drawn at runtime, so the container needs no fonts.

### The requests channel

**Refreshing.** Emby does not read channel content when a user opens it, only when its daily task "Refresh Internet Channels" runs or the channel reports a change (`IHasChangeEvent`). The plugin reports one 1 minute after server start, then every 3 hours, 10 minutes after a request, withdrawal or admin decision (several within that time cause one refresh), and a few seconds after saving the settings, after a catalog import or update, or after the task "Look up missing trailers and backgrounds" found something. Every refresh reads the whole channel.

A refresh requested while one is running is not lost, Emby queues it. But it would be a second full pass right behind the first (thousands of folder reads), so the plugin waits until Emby is done reading the channel (Emby's channel task idle and no folder read for 2 minutes) and only then reports the change.

**Failures never empty the channel.** If a list cannot be loaded from Ombi, its folder keeps the titles of the last successful load. These last good lists are kept in `ombirequests-channel-lists.json`, so they survive a restart (Ombi may start later than Emby). The channel never answers a failure with an empty folder, because Emby would then delete everything below it, including the users' hearts, and create it all again next time. Instead it throws an error, Emby keeps its items, and the plugin retries the refresh 5 minutes later.

**Only changed items are rewritten.** Emby updates an existing channel item only when the channel marks it with `ForceUpdate`. Such writes are not announced to the apps (Emby's change notifier skips items with an `ExternalId`). The real cost is SQLite writes plus an entry in Emby's metadata refresh queue per item, which adds up with thousands of titles. Therefore:

- A title is only force-updated when what Emby last got changed: for the lists its state, name, poster, background, trailer and texts (recorded in `ombirequests-channel-listed.json`), for "All movies"/"All series" its state, poster, background and trailer (recorded in the catalog file). Both hold after a restart.
- Ratings are rounded to one decimal, as Emby shows them; TMDb's average moves a little every day, and every change would rewrite the item.
- After a request, withdrawal, approval, denial or deletion (heart or web app) the plugin writes the new state into all channel items of the title in the background. It finds them with a query on an indexed key (`PresentationUniqueKey`), because Emby has no index on `ExternalId`. The banner shows the new state as soon as an app loads the title again, without waiting for the next refresh.

**Name.** The channel is called "Requests" (German: "Anfragen"). It keeps the name it was registered with first (stored as `ChannelRegisteredName` in the plugin configuration), because Emby derives the channel id from its name: another name would create a second channel and drop all items and hearts of the first one. Switching the language later does not rename it.

**Switching it off.** **Requests channel** turns the channel off for the whole server. Emby has no interface for that, so the channel deliberately fails when Emby creates it, and Emby skips it (`ApplicationHost.CreateInstanceSafe`). This logs one "Error creating … OmbiChannel" entry per start, which is intended. Switching it off while Emby runs removes the channel from the library immediately; switching it back on needs an Emby restart if it was off when Emby started. Users can also hide the channel in their home screen settings, and admins can restrict it per user (**Requests folder (all apps)**).

**Folder pictures.** The folders have their own pictures (`Images/folder-*.png`, `letter-*.png`); otherwise Emby would show the poster of a title inside. Emby accepts channel images only as URL or file, so the plugin writes them to `ombirequests-images/` in Emby's data folder.

**Movies** are Emby movies, so the TV apps show their details page with the background, like for series (for plain videos they show the poster instead). They carry no TMDb id: with one, Emby refreshed them from TMDb including collections, and automatic collections or other plugins then treated titles that are not in the library as real movies. Movie entries of older versions were plain videos; Emby never changes the type of an item, so the plugin removes them once at the start of a channel refresh and the refresh creates them again as movies (hearts on them are not carried over).

**Series** are real Emby series (`ChannelFolderType.Series`), so every app shows the usual series page with background and heart. They have no seasons or episodes: popular lists contain daily shows with thousands of episodes, and every item costs database writes and a metadata refresh, which can slow down all of Emby. The heart requests the whole series; single seasons and episodes are requested in the web app. Emby would look series up at TMDb by name after every refresh and add ids, pictures and collections; the channel therefore provides its own library options without metadata and image fetchers (`IHasChannelFeatures`), which Emby uses instead of its defaults. Each copy of a series has its own id per list (`ombirequests|tv|1399|tv:popular`), as in earlier versions, so existing items and their hearts stay.

**Play button.** Emby clients show a play button on every video and folder. A plugin cannot turn it off, and channel titles have nothing to play ("No compatible streams"). In the web app, `client.js` hides Play, Shuffle, Resume, Queue, Download and "Played" on the channel's pages: the pages of movies and series, the folders and the channel's home page. It recognizes them by the provider ids `OmbiStatus` / `OmbiFolder` (the channel itself by its name), and reads which item a page shows from the data of the `viewshow` event, like Emby's own pages do, because the address bar may still show the previous page at that moment. In the other apps, and for channel titles in Emby's search results, the button stays.

**Trailers.** Titles get Ombi's trailer link (for movies without one, the first YouTube trailer from Ombi's movie details) as `RemoteTrailers`, so Emby's trailer button works in all apps. Ombi's movie lists contain no trailer link:

- For the titles of the lists, the plugin takes it from the details (cached for a day).
- Catalog titles get their trailer during import and update. The catalog uses 2 parallel lookups, so users' requests always find free Ombi slots.
- Whatever is still missing (Ombi was unreachable, or the catalog is older) is filled in hourly, 250 titles per hour, movies first, or all at once with **Look up missing trailers and backgrounds now** (Emby task "Look up missing trailers and backgrounds", which refreshes the channel when it found something). Trailers and backgrounds from the hourly run reach Emby with the next regular channel refresh.

**Backgrounds.** A channel item can only bring a poster. Without a background, the TV apps show the poster on a grey page. Movies and series therefore carry Ombi's TMDb background (provider id `OmbiBackdrop`), and the plugin sets it as the item's backdrop when the item has none yet. A background once set stays, because Emby replaces the link with its own downloaded copy. The TV apps hide the poster on details pages by default, so the status banner is not visible there; **Show a poster on the detail screen** in the app's display settings adds the poster with banner. List titles bring theirs from Ombi's lists. Catalog titles get it during import and update, and catalogs of older versions get it through the same hourly lookup as the trailers.

**No "Ombi" for users.** The channel and its pictures are called "Requests", and technical errors are reported neutrally (e.g. "The request service cannot be reached at the moment."). The details are in Emby's log.

**Language and posters.** Ombi returns titles, texts and posters in the language of the requesting user, and in English for the API key alone. The requests page runs as the user. Channel and catalog have no user, so they load as an Ombi user with the configured language (preferably an admin, and only a user name that fits into an HTTP header, i.e. plain ASCII). Both therefore show the same posters; the diagnostics show which Ombi user is used. Emby takes a channel item's image only when it creates the item, so the plugin remembers per item which poster it set (`ombirequests-posters.json`) and swaps the picture when Ombi delivers a different one.

### Request states

States are Not requested, Requested, Approved, Partially available, Available and Denied. A title counts as in the library when Emby has an item with the same TMDb, IMDb or TVDB id. For series, Ombi's own "available" only means that the series is in Emby at all; the plugin counts a series as available when all aired episodes of its regular seasons are in Emby (specials and future episodes do not count). Ombi's series lists come without seasons, so for series from these lists that are in the Emby library, the plugin checks the details (cached for a few hours) to see whether episodes are missing.

### Talking to Ombi

- Lists are loaded in pages of 20 titles, like in Ombi's own UI. Ombi looks up every title of a page at TMDb, which can take long for series, so list pages (and Ombi's full request lists) may take up to 2 minutes, other calls 30 seconds. The pages of a discover list load in parallel, during the catalog import one after another; if a later page fails, the titles loaded so far are kept. Emby's log shows per list how many titles were loaded and shown ("Requests channel: tv:trending has …").
- At most **4 calls** go to Ombi at the same time; further calls wait.
- Reads are retried after 1 and 3 seconds (or after the proxy's `Retry-After`, at most 10 seconds) when Ombi or a reverse proxy in front of it answers **HTTP 429, 502, 503 or 504** (e.g. right after a restart, or because of a limit on parallel connections). Requests, approvals, denials and deletions are never sent twice.
- **Redirects are not followed**, because they would carry the API key to wherever they point. A redirecting Ombi URL is reported in the settings ("Connection", "Last error") with the target address; enter that address instead.
- The requests page keeps list pages fresh for 15 minutes. After that it shows the stored version for up to 6 hours right away and loads a new one in the background. The channel only takes pages that are at most 15 minutes old. A request through the plugin drops the cached pages and details of that title.

### "All movies" and "All series"

These two folders contain a large catalog, mainly so that Emby's search in all apps also finds titles that are not in Emby yet. It consists of the most popular and the best rated titles at TMDb (two thirds and one third of the configured number), plus what is in cinemas, announced or trending. There is no upper limit: if that is not enough, the plugin continues these lists (TMDb serves at most 10,000 titles per list), for series also "trending" and "anticipated", and then goes through the movies year by year with Ombi's advanced search (round by round over all years, best known titles first). Ombi cannot search series by year, so series stop when the lists are exhausted; the log then says "Ombi delivered … of the requested …". Every title becomes an item in Emby's database and takes about 1 KB in the catalog file.

Loading thousands of titles from Ombi takes a while, so it happens in two steps:

1. **Import (once, manually):** set the numbers in the settings and click **Save and start import**. The import runs as the Emby task "Import all movies and series" (progress under *Dashboard → Scheduled tasks*). 5,000 movies or 1,500 series take roughly 15 to 30 minutes. Titles are loaded in the configured language; if the language changes, the status line suggests a new import. A new import replaces the catalog; if Ombi delivers neither of the two main lists of a type (popular, best rated), the previous titles of that type stay.
2. **Updates (automatic):** afterwards the plugin loads Ombi's current lists every *n* days (**Load new titles every ... days**, default 7): movies now in cinemas, upcoming and popular; series trending, anticipated and popular; 200 titles each. Only titles that are not in the catalog yet are added, so no date is needed. The plugin checks hourly whether an update is due; a failed one is tried again after 6 hours. To run it by hand, click **Load new titles now** or start the task "Load new movies and series".

The catalog is stored in `ombirequests-catalog.json` in Emby's data folder. The request states are recomputed on every channel refresh from Ombi's request lists of all users and the Emby library: a few calls instead of one per title. The plugin remembers per title which state, poster, background and trailer Emby last got and force-updates only titles where one of them changed, so Emby does not rewrite thousands of items on every refresh.

"All movies" and "All series" contain all titles, including those of "Popular movies", "Now in cinemas" and so on, split into sub-folders A–Z and # (digits, symbols, other scripts). The split is needed because on every refresh Emby compares each item of a channel folder with every other one, which takes very long with tens of thousands of items in one folder.

Emby creates one item per folder, so a movie in "Popular movies" and "All movies" exists twice. Emby's search merges items with the same `PresentationUniqueKey` (for channel items this is normally the item's own id). The plugin gives all items of a title the same key (`ombirequests|movie|603`), so the search shows it once. Checked in Emby's code (4.10): all apps search via `/Items?SearchTerm=…`; `/Search/Hints` is retired.

The folders appear after the first import.

## Settings

The settings page is grouped like the sections below. Setting names are as shown in Emby.

### Ombi server

| Setting | Default | Description |
|---|---|---|
| Ombi URL | – | Address of Ombi as seen from the Emby server, e.g. `https://ombi.example.com` or `http://ombi:3579`, including a base path if Ombi uses one. |
| API key | – | *Ombi → Settings → Ombi → API Key*. Grants admin access to Ombi. It stays on the Emby server and is never sent to the browser; the page shows "(unchanged)" instead. |
| Test connection | – | Button. Checks URL and API key; "Connection" shows the number of Ombi users or the error, e.g. a redirect target. |

### Users

| Setting | Default | Description |
|---|---|---|
| User mapping | empty | Optional, one line per exception: `EmbyUser=OmbiUser`. The right side may be an Ombi user name, e-mail or alias. |
| Allow users without Ombi account | off | Such users request with the API key and their Emby name as alias. **Warning:** Ombi treats these as admin requests (no quota, auto-approval). |
| Requests page (web app) | all users | Users who get the requests page in the web app: the menu entry, the results below Emby's search and the buttons on library pages. Users created later get it too. For deselected users the entry disappears and the plugin API refuses their calls; the web app still hides the play buttons in the channel for them. |
| Requests folder (all apps) | Emby's channel access | Users who see the requests folder and can request with the heart. This is Emby's channel access of the user (*Users → Access → Channels*); the plugin shows and changes it there. A user who had access to all channels gets the list of all other channels when deselected, and "all channels" again when selected again. Only possible while the channel is on. |

### Integration

| Setting | Default | Description |
|---|---|---|
| Web app integration | on | Turns the web app UI (menu entry, search section, library buttons) on or off, even when the script tag is present. |
| Load the script automatically | off | Serves Emby's web app from a copy with the script tag (`DashboardSourcePath`), see [Installation](#adding-the-script-to-the-web-app). "Script injection" below it shows whether the copy is active. Turn it off before removing the plugin. |
| Language | Automatic (server language) | Language of the request UI, the channel and the notifications: Automatic (server language), English or Deutsch. Channel and catalog also load titles from Ombi in this language. |

### Requests channel (all Emby apps)

Changes in this section are applied within a few seconds after saving.

| Setting | Default | Description |
|---|---|---|
| Requests channel | on | Switches the channel on or off for the whole server (see [The requests channel](#the-requests-channel)). The "Channel" status shows whether a restart is needed. |
| Request via favorites | on | Marking a movie or series in the channel as favorite requests it (a series as a whole). |
| Status banner on posters | on | Shows on the posters whether a title is missing (and can be requested with the heart), requested, approved, denied or available. |
| Keep other poster overlays off the channel | on | Plugins such as Cover Art put frames, bars or badges on all posters, including the channel's. With this on, they leave out the posters and folder pictures of the channel and keep working everywhere else. Emby applies all image enhancers one after another in load order; at startup the plugin wraps every other enhancer so that it does not take channel items. |
| Withdraw by removing the heart | on | Removing the heart withdraws a request that was made with the heart, as long as it waits for approval (with the next setting also when it is approved but not available yet). Denied and available requests stay. |
| Also withdraw approved requests | off | Withdrawing (heart or *My requests*) also takes requests that are approved but not available yet. Ombi then forgets them, but Radarr/Sonarr keep what they already got; remove it there if needed. A request always stays while another user wants it too. |
| Tell users when their request is available | on | Checks Ombi's requests every 15 minutes and sends the requester an Emby message; users who are offline get it the next time they open an Emby app. Nothing is sent on the first check after installation. |
| List: popular movies, List: now in cinemas, List: upcoming movies, List: top rated movies, List: seasonal movies, List: trending series, List: popular series, List: anticipated series, List: top rated series | all on | Which list folders the channel shows, each one separately. "Seasonal movies" is Ombi's list for the time of year (e.g. Christmas) and empty outside such seasons. "Anticipated series" fails on some Ombi versions; the folder then stays hidden and the diagnostics show the error. Empty folders are hidden. |
| Titles per movie list / Titles per series list | 30 each (10–500) | How many titles each list folder shows. The plugin loads a few more from Ombi (50 for 30), because the state filter hides some. More titles make a channel refresh longer. |
| Show titles that are not requested, Show requested titles (pending approval), Show approved titles (in progress), Show partially available titles, Show denied titles, Show available titles | all on except available | Which states the channel shows. With "Show available titles" off, the *Discover* lists of the web app also leave out available titles (movies already in Emby and complete series). The search is never filtered. |

### All movies / All series

| Setting | Default | Description |
|---|---|---|
| Folder 'All movies' / Folder 'All series' | on | Turns the catalog folders on or off. Types that are off are neither imported nor updated. |
| Movies to import / Series to import | 5000 / 1500 (min. 100 / 50) | Size of the import, without upper limit. Series at most as many as Ombi's series lists deliver. |
| Load new titles every ... days | 7 (0–90) | Interval of the automatic update after the import; 0 = only by hand. |
| Save and start import, Load new titles now, Look up missing trailers and backgrounds now | – | Buttons. The first saves and starts the import, the second loads new titles right away, the third looks up all missing trailers and backgrounds in one run instead of 250 per hour. The "Catalog" status shows the number of titles, the times of import and update, the next update and running tasks (as of opening the page). |

### Diagnostics

Read-only, as of opening the page (since Emby's start): which Ombi user and language channel and catalog load as ("Language of channel and catalog"), the last channel lists with duration and the titles shown and loaded per list ("Last channel lists"), the progress of the catalog's trailer and background lookup ("Catalog trailers and backgrounds"), the availability messages ("Availability messages"), whether Ombi accepts problem reports, otherwise "Report a problem" is not offered ("Problem reports"), and the last error ("Last error"). Details are in Emby's log.

## Data files

The plugin keeps its state in Emby's data folder (`/config/data` in Docker). JSON files are written via a temporary file, so they are never half written.

| File | Content |
|---|---|
| `ombirequests-catalog.json` | The catalog of "All movies" and "All series", with what Emby last got per title. |
| `ombirequests-posters.json` | The poster last applied to each channel item. |
| `ombirequests-interest.json` | Which Emby users want which title, season or episode, and requests whose owner withdrew while others still want them. |
| `ombirequests-channel-listed.json` | What Emby last got for each list title, so only changed ones are force-updated. |
| `ombirequests-channel-lists.json` | The titles of the last successful load of each list, used when Ombi does not answer. |
| `ombirequests-notifications.json` | Requests that were available at the last check, and waiting messages for users who were offline. |
| `ombirequests-favorite-requests.json` | Which user requested which channel item with the heart; only those are withdrawn by removing the heart. |
| `ombirequests-images/` | Pictures of the channel folders. |
| `ombirequests-webapp/` | Copy of Emby's web app with the script tag (only with **Load the script automatically**). |

## Plugin API

All endpoints except the static script files need a signed-in Emby session. The user is always taken from the session, never from the request body. Answers come as `{ "Success": true, "Message": "…", "Data": … }`; expected failures (no Ombi account, quota reached, ...) come as `Success: false` with a message rather than as HTTP errors.

Access rules:

- **page**: refused when the **Requests page (web app)** is switched off for the user.
- **admin**: only for users whose mapped Ombi user has the role Admin or PowerUser in Ombi, checked on every call.

| Method | Path | Body / parameters | Purpose | Access |
|---|---|---|---|---|
| GET | `/OmbiRequests/Status` | – | Configuration, mapped Ombi user, language, channel name, quotas, `IsAdmin`, `PageEnabled` | any signed-in user |
| GET | `/OmbiRequests/Search?Query=…&Type=all\|movie\|tv` | – | Search including request state and library match | page |
| GET | `/OmbiRequests/Discover?Category=movie:popular&Count=20` | `Category`: `movie:popular\|nowplaying\|upcoming\|toprated\|seasonal`, `tv:trending\|popular\|anticipated\|mostwatched` (Ombi's name for top rated series); `Count` 1–50, default 20 | Discover lists; without **Show available titles** available titles are left out | page |
| GET | `/OmbiRequests/Details?MediaType=tv&TmdbId=1399` | – | Details and season/episode states | page |
| POST | `/OmbiRequests/Request` | `{ "MediaType": "tv", "TmdbId": 1399, "Scope": "All\|First\|Latest\|Seasons\|Episodes", "Seasons": [1, 2], "Episodes": ["1:3"] }` | Request as the user. `Scope`, `Seasons` (with `Seasons`) and `Episodes` (with `Episodes`, as `"season:episode"`) are for series only. | page |
| GET | `/OmbiRequests/MyRequests` | – | The user's own requests, with `CanWithdraw` | page |
| POST | `/OmbiRequests/Withdraw` | `{ "MediaType": "tv", "TmdbId": 1399, "Seasons": [2] }` (`Seasons` optional) | Withdraw the user's own requests of a title (see [Several users want the same title](#several-users-want-the-same-title)) | page |
| GET | `/OmbiRequests/LibraryItem?ItemId=…` | – | Missing episodes and problem categories for a library item | page |
| POST | `/OmbiRequests/Issue` | `{ "ItemId": "…", "CategoryId": 2, "Subject": "…", "Description": "…" }` | Report a problem to Ombi as the user | page |
| GET | `/OmbiRequests/Admin/Requests?IncludeAvailable=false` | – | Requests of all users with state, requester and `AlsoWantedBy` | page, admin |
| POST | `/OmbiRequests/Admin/Decide` | `{ "MediaType": "movie", "RequestId": 5, "TmdbId": 603, "Approve": false, "Reason": "…" }` | Approve or deny, also a request decided before | page, admin |
| POST | `/OmbiRequests/Admin/Delete` | `{ "MediaType": "tv", "RequestId": 21, "TmdbId": 1399 }` | Delete a request; the title can then be requested again | page, admin |
| GET | `/OmbiRequests/web/client.js`, `/OmbiRequests/web/client.css` | – | The web app script and stylesheet | no authentication |

`RequestId` is Ombi's id of the movie request, or of the season request for series. For *Decide* and *Delete* the plugin looks the title up in Ombi by this id; the `TmdbId` sent along is not used.

### Ombi endpoints used

| Purpose | Ombi endpoint |
|---|---|
| Users and roles (mapping, admin check) | `GET api/v1/Identity/Users` |
| Default language | `GET api/v1/Settings/defaultlanguage` |
| Search | `POST api/v2/Search/multi/{term}` |
| Movie details | `GET api/v2/Search/movie/{tmdbId}` |
| Series details | `GET api/v2/Search/tv/moviedb/{tmdbId}`; by TheTVDB id for library series without TMDb id: `GET api/v2/Search/tv/{tvdbId}` |
| Movie lists | `GET api/v2/Search/movie/{popular\|nowplaying\|upcoming\|toprated\|seasonal}/{start}/{count}` |
| Series lists | `GET api/v2/Search/tv/{popular\|trending\|anticipated\|mostwatched}/{start}/{count}` |
| Movies by release year (catalog) | `POST api/v2/Search/advancedSearch/movie/{start}/{count}` |
| Request a movie | `POST api/v1/Request/movie` |
| Request a series | `POST api/v2/Requests/tv` |
| Request lists (My requests, All requests, states, notifications) | `GET api/v2/Requests/movie/{count}/0/requestedDate/desc`, `GET api/v2/Requests/tv/{count}/0/requestedDate/desc` |
| Remaining quota | `GET api/v1/Request/movie/remaining`, `GET api/v1/Request/tv/remaining` |
| Approve | `POST api/v1/Request/movie/approve`, `POST api/v1/Request/tv/approve` |
| Deny | `PUT api/v1/Request/movie/deny`, `PUT api/v1/Request/tv/deny` |
| Delete | `DELETE api/v1/Request/movie/{id}`, `DELETE api/v1/Request/tv/child/{id}` |
| Problem reports | `GET api/v1/Settings/issuesenabled`, `GET api/v1/Issues/categories`, `POST api/v1/Issues` |

The old v1 request lists (`api/v1/Request/movie`, `api/v1/Request/tvlite`) return HTTP 500 on current Ombi versions, so the plugin uses the v2 lists that Ombi's own UI uses.

## Security notes

- The **API key stays on the Emby server**. The settings page shows "(unchanged)" instead of it, and it is never sent to the browser. It grants admin access to Ombi.
- The user is always taken from the authenticated Emby session, never from the request body.
- Approving, denying and deleting run with the API key; before each call the plugin checks that the user's mapped Ombi user is Admin or PowerUser. Keep the **User mapping** correct.
- **Allow users without Ombi account** makes Ombi treat those users' requests as admin requests (no quota, auto-approval). It is off by default.
- Redirects from Ombi are not followed, so the API key never goes to another address.
- `client.js` and `client.css` are served without authentication, because a `<script>` tag sends no Emby token. They are static and contain no data.
- Texts from Ombi are HTML-escaped before the web app shows them, also in Emby's toast messages, which insert text as HTML.
- The settings page is only available to Emby administrators.

## Known limitations

- The web integration only works in a browser (`http://server:8096/web`). The Emby apps bring their own web client; there the channel is the way to request.
- There is no free-text search inside the channel: Emby no longer searches channels (`ISearchableChannel` is empty). The "All movies" and "All series" folders make titles findable through Emby's normal search instead.
- The channel's lists are the same for every user, because Emby lists channel content without a user.
- In the channel a series can only be requested as a whole; single seasons and episodes are requested in the web app.
- HTTP headers allow only ASCII characters. Ombi user names with other characters (umlauts, accents) cannot be sent as `UserName` header; the plugin then requests with the API key and Ombi's `requestOnBehalf`. Ombi treats this as an admin request on behalf of the user, so quotas and auto-approval may differ (not tested against a real Ombi).
- The play button cannot be hidden in the other Emby apps.
- Switching the channel back on needs an Emby restart if it was off when Emby started.

### Web app dependencies on Emby's web client

The web app UI hooks into internals of Emby's web client. If an Emby update changes them, `client.js` has to follow:

- the side menu `.mainDrawer .mainDrawerScrollSlider` with the "Home" entry (`.drawer-home` or `.navMenuOption[data-navmenuid="home"]` / `"/home"`), whose markup is copied for the "Requests" entry, `.navMenuOption-selected` and `.mainDrawer.drawer-docked`;
- the header `.skinHeader` (incl. `semiTransparent`) and its title `.skinHeader .pageTitle`;
- the background `.backgroundContainer` (`withBackdrop`) and `.backdropContainer` (hidden by `client.css` while the page is open);
- the views (`.view`) and the `viewshow` / `viewbeforeshow` events with `detail.params`;
- `.mainDetailButtons` and the `fab detailButton detailButton-autotext` classes for the library buttons;
- the search field `.searchfields-txtSearch` inside `.searchContainer` (desktop) or `.searchFields` (TV layout);
- the header buttons `.headerHomeButton`, `.headerBackButton`, `.headerSearchButton` and `.btnNavDrawerLogo`, which close the requests page;
- the play button classes hidden in channel views (`.btnPlay`, `.btnResume`, `.btnShuffle`, `.btnQueue`, `.btnPlaystate`, `.btnSyncDownload`, `emby-playstatebutton`, `data-action="play"` etc.);
- `Emby.importModule` with the modules `viewmanager`, `approuter`, `navdrawer` and `toast`, and `window.ApiClient`.

**Load the script automatically** relies on Emby's server option `DashboardSourcePath`, its `dashboard-ui` folder and the web resources inside `Emby.Web.dll`. If an Emby version changes them, the copy cannot be built and Emby keeps its original web app; the reverse proxy still works.

## Project structure

```
src/OmbiRequests/
  Plugin.cs                    Plugin entry point, configuration, settings page
  Localization.cs              Server-side texts (channel, notifications, API messages) in English and German
  DataFile.cs                  JSON files in Emby's data folder
  Diagnostics.cs               State shown in the diagnostics section
  Configuration/               Stored configuration, settings page (GenericUI), per-user access
  Ombi/                        HTTP client and models for the Ombi API
  Requests/                    Logic: search, states, requests, library matching, quota, withdrawing,
                               interested users, all requests for admins, library pages, problem reports; DTOs
  Api/RequestsApi.cs           REST endpoints for the web app
  Api/ClientResourcesApi.cs    Serves client.js/client.css under /OmbiRequests/web/
  Channel/                     "Requests" channel, refreshing, favorites (request/withdraw), "now available"
                               messages, item updates, poster banners (IImageEnhancer), filter for other overlays
  Catalog/                     Catalog for "All movies"/"All series": file, import, update, states, Emby tasks
  Images/                      Pre-rendered status banners, channel and folder pictures (English/German)
  Web/client.js, client.css    UI inside Emby's web client
  Web/WebAppInjector.cs        Automatic script loading via DashboardSourcePath
tools/generate-images.ps1      Regenerates banners, channel and folder pictures in Images/ (Windows)
tests/OmbiRequests.Tests/      Tests against a fake Ombi (dotnet run)
tests/web/                     Browser tests of client.js
.github/workflows/build.yml    Build, tests and releases
```

## Building

```
dotnet build src/OmbiRequests -c Release
```

The plugin is written to `src/OmbiRequests/bin/Release/netstandard2.0/OmbiRequests.dll`. To copy it into Emby's plugins folder after every build, pass the folder:

```
dotnet build src/OmbiRequests -c Release -p:EmbyPluginsDir=/path/to/emby/plugins
```

Release builds contain no PDB and no local source paths. GitHub Actions (`.github/workflows/build.yml`) builds and tests every push to `main`, every pull request and every `v*` tag; pushing a tag such as `v0.9.0` publishes `OmbiRequests.dll` as a release asset (tags with a suffix like `-beta` are marked as pre-releases).

## Tests

```
dotnet run --project tests/OmbiRequests.Tests            # plugin against a fake Ombi; exit code = number of failed checks
powershell -File tests/web/run-web-tests.ps1             # client.js in headless Edge or Chrome
```

`tests/OmbiRequests.Tests` (needs the .NET 10 SDK) runs a fake Ombi as a small HTTP server (`FakeOmbi.cs`) and replaces Emby's services with stand-ins (`Fakes.cs`). It covers user matching, catalog import and update with states, trailers, backgrounds and language, the series of Ombi's lists, the channel folders (movies as movies, series without seasons, removal of old video items), the channel item updates (search key, poster, background, trailer, state after a request), keeping other poster overlays off, quota, withdrawing (also with several interested users), approvals with Ombi roles, library pages, favorites, "now available" messages, list caching, Ombi or its proxy being busy (503), and per-user access. Add `-- --quiet` to hide the log output.

`tests/web` (Windows with Edge or Chrome) loads the real `client.js` with a fake Emby and checks the menu entry, hiding the play buttons on channel pages, quota, withdrawing, filter, sorting and actions of *All requests*, the buttons and dialog on library pages, and the trailer dialog. The exit code is the number of pages with failures.

## License

GNU General Public License version 3 or later, see [LICENSE](LICENSE), with an additional permission to combine the plugin with Emby Server, which is not free software. See [LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md).

The icons in the pictures under `src/OmbiRequests/Images` are [Material Symbols](https://fonts.google.com/icons) (Rounded) by Google, licensed under the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0). `tools/generate-images.ps1` downloads the font from Google Fonts on its first run.
