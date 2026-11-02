using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Querying;
using OmbiRequests.Ombi;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Tells users in Emby when a title they requested has become available: every 15 minutes the
    /// request lists are compared with the last check. Users who are not online get the message the
    /// next time they open an Emby app. On the first check after installing nothing is sent, so already
    /// available requests do not flood anybody.
    /// </summary>
    public class AvailabilityNotifier : IServerEntryPoint
    {
        private const string FileName = "ombirequests-notifications.json";
        private const int MaxRequests = 100000;
        private const int MaxPendingPerUser = 20;
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

        /// <summary>The apps connect their message channel a moment after the session starts (tests shorten it).</summary>
        internal static TimeSpan SessionStartDelay { get; set; } = TimeSpan.FromSeconds(15);

        private readonly IUserManager userManager;
        private readonly ISessionManager sessionManager;
        private readonly ILogger logger;
        private readonly DataFile<NotificationState> file;
        private readonly SemaphoreSlim checkLock = new SemaphoreSlim(1, 1);
        private readonly object stateLock = new object();
        private NotificationState state;
        private Timer timer;

        public AvailabilityNotifier(IUserManager userManager, ISessionManager sessionManager, IApplicationPaths applicationPaths, IJsonSerializer json, ILogManager logManager)
        {
            this.userManager = userManager;
            this.sessionManager = sessionManager;
            logger = logManager.GetLogger(Plugin.PluginName);
            file = new DataFile<NotificationState>(applicationPaths.DataPath, FileName, json, logger);
        }

        /// <summary>The running instance, for the diagnostics of the settings page.</summary>
        public static AvailabilityNotifier Current { get; private set; }

        public void Run()
        {
            Current = this;
            timer = new Timer(_ => Task.Run(CheckAsync), null, StartupDelay, Interval);
            sessionManager.SessionStarted += OnSessionStarted;
        }

        public void Dispose()
        {
            timer?.Dispose();
            sessionManager.SessionStarted -= OnSessionStarted;
        }

        private NotificationState State
        {
            get
            {
                lock (stateLock)
                {
                    return state ?? (state = file.Load());
                }
            }
        }

        public async Task CheckAsync()
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Configuration.IsConfigured || !plugin.Configuration.EnableAvailableNotifications
                || !await checkLock.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                var ombi = plugin.OmbiClient;
                var movies = (await ombi.GetMovieRequestsAsync(null, MaxRequests, CancellationToken.None).ConfigureAwait(false))?.collection ?? new List<MovieRequest>();
                var seasons = (await ombi.GetTvRequestsAsync(null, MaxRequests, CancellationToken.None).ConfigureAwait(false))?.collection ?? new List<ChildRequest>();
                var available = movies.Where(m => m.available).Select(m => new AvailableRequest(m))
                    .Concat(seasons.Where(c => c.available).Select(c => new AvailableRequest(c, plugin.RequestService)))
                    .ToList();

                List<AvailableRequest> fresh;
                lock (stateLock)
                {
                    var known = new HashSet<string>(State.Available);
                    fresh = State.Initialized ? available.Where(r => !known.Contains(r.Key)).ToList() : new List<AvailableRequest>();
                    State.Initialized = true;
                    State.Available = available.Select(r => r.Key).ToList();
                    file.Save(State);
                }

                Diagnostics.LastNotificationCheck = DateTime.UtcNow;
                if (fresh.Count > 0)
                {
                    await NotifyAsync(plugin, fresh).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Checking for newly available requests failed: {0}", ex.Message);
                Diagnostics.Error("Availability check: " + ex.Message);
            }
            finally
            {
                checkLock.Release();
            }
        }

        private async Task NotifyAsync(Plugin plugin, List<AvailableRequest> requests)
        {
            foreach (var user in userManager.GetUserList(new UserQuery()))
            {
                OmbiIdentity identity;
                try
                {
                    identity = await plugin.OmbiClient.ResolveIdentityAsync(user.Name, user.ConnectUserName, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OmbiException)
                {
                    continue;
                }

                var titles = requests.Where(r => identity != null && identity.Matches(r.User, r.Alias)).Select(r => r.DisplayTitle).Distinct().ToList();
                if (titles.Count == 0)
                {
                    continue;
                }

                if (await SendAsync(plugin, user.InternalId, titles).ConfigureAwait(false) > 0)
                {
                    continue;
                }

                lock (stateLock)
                {
                    var key = user.InternalId.ToString(CultureInfo.InvariantCulture);
                    var pending = State.Pending.TryGetValue(key, out var list) ? list : State.Pending[key] = new List<string>();
                    pending.AddRange(titles.Where(t => !pending.Contains(t)));
                    pending.RemoveRange(0, Math.Max(0, pending.Count - MaxPendingPerUser));
                    file.Save(State);
                }
            }
        }

        private async Task<int> SendAsync(Plugin plugin, long userInternalId, List<string> titles)
        {
            var service = plugin.RequestService;
            var text = titles.Count == 1 ? service.Text("NowAvailable", titles[0]) : service.Text("NowAvailableSeveral", string.Join(", ", titles));
            var sent = await UserMessenger.SendAsync(sessionManager, logger, userInternalId, service.Text("NowAvailableTitle"), text).ConfigureAwait(false);
            if (sent > 0)
            {
                Diagnostics.NotificationsSent += titles.Count;
                logger.Info("Told user {0} about {1} newly available title(s)", userInternalId, titles.Count);
            }

            return sent;
        }

        internal void OnSessionStarted(object sender, SessionEventArgs e)
        {
            var userId = e.SessionInfo?.UserInternalId ?? 0;
            var plugin = Plugin.Instance;
            var key = userId.ToString(CultureInfo.InvariantCulture);
            List<string> titles;
            lock (stateLock)
            {
                // A copy: a check running at the same time may add titles to the waiting list.
                if (userId == 0 || plugin == null || !State.Pending.TryGetValue(key, out var waiting) || waiting.Count == 0)
                {
                    return;
                }

                titles = waiting.ToList();
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SessionStartDelay).ConfigureAwait(false);
                    if (await SendAsync(plugin, userId, titles).ConfigureAwait(false) > 0)
                    {
                        lock (stateLock)
                        {
                            // Only the titles sent; ones added meanwhile wait for the next session.
                            if (State.Pending.TryGetValue(key, out var waiting) && waiting.RemoveAll(titles.Contains) > 0 && waiting.Count == 0)
                            {
                                State.Pending.Remove(key);
                            }

                            file.Save(State);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.ErrorException("Sending the waiting messages to user {0} failed", ex, userId);
                }
            });
        }

        /// <summary>Waiting messages of users who were offline, for the diagnostics.</summary>
        public int PendingCount
        {
            get
            {
                lock (stateLock)
                {
                    return State.Pending.Values.Sum(l => l.Count);
                }
            }
        }

        private sealed class AvailableRequest
        {
            public AvailableRequest(MovieRequest movie)
            {
                Key = "m" + movie.id.ToString(CultureInfo.InvariantCulture);
                User = movie.requestedUser;
                Alias = movie.requestedByAlias;
                DisplayTitle = movie.title;
            }

            public AvailableRequest(ChildRequest season, Requests.RequestService service)
            {
                Key = "t" + season.id.ToString(CultureInfo.InvariantCulture);
                User = season.requestedUser;
                Alias = season.requestedByAlias;
                var numbers = season.seasonRequests?.Select(s => s.seasonNumber).OrderBy(n => n).ToList() ?? new List<int>();
                var title = season.parentRequest?.title ?? season.title;
                DisplayTitle = numbers.Count == 0
                    ? title
                    : title + " (" + string.Join(", ", numbers.Select(n => service.Text("SeasonShort", n))) + ")";
            }

            public string Key { get; }

            public RequestUser User { get; }

            public string Alias { get; }

            public string DisplayTitle { get; }
        }
    }

    /// <summary>Content of the notification file.</summary>
    public class NotificationState
    {
        public bool Initialized { get; set; }

        /// <summary>Requests that were available at the last check ("m12" movie, "t34" season request).</summary>
        public List<string> Available { get; set; } = new List<string>();

        /// <summary>Titles for users who were offline, by Emby user id.</summary>
        public Dictionary<string, List<string>> Pending { get; set; } = new Dictionary<string, List<string>>();
    }
}
