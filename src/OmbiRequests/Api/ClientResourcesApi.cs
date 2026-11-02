using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace OmbiRequests.Api
{
    /// <summary>
    /// Serves the web app integration script. Emby ignores "/web/..." routes from plugins
    /// (ServiceController.RegisterRestPaths), so the script tag is added to a copy of Emby's
    /// index.html (see WebAppInjector) or by a reverse proxy - see README.
    /// </summary>
    [Route("/OmbiRequests/web/{Name}", "GET", IsHidden = true)]
    public class GetClientResource
    {
        public string Name { get; set; }
    }

    // Loaded by a plain <script> tag, which sends no Emby token. Emby makes plugin routes
    // authenticated by default, so this static, non-sensitive content has to opt out.
    [Unauthenticated]
    public class ClientResourcesApi : IService, IRequiresRequest
    {
        private static readonly Dictionary<string, string> Resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["client.js"] = "application/javascript",
            ["client.css"] = "text/css"
        };

        private readonly IHttpResultFactory resultFactory;

        public ClientResourcesApi(IHttpResultFactory resultFactory)
        {
            this.resultFactory = resultFactory;
        }

        public IRequest Request { get; set; }

        public object Get(GetClientResource request)
        {
            if (!Resources.TryGetValue(request.Name ?? string.Empty, out var contentType))
            {
                throw new FileNotFoundException(request.Name);
            }

            // Resource names are case-sensitive, the URL lookup above is not.
            var name = Resources.Keys.First(k => string.Equals(k, request.Name, StringComparison.OrdinalIgnoreCase));
            var stream = typeof(Plugin).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web." + name);

            // Versioned URLs (automatic injection) change with every plugin build and can be cached for good;
            // unversioned ones (reverse proxy tag) are revalidated so plugin updates show up immediately.
            var versioned = !string.IsNullOrEmpty(Request.QueryString["v"]);
            var headers = new Dictionary<string, string> { ["Cache-Control"] = versioned ? "public, max-age=31536000" : "no-cache" };
            return resultFactory.GetResult(Request, stream, contentType + "; charset=utf-8", headers);
        }
    }
}
