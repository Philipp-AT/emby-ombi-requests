using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;

namespace OmbiRequests.Catalog
{
    /// <summary>
    /// The import as an Emby task: started from the plugin settings, shown with progress under
    /// Dashboard > Scheduled tasks. Deliberately without trigger, the admin starts it.
    /// </summary>
    public class CatalogImportTask : IScheduledTask, IConfigurableScheduledTask
    {
        public string Name => "Import all movies and series";

        public string Key => "OmbiRequestsCatalogImport";

        public string Description => "Loads the titles of the folders 'All movies' and 'All series' of the requests channel from Ombi "
                                     + "and replaces the existing ones. The numbers are set in the plugin settings.";

        public string Category => Plugin.PluginName;

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            return Plugin.Instance.Catalog.ImportAsync(progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }

    /// <summary>Adds new titles; queued by <see cref="CatalogScheduler"/> at the interval from the plugin settings.</summary>
    public class CatalogUpdateTask : IScheduledTask, IConfigurableScheduledTask
    {
        public string Name => "Load new movies and series";

        public string Key => "OmbiRequestsCatalogUpdate";

        public string Description => "Adds the titles of Ombi's current lists (cinema, upcoming, trending, popular) that are not in "
                                     + "'All movies' / 'All series' yet. Runs automatically at the interval set in the plugin settings.";

        public string Category => Plugin.PluginName;

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            return Plugin.Instance.Catalog.UpdateAsync(progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }

    /// <summary>
    /// Looks up all missing trailers and backgrounds of the catalog in one go, instead of 250 per hour (new
    /// titles get theirs during the import and the update already). Started from the plugin settings.
    /// </summary>
    public class CatalogTrailerTask : IScheduledTask, IConfigurableScheduledTask
    {
        public string Name => "Look up missing trailers and backgrounds";

        public string Key => "OmbiRequestsCatalogTrailers";

        public string Description => "Looks up the trailers and background pictures of all titles of 'All movies' / 'All series' that were not looked up yet. "
                                     + "Otherwise this happens 250 titles per hour.";

        public string Category => Plugin.PluginName;

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var found = await Plugin.Instance.Catalog.FillTrailersAsync(int.MaxValue, cancellationToken, progress, wait: true).ConfigureAwait(false);
            if (found > 0)
            {
                // Once at the end; trailers and backgrounds reach Emby with this refresh.
                Channel.OmbiChannel.RequestRefresh(TimeSpan.FromSeconds(5));
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }

    /// <summary>
    /// Emby's task triggers cannot be set from a plugin's settings, so the interval is checked here
    /// and the update queued when it is due.
    /// </summary>
    public class CatalogScheduler : IServerEntryPoint
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

        /// <summary>Trailers looked up per hour, see <see cref="TitleCatalog.FillTrailersAsync"/>.</summary>
        private const int TrailersPerHour = 250;

        private readonly ITaskManager taskManager;
        private Timer timer;

        public CatalogScheduler(ITaskManager taskManager)
        {
            this.taskManager = taskManager;
        }

        public void Run()
        {
            timer = new Timer(_ => Check(), null, StartupDelay, CheckInterval);
        }

        public void Dispose()
        {
            timer?.Dispose();
        }

        private void Check()
        {
            var plugin = Plugin.Instance;
            try
            {
                if (plugin == null || !plugin.Configuration.IsConfigured)
                {
                    return;
                }

                if (plugin.Catalog.IsUpdateDue(plugin.Configuration))
                {
                    taskManager.QueueIfNotRunning<CatalogUpdateTask>();
                }

                if (plugin.Catalog.IsImported && (plugin.Configuration.ChannelAllMovies || plugin.Configuration.ChannelAllTv))
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            await plugin.Catalog.FillTrailersAsync(TrailersPerHour, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            plugin.Logger.ErrorException("Looking up trailers failed", ex);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                plugin?.Logger.ErrorException("Checking whether new titles are due failed", ex);
            }
        }
    }
}
