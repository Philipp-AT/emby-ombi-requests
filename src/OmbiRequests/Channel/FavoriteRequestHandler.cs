using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using OmbiRequests.Ombi;
using OmbiRequests.Requests;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Marking an Ombi channel item as favorite requests it in Ombi as that user. The heart button
    /// exists in every Emby app, which makes this the request action outside the web client. Removing
    /// the heart again withdraws the request while it waits for approval (only requests made with the
    /// heart, so rating or un-favoriting other titles never withdraws anything).
    /// </summary>
    public class FavoriteRequestHandler : IServerEntryPoint
    {
        private const string FileName = "ombirequests-favorite-requests.json";
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(30);

        private readonly IUserDataManager userDataManager;
        private readonly ISessionManager sessionManager;
        private readonly ILogger logger;
        private readonly DataFile<List<string>> file;
        private readonly ConcurrentDictionary<string, DateTime> recentChanges = new ConcurrentDictionary<string, DateTime>();
        private readonly object requestedLock = new object();
        private HashSet<string> requestedWithHeart;

        public FavoriteRequestHandler(
            IUserDataManager userDataManager,
            ISessionManager sessionManager,
            IApplicationPaths applicationPaths,
            IJsonSerializer json,
            ILogManager logManager)
        {
            this.userDataManager = userDataManager;
            this.sessionManager = sessionManager;
            logger = logManager.GetLogger(Plugin.PluginName);
            file = new DataFile<List<string>>(applicationPaths.DataPath, FileName, json, logger);
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
            if (e.SaveReason != UserDataSaveReason.UpdateUserRating || e.UserData == null || e.User == null)
            {
                return;
            }

            if (!ChannelItemIds.TryGetTarget(e.Item?.ExternalId, out var target))
            {
                return;
            }

            var plugin = Plugin.Instance;
            var key = e.User.InternalId + "|" + e.Item.ExternalId;
            var favorite = e.UserData.IsFavorite;
            if (plugin == null
                || (favorite && !plugin.Configuration.EnableFavoriteRequests)
                || (!favorite && (!plugin.Configuration.EnableWithdrawOnUnfavorite || !RequestedWithHeart(key))))
            {
                return;
            }

            // Rating changes on an item fire this event again with the same favorite state.
            var changeKey = key + (favorite ? "|+" : "|-");
            var now = DateTime.UtcNow;
            if (recentChanges.TryGetValue(changeKey, out var last) && now - last < DuplicateWindow)
            {
                return;
            }

            recentChanges[changeKey] = now;
            foreach (var stale in recentChanges.Where(p => now - p.Value > DuplicateWindow).Select(p => p.Key).ToList())
            {
                recentChanges.TryRemove(stale, out _);
            }

            Task.Run(() => favorite
                ? RequestAsync(plugin.RequestService, e.User, target, key)
                : WithdrawAsync(plugin.RequestService, e.User, target, key));
        }

        private async Task RequestAsync(RequestService service, User user, ChannelTarget target, string key)
        {
            // Also when Ombi refuses ("already requested"): removing the heart then takes back this user's wish.
            SetRequestedWithHeart(key, true);
            RequestOutcomeDto outcome;
            try
            {
                outcome = await service.RequestAsync(user, target.MediaType, target.TmdbId, TvRequestScope.All, null, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UserFacingException || ex is OmbiException)
            {
                outcome = new RequestOutcomeDto { Success = false, Message = service.DescribeForUser(ex) };
            }
            catch (Exception ex)
            {
                logger.ErrorException("Favorite request of {0} {1} by {2} failed", ex, target.MediaType, target.TmdbId, user.Name);
                outcome = new RequestOutcomeDto { Success = false, Message = ex.Message };
            }

            await UserMessenger.SendAsync(sessionManager, logger, user.InternalId, service.Text(outcome.Success ? "RequestSent" : "RequestFailed"), outcome.Message).ConfigureAwait(false);
        }

        private async Task WithdrawAsync(RequestService service, User user, ChannelTarget target, string key)
        {
            RequestOutcomeDto outcome;
            try
            {
                outcome = await service.WithdrawAsync(user, target.MediaType, target.TmdbId, null, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UserFacingException || ex is OmbiException)
            {
                outcome = new RequestOutcomeDto { Success = false, Message = service.DescribeForUser(ex) };
            }
            catch (Exception ex)
            {
                logger.ErrorException("Withdrawing {0} {1} for {2} failed", ex, target.MediaType, target.TmdbId, user.Name);
                outcome = new RequestOutcomeDto { Success = false, Message = ex.Message };
            }

            // Approved requests stay; the heart then only says "no longer marked".
            SetRequestedWithHeart(key, false);
            await UserMessenger.SendAsync(sessionManager, logger, user.InternalId, service.Text(outcome.Success ? "RequestWithdrawnHeader" : "WithdrawFailed"), outcome.Message).ConfigureAwait(false);
        }

        private bool RequestedWithHeart(string key)
        {
            lock (requestedLock)
            {
                requestedWithHeart = requestedWithHeart ?? new HashSet<string>(file.Load());
                return requestedWithHeart.Contains(key);
            }
        }

        private void SetRequestedWithHeart(string key, bool requested)
        {
            lock (requestedLock)
            {
                requestedWithHeart = requestedWithHeart ?? new HashSet<string>(file.Load());
                if (requested ? requestedWithHeart.Add(key) : requestedWithHeart.Remove(key))
                {
                    file.Save(requestedWithHeart.ToList());
                }
            }
        }
    }
}
