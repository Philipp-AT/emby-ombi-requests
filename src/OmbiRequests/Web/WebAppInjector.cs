using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace OmbiRequests.Web
{
    /// <summary>
    /// Loads client.js into Emby's web app without a reverse proxy: Emby serves the web app from
    /// ServerConfiguration.DashboardSourcePath if set, so this keeps a copy of dashboard-ui in Emby's
    /// data folder with the script tag added and points that option to it. The copy is rebuilt for
    /// every Emby version; until it is ready the original web app is served.
    /// </summary>
    public class WebAppInjector
    {
        private const string MarkerFileName = ".ombirequests";
        private const string EmbeddedPrefix = "Emby.Web.dashboard_ui.";
        private static readonly object Sync = new object();

        private readonly IServerConfigurationManager configurationManager;
        private readonly IServerApplicationHost appHost;
        private readonly ILogger logger;

        public WebAppInjector(IServerConfigurationManager configurationManager, IServerApplicationHost appHost, ILogger logger)
        {
            this.configurationManager = configurationManager;
            this.appHost = appHost;
            this.logger = logger;
        }

        public string CopyPath => Path.Combine(configurationManager.ApplicationPaths.DataPath, "ombirequests-webapp");

        private string SourcePath => Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath, "dashboard-ui");

        /// <summary>Changes with every build of the plugin, so browsers fetch a new client.js after an update.</summary>
        public static string BuildId => typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);

        private static string ScriptTag => $"<script src=\"../OmbiRequests/web/client.js?v={BuildId}\" defer></script>";

        private string ExpectedMarker => appHost.ApplicationVersion + "|" + BuildId + "|" + SourcePath;

        public bool IsActive => PathEquals(configurationManager.Configuration.DashboardSourcePath, CopyPath);

        /// <summary>Human readable state for the settings page.</summary>
        public string Describe()
        {
            var current = configurationManager.Configuration.DashboardSourcePath;
            if (IsActive)
            {
                return "Active - the web app is served from " + CopyPath;
            }

            return string.IsNullOrEmpty(current)
                ? "Off - Emby serves its original web app."
                : "Not possible - DashboardSourcePath is already set to " + current;
        }

        /// <summary>
        /// Runs in the plugin constructor, before Emby serves requests: a copy made for another Emby
        /// version must never be served, its scripts would be cached under the new version's URLs.
        /// </summary>
        public void DisableIfOutdated()
        {
            try
            {
                if (IsActive && ReadMarker() != ExpectedMarker)
                {
                    logger.Info("Web app copy is outdated, serving Emby's original web app until it is rebuilt.");
                    SetDashboardSourcePath(null);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Checking the web app copy failed", ex);
            }
        }

        public void Apply(bool enabled)
        {
            lock (Sync)
            {
                try
                {
                    if (!enabled)
                    {
                        Remove();
                        return;
                    }

                    var current = configurationManager.Configuration.DashboardSourcePath;
                    if (!string.IsNullOrEmpty(current) && !IsActive)
                    {
                        logger.Warn("Not injecting into the web app: DashboardSourcePath is already set to {0}.", current);
                        return;
                    }

                    if (ReadMarker() != ExpectedMarker)
                    {
                        if (IsActive)
                        {
                            SetDashboardSourcePath(null);
                        }

                        BuildCopy();
                    }

                    if (!IsActive)
                    {
                        SetDashboardSourcePath(CopyPath);
                        logger.Info("Web app is now served from {0} with the Ombi script.", CopyPath);
                    }
                }
                catch (Exception ex)
                {
                    logger.ErrorException("Injecting the Ombi script into the web app failed, Emby keeps its original web app", ex);
                    if (IsActive)
                    {
                        SetDashboardSourcePath(null);
                    }
                }
            }
        }

        public void Remove()
        {
            lock (Sync)
            {
                if (IsActive)
                {
                    SetDashboardSourcePath(null);
                    logger.Info("Web app injection removed, Emby serves its original web app again.");
                }

                TryDeleteDirectory(CopyPath);
            }
        }

        private void BuildCopy()
        {
            // Emby normally serves a few scripts from Emby.Web.dll instead of the disk. With
            // DashboardSourcePath set it reads everything from disk, so they have to be in the copy.
            var webAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Emby.Web")
                              ?? Assembly.Load(new AssemblyName("Emby.Web"));
            var resources = webAssembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
            if (resources.Count == 0)
            {
                throw new InvalidOperationException("Emby.Web contains no web resources - this Emby version is not supported.");
            }

            var tempPath = CopyPath + ".tmp";
            TryDeleteDirectory(tempPath);
            CopyDirectory(SourcePath, tempPath);

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(tempPath, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = file.Substring(tempPath.Length + 1).Replace('\\', '/');
                var resource = FindWebResource(relative, resources);
                if (resource != null)
                {
                    WriteResource(webAssembly, resource, file);
                    written.Add(resource);
                }
            }

            foreach (var resource in resources.Keys.Where(r => !written.Contains(r)))
            {
                foreach (var relative in GuessRelativePaths(resource.Substring(EmbeddedPrefix.Length)))
                {
                    WriteResource(webAssembly, resource, Path.Combine(tempPath, relative));
                }
            }

            var indexPath = Path.Combine(tempPath, "index.html");
            var html = File.ReadAllText(indexPath, Encoding.UTF8);
            var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyEnd < 0)
            {
                throw new InvalidOperationException("index.html has no </body>.");
            }

            File.WriteAllText(indexPath, html.Insert(bodyEnd, "    " + ScriptTag + "\n"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(tempPath, MarkerFileName), ExpectedMarker);

            TryDeleteDirectory(CopyPath);
            Directory.Move(tempPath, CopyPath);
            logger.Info("Built web app copy for Emby {0} with {1} embedded resources.", appHost.ApplicationVersion, resources.Count);
        }

        /// <summary>Same lookup as Emby.Web's WebAppService.FindWebResource.</summary>
        private static string FindWebResource(string relativePath, IDictionary<string, string> resources)
        {
            if (resources.TryGetValue(EmbeddedPrefix + relativePath.Replace('/', '.'), out var name))
            {
                return name;
            }

            var parts = relativePath.Split('/');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                parts[i] = parts[i].Replace('-', '_');
            }

            return resources.TryGetValue(EmbeddedPrefix + string.Join(".", parts), out name) ? name : null;
        }

        /// <summary>
        /// "modules.virtual_scroller.virtual-scroller.js" -> "modules/virtual-scroller/virtual-scroller.js".
        /// Resource names lose the folder separators and turn '-' in folder names into '_', so the
        /// last two segments are the file name and both folder spellings are written.
        /// </summary>
        private static IEnumerable<string> GuessRelativePaths(string resourcePath)
        {
            var segments = resourcePath.Split('.');
            if (segments.Length < 2)
            {
                return Enumerable.Empty<string>();
            }

            // The boundary between folders and file name is lost ("a.b.min.js" may be a/b.min.js or
            // a/b/min.js), so every split is written; unused extra files in the copy do no harm.
            var paths = new List<string>();
            for (var fileStart = segments.Length - 2; fileStart >= 0; fileStart--)
            {
                var fileName = string.Join(".", segments.Skip(fileStart));
                var folders = segments.Take(fileStart).ToArray();
                paths.Add(Path.Combine(folders.Select(f => f.Replace('_', '-')).Concat(new[] { fileName }).ToArray()));
                paths.Add(Path.Combine(folders.Concat(new[] { fileName }).ToArray()));
            }

            return paths.Distinct();
        }

        private static void WriteResource(Assembly assembly, string resource, string targetFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
            using (var source = assembly.GetManifestResourceStream(resource))
            using (var target = File.Create(targetFile))
            {
                source.CopyTo(target);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(target, directory.Substring(source.Length + 1)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(target, file.Substring(source.Length + 1)), true);
            }
        }

        private string ReadMarker()
        {
            var path = Path.Combine(CopyPath, MarkerFileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private void SetDashboardSourcePath(string path)
        {
            configurationManager.Configuration.DashboardSourcePath = path;
            configurationManager.SaveConfiguration();
        }

        private void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Could not delete {0}: {1}", path, ex.Message);
            }
        }

        private static bool PathEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }

            return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.Ordinal);
        }
    }

    /// <summary>Builds or refreshes the web app copy after startup without delaying it.</summary>
    public class WebAppInjectorEntryPoint : IServerEntryPoint
    {
        public void Run()
        {
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                System.Threading.Tasks.Task.Run(() => plugin.WebAppInjector.Apply(plugin.Configuration.InjectIntoWebApp));
            }
        }

        public void Dispose()
        {
        }
    }
}
