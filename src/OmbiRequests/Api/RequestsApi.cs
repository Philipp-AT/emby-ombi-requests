using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using OmbiRequests.Ombi;
using OmbiRequests.Requests;

namespace OmbiRequests.Api
{
    /// <summary>Envelope so the web UI can show expected failures (no Ombi account, quota, ...) without HTTP errors.</summary>
    public class ApiResponse<T>
    {
        public bool Success { get; set; }

        public string Message { get; set; }

        public T Data { get; set; }
    }

    [Route("/OmbiRequests/Status", "GET", Summary = "Ombi connection status for the current user")]
    public class GetStatus : IReturn<ApiResponse<StatusDto>>
    {
    }

    [Route("/OmbiRequests/Search", "GET", Summary = "Searches movies and series in Ombi")]
    public class SearchMedia : IReturn<ApiResponse<List<MediaItemDto>>>
    {
        [ApiMember(Name = "Query", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string Query { get; set; }

        [ApiMember(Name = "Type", Description = "all, movie or tv", DataType = "string", ParameterType = "query", Verb = "GET")]
        public string Type { get; set; }
    }

    [Route("/OmbiRequests/Discover", "GET", Summary = "Popular, upcoming and trending titles")]
    public class DiscoverMedia : IReturn<ApiResponse<List<MediaItemDto>>>
    {
        [ApiMember(Name = "Category", Description = "e.g. movie:popular, movie:upcoming, tv:trending", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string Category { get; set; }

        [ApiMember(Name = "Count", DataType = "int", ParameterType = "query", Verb = "GET")]
        public int? Count { get; set; }
    }

    [Route("/OmbiRequests/Details", "GET", Summary = "Details and request status of a title")]
    public class GetMediaDetails : IReturn<ApiResponse<MediaItemDto>>
    {
        [ApiMember(Name = "MediaType", Description = "movie or tv", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string MediaType { get; set; }

        [ApiMember(Name = "TmdbId", IsRequired = true, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int TmdbId { get; set; }
    }

    [Route("/OmbiRequests/Request", "POST", Summary = "Requests a movie or series in Ombi as the current user")]
    public class RequestMedia : IReturn<ApiResponse<RequestOutcomeDto>>
    {
        public string MediaType { get; set; }

        public int TmdbId { get; set; }

        /// <summary>TV only: All, First, Latest, Seasons or Episodes.</summary>
        public string Scope { get; set; }

        /// <summary>TV only, used with Scope=Seasons.</summary>
        public List<int> Seasons { get; set; }

        /// <summary>TV only, used with Scope=Episodes: "season:episode", e.g. "1:3".</summary>
        public List<string> Episodes { get; set; }
    }

    [Route("/OmbiRequests/MyRequests", "GET", Summary = "Requests of the current user")]
    public class GetMyRequests : IReturn<ApiResponse<List<UserRequestDto>>>
    {
    }

    [Route("/OmbiRequests/Withdraw", "POST", Summary = "Withdraws the current user's pending requests of a title")]
    public class WithdrawRequest : IReturn<ApiResponse<RequestOutcomeDto>>
    {
        public string MediaType { get; set; }

        public int TmdbId { get; set; }

        /// <summary>TV only: withdraw just the requests of these seasons.</summary>
        public List<int> Seasons { get; set; }
    }

    [Route("/OmbiRequests/Admin/Requests", "GET", Summary = "Requests of all users (Ombi admins and power users)")]
    public class GetAdminRequests : IReturn<ApiResponse<List<AdminRequestDto>>>
    {
        /// <summary>Also requests that are available in Emby already.</summary>
        public bool IncludeAvailable { get; set; }
    }

    [Route("/OmbiRequests/Admin/Delete", "POST", Summary = "Deletes a request (Ombi admins and power users)")]
    public class AdminDeleteRequest : IReturn<ApiResponse<RequestOutcomeDto>>
    {
        public string MediaType { get; set; }

        /// <summary>Movie request id or season request id.</summary>
        public int RequestId { get; set; }

        public int TmdbId { get; set; }
    }

    [Route("/OmbiRequests/Admin/Decide", "POST", Summary = "Approves or denies a request, also one decided before (Ombi admins and power users)")]
    public class DecideRequest : IReturn<ApiResponse<RequestOutcomeDto>>
    {
        public string MediaType { get; set; }

        /// <summary>Movie request id or season request id.</summary>
        public int RequestId { get; set; }

        public int TmdbId { get; set; }

        public bool Approve { get; set; }

        public string Reason { get; set; }
    }

    [Route("/OmbiRequests/LibraryItem", "GET", Summary = "Request and issue options for a title in the library")]
    public class GetLibraryItem : IReturn<ApiResponse<LibraryItemDto>>
    {
        [ApiMember(Name = "ItemId", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string ItemId { get; set; }
    }

    [Route("/OmbiRequests/Issue", "POST", Summary = "Reports a problem with a title in the library to Ombi")]
    public class ReportIssue : IReturn<ApiResponse<RequestOutcomeDto>>
    {
        public string ItemId { get; set; }

        public int CategoryId { get; set; }

        public string Subject { get; set; }

        public string Description { get; set; }
    }

    [Authenticated]
    public class RequestsApi : IService, IRequiresRequest
    {
        private static readonly HashSet<string> DiscoverCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "movie:popular", "movie:upcoming", "movie:nowplaying", "movie:toprated", "movie:seasonal",
            "tv:popular", "tv:trending", "tv:anticipated", "tv:mostwatched"
        };

        private readonly IAuthorizationContext authContext;
        private readonly ILogger logger;

        public RequestsApi(IAuthorizationContext authContext, ILogManager logManager)
        {
            this.authContext = authContext;
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public IRequest Request { get; set; }

        private static RequestService Service => Plugin.Instance.RequestService;

        // All handlers return Task<object>: Emby's ServiceController.GetTaskResult only reads the
        // result of other Task<T> types if the task completed synchronously, otherwise the response is empty.
        public Task<object> Get(GetStatus request)
        {
            return Run((user, ct) => Service.GetStatusAsync(user, ct), forPage: false);
        }

        public Task<object> Get(SearchMedia request)
        {
            var type = (request.Type ?? "all").ToLowerInvariant();
            return Run((user, ct) => Service.SearchAsync(user, request.Query, type, ct));
        }

        public Task<object> Get(DiscoverMedia request)
        {
            if (!DiscoverCategories.Contains(request.Category ?? string.Empty))
            {
                throw new ArgumentException("Unknown category " + request.Category);
            }

            var count = Math.Max(1, Math.Min(request.Count ?? 20, 50));
            var hideAvailable = !Plugin.Instance.Configuration.ChannelShowAvailable;
            return Run(async (user, ct) =>
            {
                var identity = await Service.GetIdentityAsync(user, ct).ConfigureAwait(false);

                // Fetch more than shown when available titles are hidden, so the lists stay filled.
                var fetch = hideAvailable ? Math.Min(count * 2, 50) : count;
                var items = await Service.DiscoverAsync(user, identity, request.Category.ToLowerInvariant(), fetch, ct).ConfigureAwait(false);
                return hideAvailable
                    ? items.Where(i => !RequestService.IsInLibrary(i)).Take(count).ToList()
                    : items;
            });
        }

        public Task<object> Get(GetMediaDetails request)
        {
            var mediaType = ParseMediaType(request.MediaType);
            return Run((user, ct) => Service.GetDetailsAsync(user, mediaType, request.TmdbId, ct));
        }

        public Task<object> Post(RequestMedia request)
        {
            var mediaType = ParseMediaType(request.MediaType);
            if (!Enum.TryParse(request.Scope ?? nameof(TvRequestScope.All), true, out TvRequestScope scope))
            {
                throw new ArgumentException("Unknown scope " + request.Scope);
            }

            return Run((user, ct) => Service.RequestAsync(user, mediaType, request.TmdbId, scope, request.Seasons, request.Episodes, ct));
        }

        public Task<object> Get(GetMyRequests request)
        {
            return Run((user, ct) => Service.GetMyRequestsAsync(user, ct));
        }

        public Task<object> Post(WithdrawRequest request)
        {
            var mediaType = ParseMediaType(request.MediaType);
            RequestService.CheckSelection(request.TmdbId, request.Seasons, null);
            return Run((user, ct) => Service.WithdrawAsync(user, mediaType, request.TmdbId, request.Seasons, ct));
        }

        public Task<object> Get(GetAdminRequests request)
        {
            return Run((user, ct) => Service.GetAllRequestsAsync(request.IncludeAvailable, ct), adminOnly: true);
        }

        public Task<object> Post(AdminDeleteRequest request)
        {
            var mediaType = ParseMediaType(request.MediaType);
            return Run((user, ct) => Service.DeleteRequestAsync(user, mediaType, request.RequestId, request.TmdbId, ct), adminOnly: true);
        }

        public Task<object> Post(DecideRequest request)
        {
            var mediaType = ParseMediaType(request.MediaType);
            var reason = request.Approve ? null : (request.Reason ?? string.Empty).Trim();
            return Run((user, ct) => Service.DecideAsync(user, mediaType, request.RequestId, request.TmdbId, reason, ct), adminOnly: true);
        }

        public Task<object> Get(GetLibraryItem request)
        {
            return Run((user, ct) => Service.GetLibraryItemAsync(user, request.ItemId, ct));
        }

        public Task<object> Post(ReportIssue request)
        {
            return Run((user, ct) => Service.ReportIssueAsync(user, request.ItemId, request.CategoryId, request.Subject, request.Description, ct));
        }

        /// <param name="adminOnly">
        /// Managing all requests: only admins and power users in Ombi (roles of the linked Ombi user),
        /// checked on every call.
        /// </param>
        /// <param name="forPage">Part of the requests page, which an admin can switch off per user.</param>
        private async Task<object> Run<T>(Func<User, CancellationToken, Task<T>> action, bool adminOnly = false, bool forPage = true)
        {
            // Identity always comes from the authenticated Emby session, never from the request body.
            var user = authContext.GetAuthorizationInfo(Request).User
                       ?? throw new System.Security.SecurityException("No user session");
            if (forPage && Plugin.Instance?.Configuration.IsRequestsPageEnabled(user.InternalId) == false)
            {
                return new ApiResponse<T> { Success = false, Message = Service.Text("RequestsPageDisabled") };
            }

            try
            {
                if (adminOnly && !await Service.CanManageRequestsAsync(user, CancellationToken.None).ConfigureAwait(false))
                {
                    throw new System.Security.SecurityException("Not allowed");
                }

                return new ApiResponse<T> { Success = true, Data = await action(user, CancellationToken.None).ConfigureAwait(false) };
            }
            catch (Exception ex) when (ex is UserFacingException || ex is OmbiException)
            {
                return new ApiResponse<T> { Success = false, Message = Service.DescribeForUser(ex) };
            }
            catch (Exception ex) when (!(ex is ArgumentException) && !(ex is System.Security.SecurityException))
            {
                logger.ErrorException("Ombi request for {0} failed", ex, user.Name);
                return new ApiResponse<T> { Success = false, Message = Service.Text("UnexpectedError") };
            }
        }

        private static string ParseMediaType(string mediaType)
        {
            switch ((mediaType ?? string.Empty).ToLowerInvariant())
            {
                case RequestService.Movie:
                    return RequestService.Movie;
                case RequestService.Tv:
                    return RequestService.Tv;
                default:
                    throw new ArgumentException("MediaType must be movie or tv");
            }
        }
    }
}
