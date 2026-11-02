// Tests of the plugin against a fake Ombi (FakeOmbi.cs) and stand-ins for Emby's services (Fakes.cs).
// Run: dotnet run --project tests/OmbiRequests.Tests   (exit code = number of failed checks)
// The web app parts are tested in the browser, see tests/web.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using OmbiRequests;
using OmbiRequests.Catalog;
using OmbiRequests.Channel;
using OmbiRequests.Configuration;
using OmbiRequests.Ombi;
using OmbiRequests.Requests;
using OmbiRequests.Tests;

LogProxy.Quiet = args.Contains("--quiet");
var dataDir = Path.Combine(Path.GetTempPath(), "ombirequests-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dataDir);
PathsProxy.DataPath = dataDir;
using var ombiServer = new FakeOmbi();

var fails = 0;
void Check(string label, object actual, object expected)
{
    var ok = Equals(actual, expected);
    fails += ok ? 0 : 1;
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}: {actual} (expected {expected})");
}

void Section(string title) => Console.WriteLine(Environment.NewLine + "== " + title);

async Task WaitFor(Func<bool> condition)
{
    for (var i = 0; i < 100 && !condition(); i++)
    {
        await Task.Delay(50);
    }
}

var json = Fake.Create<IJsonSerializer, JsonProxy>();
var logger = Fake.Create<ILogger, LogProxy>();
var logManager = Fake.Create<ILogManager, LogManagerProxy>();
var library = Fake.Create<ILibraryManager, LibraryProxy>();
var paths = Fake.Create<IApplicationPaths, PathsProxy>();
BaseItem.LibraryManager = library;
BaseItem.ItemRepository = Fake.Create<IItemRepository, NullProxy>();

// A real plugin instance: notifications and favorites take their services from Plugin.Instance.
var plugin = new Plugin(paths, Fake.Create<IXmlSerializer, XmlProxy>(), Fake.Create<IServerApplicationHost, AppHostProxy>(),
    Fake.Create<IServerConfigurationManager, ServerConfigProxy>(), json, logManager);
plugin.SetAttributes(typeof(Plugin).Assembly.Location, dataDir, new Version(1, 0));
var config = plugin.Configuration;
config.OmbiUrl = ombiServer.Url;
config.ApiKey = "test";
config.CatalogMovieCount = 400;
config.CatalogTvCount = 120;
config.CatalogUpdateIntervalDays = 7;
var ombi = plugin.OmbiClient;
var service = plugin.RequestService;
TitleCatalog NewCatalog() => new TitleCatalog(dataDir, json, ombi, service, () => config, new Lazy<ILibraryManager>(() => library), logger);

// ---------------------------------------------------------------------------------------------
Section("User matching");
var users = new List<OmbiUser>
{
    new OmbiUser { userName = "paul@example.com", alias = "Paul", emailAddress = "paul@example.com" },
    new OmbiUser { userName = "anna", alias = "", emailAddress = "anna@example.com" },
    new OmbiUser { userName = "max@example.com", alias = "Familie", emailAddress = "max@example.com" },
    new OmbiUser { userName = "eva@example.com", alias = "Familie", emailAddress = "eva@example.com" },
    new OmbiUser { userName = "tom", alias = "Thomas", emailAddress = "" },
};
var noMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Opa"] = "Thomas", ["Kaputt"] = "gibtsnicht" };
Check("alias of an Emby Connect user", OmbiClient.FindUser(users, noMappings, "Paul", null)?.userName, "paul@example.com");
Check("Emby Connect e-mail", OmbiClient.FindUser(users, noMappings, "Pauli", "paul@example.com")?.userName, "paul@example.com");
Check("same user name, any case", OmbiClient.FindUser(users, noMappings, "ANNA", null)?.userName, "anna");
Check("Emby Connect name matches the e-mail field", OmbiClient.FindUser(users, noMappings, "Annie", "anna@example.com")?.userName, "anna");
Check("an alias two users have is ignored", OmbiClient.FindUser(users, noMappings, "Familie", null)?.userName, null);
Check("mapping to an alias", OmbiClient.FindUser(users, mappings, "Opa", null)?.userName, "tom");
Check("mapping to an unknown name does not fall back", OmbiClient.FindUser(users, mappings, "Kaputt", null)?.userName, null);
Check("unknown user", OmbiClient.FindUser(users, noMappings, "Fremder", null)?.userName, null);
Check("user name wins over the alias of another user",
    OmbiClient.FindUser(new List<OmbiUser> { new OmbiUser { userName = "x", alias = "tom" }, new OmbiUser { userName = "tom" } }, noMappings, "tom", null)?.userName, "tom");

// ---------------------------------------------------------------------------------------------
Section("Catalog: import, states, trailers");
var catalog = NewCatalog();
Check("not imported at start", catalog.IsImported, false);
Check("no update due before the import", catalog.IsUpdateDue(config), false);

var reports = new List<double>();
await catalog.ImportAsync(new SyncProgress(reports), CancellationToken.None);
var entries = await catalog.GetEntriesAsync(CancellationToken.None);
int Count(List<CatalogEntry> list, string type) => list.Count(e => e.MediaType == type);
string StatusOf(string type, int id) => entries.FirstOrDefault(e => e.MediaType == type && e.TmdbId == id)?.Status ?? "(missing)";

Check("movies after import (lists + years)", Count(entries, "movie"), 414);
Check("series after import (lists exhausted)", Count(entries, "tv"), 91);
Check("year 2027 loaded", entries.Any(e => e.TmdbId == 802700), true);
Check("year 2023 only its first page", entries.Count(e => e.TmdbId >= 802300 && e.TmdbId < 802340), 20);
Check("year 2022 not needed", entries.Any(e => e.TmdbId >= 802200 && e.TmdbId < 802240), false);
Check("title from \"now playing\" included", entries.Any(e => e.TmdbId == 200000), true);
Check("no duplicate titles", entries.Select(e => e.GetKey()).Distinct().Count(), entries.Count);
Check("progress never above 100", reports.Max() <= 100.0001, true);
Check("progress never goes back", reports.Zip(reports.Skip(1), (a, b) => b >= a - 0.0001).All(x => x), true);
Check("progress ends at 100", Math.Round(reports.Last(), 3), 100.0);

Check("movie: approved request", StatusOf("movie", 100001), "Approved");
Check("movie: denied request", StatusOf("movie", 100002), "Denied");
Check("movie: request available", StatusOf("movie", 100003), "Available");
Check("movie: pending request", StatusOf("movie", 100004), "Requested");
Check("movie: in the library by TMDb id", StatusOf("movie", 100005), "Available");
Check("movie: in the library by IMDb id", StatusOf("movie", 100006), "Available");
Check("movie: own channel item is not the library", StatusOf("movie", 100007), "None");
Check("movie: nothing", StatusOf("movie", 100008), "None");
Check("series: approved", StatusOf("tv", 500001), "Approved");
Check("series: in the library with an open request", StatusOf("tv", 500002), "PartiallyAvailable");
Check("series: denied", StatusOf("tv", 500003), "Denied");
Check("series: in the library by TVDB id, partly when loaded", StatusOf("tv", 500004), "PartiallyAvailable");
Check("series: in the library by TMDb id", StatusOf("tv", 500005), "Available");
Check("series: all requests available", StatusOf("tv", 500006), "Available");
Check("series: nothing", StatusOf("tv", 500007), "None");
Check("poster link", entries.First(e => e.TmdbId == 100001).PosterUrl, "https://image.tmdb.org/t/p/w342/m100001.jpg");

Check("series trailer from the list", entries.First(e => e.TmdbId == 500002).TrailerUrl, "https://www.youtube.com/watch?v=tv500002");
Check("trailers looked up during the import (Ombi's movie lists have none)",
    entries.Count(e => e.MediaType == "movie" && e.TrailerChecked) + " / " + entries.Count(e => e.MediaType == "movie" && e.TrailerUrl != null), "414 / 414");
Check("movie trailer: trailer before teaser", entries.First(e => e.TmdbId == 100001).TrailerUrl, "https://www.youtube.com/watch?v=m100001");
Check("series without list trailer and without details stay open for the hourly run", entries.Any(e => e.MediaType == "tv" && !e.TrailerChecked), true);
Check("the hourly run finds nothing more", await catalog.FillTrailersAsync(1000, CancellationToken.None), 0);
Check("background from the list", entries.First(e => e.TmdbId == 100001).BackdropUrl, "https://image.tmdb.org/t/p/w1280/bm100001.jpg");
Check("background missing in the list comes from the details", entries.First(e => e.TmdbId == 100003).BackdropUrl, "https://image.tmdb.org/t/p/w1280/bm100003.jpg");
Check("series background from the list", entries.First(e => e.TmdbId == 500007).BackdropUrl, "https://image.tmdb.org/t/p/w1280/bt500007.jpg");
// Catalogs of older versions stored no backgrounds: the hourly run looks them up.
foreach (var e in entries.Where(e => e.MediaType == "movie").Take(3))
{
    e.BackdropUrl = null;
    e.BackdropChecked = false;
}
Check("older catalog: backgrounds looked up by the hourly run", await catalog.FillTrailersAsync(1000, CancellationToken.None) + " " + entries.Count(e => e.MediaType == "movie" && e.BackdropUrl == null), "3 0");
Check("trailer key becomes a link", RequestService.TrailerUrl("dQw4w9WgXcQ", null), "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
Check("trailer link kept", RequestService.TrailerUrl(" https://www.youtube.com/watch?v=abc ", null), "https://www.youtube.com/watch?v=abc");
Check("no YouTube, no trailer", RequestService.TrailerUrl(null, new List<TmdbVideo> { new TmdbVideo { key = "x", site = "Vimeo", type = "Trailer" } }), null);

Check("all titles to hand to Emby", entries.Count(e => e.ListedStatus != e.Status), entries.Count);
catalog.MarkListed(entries);
Check("nothing to hand over after listing", entries.Count(e => e.ListedStatus != e.Status), 0);
catalog.ForgetListed("movie", 100001);
Check("after a request the catalog title is handed over again", entries.Count(e => e.ListedStatus != e.Status) + " " + entries.First(e => e.TmdbId == 100001).ListedStatus, "1 ");
catalog.MarkListed(entries);

var reloaded = NewCatalog();
var reloadedEntries = await reloaded.GetEntriesAsync(CancellationToken.None);
Check("read back: imported", reloaded.IsImported, true);
Check("read back: titles", reloadedEntries.Count, entries.Count);
Check("read back: handed-over states", reloadedEntries.Count(e => e.ListedStatus != e.Status), 0);
Check("read back: trailers", reloadedEntries.Count(e => e.TrailerUrl != null && e.ListedTrailer == e.TrailerUrl), entries.Count(e => e.TrailerUrl != null));
Check("read back: backgrounds", reloadedEntries.Count(e => e.BackdropUrl != null && e.ListedBackdrop == e.BackdropUrl), entries.Count);
Check("no update due right after the import", reloaded.IsUpdateDue(config), false);

ombiServer.ExtraNew = true;
await reloaded.UpdateAsync(new SyncProgress(new List<double>()), CancellationToken.None);
var afterUpdate = await reloaded.GetEntriesAsync(CancellationToken.None);
Check("movies after the update", Count(afterUpdate, "movie"), 421);
Check("series after the update", Count(afterUpdate, "tv"), 94);
Check("existing titles keep their handed-over state", afterUpdate.First(e => e.TmdbId == 100001).ListedStatus, "Approved");
Check("only new titles to hand over", afterUpdate.Count(e => e.ListedStatus != e.Status), 10);
Check("no update due right after the update", reloaded.IsUpdateDue(config), false);

ombiServer.FailTvMain = true;
await reloaded.ImportAsync(new SyncProgress(new List<double>()), CancellationToken.None);
var afterFailedTv = await reloaded.GetEntriesAsync(CancellationToken.None);
Check("movies after a new import", Count(afterFailedTv, "movie"), 400);
Check("series kept when their lists fail", Count(afterFailedTv, "tv"), 94);
Check("new import keeps handed-over states", afterFailedTv.First(e => e.TmdbId == 100001).ListedStatus, "Approved");
Check("new import keeps looked-up trailers", afterFailedTv.First(e => e.TmdbId == 100001).TrailerUrl, "https://www.youtube.com/watch?v=m100001");
Check("new import keeps handed-over backgrounds", afterFailedTv.First(e => e.TmdbId == 100003).ListedBackdrop, "https://image.tmdb.org/t/p/w1280/bm100003.jpg");
ombiServer.FailTvMain = false;

config.CatalogUpdateIntervalDays = 0;
Check("no automatic update with interval 0", reloaded.IsUpdateDue(config), false);
config.CatalogUpdateIntervalDays = 7;

var pageSizes = ombiServer.Calls.Select(c => System.Text.RegularExpressions.Regex.Match(c.Path, @"/(\d+)/(\d+)$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[2].Value)).ToList();
Check("lists loaded in pages", pageSizes.Count > 50, true);
Check("no page larger than 20", pageSizes.Max(), 20);
Check("movies loaded by release year", ombiServer.Calls.Any(c => c.Path.Contains("advancedSearch") && (int?)c.Body?["releaseYear"] == 2027), true);

// ---------------------------------------------------------------------------------------------
Section("Series of Ombi's lists");
var popularSeries = await service.DiscoverAsync(null, await service.GetContentIdentityAsync(CancellationToken.None), "tv:popular", 50, CancellationToken.None);
Check("\"fully available\" without seasons ignored, library decides, missing episodes (details) make it partial",
    string.Join(", ", popularSeries.GroupBy(s => s.Status).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())),
    "Available 1, None 47, PartiallyAvailable 2");

// ---------------------------------------------------------------------------------------------
Section("Language of channel and catalog");
Check("Ombi user for German", (await service.GetContentIdentityAsync(CancellationToken.None))?.UserName, "boss");
Check("catalog matches the language", reloaded.Describe(config).Contains("start the import again"), false);
config.Language = UiLanguage.English;
Check("Ombi user for English", (await service.GetContentIdentityAsync(CancellationToken.None))?.UserName, "fritz");
Check("language change asks for a new import", reloaded.Describe(config).Contains("loaded in 'de', the language is now 'en'"), true);
config.Language = UiLanguage.Auto;
Check("lists loaded as the German admin", ombiServer.Calls.Where(c => c.Path.StartsWith("/api/v2/Search/", StringComparison.Ordinal)).All(c => c.User == "boss"), true);
Check("request lists loaded without user", ombiServer.Calls.Where(c => c.Path.StartsWith("/api/v2/Requests/", StringComparison.Ordinal)).All(c => c.User == null), true);

// ---------------------------------------------------------------------------------------------
Section("Channel folders");
var channel = new OmbiChannel(logManager, json);
async Task<string> RootFolders() => string.Join(", ", (await channel.GetChannelItems(new MediaBrowser.Controller.Channels.InternalChannelItemQuery(), CancellationToken.None)).Items.Select(i => i.Name));
// "Erwartete Serien" fails in the fake (like on some Ombi versions) and stays hidden.
Check("all folders by default", await RootFolders(),
    "Beliebte Filme, Aktuell im Kino, Demnächst erscheinende Filme, Bestbewertete Filme, Saisonale Filme, Alle Filme, Angesagte Serien, Beliebte Serien, Bestbewertete Serien, Alle Serien");
config.ChannelShowNotRequested = config.ChannelShowRequested = config.ChannelShowApproved = config.ChannelShowPartiallyAvailable = config.ChannelShowDenied = false;
config.ChannelShowAvailable = true;
// "Angesagte Serien" only has 500002, which misses episodes (details) although it is in the library.
Check("lists with nothing to show are left out", await RootFolders(), "Alle Filme, Beliebte Serien, Alle Serien");
config.ChannelShowNotRequested = config.ChannelShowRequested = config.ChannelShowApproved = config.ChannelShowPartiallyAvailable = config.ChannelShowDenied = true;
config.ChannelShowAvailable = false;
ombiServer.FailTvMain = true;
Check("Ombi not answering keeps the last titles", await RootFolders(),
    "Beliebte Filme, Aktuell im Kino, Demnächst erscheinende Filme, Bestbewertete Filme, Saisonale Filme, Alle Filme, Angesagte Serien, Beliebte Serien, Bestbewertete Serien, Alle Serien");
config.ChannelSeasonalMovies = config.ChannelTopRatedTv = false;
Check("folders switched off", await RootFolders(),
    "Beliebte Filme, Aktuell im Kino, Demnächst erscheinende Filme, Bestbewertete Filme, Alle Filme, Angesagte Serien, Beliebte Serien, Alle Serien");
config.ChannelSeasonalMovies = config.ChannelTopRatedTv = true;
ombiServer.FailTvMain = false;
config.ChannelMovieListSize = 10;
await RootFolders();
var popularFolder = await channel.GetChannelItems(new MediaBrowser.Controller.Channels.InternalChannelItemQuery { FolderId = "ombirequests|list|movie:popular" }, CancellationToken.None);
Check("titles per movie list", popularFolder.Items.Count, 10);
Check("list title brings its background", popularFolder.Items[0].ProviderIds.TryGetValue("OmbiBackdrop", out var listBackdrop) ? listBackdrop : "(none)",
    "https://image.tmdb.org/t/p/w1280/bm100001.jpg");
Check("movies are movies (the TV apps show their background like for series)", popularFolder.Items[0].ContentType, MediaBrowser.Model.Channels.ChannelMediaContentType.Movie);

// Older versions created plain videos; Emby never changes an item's type.
LibraryProxy.ChannelEntity = new MediaBrowser.Controller.Channels.Channel { InternalId = 99 };
var oldMovie = new Video { ExternalId = "ombirequests|movie|100001", InternalId = 90 };
var newMovie = new MediaBrowser.Controller.Entities.Movies.Movie { ExternalId = "ombirequests|movie|100002", InternalId = 91 };
LibraryProxy.ByExternalId.AddRange(new BaseItem[] { oldMovie, newMovie });
await RootFolders();
Check("movies of older versions removed before Emby reads the lists, movies stay", string.Join(",", LibraryProxy.Deleted), "90");
LibraryProxy.ByExternalId.Remove(newMovie);
LibraryProxy.ChannelEntity = null;
config.ChannelMovieListSize = 30;

// Series are series pages without seasons: daily shows alone have thousands of episodes.
async Task<List<MediaBrowser.Controller.Channels.ChannelItemInfo>> Children(string id) =>
    (await channel.GetChannelItems(new MediaBrowser.Controller.Channels.InternalChannelItemQuery { FolderId = id }, CancellationToken.None)).Items;
var seriesItem = (await Children("ombirequests|list|tv:trending")).First(i => i.Id.Contains("|600001|"));
Check("series in a list", $"{seriesItem.Id} {seriesItem.Type}/{seriesItem.FolderType}", "ombirequests|tv|600001|tv:trending Folder/Series");
Check("series folders have no seasons", (await Children("ombirequests|tv|600001|tv:trending")).Count + (await Children("ombirequests|tv|500045|tv:popular")).Count, 0);
Check("seasons of older versions have no episodes any more (Emby removes them)", (await Children("ombirequests|season|600001|1")).Count, 0);
await RootFolders();
await RootFolders();
await Children("ombirequests|list|movie:nowplaying");
await RootFolders();
var unchangedTitles = await Children("ombirequests|list|movie:nowplaying");
Check("titles of a list are only written again when they change", unchangedTitles.Count(i => i.ForceUpdate) + " of " + unchangedTitles.Count, "0 of 30");
ChannelItemIds.TryGetTarget(unchangedTitles[0].Id, out var changedTitle);
OmbiChannel.ForgetListed("movie", changedTitle.TmdbId);
await RootFolders();
Check("after a request the title is sent again", string.Join(",", (await Children("ombirequests|list|movie:nowplaying")).Where(i => i.ForceUpdate).Select(i => i.Id)), unchangedTitles[0].Id);

Check("no metadata or image fetchers for the channel's items", channel.Features.LibraryOptions.TypeOptions.Length, 0);
Check("the lists are kept for a restart (Ombi may start later than Emby)", File.Exists(Path.Combine(dataDir, "ombirequests-channel-lists.json")), true);
var firstName = channel.Name;
config.Language = UiLanguage.English;
Check("after a language switch and a restart the channel keeps its first name (Emby derives its id from it)",
    firstName + " / " + new OmbiChannel(logManager, json).Name, "Anfragen / Anfragen");
config.Language = UiLanguage.Auto;
var catalogSeries = (await Children("ombirequests|list|tv:all:S")).First(i => i.Id.Contains("|500045|"));
Check("catalog series: a series without seasons", $"{catalogSeries.Id} {catalogSeries.FolderType} {(await Children(catalogSeries.Id)).Count}",
    "ombirequests|tv|500045|tv:all Series 0");

var discovered = await service.DiscoverAsync(UserManagerProxy.Boss, null, "tv:popular", 20, CancellationToken.None);
Check("series in the library: missing episodes make it partially available, without details it stays available",
    discovered.First(i => i.TmdbId == 500002).Status + "/" + discovered.First(i => i.TmdbId == 500005).Status, "PartiallyAvailable/Available");
Check("details: in Emby with missing episodes is partially available (Ombi says \"available\")",
    (await service.GetDetailsAsync(UserManagerProxy.Boss, "tv", 500999, CancellationToken.None)).Status, "PartiallyAvailable");

var heartTargets = new[] { "ombirequests|movie|603", "ombirequests|tv|1399|tv:popular", "ombirequests|season|1399|2", "ombirequests|list|movie:popular" }
    .Select(id => ChannelItemIds.TryGetTarget(id, out var target) ? target.MediaType + " " + target.TmdbId : "-");
Check("a heart requests the movie or the whole series; seasons of older versions and folders nothing", string.Join(" | ", heartTargets), "movie 603 | tv 1399 | - | -");

// ---------------------------------------------------------------------------------------------
Section("Channel items: search key, poster, trailer, state after a request");
LibraryProxy.Updates.Clear();
var updater = new ChannelItemUpdater(library, paths, json, logManager);
void Changed(ChannelItemUpdater target, BaseItem item) => target.OnItemChanged(null, new ItemChangeEventArgs(library) { Item = item });
string PosterOf(BaseItem item) => item.GetImageInfo(ImageType.Primary, 0)?.Path;
const string PosterA = "https://image.tmdb.org/t/p/w780/a.jpg";
const string PosterB = "https://image.tmdb.org/t/p/w780/b.jpg";
const string LocalFile = "/config/metadata/library/x/poster.jpg";

var clip = new Video { ExternalId = "ombirequests|movie|603", InternalId = 42 };
clip.SetProviderId("OmbiPoster", PosterA);
clip.SetImage(new ItemImageInfo { Path = PosterA, Type = ImageType.Primary }, 0);
Changed(updater, clip);
Check("search key set", clip.PresentationUniqueKey, "ombirequests|movie|603");
Check("new item keeps its poster", PosterOf(clip), PosterA);
Check("one save for the new item", LibraryProxy.Updates.Count, 1);

clip.SetImage(new ItemImageInfo { Path = LocalFile, Type = ImageType.Primary }, 0);
LibraryProxy.Updates.Clear();
Changed(updater, clip);
Check("downloaded poster left alone", PosterOf(clip), LocalFile);
Check("nothing saved without a change", LibraryProxy.Updates.Count, 0);

clip.SetProviderId("OmbiPoster", PosterB);
Changed(updater, clip);
Check("changed poster applied", PosterOf(clip), PosterB);
Check("saved once for the poster", LibraryProxy.Updates.Count, 1);

var older = new Video { ExternalId = "ombirequests|movie|604", InternalId = 43, PresentationUniqueKey = "ombirequests|movie|604" };
older.SetProviderId("OmbiPoster", PosterA);
older.SetImage(new ItemImageInfo { Path = LocalFile, Type = ImageType.Primary }, 0);
Changed(updater, older);
Check("item of an older version gets the wanted poster", PosterOf(older), PosterA);

string BackdropOf(BaseItem item) => item.GetImageInfo(ImageType.Backdrop, 0)?.Path;
var withBackdrop = new Video { ExternalId = "ombirequests|movie|605", InternalId = 48, PresentationUniqueKey = "ombirequests|movie|605" };
withBackdrop.SetProviderId("OmbiBackdrop", "https://image.tmdb.org/t/p/w1280/a.jpg");
LibraryProxy.Updates.Clear();
Changed(updater, withBackdrop);
Check("background set (channel items can only bring a poster)", BackdropOf(withBackdrop) + " " + LibraryProxy.Updates.Count, "https://image.tmdb.org/t/p/w1280/a.jpg 1");
withBackdrop.SetImage(new ItemImageInfo { Path = LocalFile, Type = ImageType.Backdrop }, 0);
withBackdrop.SetProviderId("OmbiBackdrop", "https://image.tmdb.org/t/p/w1280/b.jpg");
LibraryProxy.Updates.Clear();
Changed(updater, withBackdrop);
Check("a background once set stays (Emby keeps its own copy)", BackdropOf(withBackdrop) + " " + LibraryProxy.Updates.Count, LocalFile + " 0");

var seriesCopy = new Series { ExternalId = "ombirequests|tv|1399|tv:popular", InternalId = 47 };
Changed(updater, seriesCopy);
Check("all copies of a series share one search key", seriesCopy.PresentationUniqueKey, "ombirequests|tv|1399");

clip.SetProviderId("OmbiTrailer", "https://www.youtube.com/watch?v=m603");
Changed(updater, clip);
Check("trailer goes into RemoteTrailers", string.Join(",", clip.RemoteTrailers), "https://www.youtube.com/watch?v=m603");
clip.ProviderIds.Remove("OmbiTrailer");
Changed(updater, clip);
Check("trailer removed again", clip.RemoteTrailers.Length, 0);

var foreign = new Video { InternalId = 45 };
foreign.SetProviderId("OmbiPoster", PosterA);
LibraryProxy.Updates.Clear();
Changed(updater, foreign);
Check("library items are not touched", LibraryProxy.Updates.Count + (PosterOf(foreign) == null ? 0 : 1), 0);

updater.Dispose();
var updater2 = new ChannelItemUpdater(library, paths, json, logManager);
clip.SetImage(new ItemImageInfo { Path = LocalFile, Type = ImageType.Primary }, 0);
LibraryProxy.Updates.Clear();
Changed(updater2, clip);
Check("applied posters survive a restart", PosterOf(clip) == LocalFile && LibraryProxy.Updates.Count == 0, true);

string StateOf(BaseItem item) => item.GetProviderId("OmbiStatus");
BaseItem Titled(string externalId, long id, string state)
{
    // With the keys ChannelItemUpdater gives channel items.
    var item = new Video
    {
        ExternalId = externalId,
        InternalId = id,
        PresentationUniqueKey = ChannelItemIds.PresentationKey(externalId) ?? externalId
    };
    item.SetProviderId("OmbiStatus", state);
    LibraryProxy.ByExternalId.Add(item);
    return item;
}

var inPopular = Titled("ombirequests|movie|603", 50, "None");
var inAll = Titled("ombirequests|movie|603", 51, "None");
var otherMovie = Titled("ombirequests|movie|604", 52, "None");
var show = Titled("ombirequests|tv|1399", 53, "None");
var showInAll = Titled("ombirequests|tv|1399|tv:all", 56, "None");
void RequestChanged(string type, int id, MediaItemDto details, bool withdrawn = false) =>
    updater2.ApplyRequestChange(new RequestChangedEventArgs(type, id, details, withdrawn));

LibraryProxy.Updates.Clear();
RequestChanged("movie", 603, new MediaItemDto { Status = "Approved" });
Check("both folders show the new state", StateOf(inPopular) + "/" + StateOf(inAll), "Approved/Approved");
Check("other titles untouched", StateOf(otherMovie), "None");
Check("one save per changed item", LibraryProxy.Updates.Count, 2);
RequestChanged("movie", 603, null);
Check("known state kept without details", StateOf(inPopular), "Approved");
RequestChanged("movie", 604, null);
Check("without details at least \"requested\"", StateOf(otherMovie), "Requested");
RequestChanged("tv", 1399, new MediaItemDto { Status = "Requested" });
Check("series updated in every copy", StateOf(show) + "/" + StateOf(showInAll), "Requested/Requested");
LibraryProxy.Updates.Clear();
RequestChanged("movie", 603, new MediaItemDto { Status = "Approved" });
Check("nothing saved when the state is the same", LibraryProxy.Updates.Count, 0);
RequestChanged("movie", 604, new MediaItemDto { Status = "None" }, withdrawn: true);
Check("withdrawn: back to \"not requested\"", StateOf(otherMovie), "None");
RequestChanged("movie", 603, null, withdrawn: true);
Check("withdrawn without details: state kept", StateOf(inPopular), "Approved");
updater2.Dispose();

var letters = new[] { ("Ärger im Paradies", "A"), ("the Matrix", "T"), ("(500) Days of Summer", "#"), ("東京物語", "#"), (string.Empty, "#"), ("Élite", "E") };
Check("A-Z folder of titles", string.Join(" ", letters.Select(l => OmbiChannel.LetterOf(l.Item1))), string.Join(" ", letters.Select(l => l.Item2)));

// ---------------------------------------------------------------------------------------------
Section("Other plugins' poster overlays");
ImageProcessorProxy.Enhancers = new IImageEnhancer[] { new FakeCoverArt(), new StatusBannerEnhancer() };
var filter = new ForeignOverlayFilter(Fake.Create<IImageProcessor, ImageProcessorProxy>());
filter.Run();
filter.Run();
var registered = ImageProcessorProxy.Enhancers;
Check("enhancers kept, none wrapped twice", registered.Length + "/" + registered[0].GetType().Name + "/" + registered[1].GetType().Name, "2/ChannelExcludingEnhancer/StatusBannerEnhancer");
var libraryMovie = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "Dune" };
Check("Cover Art leaves channel posters out", registered[0].Supports(new Video { ExternalId = "ombirequests|movie|603" }, ImageType.Primary), false);
Check("Cover Art leaves channel folders out", registered[0].Supports(new Folder { ExternalId = "ombirequests|list|movie:all:A" }, ImageType.Primary), false);
Check("Cover Art still works for the library", registered[0].Supports(libraryMovie, ImageType.Primary), true);
Check("cache key passed through", registered[0].GetConfigurationCacheKey(libraryMovie, ImageType.Primary), "coverart-v1");
config.HideOtherOverlays = false;
Check("switched off: Cover Art takes channel posters again", registered[0].Supports(new Video { ExternalId = "ombirequests|movie|603" }, ImageType.Primary), true);
config.HideOtherOverlays = true;

// ---------------------------------------------------------------------------------------------
Section("Quota, withdrawing, approvals");
var boss = UserManagerProxy.Boss;
var changes = new List<RequestChangedEventArgs>();
service.RequestChanged += (sender, e) => changes.Add(e);

var bossQuotas = await service.GetQuotasAsync(await service.GetIdentityAsync(boss, CancellationToken.None), CancellationToken.None);
Check("quota only where Ombi has a limit", string.Join(",", bossQuotas.Select(q => $"{q.MediaType} {q.Remaining}/{q.Limit}")), "movie 3/10");
var annaQuotas = await service.GetQuotasAsync(await service.GetIdentityAsync(UserManagerProxy.Anna, CancellationToken.None), CancellationToken.None);
Check("no quota without limits", annaQuotas.Count, 0);

var withdrawn = await service.WithdrawAsync(boss, "movie", 100004, null, CancellationToken.None);
Check("own pending movie request withdrawn", withdrawn.Success, true);
Check("Ombi deletes exactly that request", string.Join(",", ombiServer.Calls.Where(c => c.Method == "DELETE").Select(c => c.Path)), "/api/v1/Request/movie/4");
Check("message about it", withdrawn.Message, "Die Anfrage für \"Movie 100004\" wurde zurückgezogen.");
Check("event for the banner", changes.LastOrDefault()?.Withdrawn == true && changes.Last().TmdbId == 100004, true);
Check("someone else's request stays", (await service.WithdrawAsync(boss, "movie", 100009, null, CancellationToken.None)).Message, "Es gibt keine Anfrage von dir für diesen Titel.");
Check("approved request stays", (await service.WithdrawAsync(boss, "movie", 100011, null, CancellationToken.None)).Message,
    "Die Anfrage für \"Movie 100011\" ist schon genehmigt und kann nicht mehr zurückgezogen werden.");
Check("approved request: no withdraw button", (await service.GetMyRequestsAsync(boss, CancellationToken.None)).First(r => r.TmdbId == 100011).CanWithdraw, false);
config.WithdrawApprovedRequests = true;
Check("approved request: withdraw button with the setting", (await service.GetMyRequestsAsync(boss, CancellationToken.None)).First(r => r.TmdbId == 100011).CanWithdraw, true);
Check("approved request withdrawn with the setting", (await service.WithdrawAsync(boss, "movie", 100011, null, CancellationToken.None)).Success
    && !ombiServer.MovieRequests.Any(r => (int)r["theMovieDbId"] == 100011), true);
config.WithdrawApprovedRequests = false;
Check("available request stays", (await service.WithdrawAsync(UserManagerProxy.Anna, "movie", 100003, null, CancellationToken.None)).Message,
    "\"Movie 100003\" ist schon in Emby verfügbar.");
Check("season 1 alone does not take a request for seasons 1+2", (await service.WithdrawAsync(boss, "tv", 500008, new List<int> { 1 }, CancellationToken.None)).Success, false);
Check("seasons 1+2 withdrawn", (await service.WithdrawAsync(boss, "tv", 500008, new List<int> { 1, 2 }, CancellationToken.None)).Success, true);
Check("season request deleted", ombiServer.Calls.Any(c => c.Method == "DELETE" && c.Path == "/api/v1/Request/tv/child/80"), true);
Check("another episode does not take an episode request", (await service.WithdrawAsync(boss, "tv", 590001, null, new List<string> { "1:5" }, CancellationToken.None)).Success, false);
Check("episode request withdrawn", (await service.WithdrawAsync(boss, "tv", 590001, null, new List<string> { "1:4" }, CancellationToken.None)).Success
    && ombiServer.Calls.Any(c => c.Method == "DELETE" && c.Path == "/api/v1/Request/tv/child/90"), true);

// Season requests stay while somebody wants one of their seasons, and only those (anna's S1 and S2 of 590003).
var seasonWishes = new RequestInterest(null, null, null);
var seasonService = new RequestService(ombi, () => config, Fake.Create<IServerConfigurationManager, ServerConfigProxy>(), new Lazy<ILibraryManager>(() => library), logger, seasonWishes);
var fritz = new User { Name = "fritz", InternalId = 9 };
seasonWishes.Add("tv", 590003, 8, new List<int> { 1, 2 });
seasonWishes.Add("tv", 590003, 7, new List<int> { 1 });
seasonWishes.Add("tv", 590003, 9, new List<int> { 2 });
bool ChildExists(int id) => ombiServer.TvRequests.Any(r => (int)r["id"] == id);
Check("the owner withdraws: both stay, others want them", (await seasonService.WithdrawAsync(UserManagerProxy.Anna, "tv", 590003, null, CancellationToken.None)).Success + " " + ChildExists(97) + " " + ChildExists(98),
    "False True True");
await seasonService.WithdrawAsync(boss, "tv", 590003, new List<int> { 1 }, CancellationToken.None);
Check("the last one wanting season 1 withdraws: only the season 1 request goes", ChildExists(97) + " " + ChildExists(98), "False True");
await seasonService.WithdrawAsync(fritz, "tv", 590003, new List<int> { 2 }, CancellationToken.None);
Check("then season 2 as well", ChildExists(98), false);

// Wishes of series count per season and episode: somebody wanting season 2 does not keep season 1.
var wishes = new RequestInterest(null, null, null);
wishes.Add("tv", 1, 8, new List<int> { 1 });
wishes.Add("tv", 1, 7, new List<int> { 2 });
Check("someone else's season does not count", wishes.OthersCount("tv", 1, 8, new List<int> { 1 }), 0);
wishes.Add("tv", 1, 9);
Check("a wish for the whole series counts for every season", wishes.OthersCount("tv", 1, 8, new List<int> { 1 }), 1);
wishes.Add("tv", 1, 7, null, new List<string> { "1:3" });
Check("an episode wish counts for its season", wishes.OthersCount("tv", 1, 8, new List<int> { 1 }), 2);
Check("withdrawing a season takes back its episode wishes", wishes.Remove("tv", 1, 7, new List<int> { 1 }) + " " + wishes.OthersCount("tv", 1, 8, new List<int> { 1 }), "True 1");
Check("everybody who wants a part of the series", wishes.Users("tv", 1).Count, 3);

Check("managing all requests follows the Ombi roles (admin, user, power user, admin role switched off)",
    string.Join(",", new[] { "boss", "anna", "power", "former" }.Select(n => service.CanManageRequestsAsync(new User { Name = n }, CancellationToken.None).Result)),
    "True,False,True,False");
Check("status tells the web app", (await service.GetStatusAsync(boss, CancellationToken.None)).IsAdmin + "/" + (await service.GetStatusAsync(UserManagerProxy.Anna, CancellationToken.None)).IsAdmin, "True/False");
var all = await service.GetAllRequestsAsync(false, CancellationToken.None);
Check("all requests without available ones, newest first", string.Join(", ", all.Select(p => $"{p.MediaType} {p.TmdbId} {p.Status}")),
    "tv 500002 Requested, tv 500003 Denied, tv 500001 Approved, movie 100009 Requested, movie 100002 Denied, movie 100001 Approved");
Check("requester shown", all.First(p => p.TmdbId == 100009).RequestedBy, "Anna");
Check("with available ones", (await service.GetAllRequestsAsync(true, CancellationToken.None)).Count(p => p.Status == "Available"), 3);
var approved = await service.DecideAsync(boss, "movie", 5, 100009, null, CancellationToken.None);
Check("approved", approved.Success && (bool)ombiServer.MovieRequests.First(r => (int)r["id"] == 5)["approved"], true);
var denied = await service.DecideAsync(boss, "tv", 21, 500002, "Gibt es schon", CancellationToken.None);
Check("denied with reason", denied.Success ? (string)ombiServer.TvRequests.First(r => (int)r["id"] == 21)["deniedReason"] : "failed", "Gibt es schon");
Check("nothing waiting any more", (await service.GetAllRequestsAsync(false, CancellationToken.None)).Count(p => p.Status == "Requested"), 0);
await service.DecideAsync(boss, "movie", 5, 100009, "Doch nicht", CancellationToken.None);
var deniedLater = (await service.GetAllRequestsAsync(false, CancellationToken.None)).First(p => p.RequestId == 5);
Check("approved request denied afterwards", deniedLater.Status + " / " + deniedLater.DeniedReason, "Denied / Doch nicht");
await service.DecideAsync(boss, "movie", 5, 100009, null, CancellationToken.None);
Check("denied request approved again", (await service.GetAllRequestsAsync(false, CancellationToken.None)).First(p => p.RequestId == 5).Status, "Approved");
Check("unknown request reported", (await service.DecideAsync(boss, "movie", 999, 0, null, CancellationToken.None)).Success, false);

// Two users want the same movie: the request stays until nobody wants it any more, in either order.
var anna = UserManagerProxy.Anna;
bool MovieRequested(int tmdb) => ombiServer.MovieRequests.Any(r => (int)r["theMovieDbId"] == tmdb);
Check("first user requests", (await service.RequestAsync(anna, "movie", 100060, TvRequestScope.All, null, null, CancellationToken.None)).Success, true);
Check("second user gets \"already requested\"", (await service.RequestAsync(boss, "movie", 100060, TvRequestScope.All, null, null, CancellationToken.None)).Success, false);
var shared = (await service.GetAllRequestsAsync(false, CancellationToken.None)).First(p => p.TmdbId == 100060);
Check("admin list: requester and who else wants it", shared.RequestedBy + " / " + string.Join(",", shared.AlsoWantedBy), "Anna / boss");
var kept = await service.WithdrawAsync(anna, "movie", 100060, null, CancellationToken.None);
Check("the owner withdraws: kept for the other user", kept.Message + " / " + MovieRequested(100060),
    "\"Movie 100060\" bleibt angefragt, weil ihn sich noch jemand anderes wünscht. / True");
var last = await service.WithdrawAsync(boss, "movie", 100060, null, CancellationToken.None);
Check("the other user withdraws too: request gone", last.Message + " / " + MovieRequested(100060), "Die Anfrage für \"Movie 100060\" wurde zurückgezogen. / False");

await service.RequestAsync(anna, "movie", 100061, TvRequestScope.All, null, null, CancellationToken.None);
await service.RequestAsync(boss, "movie", 100061, TvRequestScope.All, null, null, CancellationToken.None);
Check("the other user withdraws first: only the wish goes", (await service.WithdrawAsync(boss, "movie", 100061, null, CancellationToken.None)).Message + " / " + MovieRequested(100061),
    "Dein Wunsch nach \"Movie 100061\" ist entfernt, die Anfrage eines anderen Benutzers bleibt bestehen. / True");
Check("then the owner: request gone", (await service.WithdrawAsync(anna, "movie", 100061, null, CancellationToken.None)).Success && !MovieRequested(100061), true);

await service.RequestAsync(anna, "movie", 100062, TvRequestScope.All, null, null, CancellationToken.None);
await service.RequestAsync(boss, "movie", 100062, TvRequestScope.All, null, null, CancellationToken.None);
await service.WithdrawAsync(anna, "movie", 100062, null, CancellationToken.None);
await service.RequestAsync(anna, "movie", 100062, TvRequestScope.All, null, null, CancellationToken.None);
Check("the owner wants it again: the other user's withdrawal leaves it", (await service.WithdrawAsync(boss, "movie", 100062, null, CancellationToken.None)).Success && MovieRequested(100062), true);

// An admin deletes a request: gone, and nobody's wish counts any more.
var annaRequest = (int)ombiServer.MovieRequests.First(r => (int)r["theMovieDbId"] == 100062)["id"];
var deleted = await service.DeleteRequestAsync(boss, "movie", annaRequest, 100062, CancellationToken.None);
Check("admin deletes a request", deleted.Message + " / " + MovieRequested(100062), "Anfrage gelöscht. Der Titel kann wieder angefragt werden. / False");
Check("wishes forgotten", (await service.WithdrawAsync(anna, "movie", 100062, null, CancellationToken.None)).Message, "Es gibt keine Anfrage von dir für diesen Titel.");

// ---------------------------------------------------------------------------------------------
Section("Library pages: missing episodes, problem reports");
BaseItem.UserManager = Fake.Create<IUserManager, UserManagerProxy>();
var libraryFilm = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "Dune (Bibliothek)", InternalId = 70 };
libraryFilm.SetProviderId("Tmdb", "100030");
var librarySeries = new MediaBrowser.Controller.Entities.TV.Series { Name = "Serie (Bibliothek)", InternalId = 71 };
librarySeries.SetProviderId("Tmdb", "500999");
LibraryProxy.ById[70] = libraryFilm;
LibraryProxy.ById[71] = librarySeries;
LibraryProxy.ById[72] = new Video { ExternalId = "ombirequests|movie|603", InternalId = 72 };

var filmInfo = await service.GetLibraryItemAsync(boss, "70", CancellationToken.None);
Check("movie: problem report possible", $"{filmInfo?.MediaType} {filmInfo?.TmdbId} {filmInfo?.CanReportIssue} {filmInfo?.IssueCategories.Count}", "movie 100030 True 2");
var seriesInfo = await service.GetLibraryItemAsync(boss, "71", CancellationToken.None);
Check("series: missing episodes counted (not the requested ones)", seriesInfo?.MissingEpisodes, 1);
Check("channel items are no library titles", await service.GetLibraryItemAsync(boss, "72", CancellationToken.None), null);
Check("unknown item", await service.GetLibraryItemAsync(boss, "999", CancellationToken.None), null);
LibraryProxy.Hidden.Add(71);
Check("titles the user may not see", await service.GetLibraryItemAsync(boss, "71", CancellationToken.None), null);
LibraryProxy.Hidden.Clear();

var reported = await service.ReportIssueAsync(boss, "70", 2, "Ton fehlt", "ab Minute 5", CancellationToken.None);
var issue = ombiServer.Issues.LastOrDefault();
Check("problem reported", reported.Success, true);
Check("issue sent to Ombi", $"{issue?["title"]} | {issue?["requestType"]} | {issue?["providerId"]} | {issue?["subject"]} | {issue?["issueCategoryId"]}",
    "Dune (Bibliothek) | 1 | 100030 | Ton fehlt | 2");
Check("reported as the user", ombiServer.Calls.Last(c => c.Path == "/api/v1/Issues").User, "boss");
Check("unknown category refused", (await service.ReportIssueAsync(boss, "70", 99, "x", "y", CancellationToken.None)).Success, false);

// ---------------------------------------------------------------------------------------------
Section("Favorites: heart requests, removing it withdraws");
SessionManagerProxy.Messages.Clear();
var favorites = new FavoriteRequestHandler(Fake.Create<IUserDataManager, UserDataManagerProxy>(), Fake.Create<ISessionManager, SessionManagerProxy>(), paths, json, logManager);
var channelMovie = new Video { ExternalId = "ombirequests|movie|100050", InternalId = 60 };
void Heart(bool favorite) => favorites.OnUserDataSaved(null, new UserDataSaveEventArgs
{
    SaveReason = UserDataSaveReason.UpdateUserRating,
    User = boss,
    Item = channelMovie,
    UserData = new UserItemData { IsFavorite = favorite }
});

Heart(true);
await WaitFor(() => SessionManagerProxy.Messages.Count >= 1);
Check("heart requests as the user", ombiServer.Calls.Any(c => c.Method == "POST" && c.Path == "/api/v1/Request/movie" && c.User == "boss"), true);
Check("confirmation with quota", SessionManagerProxy.Messages.LastOrDefault().Text, "\"Movie 100050\" wurde angefragt. Noch 3 Filmanfragen übrig.");
var requestId = (int)ombiServer.MovieRequests.Last()["id"];
Heart(false);
await WaitFor(() => SessionManagerProxy.Messages.Count >= 2);
Check("removing the heart withdraws", ombiServer.Calls.Any(c => c.Method == "DELETE" && c.Path == "/api/v1/Request/movie/" + requestId), true);
Check("message about it", SessionManagerProxy.Messages.LastOrDefault().Header, "Anfrage zurückgezogen");
var deletesBefore = ombiServer.Calls.Count(c => c.Method == "DELETE");
var otherTitle = new Video { ExternalId = "ombirequests|movie|100001", InternalId = 61 };
favorites.OnUserDataSaved(null, new UserDataSaveEventArgs { SaveReason = UserDataSaveReason.UpdateUserRating, User = boss, Item = otherTitle, UserData = new UserItemData { IsFavorite = false } });
await Task.Delay(300);
Check("titles not requested with the heart are left alone", ombiServer.Calls.Count(c => c.Method == "DELETE"), deletesBefore);

var requestedByAnna = new Video { ExternalId = "ombirequests|movie|100009", InternalId = 62 };
SessionManagerProxy.Messages.Clear();
favorites.OnUserDataSaved(null, new UserDataSaveEventArgs { SaveReason = UserDataSaveReason.UpdateUserRating, User = boss, Item = requestedByAnna, UserData = new UserItemData { IsFavorite = true } });
await WaitFor(() => SessionManagerProxy.Messages.Count >= 1);
favorites.OnUserDataSaved(null, new UserDataSaveEventArgs { SaveReason = UserDataSaveReason.UpdateUserRating, User = boss, Item = requestedByAnna, UserData = new UserItemData { IsFavorite = false } });
await WaitFor(() => SessionManagerProxy.Messages.Count >= 2);
Check("heart on someone else's request: removing it only takes back the wish", SessionManagerProxy.Messages.LastOrDefault().Text + " / " + ombiServer.MovieRequests.Any(r => (int)r["id"] == 5),
    "Dein Wunsch nach \"Movie 100009\" ist entfernt, die Anfrage eines anderen Benutzers bleibt bestehen. / True");

// ---------------------------------------------------------------------------------------------
Section("\"Now available\" messages");
SessionManagerProxy.Messages.Clear();
AvailabilityNotifier.SessionStartDelay = TimeSpan.Zero;
var notifier = new AvailabilityNotifier(Fake.Create<IUserManager, UserManagerProxy>(), Fake.Create<ISessionManager, SessionManagerProxy>(), paths, json, logManager);
await notifier.CheckAsync();
Check("first check only remembers", SessionManagerProxy.Messages.Count, 0);
ombiServer.MovieRequests.First(r => (int)r["id"] == 1)["available"] = true;
ombiServer.MovieRequests.First(r => (int)r["id"] == 5)["available"] = true;
await notifier.CheckAsync();
Check("message to the online requester", string.Join(" | ", SessionManagerProxy.Messages.Select(m => m.SessionId + ": " + m.Text)), "boss-web: \"Movie 100001\" ist jetzt in Emby verfügbar.");
Check("offline requester waits", notifier.PendingCount, 1);
await notifier.CheckAsync();
Check("no second message for the same title", SessionManagerProxy.Messages.Count, 1);
SessionManagerProxy.Sessions.Add(new SessionInfo { Id = "anna-tv", UserInternalId = 8 });
notifier.OnSessionStarted(null, new SessionEventArgs { SessionInfo = SessionManagerProxy.Sessions.Last() });
await WaitFor(() => SessionManagerProxy.Messages.Count >= 2);
Check("message when the user comes online", SessionManagerProxy.Messages.LastOrDefault().SessionId + ": " + SessionManagerProxy.Messages.LastOrDefault().Text, "anna-tv: \"Movie 100009\" ist jetzt in Emby verfügbar.");
Check("nothing waiting any more", notifier.PendingCount, 0);
var restarted = new AvailabilityNotifier(Fake.Create<IUserManager, UserManagerProxy>(), Fake.Create<ISessionManager, SessionManagerProxy>(), paths, json, logManager);
await restarted.CheckAsync();
Check("after a restart nothing is sent twice", SessionManagerProxy.Messages.Count, 2);
notifier.Dispose();
restarted.Dispose();

// ---------------------------------------------------------------------------------------------
Section("Lists of the requests page: cached, pages at once");
var callsBefore = ombiServer.Calls.Count(c => c.Path.StartsWith("/api/v2/Search/movie/upcoming/", StringComparison.Ordinal));
int ListCalls(string list) => ombiServer.Calls.Count(c => c.Path.StartsWith("/api/v2/Search/movie/" + list + "/", StringComparison.Ordinal)) - callsBefore;
var firstLoad = await service.DiscoverAsync(boss, null, "movie:upcoming", 40, CancellationToken.None);
Check("two pages for 40 titles", ListCalls("upcoming"), 2);
var again = await service.DiscoverAsync(UserManagerProxy.Anna, null, "movie:upcoming", 40, CancellationToken.None);
Check("shown again: from the cache, same titles", ListCalls("upcoming") + " " + (string.Join(",", again.Select(i => i.TmdbId)) == string.Join(",", firstLoad.Select(i => i.TmdbId))), "2 True");
again[0].Status = "Changed";
Check("every caller gets its own copies", (await service.DiscoverAsync(boss, null, "movie:upcoming", 40, CancellationToken.None))[0].Status != "Changed", true);
await service.RequestAsync(boss, "movie", firstLoad[0].TmdbId, TvRequestScope.All, null, null, CancellationToken.None);
await service.DiscoverAsync(boss, null, "movie:upcoming", 40, CancellationToken.None);
Check("a request reloads the page with the title, not the others", ListCalls("upcoming"), 3);

// ---------------------------------------------------------------------------------------------
Section("Ombi or its proxy busy (503)");
int CallsTo(string path) => ombiServer.Calls.Count(c => c.Path == path);
ombiServer.BusyResponses = 1;
var readBefore = CallsTo("/api/v2/Search/movie/100777");
Check("a read is tried again", (await plugin.OmbiClient.GetMovieAsync(null, 100777, CancellationToken.None))?.theMovieDbId + " after " + (CallsTo("/api/v2/Search/movie/100777") - readBefore) + " calls",
    "100777 after 2 calls");
await service.GetIdentityAsync(boss, CancellationToken.None);
ombiServer.BusyResponses = 1;
var postsBefore = CallsTo("/api/v1/Request/movie");
string busyOutcome;
try
{
    busyOutcome = (await service.RequestAsync(boss, "movie", 100031, TvRequestScope.All, null, null, CancellationToken.None)).Success.ToString();
}
catch (OmbiException ex)
{
    busyOutcome = "HTTP " + (int?)ex.StatusCode;
}
Check("a request is never sent twice", busyOutcome + " after " + (CallsTo("/api/v1/Request/movie") - postsBefore) + " call", "HTTP 503 after 1 call");

// ---------------------------------------------------------------------------------------------
Section("Who may use the requests page and the requests folder");
var everybody = new[] { boss, UserManagerProxy.Anna };
UserAccess.ApplyPageUsers(config, everybody, "7");
Check("page off for the users not selected, on for users created later",
    config.RequestsPageDisabledUsers + " / " + config.IsRequestsPageEnabled(8) + " / " + config.IsRequestsPageEnabled(99), "8 / False / True");
Check("the web app learns it", (await service.GetStatusAsync(UserManagerProxy.Anna, CancellationToken.None)).PageEnabled + "/" + (await service.GetStatusAsync(boss, CancellationToken.None)).PageEnabled, "False/True");
Check("selection shown again", UserAccess.PageUsers(config, everybody), "7");
config.RequestsPageDisabledUsers = string.Empty;

var embyUsers = Fake.Create<IUserManager, UserManagerProxy>();
var accessLog = logManager.GetLogger("test");
var channelIds = new List<string> { "ours", "other" };
Check("folder: everybody has all channels at first", UserAccess.FolderUsers(embyUsers, everybody, "ours"), "7,8");
UserAccess.ApplyFolderUsers(embyUsers, everybody, "7", "ours", channelIds, accessLog);
var annaPolicy = embyUsers.GetUserPolicy(UserManagerProxy.Anna);
Check("folder taken away, the other channels stay", annaPolicy.EnableAllChannels + " " + string.Join(",", annaPolicy.EnabledChannels) + " / " + UserAccess.FolderUsers(embyUsers, everybody, "ours"),
    "False other / 7");
UserAccess.ApplyFolderUsers(embyUsers, everybody, "7,8", "ours", channelIds, accessLog);
Check("given back: all channels again", embyUsers.GetUserPolicy(UserManagerProxy.Anna).EnableAllChannels, true);
Check("channel list without the requests channel: nothing changes", UserAccess.ApplyFolderUsers(embyUsers, everybody, "7", "ours", new List<string> { "other" }, accessLog), 0);
UserManagerProxy.Policies.Clear();

// ---------------------------------------------------------------------------------------------
Directory.Delete(dataDir, true);
Console.WriteLine();
Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
return fails;
