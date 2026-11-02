using System;
using System.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace OmbiRequests
{
    /// <summary>A JSON file in Emby's data folder, written via a temporary file so it is never half written.</summary>
    public sealed class DataFile<T>
        where T : class, new()
    {
        private readonly string path;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;

        public DataFile(string dataPath, string fileName, IJsonSerializer json, ILogger logger)
        {
            path = Path.Combine(dataPath, fileName);
            this.json = json;
            this.logger = logger;
        }

        /// <returns>The content, a new object if there is no file or it cannot be read.</returns>
        public T Load()
        {
            try
            {
                if (File.Exists(path))
                {
                    return json.DeserializeFromFile<T>(path) ?? new T();
                }

                // Written completely, but the server stopped before it replaced the file.
                if (File.Exists(path + ".tmp"))
                {
                    return json.DeserializeFromFile<T>(path + ".tmp") ?? new T();
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Reading {0} failed", ex, path);
            }

            return new T();
        }

        public void Save(T value)
        {
            var tempFile = path + ".tmp";
            try
            {
                json.SerializeToFile(value, tempFile);
                if (File.Exists(path))
                {
                    File.Replace(tempFile, path, null);
                }
                else
                {
                    File.Move(tempFile, path);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Saving {0} failed", ex, path);
            }
        }
    }
}
