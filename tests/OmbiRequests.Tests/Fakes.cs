using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace OmbiRequests.Tests
{
    // Stand-ins for Emby's services, built with DispatchProxy: only the members the plugin uses do something.

    internal static class Fake
    {
        public static T Create<T, TProxy>()
            where TProxy : DispatchProxy => (T)(object)DispatchProxy.Create<T, TProxy>();

        /// <summary>Default value of a return type: empty arrays and lists instead of null.</summary>
        public static object Empty(Type type)
        {
            if (type == typeof(void))
            {
                return null;
            }

            if (type.IsArray)
            {
                return Array.CreateInstance(type.GetElementType(), 0);
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                return Activator.CreateInstance(type);
            }

            if (type == typeof(Task))
            {
                return Task.CompletedTask;
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    public class SyncProgress : IProgress<double>
    {
        private readonly List<double> values;

        public SyncProgress(List<double> values) => this.values = values;

        public void Report(double value) => values.Add(value);
    }

    /// <summary>Emby's IJsonSerializer, case-insensitive like Emby's.</summary>
    public class JsonProxy : DispatchProxy
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "SerializeToString":
                    return JsonSerializer.Serialize(args[0], args[0].GetType(), Options);
                case "SerializeToFile":
                    File.WriteAllText((string)args[1], JsonSerializer.Serialize(args[0], args[0].GetType(), Options));
                    return null;
                case "DeserializeFromString" when method.IsGenericMethod:
                    return JsonSerializer.Deserialize((string)args[0], method.GetGenericArguments()[0], Options);
                case "DeserializeFromFile" when method.IsGenericMethod:
                    return JsonSerializer.Deserialize(File.ReadAllText((string)args[0]), method.GetGenericArguments()[0], Options);
            }

            throw new NotSupportedException(method.Name);
        }
    }

    /// <summary>Emby's XML serializer for the plugin configuration: nothing to read, writing is ignored.</summary>
    public class XmlProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args) =>
            method.Name.StartsWith("Serialize", StringComparison.Ordinal) ? null : throw new FileNotFoundException("no configuration in tests");
    }

    public class LogProxy : DispatchProxy
    {
        public static bool Quiet { get; set; }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (!Quiet && args.Length > 0 && args[0] is string message)
            {
                var rest = args.Skip(1).ToArray();
                var exception = rest.OfType<Exception>().FirstOrDefault();
                var parameters = rest.OfType<object[]>().FirstOrDefault() ?? Array.Empty<object>();
                string text;
                try
                {
                    text = string.Format(message, parameters);
                }
                catch (FormatException)
                {
                    text = message;
                }

                Console.WriteLine($"    [{method.Name}] {text}{(exception != null ? " - " + exception.Message : string.Empty)}");
            }

            return null;
        }
    }

    public class LogManagerProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args) =>
            method.Name == "GetLogger" ? DispatchProxy.Create<ILogger, LogProxy>() : throw new NotSupportedException(method.Name);
    }

    public class ServerConfigProxy : DispatchProxy
    {
        public static readonly ServerConfiguration Configuration = new ServerConfiguration { UICulture = "de-DE" };

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "get_Configuration":
                    return Configuration;
                case "get_ApplicationPaths":
                    // IServerApplicationPaths: every folder is the test's data folder.
                    return typeof(DispatchProxy).GetMethods().First(m => m.Name == "Create" && m.GetGenericArguments().Length == 2)
                        .MakeGenericMethod(method.ReturnType, typeof(PathsProxy)).Invoke(null, null);
                default:
                    return Fake.Empty(method.ReturnType);
            }
        }
    }

    /// <summary>All of Emby's folders point to the test's data folder.</summary>
    public class PathsProxy : DispatchProxy
    {
        public static string DataPath { get; set; }

        protected override object Invoke(MethodInfo method, object[] args) =>
            method.ReturnType == typeof(string) ? DataPath : Fake.Empty(method.ReturnType);
    }

    public class AppHostProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
        {
            var type = method.Name == "Resolve" && method.IsGenericMethod ? method.GetGenericArguments()[0] : null;
            if (type == typeof(MediaBrowser.Controller.Library.ILibraryManager))
            {
                return Fake.Create<MediaBrowser.Controller.Library.ILibraryManager, LibraryProxy>();
            }

            return type == typeof(MediaBrowser.Controller.Library.IUserManager)
                ? Fake.Create<MediaBrowser.Controller.Library.IUserManager, UserManagerProxy>()
                : Fake.Empty(method.ReturnType);
        }
    }

    /// <summary>A library with a few movies and series (for the catalog states) and the channel items of a test.</summary>
    public class LibraryProxy : DispatchProxy
    {
        public static readonly List<string> Updates = new List<string>();
        public static readonly List<BaseItem> ByExternalId = new List<BaseItem>();
        public static readonly Dictionary<long, BaseItem> ById = new Dictionary<long, BaseItem>();

        /// <summary>Items the user may not see (Emby's access check, used by BaseItem.IsVisible).</summary>
        public static readonly HashSet<long> Hidden = new HashSet<long>();

        /// <summary>Ids of deleted items.</summary>
        public static readonly List<long> Deleted = new List<long>();

        /// <summary>The requests channel's entity, if the test registers it; its items are those of <see cref="ByExternalId"/>.</summary>
        public static BaseItem ChannelEntity { get; set; }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name.StartsWith("UpdateItem", StringComparison.Ordinal))
            {
                Updates.Add(((BaseItem)args[0]).ExternalId);
                return null;
            }

            if (method.Name == "DeleteItem")
            {
                var deleted = (BaseItem)args[0];
                Deleted.Add(deleted.InternalId);
                ByExternalId.Remove(deleted);
                return null;
            }

            if (method.Name == "GetItemById" && args.Length == 1 && args[0] is Guid)
            {
                return ChannelEntity;
            }

            if (method.Name == "FilterItemsToIdsForUser")
            {
                return ((IEnumerable<BaseItem>)args[0]).Select(i => i.InternalId).Where(i => !Hidden.Contains(i)).ToArray();
            }

            if (method.Name == "GetItemById" && args.Length == 1 && args[0] is long id)
            {
                return ById.TryGetValue(id, out var item) ? item : null;
            }

            if (method.Name != "GetItemList")
            {
                return Fake.Empty(method.ReturnType);
            }

            var query = (InternalItemsQuery)args[0];
            if (ChannelEntity != null && query.AncestorIds != null && query.AncestorIds.Contains(ChannelEntity.InternalId))
            {
                return ByExternalId.Where(i => query.IncludeItemTypes.Contains(i.GetType().Name)).ToArray();
            }

            if (!string.IsNullOrEmpty(query.ExternalId))
            {
                return ByExternalId.Where(i => i.ExternalId == query.ExternalId).ToArray();
            }

            // The plugin looks channel items up by the keys Emby has an index for.
            if (!string.IsNullOrEmpty(query.PresentationUniqueKey))
            {
                return ByExternalId.Where(i => i.PresentationUniqueKey == query.PresentationUniqueKey).ToArray();
            }

            if (!string.IsNullOrEmpty(query.SeriesPresentationUniqueKey))
            {
                return ByExternalId.Where(i => i.SeriesPresentationUniqueKey == query.SeriesPresentationUniqueKey).ToArray();
            }

            var items = new List<BaseItem>();
            if (query.IncludeItemTypes.Contains("Movie"))
            {
                items.Add(With(new Movie(), "Tmdb", "100005"));
                items.Add(With(new Movie(), "Imdb", "tt100006"));
                var own = With(new Movie(), "Tmdb", "100007");
                own.ExternalId = "ombirequests|movie|100007";
                items.Add(own);
            }
            else if (query.IncludeItemTypes.Contains("Series"))
            {
                items.Add(With(new Series(), "Tvdb", "900004"));
                items.Add(With(new Series(), "Tmdb", "500005"));
                items.Add(With(new Series(), "Tmdb", "500002"));
            }

            return items.ToArray();
        }

        private static BaseItem With(BaseItem item, string key, string value)
        {
            item.SetProviderId(key, value);
            return item;
        }
    }

    /// <summary>Emby users "boss" (online) and "anna" (offline).</summary>
    public class UserManagerProxy : DispatchProxy
    {
        public static readonly User Boss = new User { Name = "boss", InternalId = 7 };
        public static readonly User Anna = new User { Name = "anna", InternalId = 8 };

        /// <summary>Policies saved with UpdateUserPolicy; others have no restrictions.</summary>
        public static readonly Dictionary<long, MediaBrowser.Model.Users.UserPolicy> Policies = new Dictionary<long, MediaBrowser.Model.Users.UserPolicy>();

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "GetUserList":
                    return new[] { Boss, Anna };
                case "GetUserPolicy":
                    // Emby's parental check reads it too.
                    return Policies.TryGetValue(((User)args[0]).InternalId, out var policy) ? policy : new MediaBrowser.Model.Users.UserPolicy();
                case "UpdateUserPolicy":
                    Policies[(long)args[0]] = (MediaBrowser.Model.Users.UserPolicy)args[1];
                    return null;
                default:
                    return Fake.Empty(method.ReturnType);
            }
        }
    }

    /// <summary>One session of "boss"; records the messages sent.</summary>
    public class SessionManagerProxy : DispatchProxy
    {
        public static readonly List<(string SessionId, string Header, string Text)> Messages = new List<(string, string, string)>();
        public static readonly List<SessionInfo> Sessions = new List<SessionInfo> { new SessionInfo { Id = "boss-web", UserInternalId = 7 } };

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "get_Sessions":
                    return Sessions.ToList();
                case "SendMessageCommand":
                    var command = (MessageCommand)args[2];
                    lock (Messages)
                    {
                        Messages.Add(((string)args[1], command.Header, command.Text));
                    }

                    return Task.CompletedTask;
                default:
                    return Fake.Empty(method.ReturnType);
            }
        }
    }

    public class UserDataManagerProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args) => Fake.Empty(method.ReturnType);
    }

    /// <summary>An overlay plugin like Cover Art that takes every poster.</summary>
    public class FakeCoverArt : IImageEnhancer
    {
        public MetadataProviderPriority Priority => MetadataProviderPriority.First;

        public bool Supports(BaseItem item, ImageType imageType) => true;

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType) => "coverart-v1";

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalImageSize) => originalImageSize;

        public EnhancedImageInfo GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) => new EnhancedImageInfo();

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) => Task.CompletedTask;
    }

    public class ImageProcessorProxy : DispatchProxy
    {
        public static IImageEnhancer[] Enhancers { get; set; }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "get_ImageEnhancers")
            {
                return Enhancers;
            }

            if (method.Name == "AddParts")
            {
                Enhancers = ((IEnumerable<IImageEnhancer>)args[0]).ToArray();
                return null;
            }

            throw new NotSupportedException(method.Name);
        }
    }

    public class NullProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args) => Fake.Empty(method.ReturnType);
    }
}
