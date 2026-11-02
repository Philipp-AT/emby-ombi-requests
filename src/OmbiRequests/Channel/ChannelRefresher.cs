using System;
using System.Threading;
using MediaBrowser.Controller.Plugins;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Keeps the channel filled: Emby itself only refreshes channels once a day, so a fresh install
    /// would show an empty folder and request states would lag behind for hours.
    /// </summary>
    public class ChannelRefresher : IServerEntryPoint
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(3);
        /// <summary>
        /// The banners change right away (ChannelItemUpdater); the refresh only confirms the states, and it
        /// reads the whole channel, so requests within this time cause one refresh.
        /// </summary>
        private static readonly TimeSpan AfterRequestDelay = TimeSpan.FromMinutes(10);

        private Timer timer;

        public void Run()
        {
            timer = new Timer(_ => OmbiChannel.RequestRefresh(TimeSpan.Zero), null, StartupDelay, Interval);

            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                plugin.RequestService.RequestChanged += OnRequestChanged;
            }
        }

        public void Dispose()
        {
            timer?.Dispose();
            OmbiChannel.Shutdown();
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                plugin.RequestService.RequestChanged -= OnRequestChanged;
            }
        }

        private static void OnRequestChanged(object sender, EventArgs e)
        {
            OmbiChannel.RequestRefresh(AfterRequestDelay);
        }
    }
}
