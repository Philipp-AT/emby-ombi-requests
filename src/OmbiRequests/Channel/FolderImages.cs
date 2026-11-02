using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Pictures of the channel folders (Images/folder-*.png, letter-*.png). Emby takes a channel item's
    /// image from a URL or a file, so they are written to Emby's data folder. The file name contains a
    /// hash of the content: a changed picture gets a new path, which <see cref="ChannelItemUpdater"/>
    /// then puts on the folder.
    /// </summary>
    internal static class FolderImages
    {
        private const string FolderName = "ombirequests-images";
        private static readonly ConcurrentDictionary<string, string> Paths = new ConcurrentDictionary<string, string>();

        public static string ForList(string category, bool german)
        {
            string key;
            switch (category)
            {
                case "movie:popular":
                    key = "popular-movies";
                    break;
                case "movie:nowplaying":
                    key = "nowplaying-movies";
                    break;
                case "movie:upcoming":
                    key = "upcoming-movies";
                    break;
                case "movie:all":
                    key = "all-movies";
                    break;
                case "tv:trending":
                    key = "trending-tv";
                    break;
                case "tv:popular":
                    key = "popular-tv";
                    break;
                case "movie:toprated":
                    key = "toprated-movies";
                    break;
                case "movie:seasonal":
                    key = "seasonal-movies";
                    break;
                case "tv:anticipated":
                    key = "anticipated-tv";
                    break;
                case "tv:mostwatched":
                    key = "toprated-tv";
                    break;
                case "tv:all":
                    key = "all-tv";
                    break;
                default:
                    return null;
            }

            return Get($"folder-{key}-{(german ? "de" : "en")}.png");
        }

        /// <param name="letter">"A".."Z" or "#".</param>
        public static string ForLetter(string letter)
        {
            return Get($"letter-{(letter == "#" ? "other" : letter)}.png");
        }

        /// <returns>The local file, null if the picture or Emby's data folder is not available.</returns>
        private static string Get(string resourceFile)
        {
            var dataPath = Plugin.Instance?.DataPath;
            if (string.IsNullOrEmpty(dataPath))
            {
                return null;
            }

            return Paths.GetOrAdd(resourceFile, name => Extract(dataPath, name));
        }

        private static string Extract(string dataPath, string resourceFile)
        {
            try
            {
                byte[] bytes;
                using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Images.{resourceFile}"))
                {
                    if (stream == null)
                    {
                        return null;
                    }

                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        bytes = memory.ToArray();
                    }
                }

                string hash;
                using (var sha = SHA1.Create())
                {
                    hash = string.Concat(sha.ComputeHash(bytes).Take(4).Select(b => b.ToString("x2")));
                }

                var directory = Path.Combine(dataPath, FolderName);
                Directory.CreateDirectory(directory);
                var baseName = Path.GetFileNameWithoutExtension(resourceFile);
                var path = Path.Combine(directory, $"{baseName}.{hash}.png");
                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, bytes);

                    // Older versions of the same picture.
                    foreach (var old in Directory.GetFiles(directory, baseName + ".*.png").Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        File.Delete(old);
                    }
                }

                return path;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger.ErrorException("Writing the folder picture {0} failed", ex, resourceFile);
                return null;
            }
        }
    }
}
