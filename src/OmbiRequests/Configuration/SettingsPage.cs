using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using OmbiRequests.Catalog;
using OmbiRequests.Ombi;

namespace OmbiRequests.Configuration
{
    /// <summary>
    /// Admin-only settings page. Emby's generic UI API is restricted to administrators already;
    /// <see cref="IPluginPageSecurity"/> enforces it again in case that ever changes.
    /// </summary>
    internal class SettingsPageController : IPluginUIPageController, IPluginPageSecurity
    {
        private readonly Plugin plugin;

        public SettingsPageController(Plugin plugin)
        {
            this.plugin = plugin;
            PageInfo = new PluginPageInfo
            {
                Name = "OmbiRequestsSettings",
                DisplayName = Plugin.PluginName,
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "playlist_add",
                IsMainConfigPage = true
            };
        }

        public PluginPageInfo PageInfo { get; }

        public Task Initialize(CancellationToken token) => Task.CompletedTask;

        public async Task<IPluginUIView> CreateDefaultPageView()
        {
            var view = new SettingsPageView(plugin);
            if (plugin.Configuration.IsConfigured)
            {
                await view.TestConnectionAsync(plugin.Configuration).ConfigureAwait(false);
                await view.UpdateIssueDiagnosticsAsync().ConfigureAwait(false);
            }

            return view;
        }

        public Task CheckIsUserAuthorised(UserDto user, IPluginUIView view)
        {
            if (user?.Policy?.IsAdministrator != true)
            {
                throw new UnauthorizedAccessException("Only administrators can change the Ombi settings.");
            }

            return Task.CompletedTask;
        }
    }

    internal class SettingsPageView : IPluginPageView
    {
        public const string TestCommand = "TestConnection";
        public const string ImportCommand = "ImportCatalog";
        public const string UpdateCommand = "UpdateCatalog";
        public const string TrailerCommand = "CatalogTrailers";

        private readonly Plugin plugin;

        public SettingsPageView(Plugin plugin)
        {
            this.plugin = plugin;
            ContentData = SettingsUI.FromConfiguration(plugin.Configuration);
            LoadUserAccess();
            UpdateWebAppStatus();
            UpdateChannelStatus();
            UpdateCatalogStatus(null);
            UpdateDiagnostics();
        }

        // Not raised: pushes only reach admin sessions anyway and every command returns the updated view.
        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged
        {
            add { }
            remove { }
        }

        public string Caption => ContentData.EditorTitle;

        public string SubCaption => ContentData.EditorDescription;

        public string PluginId => plugin.GetPluginInfo().Id;

        public IEditableObject ContentData { get; set; }

        public UserDto User { get; set; }

        public string RedirectViewUrl { get; set; }

        public bool ShowSave { get; set; } = true;

        public bool ShowBack { get; set; }

        public bool AllowSave { get; set; } = true;

        public bool AllowBack { get; set; } = true;

        private SettingsUI UI => (SettingsUI)ContentData;

        public bool IsCommandAllowed(string commandKey) => commandKey == TestCommand || commandKey == ImportCommand || commandKey == UpdateCommand || commandKey == TrailerCommand;

        public async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case TestCommand:
                    var config = new PluginConfiguration { ApiKey = plugin.Configuration.ApiKey };
                    UI.ApplyTo(config);
                    await TestConnectionAsync(config).ConfigureAwait(false);
                    return this;

                case ImportCommand:
                    // The import uses the numbers on the page, so they are saved first.
                    await OnSaveCommand(itemId, commandId, data).ConfigureAwait(false);
                    if (!plugin.Configuration.ChannelAllMovies && !plugin.Configuration.ChannelAllTv)
                    {
                        UpdateCatalogStatus("Switch on 'All movies' or 'All series' first.");
                        return this;
                    }

                    plugin.TaskManager.QueueIfNotRunning<CatalogImportTask>();
                    UpdateCatalogStatus("Import started - progress under Dashboard > Scheduled tasks; reopen this page to see the result.");
                    return this;

                case UpdateCommand:
                    plugin.TaskManager.QueueIfNotRunning<CatalogUpdateTask>();
                    UpdateCatalogStatus("Loading new titles - progress under Dashboard > Scheduled tasks.");
                    return this;

                case TrailerCommand:
                    plugin.TaskManager.QueueIfNotRunning<CatalogTrailerTask>();
                    UpdateCatalogStatus("Looking up the missing trailers and backgrounds - progress under Dashboard > Scheduled tasks.");
                    return this;
            }

            return null;
        }

        /// <param name="message">Shown instead of the catalog summary, e.g. right after starting a task.</param>
        private void UpdateCatalogStatus(string message)
        {
            var status = UI.CatalogStatus;
            var running = plugin.TaskManager.ScheduledTasks
                .FirstOrDefault(t => (t.ScheduledTask is CatalogImportTask || t.ScheduledTask is CatalogUpdateTask) && t.State != TaskState.Idle);
            if (message != null)
            {
                status.Status = ItemStatus.InProgress;
                status.StatusText = message;
            }
            else if (running != null)
            {
                status.Status = ItemStatus.InProgress;
                status.StatusText = string.Format(CultureInfo.InvariantCulture, "{0}: {1:0}% - progress also under Dashboard > Scheduled tasks.", running.Name, running.CurrentProgress ?? 0);
            }
            else
            {
                var failed = plugin.TaskManager.ScheduledTasks
                    .FirstOrDefault(t => t.ScheduledTask is CatalogImportTask && t.LastExecutionResult?.Status == TaskCompletionStatus.Failed);
                status.Status = failed != null ? ItemStatus.Failed : plugin.Catalog.IsImported ? ItemStatus.Succeeded : ItemStatus.Unavailable;
                status.StatusText = plugin.Catalog.Describe(plugin.Configuration)
                                    + (failed != null ? " Last import failed: " + failed.LastExecutionResult.ErrorMessage : string.Empty);
            }
        }

        public async Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            UI.ApplyTo(plugin.Configuration);
            ApplyUserAccess();
            plugin.SaveConfiguration();
            LoadUserAccess();

            // Copies the web app (a few seconds) or restores Emby's original one.
            await Task.Run(() => plugin.WebAppInjector.Apply(plugin.Configuration.InjectIntoWebApp)).ConfigureAwait(false);
            UpdateWebAppStatus();

            if (plugin.Configuration.EnableChannel)
            {
                // Connection, lists, filters, banners or language may have changed.
                Channel.OmbiChannel.RequestRefresh(TimeSpan.FromSeconds(5));
            }
            else
            {
                try
                {
                    Channel.OmbiChannel.RemoveFromLibrary(plugin.LibraryManager, plugin.Logger);
                }
                catch (Exception ex)
                {
                    plugin.Logger.ErrorException("Removing the requests channel failed", ex);
                }
            }

            UpdateChannelStatus();
            UpdateCatalogStatus(null);
            UpdateDiagnostics();
            if (plugin.Configuration.IsConfigured)
            {
                await UpdateIssueDiagnosticsAsync().ConfigureAwait(false);
            }

            UI.ApiKey = string.IsNullOrEmpty(plugin.Configuration.ApiKey) ? string.Empty : SettingsUI.ApiKeyPlaceholder;
            await TestConnectionAsync(plugin.Configuration).ConfigureAwait(false);
            return this;
        }

        /// <summary>Who has the requests page (plugin setting) and the requests folder (Emby's channel access).</summary>
        private void LoadUserAccess()
        {
            try
            {
                var users = plugin.UserManager.GetUserList(new UserQuery());
                UI.UserOptions = UserAccess.Options(users);
                UI.PageUsers = UserAccess.PageUsers(plugin.Configuration, users);
                var channelId = Channel.OmbiChannel.ItemId(plugin.LibraryManager);
                UI.FolderUsers = channelId == null ? string.Empty : UserAccess.FolderUsers(plugin.UserManager, users, channelId);
            }
            catch (Exception ex)
            {
                plugin.Logger.ErrorException("Loading the users for the settings page failed", ex);
            }
        }

        private void ApplyUserAccess()
        {
            try
            {
                var users = plugin.UserManager.GetUserList(new UserQuery());
                UserAccess.ApplyPageUsers(plugin.Configuration, users, UI.PageUsers);

                var channelId = Channel.OmbiChannel.ItemId(plugin.LibraryManager);
                if (channelId != null)
                {
                    var channels = plugin.LibraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { nameof(MediaBrowser.Controller.Channels.Channel) }
                    }).Select(c => c.Id.ToString("N")).ToList();
                    UserAccess.ApplyFolderUsers(plugin.UserManager, users, UI.FolderUsers, channelId, channels, plugin.Logger);
                }
            }
            catch (Exception ex)
            {
                plugin.Logger.ErrorException("Saving who may use the requests page and folder failed", ex);
            }
        }

        private void UpdateChannelStatus()
        {
            var status = UI.ChannelStatus;
            if (!plugin.Configuration.EnableChannel)
            {
                status.Status = ItemStatus.Unavailable;
                status.StatusText = Channel.OmbiChannel.IsRegistered
                    ? "Off - removed from 'My media'; from the next Emby start on it is not loaded at all."
                    : "Off - not loaded by Emby.";
            }
            else if (Channel.OmbiChannel.IsRegistered)
            {
                status.Status = ItemStatus.Succeeded;
                status.StatusText = "On - visible under 'My media' for users with channel access.";
            }
            else
            {
                status.Status = ItemStatus.Warning;
                status.StatusText = "On - restart Emby to load the channel (it was off when Emby started).";
            }
        }

        private void UpdateDiagnostics()
        {
            string Time(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            UI.DiagnosticsIdentity.StatusText = Diagnostics.ContentIdentity ?? "Not determined yet (after the first channel refresh).";
            UI.DiagnosticsIdentity.Status = Diagnostics.ContentIdentity == null ? ItemStatus.Unavailable
                : Diagnostics.ContentIdentity.StartsWith("API", StringComparison.Ordinal) ? ItemStatus.Warning : ItemStatus.Succeeded;

            var lists = Diagnostics.LastChannelLists;
            UI.DiagnosticsChannel.StatusText = lists == null
                ? "Not built yet since Emby's start."
                : string.Format(CultureInfo.InvariantCulture, "{0}, {1:0} s (shown of loaded): {2}", Time(lists.Item1), lists.Item2.TotalSeconds,
                    string.Join(", ", lists.Item3.Select(l => string.Format(CultureInfo.InvariantCulture, "{0} {1:N0} of {2:N0}{3}",
                        l.Name, l.Shown, l.Loaded, l.Shown == 0 ? " (hidden)" : string.Empty))));

            // Hidden because everything is already in Emby is fine; nothing from Ombi is worth a look.
            UI.DiagnosticsChannel.Status = lists == null ? ItemStatus.Unavailable
                : lists.Item3.Any(l => l.Loaded == 0) ? ItemStatus.Warning : ItemStatus.Succeeded;

            var trailers = plugin.Catalog.TrailerProgress();
            UI.DiagnosticsTrailers.StatusText = trailers.Item2 == 0
                ? "No catalog imported."
                : string.Format(CultureInfo.InvariantCulture, "{0:N0} of {1:N0} titles looked up (new titles right away, the rest 250 per hour or with \"Look up missing trailers and backgrounds now\").", trailers.Item1, trailers.Item2);
            UI.DiagnosticsTrailers.Status = trailers.Item2 == 0 ? ItemStatus.Unavailable : trailers.Item1 == trailers.Item2 ? ItemStatus.Succeeded : ItemStatus.InProgress;

            var check = Diagnostics.LastNotificationCheck;
            UI.DiagnosticsNotifications.StatusText = !plugin.Configuration.EnableAvailableNotifications
                ? "Off."
                : string.Format(CultureInfo.InvariantCulture, "Last check {0}, {1} title(s) reported since Emby's start, {2} waiting for users who are offline.",
                    check.HasValue ? Time(check.Value) : "pending", Diagnostics.NotificationsSent, Channel.AvailabilityNotifier.Current?.PendingCount ?? 0);
            UI.DiagnosticsNotifications.Status = plugin.Configuration.EnableAvailableNotifications ? ItemStatus.Succeeded : ItemStatus.Unavailable;

            var error = Diagnostics.LastError;
            UI.DiagnosticsError.StatusText = error == null ? "No errors since Emby's start." : Time(error.Item1) + ": " + error.Item2;
            UI.DiagnosticsError.Status = error == null ? ItemStatus.Succeeded : ItemStatus.Failed;
        }

        private void UpdateWebAppStatus()
        {
            var injector = plugin.WebAppInjector;
            UI.WebAppStatus.StatusText = injector.Describe();
            UI.WebAppStatus.Status = injector.IsActive
                ? ItemStatus.Succeeded
                : plugin.Configuration.InjectIntoWebApp ? ItemStatus.Failed : ItemStatus.Unavailable;
        }

        public Task Cancel() => Task.CompletedTask;

        public void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data)
        {
        }

        /// <summary>Why the "Report a problem" button shows or not.</summary>
        public async Task UpdateIssueDiagnosticsAsync()
        {
            var status = UI.DiagnosticsIssues;
            try
            {
                var issues = await plugin.RequestService.GetIssueStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (!issues.Item1)
                {
                    status.Status = ItemStatus.Unavailable;
                    status.StatusText = "Off in Ombi (Ombi > Settings > Issues), so the web app shows no 'Report a problem' button.";
                }
                else if (issues.Item2 == 0)
                {
                    status.Status = ItemStatus.Warning;
                    status.StatusText = "On in Ombi, but without categories: add some under Ombi > Settings > Issues.";
                }
                else
                {
                    status.Status = ItemStatus.Succeeded;
                    status.StatusText = $"On in Ombi with {issues.Item2} categories: the web app shows 'Report a problem' on movies, series, seasons and episodes.";
                }
            }
            catch (Exception ex)
            {
                status.Status = ItemStatus.Failed;
                status.StatusText = ex.Message;
            }
        }

        public async Task TestConnectionAsync(PluginConfiguration config)
        {
            var status = UI.ConnectionStatus;
            if (!config.IsConfigured)
            {
                status.Status = ItemStatus.Unavailable;
                status.StatusText = "Enter URL and API key first.";
                return;
            }

            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    var userCount = await plugin.OmbiClient.TestConnectionAsync(config, timeout.Token).ConfigureAwait(false);
                    status.Status = ItemStatus.Succeeded;
                    status.StatusText = $"Connected - {userCount} Ombi users";
                }
            }
            catch (Exception ex) when (ex is OmbiException || ex is OperationCanceledException)
            {
                status.Status = ItemStatus.Failed;
                status.StatusText = ex is OperationCanceledException ? "Timeout - Ombi did not answer within 10 seconds." : ex.Message;
            }
        }
    }
}
