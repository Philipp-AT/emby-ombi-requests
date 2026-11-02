using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using OmbiRequests.Catalog;
using OmbiRequests.Configuration;
using OmbiRequests.Ombi;
using OmbiRequests.Requests;
using OmbiRequests.Web;

namespace OmbiRequests
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasUIPages, IHasThumbImage
    {
        public const string PluginName = "Ombi Requests";

        private readonly Guid id = new Guid("cf393c6d-6baf-4f55-a77e-35beb4e84fb1");
        private readonly Lazy<ILibraryManager> libraryManager;
        private readonly Lazy<ITaskManager> taskManager;
        private readonly Lazy<IUserManager> userManager;
        private List<IPluginUIPageController> pages;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            IServerApplicationHost appHost,
            IServerConfigurationManager serverConfig,
            IJsonSerializer jsonSerializer,
            ILogManager logManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            DataPath = applicationPaths.DataPath;
            Logger = logManager.GetLogger(PluginName);
            libraryManager = new Lazy<ILibraryManager>(() => appHost.Resolve<ILibraryManager>());
            OmbiClient = new OmbiClient(() => Configuration, jsonSerializer, Logger);
            userManager = new Lazy<IUserManager>(() => appHost.Resolve<IUserManager>());
            RequestService = new RequestService(OmbiClient, () => Configuration, serverConfig, libraryManager, Logger,
                new RequestInterest(applicationPaths.DataPath, jsonSerializer, Logger), userManager);
            taskManager = new Lazy<ITaskManager>(() => appHost.Resolve<ITaskManager>());
            Catalog = new TitleCatalog(applicationPaths.DataPath, jsonSerializer, OmbiClient, RequestService, () => Configuration, libraryManager, Logger);

            WebAppInjector = new WebAppInjector(serverConfig, appHost, Logger);
            WebAppInjector.DisableIfOutdated();
        }

        public static Plugin Instance { get; private set; }

        public ILogger Logger { get; }

        /// <summary>Emby's data folder (in Docker /config/data).</summary>
        public string DataPath { get; }

        /// <summary>Resolved on first use; plugins are created before all library services are ready.</summary>
        public ILibraryManager LibraryManager => libraryManager.Value;

        public ITaskManager TaskManager => taskManager.Value;

        public IUserManager UserManager => userManager.Value;

        public override string Name => PluginName;

        public override string Description => "Request movies and series via Ombi from within Emby.";

        public override Guid Id => id;

        public OmbiClient OmbiClient { get; }

        public RequestService RequestService { get; }

        /// <summary>Titles of the channel folders "All movies" and "All series".</summary>
        public TitleCatalog Catalog { get; }

        public WebAppInjector WebAppInjector { get; }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers =>
            pages ?? (pages = new List<IPluginUIPageController> { new SettingsPageController(this) });

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".thumb.png");
        }

        public override void OnUninstalling()
        {
            // Give Emby its original web app back before the plugin is gone.
            WebAppInjector.Remove();
            base.OnUninstalling();
        }
    }
}
