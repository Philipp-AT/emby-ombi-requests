using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using OmbiRequests.Requests;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Movies in the channel are videos without a file. Emby turns such an item's own media source into a
    /// placeholder and drops it from the playback info, so the apps get an empty list; the Apple TV app
    /// then fails on its details page ("undefined is not an object (evaluating '(this.currentMediaSource()
    /// || mediaSources[0]).Id')"). Every movie therefore gets a short video as its media source that shows
    /// the request state, like the banner on the poster.
    /// <para>The videos are rendered at build time (tools/generate-placeholder-videos.ps1) and written to
    /// Emby's data folder, one file per state. The files hold the current language, so the paths stored
    /// in the items do not change with the language.</para>
    /// <para>Emby keeps a channel item's media sources in the provider id <see cref="MediaSourcesProviderKey"/>
    /// (ChannelManager) and uses them when the item has no media streams of its own. They carry their
    /// streams, so Emby never probes the file; a probe would run a full metadata refresh of the item.</para>
    /// </summary>
    internal static class PlaceholderVideos
    {
        /// <summary>Emby's own key (Emby.Server.Implementations ChannelManager), base64 of the JSON array.</summary>
        public const string MediaSourcesProviderKey = "channel_item_mediasources";

        public const string FolderName = "ombirequests-placeholders";

        /// <summary>Length, size and format of the rendered videos (see the script).</summary>
        public static readonly long RunTimeTicks = TimeSpan.FromSeconds(10).Ticks;
        private const int Width = 1920;
        private const int Height = 1080;
        private const int Bitrate = 50000;

        private static readonly object FilesLock = new object();

        /// <summary>The state a video exists for; unknown or empty states show "not in Emby yet".</summary>
        public static string NormalizeStatus(string status)
        {
            return Enum.GetNames(typeof(MediaStatus)).Contains(status) ? status : nameof(MediaStatus.None);
        }

        public static string GetPath(string dataPath, string status)
        {
            return Path.Combine(dataPath, FolderName, NormalizeStatus(status) + ".mp4");
        }

        /// <summary>
        /// Writes the videos of the given language into the data folder where they differ (first start,
        /// language change, new plugin version). Cheap when nothing changed: a few small files are compared.
        /// </summary>
        public static void EnsureFiles(string dataPath, bool german, ILogger logger)
        {
            if (string.IsNullOrEmpty(dataPath))
            {
                return;
            }

            lock (FilesLock)
            {
                foreach (var status in Enum.GetNames(typeof(MediaStatus)))
                {
                    var target = GetPath(dataPath, status);
                    try
                    {
                        var content = ReadResource(status, german);
                        if (content == null)
                        {
                            logger?.Error("Placeholder video for {0} is missing in the plugin", status);
                            continue;
                        }

                        if (File.Exists(target) && new FileInfo(target).Length == content.Length && File.ReadAllBytes(target).SequenceEqual(content))
                        {
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        var tempFile = target + ".tmp";
                        File.WriteAllBytes(tempFile, content);
                        if (File.Exists(target))
                        {
                            File.Replace(tempFile, target, null);
                        }
                        else
                        {
                            File.Move(tempFile, target);
                        }

                        logger?.Debug("Placeholder video written: {0}", target);
                    }
                    catch (Exception ex)
                    {
                        // A video that is being played cannot be replaced on Windows; the next call retries.
                        logger?.ErrorException("Writing the placeholder video {0} failed", ex, target);
                    }
                }
            }
        }

        /// <summary>The media source of a movie in the channel: the video of its state.</summary>
        public static List<MediaSourceInfo> CreateSources(string dataPath, string externalId, string status)
        {
            return new List<MediaSourceInfo>
            {
                new MediaSourceInfo
                {
                    Id = SourceId(externalId),
                    Path = GetPath(dataPath, status),
                    Protocol = MediaProtocol.File,
                    Type = MediaSourceType.Default,
                    Container = "mp4",
                    RunTimeTicks = RunTimeTicks,
                    Bitrate = Bitrate,
                    IsRemote = false,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    MediaStreams = new List<MediaStream>
                    {
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            Index = 0,
                            Codec = "h264",
                            Profile = "High",
                            Level = 40,
                            Width = Width,
                            Height = Height,
                            AspectRatio = "16:9",
                            AverageFrameRate = 25,
                            RealFrameRate = 25,
                            PixelFormat = "yuv420p",
                            BitDepth = 8,
                            IsInterlaced = false,
                            IsDefault = true
                        },
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Index = 1,
                            Codec = "aac",
                            Profile = "LC",
                            Channels = 2,
                            ChannelLayout = "stereo",
                            SampleRate = 48000,
                            IsDefault = true
                        }
                    }
                }
            };
        }

        /// <summary>The value Emby stores in <see cref="MediaSourcesProviderKey"/> (same encoding as Emby).</summary>
        public static string Encode(IJsonSerializer json, List<MediaSourceInfo> sources)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json.SerializeToString(sources)));
        }

        /// <summary>The path of the first stored media source, null if there is none or it cannot be read.</summary>
        public static string StoredPath(IJsonSerializer json, string stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            try
            {
                var sources = json.DeserializeFromString<MediaSourceInfo[]>(Encoding.UTF8.GetString(Convert.FromBase64String(stored)));
                return sources?.FirstOrDefault()?.Path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Stable per title, in the format of Emby's own media source ids (32 hex digits).</summary>
        private static string SourceId(string externalId)
        {
            using (var md5 = MD5.Create())
            {
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("ombirequests-placeholder|" + externalId))).ToString("N");
            }
        }

        private static byte[] ReadResource(string status, bool german)
        {
            var name = $"{typeof(Plugin).Namespace}.Videos.placeholder-{status}-{(german ? "de" : "en")}.mp4";
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    return null;
                }

                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }
    }
}
