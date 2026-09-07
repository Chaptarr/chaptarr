using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.TPL;

namespace Chaptarr.Core.Test.Indexers
{
    internal sealed class DirectDownloadTestHttp
    {
        public List<string> RequestedUrls { get; } = new List<string>();

        private readonly List<Route> _routes = new List<Route>();

        public void AddRoute(Predicate<string> matcher, Func<HttpRequest, Task<HttpResponse>> responder)
        {
            _routes.Add(new Route(matcher, responder));
        }

        public NzbDrone.Common.Http.HttpClient CreateClient()
        {
            return new NzbDrone.Common.Http.HttpClient(
                Array.Empty<IHttpRequestInterceptor>(),
                new CacheManager(),
                new RateLimitService(new CacheManager(), LogManager.GetCurrentClassLogger()),
                new Dispatcher(this),
                LogManager.GetCurrentClassLogger());
        }

        private sealed class Dispatcher : IHttpDispatcher
        {
            private readonly DirectDownloadTestHttp _owner;

            public Dispatcher(DirectDownloadTestHttp owner)
            {
                _owner = owner;
            }

            public Task<HttpResponse> GetResponseAsync(HttpRequest request, CookieContainer cookies)
            {
                var url = request.Url.FullUri;
                _owner.RequestedUrls.Add(url);

                foreach (var route in _owner._routes)
                {
                    if (route.Matcher(url))
                    {
                        return route.Responder(request);
                    }
                }

                throw new InvalidOperationException($"No test route registered for '{url}'");
            }
        }

        private sealed class Route
        {
            public Route(Predicate<string> matcher, Func<HttpRequest, Task<HttpResponse>> responder)
            {
                Matcher = matcher;
                Responder = responder;
            }

            public Predicate<string> Matcher { get; }

            public Func<HttpRequest, Task<HttpResponse>> Responder { get; }
        }
    }
}
