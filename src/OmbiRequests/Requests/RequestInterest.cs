using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace OmbiRequests.Requests
{
    /// <summary>
    /// Which Emby users want a title. In Ombi a request belongs to one user; whoever asks for the same
    /// title later only gets "already requested". So that one user withdrawing does not take the title
    /// away from the others, the plugin keeps its own list of everybody who requested it (heart or
    /// requests page, also when Ombi answered "already requested"). Series are noted as a whole, per
    /// season or per episode, so wanting season 2 does not keep somebody else's season 1. If the owner
    /// withdraws while others still want the title, the request stays and is noted as abandoned; it
    /// goes once the last of them withdraws too.
    /// </summary>
    public sealed class RequestInterest
    {
        private const string FileName = "ombirequests-interest.json";

        private readonly DataFile<InterestData> file;
        private readonly object dataLock = new object();
        private InterestData data;

        /// <param name="dataPath">Emby's data folder; null keeps the list in memory only.</param>
        public RequestInterest(string dataPath, IJsonSerializer json, ILogger logger)
        {
            file = dataPath == null ? null : new DataFile<InterestData>(dataPath, FileName, json, logger);
        }

        private InterestData Data
        {
            get
            {
                if (data == null)
                {
                    data = file?.Load() ?? new InterestData();
                    data.Wishes = data.Wishes ?? new Dictionary<string, List<long>>();
                    data.Abandoned = data.Abandoned ?? new Dictionary<string, List<AbandonedRequest>>();
                }

                return data;
            }
        }

        /// <param name="seasons">Series: the wish covers only these seasons.</param>
        /// <param name="episodes">Series: the wish covers only these episodes ("season:episode").</param>
        public void Add(string mediaType, int tmdbId, long userId, IList<int> seasons = null, IList<string> episodes = null)
        {
            lock (dataLock)
            {
                var title = Key(mediaType, tmdbId);
                var changed = false;
                foreach (var key in Scopes(seasons, episodes).Select(s => s.Length == 0 ? title : title + ":" + s))
                {
                    var users = Data.Wishes.TryGetValue(key, out var list) ? list : Data.Wishes[key] = new List<long>();
                    if (!users.Contains(userId))
                    {
                        users.Add(userId);
                        changed = true;
                    }
                }

                // An owner who withdrew and wants the title again keeps the request.
                if (Data.Abandoned.TryGetValue(title, out var abandoned) && abandoned.RemoveAll(a => a.UserId == userId) > 0)
                {
                    RemoveIfEmpty(Data.Abandoned, title, abandoned);
                    changed = true;
                }

                if (changed)
                {
                    file?.Save(Data);
                }
            }
        }

        /// <summary>Removes the user's wishes covered by a withdrawal (the whole title without seasons and episodes).</summary>
        /// <returns>True if the user had such a wish.</returns>
        public bool Remove(string mediaType, int tmdbId, long userId, IList<int> seasons = null, IList<string> episodes = null)
        {
            lock (dataLock)
            {
                var removed = false;
                foreach (var key in KeysOf(mediaType, tmdbId).Where(k => Covers(ScopeOf(k), seasons, episodes)).ToList())
                {
                    var users = Data.Wishes[key];
                    if (users.Remove(userId))
                    {
                        RemoveIfEmpty(Data.Wishes, key, users);
                        removed = true;
                    }
                }

                if (removed)
                {
                    file?.Save(Data);
                }

                return removed;
            }
        }

        /// <summary>All users who want the title or a part of it.</summary>
        public List<long> Users(string mediaType, int tmdbId)
        {
            lock (dataLock)
            {
                return KeysOf(mediaType, tmdbId).SelectMany(k => Data.Wishes[k]).Distinct().ToList();
            }
        }

        /// <summary>Other users who want what a withdrawal would remove (or the whole title).</summary>
        public int OthersCount(string mediaType, int tmdbId, long userId, IList<int> seasons = null, IList<string> episodes = null)
        {
            lock (dataLock)
            {
                return KeysOf(mediaType, tmdbId)
                    .Where(k => Overlaps(ScopeOf(k), seasons, episodes))
                    .SelectMany(k => Data.Wishes[k])
                    .Where(u => u != userId)
                    .Distinct()
                    .Count();
            }
        }

        /// <summary>The owner withdrew these requests, but they stay because others still want the title.</summary>
        public void Abandon(string mediaType, int tmdbId, long userId, IEnumerable<int> requestIds)
        {
            lock (dataLock)
            {
                var key = Key(mediaType, tmdbId);
                var abandoned = Data.Abandoned.TryGetValue(key, out var list) ? list : Data.Abandoned[key] = new List<AbandonedRequest>();
                abandoned.AddRange(requestIds
                    .Where(id => abandoned.All(a => a.RequestId != id))
                    .Select(id => new AbandonedRequest { UserId = userId, RequestId = id })
                    .ToList());
                file?.Save(Data);
            }
        }

        /// <summary>Request ids noted by <see cref="Abandon"/>.</summary>
        public List<int> AbandonedIds(string mediaType, int tmdbId)
        {
            lock (dataLock)
            {
                return Data.Abandoned.TryGetValue(Key(mediaType, tmdbId), out var abandoned) ? abandoned.Select(a => a.RequestId).ToList() : new List<int>();
            }
        }

        /// <summary>Abandoned requests that were deleted or no longer exist in Ombi.</summary>
        public void ForgetAbandoned(string mediaType, int tmdbId, IEnumerable<int> requestIds)
        {
            lock (dataLock)
            {
                var key = Key(mediaType, tmdbId);
                var ids = new HashSet<int>(requestIds);
                if (ids.Count > 0 && Data.Abandoned.TryGetValue(key, out var abandoned) && abandoned.RemoveAll(a => ids.Contains(a.RequestId)) > 0)
                {
                    RemoveIfEmpty(Data.Abandoned, key, abandoned);
                    file?.Save(Data);
                }
            }
        }

        /// <summary>
        /// Whether anybody still wants (part of) a request: the whole title, one of its seasons or one of its
        /// episodes. Movies: any wish for the title.
        /// </summary>
        /// <param name="seasons">Seasons the request has without single episodes.</param>
        /// <param name="episodes">Episodes of the request as "season:episode".</param>
        public bool IsWanted(string mediaType, int tmdbId, IList<int> seasons, IList<string> episodes)
        {
            var hasSeasons = seasons != null && seasons.Count > 0;
            var hasEpisodes = episodes != null && episodes.Count > 0;
            lock (dataLock)
            {
                return KeysOf(mediaType, tmdbId).Any(k => Data.Wishes[k].Count > 0
                    && ((!hasSeasons && !hasEpisodes)
                        || (hasSeasons && Overlaps(ScopeOf(k), seasons, null))
                        || (hasEpisodes && Overlaps(ScopeOf(k), null, episodes))));
            }
        }

        /// <summary>Forgets everybody's wish, e.g. after an admin deleted the request.</summary>
        public void Clear(string mediaType, int tmdbId)
        {
            lock (dataLock)
            {
                var keys = KeysOf(mediaType, tmdbId).ToList();
                foreach (var key in keys)
                {
                    Data.Wishes.Remove(key);
                }

                if (Data.Abandoned.Remove(Key(mediaType, tmdbId)) | keys.Count > 0)
                {
                    file?.Save(Data);
                }
            }
        }

        /// <summary>"" for the whole title, "s2" for a season, "s2e5" for an episode.</summary>
        private static IEnumerable<string> Scopes(IList<int> seasons, IList<string> episodes)
        {
            if (episodes != null && episodes.Count > 0)
            {
                return episodes.Select(ParseEpisode).Where(e => e != null).Select(e => "s" + Number(e.Item1) + "e" + Number(e.Item2)).Distinct();
            }

            return seasons != null && seasons.Count > 0 ? seasons.Distinct().Select(s => "s" + Number(s)) : new[] { string.Empty };
        }

        /// <summary>Whether a withdrawal of these seasons or episodes (or the whole title) takes back a wish of this scope.</summary>
        private static bool Covers(string scope, IList<int> seasons, IList<string> episodes)
        {
            var (season, episode) = ParseScope(scope);
            if (episodes != null && episodes.Count > 0)
            {
                return episode != null && episodes.Select(ParseEpisode).Any(e => e != null && e.Item1 == season && e.Item2 == episode);
            }

            if (seasons != null && seasons.Count > 0)
            {
                return season != null && seasons.Contains(season.Value);
            }

            return true;
        }

        /// <summary>Whether a wish of this scope also wants (part of) what a withdrawal removes.</summary>
        private static bool Overlaps(string scope, IList<int> seasons, IList<string> episodes)
        {
            var (season, episode) = ParseScope(scope);
            if (season == null)
            {
                return true;
            }

            if (episodes != null && episodes.Count > 0)
            {
                return episodes.Select(ParseEpisode).Any(e => e != null && e.Item1 == season && (episode == null || e.Item2 == episode));
            }

            return seasons == null || seasons.Count == 0 || seasons.Contains(season.Value);
        }

        private static (int? Season, int? Episode) ParseScope(string scope)
        {
            if (string.IsNullOrEmpty(scope) || scope[0] != 's')
            {
                return (null, null);
            }

            var e = scope.IndexOf('e');
            var season = int.Parse(e < 0 ? scope.Substring(1) : scope.Substring(1, e - 1), CultureInfo.InvariantCulture);
            return (season, e < 0 ? (int?)null : int.Parse(scope.Substring(e + 1), CultureInfo.InvariantCulture));
        }

        /// <summary>"2:5" -> (2, 5).</summary>
        private static Tuple<int, int> ParseEpisode(string text)
        {
            var parts = (text ?? string.Empty).Split(':');
            return parts.Length == 2
                   && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var season)
                   && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var episode)
                ? Tuple.Create(season, episode)
                : null;
        }

        /// <summary>The keys of a title: "tv:1399" and its parts "tv:1399:s2", "tv:1399:s2e5".</summary>
        private IEnumerable<string> KeysOf(string mediaType, int tmdbId)
        {
            var title = Key(mediaType, tmdbId);
            return Data.Wishes.Keys.Where(k => k == title || k.StartsWith(title + ":", StringComparison.Ordinal));
        }

        private static string ScopeOf(string key)
        {
            var colon = key.IndexOf(':');
            var second = colon < 0 ? -1 : key.IndexOf(':', colon + 1);
            return second < 0 ? string.Empty : key.Substring(second + 1);
        }

        private static void RemoveIfEmpty<T>(Dictionary<string, List<T>> dictionary, string key, List<T> list)
        {
            if (list.Count == 0)
            {
                dictionary.Remove(key);
            }
        }

        private static string Key(string mediaType, int tmdbId) => mediaType + ":" + Number(tmdbId);

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        public sealed class InterestData
        {
            /// <summary>"movie:603", "tv:1399", "tv:1399:s2", "tv:1399:s2e5" → Emby users who want it.</summary>
            public Dictionary<string, List<long>> Wishes { get; set; } = new Dictionary<string, List<long>>();

            /// <summary>"movie:603" → requests whose owner withdrew while others still wanted the title.</summary>
            public Dictionary<string, List<AbandonedRequest>> Abandoned { get; set; } = new Dictionary<string, List<AbandonedRequest>>();
        }

        public sealed class AbandonedRequest
        {
            public long UserId { get; set; }

            public int RequestId { get; set; }
        }
    }
}
