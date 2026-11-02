using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OmbiRequests.Tests
{
    /// <summary>
    /// The parts of Ombi's API the plugin uses, with fixed data, as a small HTTP server on a free local
    /// port. Records every call. Plain TCP instead of HttpListener, so it needs no admin rights on Windows.
    /// </summary>
    internal sealed class FakeOmbi : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly object dataLock = new object();

        public FakeOmbi()
        {
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(AcceptLoop);

            MovieRequests = new List<JsonObject>
            {
                MovieRequest(1, 100001, "boss", approved: true),
                MovieRequest(2, 100002, "anna", denied: true),
                MovieRequest(3, 100003, "anna", approved: true, available: true),
                MovieRequest(4, 100004, "boss"),
                MovieRequest(5, 100009, "anna"),
                MovieRequest(7, 100011, "boss", approved: true),
            };
            TvRequests = new List<JsonObject>
            {
                ChildRequest(10, 500001, "boss", new[] { 1 }, approved: true),
                ChildRequest(20, 500002, "anna", new[] { 1 }, approved: true, available: true),
                ChildRequest(21, 500002, "boss", new[] { 3 }),
                ChildRequest(30, 500003, "anna", new[] { 1 }, denied: true),
                ChildRequest(60, 500006, "anna", new[] { 1 }, approved: true, available: true),
                ChildRequest(80, 500008, "boss", new[] { 1, 2 }),
                ChildRequest(90, 590001, "boss", new[] { 1 }, episodes: new[] { 4 }),
                ChildRequest(97, 590003, "anna", new[] { 1 }, episodes: new[] { 1 }),
                ChildRequest(98, 590003, "anna", new[] { 2 }, episodes: new[] { 1 }),
            };
        }

        public string Url { get; }

        public List<Call> Calls { get; } = new List<Call>();

        /// <summary>Ombi's main series lists answer with HTTP 500.</summary>
        public bool FailTvMain { get; set; }

        /// <summary>The next calls answer 503, like a proxy in front of Ombi that limits parallel requests.</summary>
        public int BusyResponses { get; set; }

        /// <summary>New titles appear in the cinema and trending lists.</summary>
        public bool ExtraNew { get; set; }

        public List<JsonObject> MovieRequests { get; }

        public List<JsonObject> TvRequests { get; }

        public List<JsonNode> Issues { get; } = new List<JsonNode>();

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }

        public sealed class Call
        {
            public string Method { get; set; }

            public string Path { get; set; }

            public string User { get; set; }

            public JsonNode Body { get; set; }

            public override string ToString() => $"{Method} {Path} user={User ?? "-"}";
        }

        // ---------- data ----------

        private static JsonObject Movie(int tmdb, string date = "2020-01-01") => new JsonObject
        {
            ["id"] = tmdb, ["theMovieDbId"] = tmdb.ToString(), ["title"] = "Movie " + tmdb, ["overview"] = "Plot " + tmdb,
            ["posterPath"] = $"/m{tmdb}.jpg", ["releaseDate"] = date, ["imdbId"] = "tt" + tmdb,
            // Some list entries come without background; the details always have one.
            ["backdropPath"] = tmdb % 10 == 3 ? null : $"/bm{tmdb}.jpg",
            ["approved"] = false, ["requested"] = false, ["available"] = false, ["denied"] = false, ["voteAverage"] = 7.5
        };

        private static JsonObject Show(int tmdb, int? tvdb = null, bool partly = false) => new JsonObject
        {
            ["id"] = tmdb, ["theMovieDbId"] = tmdb.ToString(), ["title"] = "Show " + tmdb, ["overview"] = "Story " + tmdb,
            ["posterPath"] = $"/t{tmdb}.jpg", ["firstAired"] = "2019-05-05", ["theTvDbId"] = tvdb?.ToString(),
            ["banner"] = $"/bt{tmdb}.jpg",
            ["approved"] = false, ["requested"] = false, ["available"] = false, ["denied"] = false,
            // Like Ombi: lists have no seasons, and "all episodes of all (zero) seasons available" makes every series fully available.
            ["fullyAvailable"] = true, ["partlyAvailable"] = partly, ["rating"] = "8.1", ["runtime"] = "45", ["network"] = "HBO",
            ["trailer"] = tmdb % 2 == 0 ? "https://www.youtube.com/watch?v=tv" + tmdb : null
        };

        private static JsonObject MovieRequest(int id, int tmdb, string user, bool approved = false, bool available = false, bool denied = false) => new JsonObject
        {
            ["id"] = id, ["theMovieDbId"] = tmdb, ["title"] = "Movie " + tmdb, ["releaseDate"] = "2020-01-01",
            ["requestedDate"] = "2026-10-0" + (id % 9 + 1), ["approved"] = approved, ["available"] = available, ["denied"] = denied,
            ["requestedUser"] = new JsonObject { ["userName"] = user, ["alias"] = user == "anna" ? "Anna" : "" }
        };

        private static JsonObject ChildRequest(int id, int tmdb, string user, int[] seasons, bool approved = false, bool available = false, bool denied = false, int[] episodes = null) => new JsonObject
        {
            ["id"] = id, ["title"] = "Show " + tmdb, ["requestedDate"] = "2026-10-1" + (id % 9),
            ["approved"] = approved, ["available"] = available, ["denied"] = denied,
            ["requestedUser"] = new JsonObject { ["userName"] = user },
            ["seasonRequests"] = new JsonArray(seasons.Select(n => (JsonNode)new JsonObject
            {
                ["seasonNumber"] = n,
                ["episodes"] = new JsonArray((episodes ?? new int[0]).Select(e => (JsonNode)new JsonObject { ["episodeNumber"] = e }).ToArray())
            }).ToArray()),
            ["parentRequest"] = new JsonObject { ["id"] = tmdb, ["externalProviderId"] = tmdb, ["title"] = "Show " + tmdb, ["posterPath"] = $"/t{tmdb}.jpg" }
        };

        private Dictionary<string, List<JsonObject>> MovieLists()
        {
            var lists = new Dictionary<string, List<JsonObject>>
            {
                ["popular"] = Enumerable.Range(1, 130).Select(i => Movie(100000 + i)).ToList(),
                ["toprated"] = Enumerable.Range(120, 61).Select(i => Movie(100000 + i)).ToList(),
                ["nowplaying"] = Enumerable.Range(0, 30).Select(i => Movie(200000 + i, "2026-10-20")).Append(Movie(100001)).ToList(),
                ["upcoming"] = Enumerable.Range(0, 25).Select(i => Movie(300000 + i, "2026-12-01")).ToList(),
                ["seasonal"] = Enumerable.Range(0, 5).Select(i => Movie(350000 + i)).ToList(),
            };
            if (ExtraNew)
            {
                lists["nowplaying"] = Enumerable.Range(0, 7).Select(i => Movie(400000 + i, "2026-11-01")).Concat(lists["nowplaying"]).ToList();
            }

            return lists;
        }

        private Dictionary<string, List<JsonObject>> TvLists()
        {
            var lists = new Dictionary<string, List<JsonObject>>
            {
                ["popular"] = Enumerable.Range(1, 50).Select(i => Show(500000 + i, 900000 + i, partly: i == 4)).ToList(),
                ["mostwatched"] = Enumerable.Range(40, 31).Select(i => Show(500000 + i)).ToList(),
                ["trending"] = Enumerable.Range(0, 21).Select(i => Show(600000 + i)).Append(Show(500002)).ToList(),
            };
            if (ExtraNew)
            {
                lists["trending"] = Enumerable.Range(0, 3).Select(i => Show(700000 + i)).Concat(lists["trending"]).ToList();
            }

            return lists;
        }

        // ---------- routes ----------

        private (int Status, object Body) Route(string method, string path, string user, JsonNode body)
        {
            Match m;
            lock (dataLock)
            {
                if (method == "GET" && path == "/api/v1/Identity/Users")
                {
                    return (200, new[]
                    {
                        new { id = "1", userName = "boss", alias = "", emailAddress = "", language = "", claims = new[] { new { value = "Admin", enabled = true } } },
                        new { id = "2", userName = "fritz", alias = "", emailAddress = "", language = "en", claims = new[] { new { value = "RequestMovie", enabled = true } } },
                        new { id = "3", userName = "jürgen", alias = "", emailAddress = "", language = "de", claims = new[] { new { value = "Admin", enabled = true } } },
                        new { id = "4", userName = "anna", alias = "Anna", emailAddress = "", language = "de", claims = new[] { new { value = "RequestMovie", enabled = true } } },
                        new { id = "5", userName = "power", alias = "", emailAddress = "", language = "de", claims = new[] { new { value = "PowerUser", enabled = true } } },
                        new { id = "6", userName = "former", alias = "", emailAddress = "", language = "de", claims = new[] { new { value = "Admin", enabled = false } } },
                    });
                }

                if (method == "GET" && path == "/api/v1/Settings/defaultlanguage")
                {
                    return (200, "de");
                }

                if ((m = Regex.Match(path, @"^/api/v2/Search/movie/(\d+)$")).Success && method == "GET")
                {
                    var details = Movie(int.Parse(m.Groups[1].Value));
                    details["backdropPath"] = $"/bm{m.Groups[1].Value}.jpg";

                    // Like Ombi: the details show the state of a request for the movie.
                    var movieRequest = MovieRequests.FirstOrDefault(r => (int)r["theMovieDbId"] == int.Parse(m.Groups[1].Value));
                    if (movieRequest != null)
                    {
                        details["requested"] = true;
                        details["approved"] = (bool)movieRequest["approved"];
                        details["available"] = (bool)movieRequest["available"];
                        details["denied"] = (bool)movieRequest["denied"];
                    }

                    details["videos"] = new JsonObject
                    {
                        ["results"] = new JsonArray(
                            new JsonObject { ["key"] = "teaser" + m.Groups[1].Value, ["site"] = "YouTube", ["type"] = "Teaser" },
                            new JsonObject { ["key"] = "m" + m.Groups[1].Value, ["site"] = "YouTube", ["type"] = "Trailer" })
                    };
                    return (200, details);
                }

                // Details of the library series of the tests: season 1 has a missing episode, season 2 is requested.
                if (method == "GET" && path == "/api/v2/Search/tv/moviedb/500999")
                {
                    var series = Show(500999);
                    series["network"] = new JsonObject { ["name"] = "HBO" };
                    series["seasonRequests"] = new JsonArray(
                        new JsonObject
                        {
                            ["seasonNumber"] = 1,
                            ["episodes"] = new JsonArray(
                                new JsonObject { ["episodeNumber"] = 1, ["available"] = true },
                                new JsonObject { ["episodeNumber"] = 2, ["available"] = false })
                        },
                        new JsonObject
                        {
                            ["seasonNumber"] = 2,
                            ["episodes"] = new JsonArray(new JsonObject { ["episodeNumber"] = 1, ["requested"] = true })
                        });
                    return (200, series);
                }

                // Details of the series in the channel tests: season 1 with an available, a requested and an open episode.
                if ((m = Regex.Match(path, @"^/api/v2/Search/tv/moviedb/(500002|500045|6\d{5})$")).Success && method == "GET")
                {
                    var series = Show(int.Parse(m.Groups[1].Value));
                    series["network"] = new JsonObject { ["name"] = "HBO" };
                    series["seasonRequests"] = new JsonArray(
                        new JsonObject
                        {
                            ["seasonNumber"] = 1,
                            ["episodes"] = new JsonArray(
                                new JsonObject { ["episodeNumber"] = 1, ["title"] = "Pilot", ["available"] = true, ["airDate"] = "2024-01-05T00:00:00" },
                                new JsonObject { ["episodeNumber"] = 2, ["title"] = "", ["requested"] = true, ["airDate"] = "2024-01-12T00:00:00" },
                                new JsonObject { ["episodeNumber"] = 3, ["title"] = "Drei", ["airDate"] = "2099-01-01T00:00:00" })
                        },
                        new JsonObject
                        {
                            ["seasonNumber"] = 2,
                            ["episodes"] = new JsonArray(new JsonObject { ["episodeNumber"] = 1, ["title"] = "Neu" })
                        });
                    return (200, series);
                }

                if ((m = Regex.Match(path, @"^/api/v2/Search/(movie|tv)/(\w+)/(\d+)/(\d+)$")).Success && method == "GET")
                {
                    string kind = m.Groups[1].Value, name = m.Groups[2].Value;
                    int start = int.Parse(m.Groups[3].Value), count = int.Parse(m.Groups[4].Value);
                    if (count > 20)
                    {
                        return (400, new { message = "page too big" });
                    }

                    if (kind == "tv" && (name == "anticipated" || (FailTvMain && (name == "popular" || name == "mostwatched"))))
                    {
                        return (500, new { error = "boom" });
                    }

                    var lists = kind == "movie" ? MovieLists() : TvLists();
                    return lists.TryGetValue(name, out var list)
                        ? (200, list.Skip(start).Take(count).ToList())
                        : (404, new { message = "unknown list " + name });
                }

                if ((m = Regex.Match(path, @"^/api/v2/Search/advancedSearch/movie/(\d+)/(\d+)$")).Success && method == "POST")
                {
                    int start = int.Parse(m.Groups[1].Value), count = int.Parse(m.Groups[2].Value);
                    if (count > 20 || (string)body?["type"] != "movie" || !(body?["genreIds"] is JsonArray))
                    {
                        return (400, new { message = "bad request" });
                    }

                    var year = (int?)body["releaseYear"] ?? 0;
                    var movies = new List<JsonObject>();
                    if (year >= 2018 && year <= 2027)
                    {
                        movies = Enumerable.Range(0, 40).Select(i => Movie(800000 + (year - 2000) * 100 + i, year + "-06-01")).ToList();
                        if (year == 2026)
                        {
                            movies[39] = Movie(100005);
                        }
                    }

                    return (200, movies.Skip(start).Take(count).ToList());
                }

                if (Regex.IsMatch(path, @"^/api/v2/Requests/movie/\d+/0/requestedDate/desc$"))
                {
                    return (200, new { collection = MovieRequests, total = MovieRequests.Count });
                }

                if (Regex.IsMatch(path, @"^/api/v2/Requests/tv/\d+/0/requestedDate/desc$"))
                {
                    return (200, new { collection = TvRequests, total = TvRequests.Count });
                }

                if (method == "POST" && path == "/api/v1/Request/movie")
                {
                    var tmdb = (int)body["theMovieDbId"];

                    // Like Ombi: one request per movie, whoever comes second gets "already requested".
                    if (MovieRequests.Any(r => (int)r["theMovieDbId"] == tmdb))
                    {
                        return (200, new { result = false, isError = true, errorMessage = $"\"Movie {tmdb}\" has already been requested" });
                    }

                    var id = MovieRequests.Max(r => (int)r["id"]) + 1;
                    MovieRequests.Add(MovieRequest(id, tmdb, user));
                    return (200, new { result = true, requestId = id, message = "requested" });
                }

                if ((m = Regex.Match(path, @"^/api/v1/Request/(movie|tv)/remaining$")).Success)
                {
                    return m.Groups[1].Value == "movie" && user == "boss"
                        ? (200, (object)new { hasLimit = true, limit = 10, remaining = 3, nextRequest = (string)null })
                        : (200, new { hasLimit = false, limit = 0, remaining = 0, nextRequest = (string)null });
                }

                if ((m = Regex.Match(path, @"^/api/v1/Request/movie/(\d+)$")).Success && method == "DELETE")
                {
                    MovieRequests.RemoveAll(r => (int)r["id"] == int.Parse(m.Groups[1].Value));
                    return (200, new { result = true });
                }

                if ((m = Regex.Match(path, @"^/api/v1/Request/tv/child/(\d+)$")).Success && method == "DELETE")
                {
                    // Like Ombi: this one answers with a bare true/false.
                    return (200, TvRequests.RemoveAll(r => (int)r["id"] == int.Parse(m.Groups[1].Value)) > 0);
                }

                if ((m = Regex.Match(path, @"^/api/v1/Request/(movie|tv)/(approve|deny)$")).Success)
                {
                    var requests = m.Groups[1].Value == "movie" ? MovieRequests : TvRequests;
                    var request = requests.FirstOrDefault(r => (int)r["id"] == (int)body["id"]);
                    if (request == null)
                    {
                        return (200, new { result = false, isError = true, errorMessage = "Request does not exist" });
                    }

                    var approve = m.Groups[2].Value == "approve";
                    request[approve ? "approved" : "denied"] = true;
                    if (approve)
                    {
                        // Like Ombi: approving lifts a denial.
                        request["denied"] = false;
                    }
                    else
                    {
                        request["deniedReason"] = (string)body["reason"];
                    }

                    return (200, new { result = true });
                }

                if (method == "GET" && path == "/api/v1/Settings/issuesenabled")
                {
                    return (200, true);
                }

                if (method == "GET" && path == "/api/v1/Issues/categories")
                {
                    return (200, new[] { new { id = 1, value = "Ton" }, new { id = 2, value = "Untertitel" } });
                }

                if (method == "POST" && path == "/api/v1/Issues")
                {
                    Issues.Add(body);
                    return (200, 7);
                }
            }

            return (404, new { message = "not mocked: " + path });
        }

        // ---------- HTTP ----------

        private async Task AcceptLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => Handle(client));
            }
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int headerEnd;
                while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
                {
                    var read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    buffer.Write(chunk, 0, read);
                }

                var all = buffer.ToArray();
                var lines = Encoding.UTF8.GetString(all, 0, headerEnd).Split("\r\n");
                var requestLine = lines[0].Split(' ');
                var headers = lines.Skip(1).Select(l => l.Split(new[] { ':' }, 2)).Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                var length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value) : 0;
                var body = new MemoryStream();
                body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
                while (body.Length < length)
                {
                    var read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    body.Write(chunk, 0, read);
                }

                var method = requestLine[0];
                var path = requestLine[1].Split('?')[0];
                headers.TryGetValue("UserName", out var user);
                var json = length > 0 ? JsonNode.Parse(Encoding.UTF8.GetString(body.ToArray())) : null;
                lock (Calls)
                {
                    Calls.Add(new Call { Method = method, Path = path, User = user, Body = json });
                }

                var (status, result) = BusyResponses > 0 && BusyResponses-- > 0
                    ? (503, (object)new { message = "busy" })
                    : Route(method, path, user, json);
                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));
                var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                var headBytes = Encoding.ASCII.GetBytes(head);
                await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
                await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
            }
        }

        private static int IndexOfHeaderEnd(MemoryStream buffer)
        {
            var data = buffer.GetBuffer();
            for (var i = 0; i + 3 < buffer.Length; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
