using System;
using System.Collections.Generic;

namespace OmbiRequests
{
    /// <summary>One list of the channel at its last build.</summary>
    public class ChannelListInfo
    {
        public string Name { get; set; }

        /// <summary>Titles in the folder (after the status filter).</summary>
        public int Shown { get; set; }

        /// <summary>Titles Ombi delivered (catalog: titles in the catalog).</summary>
        public int Loaded { get; set; }
    }

    /// <summary>
    /// What the plugin did recently, for the diagnostics section of the settings page (in memory, since
    /// Emby's start). Details are in Emby's log.
    /// </summary>
    public static class Diagnostics
    {
        private static readonly object Lock = new object();
        private static Tuple<DateTime, string> lastError;
        private static Tuple<DateTime, TimeSpan, IList<ChannelListInfo>> lastChannelLists;

        /// <summary>"boss (de)": the Ombi user channel and catalog load as; null if not determined yet.</summary>
        public static string ContentIdentity { get; set; }

        public static DateTime? LastNotificationCheck { get; set; }

        public static int NotificationsSent { get; set; }

        public static void Error(string message)
        {
            lock (Lock)
            {
                lastError = Tuple.Create(DateTime.UtcNow, message);
            }
        }

        public static Tuple<DateTime, string> LastError
        {
            get
            {
                lock (Lock)
                {
                    return lastError;
                }
            }
        }

        /// <param name="lists">Titles per list, in display order.</param>
        public static void ChannelLists(DateTime started, TimeSpan duration, IList<ChannelListInfo> lists)
        {
            lock (Lock)
            {
                lastChannelLists = Tuple.Create(started, duration, lists);
            }
        }

        public static Tuple<DateTime, TimeSpan, IList<ChannelListInfo>> LastChannelLists
        {
            get
            {
                lock (Lock)
                {
                    return lastChannelLists;
                }
            }
        }
    }
}
