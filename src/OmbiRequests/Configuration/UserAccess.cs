using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Users;

namespace OmbiRequests.Configuration
{
    /// <summary>
    /// Who may use the requests page and the requests folder. The page is a setting of the plugin; the
    /// folder is Emby's channel access of each user (Users > Access > Channels), which the settings page
    /// shows and changes, so it is stored in one place only.
    /// </summary>
    internal static class UserAccess
    {
        public static List<EditorSelectOption> Options(IEnumerable<User> users)
        {
            return users
                .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(u => new EditorSelectOption { Value = Id(u), Name = u.Name, IsEnabled = true })
                .ToList();
        }

        public static string PageUsers(PluginConfiguration config, IEnumerable<User> users)
        {
            return Join(users.Where(u => config.IsRequestsPageEnabled(u.InternalId)));
        }

        /// <summary>Stores who is not selected, so that users created later get the page.</summary>
        /// <param name="selected">Null if the page sent no selection (then nothing changes, nobody is locked out by accident).</param>
        public static void ApplyPageUsers(PluginConfiguration config, IEnumerable<User> users, string selected)
        {
            if (selected == null)
            {
                return;
            }

            var chosen = PluginConfiguration.ParseUserIds(selected);
            config.RequestsPageDisabledUsers = Join(users.Where(u => !chosen.Contains(u.InternalId)));
        }

        public static bool HasFolder(UserPolicy policy, string channelId)
        {
            return policy.EnableAllChannels || (policy.EnabledChannels ?? Array.Empty<string>()).Contains(channelId, StringComparer.OrdinalIgnoreCase);
        }

        public static string FolderUsers(IUserManager userManager, IEnumerable<User> users, string channelId)
        {
            return Join(users.Where(u => HasFolder(userManager.GetUserPolicy(u), channelId)));
        }

        /// <summary>
        /// Gives or takes the channel like Emby's user settings would: a user with access to all channels
        /// gets the list of all others instead, and gets "all channels" back once the list is complete.
        /// </summary>
        /// <param name="allChannelIds">Ids of all channels on the server, including the requests channel.</param>
        /// <returns>Users whose access changed.</returns>
        public static int ApplyFolderUsers(IUserManager userManager, IEnumerable<User> users, string selected, string channelId, IList<string> allChannelIds, ILogger logger)
        {
            if (selected == null)
            {
                return 0;
            }

            if (!allChannelIds.Contains(channelId, StringComparer.OrdinalIgnoreCase))
            {
                // Without the list of channels a user could lose all of them.
                logger.Warn("The requests channel is not among Emby's channels, the folder access stays as it is");
                return 0;
            }

            var chosen = PluginConfiguration.ParseUserIds(selected);
            var changed = 0;
            foreach (var user in users)
            {
                var policy = userManager.GetUserPolicy(user);
                var wanted = chosen.Contains(user.InternalId);
                if (HasFolder(policy, channelId) == wanted)
                {
                    continue;
                }

                var enabled = (policy.EnabledChannels ?? Array.Empty<string>()).ToList();
                if (wanted)
                {
                    enabled.Add(channelId);
                    if (allChannelIds.All(id => enabled.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    {
                        policy.EnableAllChannels = true;
                        enabled.Clear();
                    }
                }
                else if (policy.EnableAllChannels)
                {
                    policy.EnableAllChannels = false;
                    enabled = allChannelIds.Where(id => !string.Equals(id, channelId, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                else
                {
                    enabled.RemoveAll(id => string.Equals(id, channelId, StringComparison.OrdinalIgnoreCase));
                }

                policy.EnabledChannels = enabled.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                userManager.UpdateUserPolicy(user.InternalId, policy);
                logger.Info("Requests folder {0} for {1}", wanted ? "enabled" : "disabled", user.Name);
                changed++;
            }

            return changed;
        }

        private static string Id(User user) => user.InternalId.ToString(CultureInfo.InvariantCulture);

        private static string Join(IEnumerable<User> users) => string.Join(",", users.Select(Id));
    }
}
