using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using OmbiRequests.Requests;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Playing a movie of the channel only shows its placeholder video (see <see cref="PlaceholderVideos"/>).
    /// Emby would then mark the movie as watched (the video is shorter than the minimum resume time) or
    /// remember a position, which puts a check mark on a movie nobody has seen and may bring it into
    /// "Continue watching". The play state of these movies is therefore reset after playback.
    /// </summary>
    public class PlaceholderPlayback : IServerEntryPoint
    {
        private readonly IUserDataManager userDataManager;
        private readonly ILogger logger;

        public PlaceholderPlayback(IUserDataManager userDataManager, ILogManager logManager)
        {
            this.userDataManager = userDataManager;
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public void Run()
        {
            userDataManager.UserDataSaved += OnUserDataSaved;
        }

        public void Dispose()
        {
            userDataManager.UserDataSaved -= OnUserDataSaved;
        }

        internal void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            // Only playback: the reset below is saved as TogglePlayed and must not come back here, and
            // the heart (UpdateUserRating) belongs to FavoriteRequestHandler.
            if (e.SaveReason != UserDataSaveReason.PlaybackStart
                && e.SaveReason != UserDataSaveReason.PlaybackProgress
                && e.SaveReason != UserDataSaveReason.PlaybackFinished)
            {
                return;
            }

            var item = e.Item;
            var data = e.UserData;
            if (e.User == null || data == null || !(item is Video)
                || !ChannelItemIds.TryGetTarget(item.ExternalId, out var target) || target.MediaType != RequestService.Movie
                || (!data.Played && data.PlaybackPositionTicks == 0 && data.PlayCount == 0 && data.LastPlayedDate == null))
            {
                return;
            }

            var user = e.User;

            // Not inside Emby's save; a later save of the same playback comes here again.
            Task.Run(() =>
            {
                try
                {
                    var current = userDataManager.GetUserData(user, item);
                    current.Played = false;
                    current.PlaybackPositionTicks = 0;
                    current.PlayCount = 0;
                    current.LastPlayedDate = null;
                    userDataManager.SaveUserData(user, item, current, UserDataSaveReason.TogglePlayed, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.ErrorException("Resetting the play state of {0} failed", ex, item.ExternalId);
                }
            });
        }
    }
}
