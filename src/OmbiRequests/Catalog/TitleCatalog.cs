using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using OmbiRequests.Channel;
using OmbiRequests.Configuration;
using OmbiRequests.Ombi;
using OmbiRequests.Requests;

namespace OmbiRequests.Catalog
{
    /// <summary>
    /// The titles of the channel folders "All movies" and "All series", mainly so that Emby's search
    /// finds titles that are not in Emby yet. Loading thousands of titles from Ombi takes long, so
    /// they are imported once (started by the admin) and afterwards only titles from Ombi's current
    /// lists that are not in the catalog yet are added every few days. The list is stored in Emby's
    /// data folder. The request states are recomputed on every channel refresh from Ombi's request
    /// lists and the Emby library, which costs a few calls instead of one per title.
    /// </summary>
    public class TitleCatalog
    {
        private const string FileName = "ombirequests-catalog.json";
        /// <summary>Requests of all users loaded for the states; effectively all of them.</summary>
        private const int MaxRequests = 100000;
        private const int NewTitlesPerList = 200;
        private const int MinMovies = 100;
        private const int MinSeries = 50;

        /// <summary>
        /// Trailers looked up at the same time: half of the calls the Ombi client sends at once, so pages and
        /// buttons of the users never wait behind a long lookup run.
        /// </summary>
        private const int TrailerLookupsAtOnce = 2;

        /// <summary>TMDb serves at most 500 pages of 20 per list.</summary>
        private const int TmdbListLimit = 10000;
        private const int TopUpChunk = 200;

        /// <summary>Movies taken per year and round when going through the years.</summary>
        private const int YearStep = 100;

        /// <summary>Ombi ignores release years up to 1900.</summary>
        private const int FirstYear = 1901;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(6);

        // Sources of new titles: in cinemas, announced and currently watched. Only titles that are
        // not in the catalog yet are added, so no date filter is needed.
        private static readonly string[] NewMovieLists = { "nowplaying", "upcoming", "popular" };
        private static readonly string[] NewTvLists = { "trending", "anticipated", "popular" };

        private readonly string path;
        private readonly IJsonSerializer json;
        private readonly OmbiClient ombi;
        private readonly RequestService service;
        private readonly Func<PluginConfiguration> getConfig;
        private readonly LibraryIndex libraryIndex;
        private readonly ILogger logger;
        private readonly SemaphoreSlim jobLock = new SemaphoreSlim(1, 1);
        private readonly object dataLock = new object();

        private CatalogData data;
        private DateTime? lastUpdateAttemptUtc;

        public TitleCatalog(
            string dataPath,
            IJsonSerializer json,
            OmbiClient ombi,
            RequestService service,
            Func<PluginConfiguration> getConfig,
            Lazy<ILibraryManager> libraryManager,
            ILogger logger)
        {
            path = Path.Combine(dataPath, FileName);
            this.json = json;
            this.ombi = ombi;
            this.service = service;
            this.getConfig = getConfig;
            libraryIndex = new LibraryIndex(libraryManager, logger);
            this.logger = logger;
        }

        private CatalogData Data
        {
            get
            {
                lock (dataLock)
                {
                    return data ?? (data = Load());
                }
            }
        }

        public bool IsImported => Data.ImportedUtc != null;

        /// <summary>Titles whose trailer and background were looked up, and all titles (diagnostics).</summary>
        public Tuple<int, int> TrailerProgress()
        {
            lock (dataLock)
            {
                var entries = Data.Entries;
                return Tuple.Create(entries.Count(e => !e.NeedsDetails()), entries.Count);
            }
        }

        public bool HasTitles(string mediaType) => Data.Entries.Any(e => e.MediaType == mediaType);

        public bool IsUpdateDue(PluginConfiguration config)
        {
            if (!config.IsConfigured || config.CatalogUpdateIntervalDays <= 0 || (!config.ChannelAllMovies && !config.ChannelAllTv))
            {
                return false;
            }

            var current = Data;
            var now = DateTime.UtcNow;
            if (current.ImportedUtc == null || (lastUpdateAttemptUtc.HasValue && now - lastUpdateAttemptUtc.Value < RetryDelay))
            {
                return false;
            }

            return now - (current.UpdatedUtc ?? current.ImportedUtc.Value) >= TimeSpan.FromDays(config.CatalogUpdateIntervalDays);
        }

        /// <summary>For the settings page.</summary>
        public string Describe(PluginConfiguration config)
        {
            var current = Data;
            if (current.ImportedUtc == null)
            {
                return "Not imported yet. Set the numbers below, save, then start the import.";
            }

            var movies = current.Entries.Count(e => e.MediaType == RequestService.Movie);
            var series = current.Entries.Count - movies;
            var text = string.Format(CultureInfo.InvariantCulture, "{0:N0} movies and {1:N0} series, imported {2}", movies, series, Format(current.ImportedUtc));
            var last = current.UpdatedUtc ?? current.ImportedUtc.Value;
            if (last > current.ImportedUtc.Value)
            {
                text += ", new titles loaded " + Format(last);
            }

            text += config.CatalogUpdateIntervalDays > 0
                ? ". Next automatic update: " + Format(last.AddDays(config.CatalogUpdateIntervalDays)) + "."
                : ". Automatic updates are off.";

            var imported = current.Language ?? "en";
            if (!string.Equals(imported, service.ContentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                text += $" Titles and posters were loaded in '{imported}', the language is now '{service.ContentLanguage}': start the import again.";
            }

            return text;
        }

        /// <summary>
        /// Replaces the catalog with the number of titles from the settings, without upper limit. Per
        /// type: the most popular and the best rated titles (2/3 and 1/3), the current lists, and if
        /// that is not enough, the rest of these lists, more series lists and the movies year by year,
        /// until the number is reached or Ombi has no more titles.
        /// </summary>
        public async Task ImportAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await jobLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var config = getConfig();
                EnsureConfigured(config);

                var targets = new Dictionary<string, int>();
                if (config.ChannelAllMovies)
                {
                    targets[RequestService.Movie] = Math.Max(MinMovies, config.CatalogMovieCount);
                }

                if (config.ChannelAllTv)
                {
                    targets[RequestService.Tv] = Math.Max(MinSeries, config.CatalogTvCount);
                }

                if (targets.Count == 0)
                {
                    throw new InvalidOperationException("The folders 'All movies' and 'All series' are both switched off, nothing to import.");
                }

                logger.Info("Catalog import started");
                var run = new ImportRun(new PartProgress(progress, 0, 60), targets.Values.Sum(), await service.GetContentIdentityAsync(cancellationToken).ConfigureAwait(false));
                var loadedTypes = new HashSet<string>();
                foreach (var target in targets)
                {
                    if (await ImportTypeAsync(run, target.Key, target.Value, cancellationToken).ConfigureAwait(false))
                    {
                        loadedTypes.Add(target.Key);
                    }
                }

                if (loadedTypes.Count == 0)
                {
                    throw new OmbiException("Ombi did not deliver any of the lists, see the errors above.");
                }

                var now = DateTime.UtcNow;
                CatalogData imported;
                lock (dataLock)
                {
                    // If Ombi delivered none of the main lists of a type, the previous titles of that type stay.
                    var kept = Data.Entries.Where(e => !loadedTypes.Contains(e.MediaType) && targets.ContainsKey(e.MediaType)).ToList();
                    var entries = Merge(kept, run.Items, now, out _);

                    // Keeps what Emby already got, so the first refresh only updates changed titles.
                    var previous = Data.Entries.GroupBy(e => e.GetKey()).ToDictionary(g => g.Key, g => g.First());
                    foreach (var entry in entries)
                    {
                        if (previous.TryGetValue(entry.GetKey(), out var old))
                        {
                            entry.ListedStatus = old.ListedStatus;
                            entry.ListedPoster = old.ListedPoster;
                            entry.ListedTrailer = old.ListedTrailer;
                            entry.ListedBackdrop = old.ListedBackdrop;
                            if (!entry.TrailerChecked && old.TrailerChecked)
                            {
                                entry.TrailerUrl = old.TrailerUrl;
                                entry.TrailerChecked = true;
                            }

                            if (!entry.BackdropChecked && old.BackdropChecked)
                            {
                                entry.BackdropUrl = old.BackdropUrl;
                                entry.BackdropChecked = true;
                            }

                            entry.AddedUtc = old.AddedUtc;
                        }
                    }

                    // Without Ombi user Ombi answers in English (see RequestService.GetContentIdentityAsync).
                    var language = run.Identity != null ? service.ContentLanguage : "en";
                    data = imported = new CatalogData { ImportedUtc = now, UpdatedUtc = now, Language = language, Entries = entries };
                    Save(data);
                }

                await LookUpTrailersAsync(imported.Entries.Where(e => e.NeedsDetails()).ToList(), new PartProgress(progress, 60, 100), cancellationToken).ConfigureAwait(false);
                progress.Report(100);
                logger.Info("Catalog import finished: {0} movies, {1} series",
                    imported.Entries.Count(e => e.MediaType == RequestService.Movie), imported.Entries.Count(e => e.MediaType == RequestService.Tv));
            }
            finally
            {
                jobLock.Release();
            }

            OmbiChannel.RequestRefresh(TimeSpan.FromSeconds(5));
        }

        /// <summary>Adds the titles of Ombi's current lists (cinema, upcoming, trending, popular) that are not in the catalog yet.</summary>
        public async Task UpdateAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            int added;
            await jobLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lastUpdateAttemptUtc = DateTime.UtcNow;
                var config = getConfig();
                EnsureConfigured(config);
                if (Data.ImportedUtc == null)
                {
                    throw new InvalidOperationException("Start the import in the plugin settings first.");
                }

                var sources = NewTitleSources(config).ToList();
                if (sources.Count == 0)
                {
                    logger.Info("Catalog update skipped: the folders 'All movies' and 'All series' are switched off.");
                    return;
                }

                var identity = await service.GetContentIdentityAsync(cancellationToken).ConfigureAwait(false);
                var loaded = await LoadAsync(sources, identity, new PartProgress(progress, 0, 70), cancellationToken).ConfigureAwait(false);
                List<CatalogEntry> newEntries;
                lock (dataLock)
                {
                    var current = Data;
                    var now = DateTime.UtcNow;
                    var entries = Merge(current.Entries, loaded, now, out added);
                    data = new CatalogData { ImportedUtc = current.ImportedUtc, UpdatedUtc = now, Language = current.Language, Entries = entries };
                    Save(data);
                    newEntries = entries.Where(e => e.AddedUtc == now && e.NeedsDetails()).ToList();
                }

                await LookUpTrailersAsync(newEntries, new PartProgress(progress, 70, 100), cancellationToken).ConfigureAwait(false);

                logger.Info("Catalog update finished: {0} new titles", added);
            }
            finally
            {
                jobLock.Release();
            }

            if (added > 0)
            {
                OmbiChannel.RequestRefresh(TimeSpan.FromSeconds(5));
            }
        }

        /// <summary>All titles with current request states.</summary>
        public async Task<List<CatalogEntry>> GetEntriesAsync(CancellationToken cancellationToken)
        {
            var entries = Data.Entries.ToList();
            if (entries.Count == 0)
            {
                return entries;
            }

            // Without user these lists contain the requests of all users.
            var movieRequests = await TryLoadRequestsAsync(ombi.GetMovieRequestsAsync(null, MaxRequests, cancellationToken), "movie").ConfigureAwait(false);
            var tvRequests = await TryLoadRequestsAsync(ombi.GetTvRequestsAsync(null, MaxRequests, cancellationToken), "tv").ConfigureAwait(false);
            var movieIds = libraryIndex.Load("Movie");
            var seriesIds = libraryIndex.Load("Series");
            if (movieIds == null || seriesIds == null)
            {
                return entries;
            }

            var movieStates = movieRequests?
                .GroupBy(r => r.theMovieDbId)
                .ToDictionary(g => g.Key, g => g.First());
            var tvStates = tvRequests?
                .Where(r => r.parentRequest != null)
                .GroupBy(r => r.parentRequest.externalProviderId)
                .ToDictionary(g => g.Key, g => g.ToList());

            lock (dataLock)
            {
                foreach (var entry in entries)
                {
                    string status;
                    if (entry.MediaType == RequestService.Movie)
                    {
                        var inLibrary = IsInLibrary(movieIds, entry);
                        if (movieStates == null && !inLibrary)
                        {
                            // Ombi did not answer: keep the last known state.
                            continue;
                        }

                        MovieRequest request = null;
                        movieStates?.TryGetValue(entry.TmdbId, out request);
                        status = MovieStatus(inLibrary, request);
                    }
                    else
                    {
                        var inLibrary = IsInLibrary(seriesIds, entry);
                        if (tvStates == null && !inLibrary)
                        {
                            continue;
                        }

                        List<ChildRequest> requests = null;
                        tvStates?.TryGetValue(entry.TmdbId, out requests);
                        status = SeriesStatus(inLibrary, requests ?? new List<ChildRequest>(), entry.LoadedStatus);
                    }

                    entry.Status = status;
                }
            }

            return entries;
        }

        /// <summary>Remembers which states and pictures Emby got (see <see cref="CatalogEntry.ListedStatus"/>).</summary>
        public void MarkListed(IEnumerable<CatalogEntry> entries)
        {
            lock (dataLock)
            {
                var changed = 0;
                foreach (var entry in entries.Where(e => e.HasUnlistedChanges()))
                {
                    entry.MarkListed();
                    changed++;
                }

                if (changed > 0 && data != null)
                {
                    // Marking runs once per A-Z folder during a refresh: the first one writes, the others
                    // within half a minute are written together afterwards.
                    if (DateTime.UtcNow - lastMarkSaveUtc > SaveBatchTime)
                    {
                        lastMarkSaveUtc = DateTime.UtcNow;
                        Save(data);
                    }
                    else
                    {
                        ScheduleSave();
                    }
                }
            }
        }

        /// <summary>
        /// After a request the plugin writes the new state into the items itself, so the next refresh sends
        /// the catalog's state again (e.g. "not requested" after a withdrawal).
        /// </summary>
        public void ForgetListed(string mediaType, int tmdbId)
        {
            lock (dataLock)
            {
                foreach (var entry in Data.Entries.Where(e => e.MediaType == mediaType && e.TmdbId == tmdbId))
                {
                    entry.ListedStatus = null;
                }
            }
        }

        private static readonly TimeSpan SaveBatchTime = TimeSpan.FromSeconds(30);
        private Timer saveTimer;
        private DateTime lastMarkSaveUtc;

        private void ScheduleSave()
        {
            lock (dataLock)
            {
                if (saveTimer == null)
                {
                    saveTimer = new Timer(_ =>
                    {
                        lock (dataLock)
                        {
                            if (data != null)
                            {
                                Save(data);
                            }
                        }
                    }, null, SaveBatchTime, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    saveTimer.Change(SaveBatchTime, Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <summary>
        /// Looks up the trailers Ombi's lists left out (movies) and the backgrounds catalogs of older
        /// versions have not stored, a batch at a time because each one costs a detail lookup in Ombi.
        /// Runs hourly (see <see cref="CatalogScheduler"/>) until every title was looked up once; skipped
        /// while an import or update runs.
        /// </summary>
        /// <returns>The number of titles with a trailer or background found.</returns>
        /// <param name="max">Titles to look up at most (the hourly run takes 250, the task all).</param>
        /// <param name="wait">Waits for a running import or update instead of skipping this run.</param>
        public async Task<int> FillTrailersAsync(int max, CancellationToken cancellationToken, IProgress<double> progress = null, bool wait = false)
        {
            if (wait)
            {
                await jobLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!await jobLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            int found, looked, left;
            try
            {
                // Movies first: series details are expensive, and Ombi's series lists often bring the trailer.
                var todo = Data.Entries
                    .Where(e => e.NeedsDetails())
                    .OrderBy(e => e.MediaType == RequestService.Tv ? 1 : 0)
                    .Take(max)
                    .ToList();
                var result = await LookUpTrailersAsync(todo, progress, cancellationToken).ConfigureAwait(false);
                looked = result.Item1;
                found = result.Item2;

                lock (dataLock)
                {
                    left = Data.Entries.Count(e => e.NeedsDetails());
                }
            }
            finally
            {
                jobLock.Release();
            }

            if (looked > 0)
            {
                logger.Info("Catalog: {0} titles without looked-up trailer or background left", left);
            }

            // No refresh of its own: it reads the whole channel, the next regular one takes them along.
            return found;
        }

        /// <summary>
        /// Looks up the trailers (Ombi's movie lists have none) and backgrounds of the given titles in Ombi's
        /// details, a few at the same time, and saves the catalog. After several failures in a row it stops:
        /// Ombi is probably down, the hourly run continues later.
        /// </summary>
        /// <returns>Titles looked up, titles with a trailer or background found.</returns>
        private async Task<Tuple<int, int>> LookUpTrailersAsync(IList<CatalogEntry> todo, IProgress<double> progress, CancellationToken cancellationToken)
        {
            int looked = 0, found = 0, done = 0, failuresInRow = 0;
            if (todo.Count == 0)
            {
                return Tuple.Create(0, 0);
            }

            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var throttle = new SemaphoreSlim(TrailerLookupsAtOnce))
            {
                await Task.WhenAll(todo.Select(async entry =>
                {
                    try
                    {
                        await throttle.WaitAsync(stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        var extras = await service.GetTrailerAndBackdropAsync(entry.MediaType, entry.TmdbId, stop.Token).ConfigureAwait(false);
                        var trailer = extras.Item1;
                        Interlocked.Exchange(ref failuresInRow, 0);
                        lock (dataLock)
                        {
                            entry.TrailerUrl = trailer ?? entry.TrailerUrl;
                            entry.TrailerChecked = true;
                            entry.BackdropUrl = extras.Item2 ?? entry.BackdropUrl;
                            entry.BackdropChecked = true;
                        }

                        Interlocked.Increment(ref looked);
                        if (trailer != null || extras.Item2 != null)
                        {
                            Interlocked.Increment(ref found);
                        }
                    }
                    catch (OmbiException ex)
                    {
                        logger.Debug("Catalog: no details for {0} {1}: {2}", entry.MediaType, entry.TmdbId, ex.Message);
                        if (Interlocked.Increment(ref failuresInRow) == 5)
                        {
                            logger.Warn("Catalog: trailer and background lookup stopped after 5 failures in a row, the hourly run continues");
                            stop.Cancel();
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Stopped after failures.
                    }
                    finally
                    {
                        throttle.Release();
                        progress?.Report(100.0 * Interlocked.Increment(ref done) / todo.Count);
                    }
                })).ConfigureAwait(false);
            }

            lock (dataLock)
            {
                if (looked > 0 && data != null)
                {
                    Save(data);
                }
            }

            logger.Info("Catalog: looked up {0} titles, trailer or background found for {1}", looked, found);
            return Tuple.Create(looked, found);
        }

        /// <returns>True if at least one of the main lists (popular, best rated) could be loaded.</returns>
        private async Task<bool> ImportTypeAsync(ImportRun run, string mediaType, int target, CancellationToken cancellationToken)
        {
            var isMovie = mediaType == RequestService.Movie;
            var cursors = new Dictionary<string, Cursor>();
            Cursor CursorOf(string list) => cursors.TryGetValue(list, out var cursor) ? cursor : cursors[list] = new Cursor(list);

            // "mostwatched" is TMDb's list of the best rated series.
            var mainLists = isMovie ? new[] { "popular", "toprated" } : new[] { "popular", "mostwatched" };
            var mainLoaded = await LoadIntoAsync(run, mediaType, CursorOf(mainLists[0]), target - target / 3, cancellationToken).ConfigureAwait(false);
            mainLoaded |= await LoadIntoAsync(run, mediaType, CursorOf(mainLists[1]), target / 3, cancellationToken).ConfigureAwait(false);

            // The popular lists are about the past; this adds what is in cinemas, announced or trending now.
            foreach (var list in isMovie ? NewMovieLists : NewTvLists)
            {
                var cursor = CursorOf(list);
                await LoadIntoAsync(run, mediaType, cursor, NewTitlesPerList - cursor.Position, cancellationToken).ConfigureAwait(false);
            }

            // Not enough yet (lists overlap, or the number is bigger than the main lists): continue them.
            foreach (var list in isMovie ? mainLists : mainLists.Concat(new[] { "trending", "anticipated" }))
            {
                var cursor = CursorOf(list);
                while (!cursor.Ended && run.Count(mediaType) < target)
                {
                    var need = target - run.Count(mediaType);
                    await LoadIntoAsync(run, mediaType, cursor, Math.Max(20, Math.Min(TopUpChunk, need)), cancellationToken).ConfigureAwait(false);
                }
            }

            if (isMovie && run.Count(mediaType) < target)
            {
                await TopUpMoviesByYearAsync(run, target, cancellationToken).ConfigureAwait(false);
            }

            var count = run.Count(mediaType);
            if (count < target)
            {
                logger.Info("Catalog: Ombi delivered {0:N0} of the requested {1:N0} {2} titles, there are no more.", count, target, mediaType);
            }

            return mainLoaded;
        }

        /// <summary>Loads the next "count" positions of a list; false if the list could not be loaded.</summary>
        private async Task<bool> LoadIntoAsync(ImportRun run, string mediaType, Cursor cursor, int count, CancellationToken cancellationToken)
        {
            count = Math.Min(count, TmdbListLimit - cursor.Position);
            if (cursor.Ended || count <= 0)
            {
                cursor.Ended |= cursor.Position >= TmdbListLimit;
                return false;
            }

            try
            {
                var range = await service.LoadListRangeAsync(null, run.Identity, mediaType, cursor.Name, cursor.Position, count, cancellationToken, run.Report).ConfigureAwait(false);
                cursor.Advance(count, range.Ended);
                var added = run.Add(range.Items);
                logger.Info("Catalog: {0} list '{1}' up to position {2}: {3} titles, {4} new", mediaType, cursor.Name, cursor.Position, range.Items.Count, added);
                return true;
            }
            catch (OmbiException ex)
            {
                cursor.Ended = true;
                logger.Error("Catalog: loading the {0} list '{1}' failed: {2}", mediaType, cursor.Name, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Goes through the release years with Ombi's advanced search, round by round and newest year
        /// first, so that every year contributes its best known movies before lesser known ones.
        /// </summary>
        private async Task TopUpMoviesByYearAsync(ImportRun run, int target, CancellationToken cancellationToken)
        {
            var years = Enumerable.Range(FirstYear, DateTime.UtcNow.Year + 2 - FirstYear)
                .Reverse()
                .Select(y => new Cursor(y.ToString(CultureInfo.InvariantCulture)) { Year = y })
                .ToList();
            var before = run.Count(RequestService.Movie);
            var failuresInRow = 0;
            while (run.Count(RequestService.Movie) < target && years.Any(c => !c.Ended))
            {
                foreach (var cursor in years.Where(c => !c.Ended))
                {
                    var need = target - run.Count(RequestService.Movie);
                    if (need <= 0)
                    {
                        break;
                    }

                    var count = Math.Min(Math.Min(YearStep, Math.Max(20, need)), TmdbListLimit - cursor.Position);
                    try
                    {
                        var range = await service.LoadMoviesOfYearAsync(run.Identity, cursor.Year, cursor.Position, count, cancellationToken, run.Report).ConfigureAwait(false);
                        cursor.Advance(count, range.Ended);
                        run.Add(range.Items);
                        failuresInRow = 0;
                    }
                    catch (OmbiException ex)
                    {
                        cursor.Ended = true;
                        logger.Error("Catalog: loading the movies of {0} failed: {1}", cursor.Year, ex.Message);
                        if (++failuresInRow >= 3)
                        {
                            logger.Warn("Catalog: Ombi's advanced search does not work, the movies are not loaded by year.");
                            return;
                        }
                    }
                }
            }

            logger.Info("Catalog: {0:N0} movies added by release year", run.Count(RequestService.Movie) - before);
        }

        private IEnumerable<Source> NewTitleSources(PluginConfiguration config)
        {
            if (config.ChannelAllMovies)
            {
                foreach (var list in NewMovieLists)
                {
                    yield return new Source(RequestService.Movie, list, NewTitlesPerList);
                }
            }

            if (config.ChannelAllTv)
            {
                foreach (var list in NewTvLists)
                {
                    yield return new Source(RequestService.Tv, list, NewTitlesPerList);
                }
            }
        }

        private async Task<List<MediaItemDto>> LoadAsync(IReadOnlyList<Source> sources, OmbiIdentity identity, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var total = (double)Math.Max(1, sources.Sum(s => s.Count));
            var done = 0;
            var failed = 0;
            var result = new List<MediaItemDto>();
            foreach (var source in sources)
            {
                var offset = done;
                try
                {
                    var items = await service.LoadListAsync(null, identity, source.MediaType, source.List, source.Count, cancellationToken,
                        n => progress.Report(100 * (offset + n) / total)).ConfigureAwait(false);
                    result.AddRange(items.Where(i => i.TmdbId > 0));
                    logger.Info("Catalog: {0} list '{1}' delivered {2} titles", source.MediaType, source.List, items.Count);
                }
                catch (OmbiException ex)
                {
                    failed++;
                    logger.Error("Catalog: loading the {0} list '{1}' failed: {2}", source.MediaType, source.List, ex.Message);
                }

                done += source.Count;
                progress.Report(100 * done / total);
            }

            if (failed == sources.Count)
            {
                throw new OmbiException("Ombi did not deliver any of the lists, see the errors above.");
            }

            return result;
        }

        private static List<CatalogEntry> Merge(List<CatalogEntry> existing, IEnumerable<MediaItemDto> loaded, DateTime now, out int added)
        {
            var result = new List<CatalogEntry>(existing);
            var byKey = new Dictionary<string, CatalogEntry>();
            foreach (var entry in existing)
            {
                byKey[entry.GetKey()] = entry;
            }

            added = 0;
            foreach (var item in loaded)
            {
                var fresh = CatalogEntry.From(item, now);
                if (byKey.TryGetValue(fresh.GetKey(), out var old))
                {
                    // Newer texts and posters; the state is recomputed on the next channel refresh.
                    old.Title = fresh.Title;
                    old.Year = fresh.Year;
                    old.Overview = fresh.Overview;
                    old.PosterUrl = fresh.PosterUrl ?? old.PosterUrl;
                    old.Rating = fresh.Rating;
                    old.ReleaseDate = fresh.ReleaseDate;
                    old.ImdbId = fresh.ImdbId ?? old.ImdbId;
                    old.TvdbId = fresh.TvdbId ?? old.TvdbId;
                    old.LoadedStatus = fresh.LoadedStatus;
                    if (fresh.TrailerChecked)
                    {
                        old.TrailerUrl = fresh.TrailerUrl;
                        old.TrailerChecked = true;
                    }

                    if (fresh.BackdropChecked)
                    {
                        old.BackdropUrl = fresh.BackdropUrl;
                        old.BackdropChecked = true;
                    }

                    continue;
                }

                byKey[fresh.GetKey()] = fresh;
                result.Add(fresh);
                added++;
            }

            return result;
        }

        private static string MovieStatus(bool inLibrary, MovieRequest request)
        {
            if (inLibrary)
            {
                return nameof(MediaStatus.Available);
            }

            return request == null
                ? nameof(MediaStatus.None)
                : RequestService.GetStatus(request.available, false, request.denied, request.approved, true).ToString();
        }

        /// <param name="requests">The season requests of all users for the series.</param>
        private static string SeriesStatus(bool inLibrary, List<ChildRequest> requests, string loadedStatus)
        {
            var open = requests.Where(r => !r.available).ToList();
            if (inLibrary)
            {
                // Whether seasons are missing is only known from Ombi (load time) or from open requests.
                return open.Count > 0 || loadedStatus == nameof(MediaStatus.PartiallyAvailable)
                    ? nameof(MediaStatus.PartiallyAvailable)
                    : nameof(MediaStatus.Available);
            }

            if (requests.Count == 0)
            {
                return nameof(MediaStatus.None);
            }

            if (open.Count == 0)
            {
                return nameof(MediaStatus.Available);
            }

            if (open.Count < requests.Count)
            {
                return nameof(MediaStatus.PartiallyAvailable);
            }

            if (open.All(r => r.denied))
            {
                return nameof(MediaStatus.Denied);
            }

            return open.Any(r => r.approved) ? nameof(MediaStatus.Approved) : nameof(MediaStatus.Requested);
        }

        private static bool IsInLibrary(HashSet<string> ids, CatalogEntry entry)
        {
            return LibraryIndex.Matches(ids, entry.TmdbId, entry.ImdbId, entry.TvdbId);
        }

        private async Task<List<T>> TryLoadRequestsAsync<T>(Task<RequestsPage<T>> request, string kind)
        {
            try
            {
                return (await request.ConfigureAwait(false))?.collection ?? new List<T>();
            }
            catch (OmbiException ex)
            {
                logger.Warn("Catalog: loading the {0} requests failed, keeping the last known states: {1}", kind, ex.Message);
                return null;
            }
        }

        private static void EnsureConfigured(PluginConfiguration config)
        {
            if (!config.IsConfigured)
            {
                throw new InvalidOperationException("Enter the Ombi URL and API key in the plugin settings first.");
            }
        }

        private static string Format(DateTime? utc) => utc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";

        private CatalogData Load()
        {
            try
            {
                var file = File.Exists(path) ? path : File.Exists(path + ".tmp") ? path + ".tmp" : null;
                if (file != null)
                {
                    var loaded = json.DeserializeFromFile<CatalogData>(file);
                    if (loaded != null)
                    {
                        loaded.Entries = loaded.Entries ?? new List<CatalogEntry>();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Reading {0} failed, the catalog starts empty", ex, path);
            }

            return new CatalogData();
        }

        /// <summary>Written to a temporary file first, so a crash never leaves a half written catalog.</summary>
        private void Save(CatalogData value)
        {
            var tempFile = path + ".tmp";
            try
            {
                json.SerializeToFile(value, tempFile);
                if (File.Exists(path))
                {
                    // In one step: a crash in between must not leave the server without catalog (Emby would
                    // then delete "All movies" / "All series" with all items).
                    File.Replace(tempFile, path, null);
                }
                else
                {
                    File.Move(tempFile, path);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Saving {0} failed", ex, path);
            }
        }

        private sealed class Source
        {
            public Source(string mediaType, string list, int count)
            {
                MediaType = mediaType;
                List = list;
                Count = count;
            }

            public string MediaType { get; }

            public string List { get; }

            public int Count { get; }
        }

        /// <summary>How far a list (or a release year) has been loaded during the import.</summary>
        private sealed class Cursor
        {
            public Cursor(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public int Year { get; set; }

            public int Position { get; private set; }

            public bool Ended { get; set; }

            public void Advance(int count, bool listEnded)
            {
                Position += count;
                Ended = listEnded || Position >= TmdbListLimit;
            }
        }

        /// <summary>The titles collected by one import, each once, with the progress shown in Emby's task list.</summary>
        /// <summary>A step of a task (e.g. the trailers after the lists) on its part of the progress bar, never going back.</summary>
        private sealed class PartProgress : IProgress<double>
        {
            private readonly IProgress<double> target;
            private readonly double from;
            private readonly double to;
            private readonly object reportLock = new object();
            private double reported = -1;

            public PartProgress(IProgress<double> target, double from, double to)
            {
                this.target = target;
                this.from = from;
                this.to = to;
            }

            public void Report(double value)
            {
                var mapped = from + ((to - from) * Math.Max(0, Math.Min(100, value)) / 100);
                lock (reportLock)
                {
                    if (mapped <= reported)
                    {
                        return;
                    }

                    reported = mapped;
                    target?.Report(mapped);
                }
            }
        }

        private sealed class ImportRun
        {
            private readonly HashSet<string> keys = new HashSet<string>();
            private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
            private readonly IProgress<double> progress;
            private readonly double total;
            private double reported;

            public ImportRun(IProgress<double> progress, int total, OmbiIdentity identity)
            {
                this.progress = progress;
                this.total = Math.Max(1, total);
                Identity = identity;
            }

            /// <summary>The Ombi user whose language the titles are loaded in.</summary>
            public OmbiIdentity Identity { get; }

            public List<MediaItemDto> Items { get; } = new List<MediaItemDto>();

            public int Count(string mediaType) => counts.TryGetValue(mediaType, out var count) ? count : 0;

            /// <returns>The number of titles that were new.</returns>
            public int Add(IEnumerable<MediaItemDto> items)
            {
                var added = 0;
                foreach (var item in items.Where(i => i.TmdbId > 0 && keys.Add(i.MediaType + ":" + i.TmdbId)))
                {
                    Items.Add(item);
                    counts[item.MediaType] = Count(item.MediaType) + 1;
                    added++;
                }

                Report(0);
                return added;
            }

            /// <param name="loading">Positions of the list that is being loaded right now.</param>
            public void Report(int loading)
            {
                // The number is a target, Ombi may deliver fewer: 100% is reported when the import is done.
                var value = Math.Min(99.9, 100 * (Items.Count + loading) / total);
                if (value > reported)
                {
                    reported = value;
                    progress.Report(value);
                }
            }
        }
    }
}
