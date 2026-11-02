using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace OmbiRequests.Channel
{
    /// <summary>Emby's message dialog in all apps of a user (request results, "now available").</summary>
    internal static class UserMessenger
    {
        /// <returns>The number of sessions the message went to; 0 if the user is not online.</returns>
        public static async Task<int> SendAsync(ISessionManager sessionManager, ILogger logger, long userInternalId, string header, string text)
        {
            var command = new MessageCommand { Header = header, Text = text, TimeoutMs = 10000 };
            var sent = 0;
            foreach (var session in sessionManager.Sessions.Where(s => s.UserInternalId == userInternalId).ToList())
            {
                try
                {
                    await sessionManager.SendMessageCommand(null, session.Id, command, CancellationToken.None).ConfigureAwait(false);
                    sent++;
                }
                catch (Exception ex)
                {
                    // Not every client supports messages.
                    logger.Debug("Could not notify session {0}: {1}", session.Id, ex.Message);
                }
            }

            return sent;
        }
    }
}
