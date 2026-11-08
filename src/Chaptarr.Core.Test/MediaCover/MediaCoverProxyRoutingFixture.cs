using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Http.Frontend.Mappers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverProxyRoutingFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private class ConfigFileProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "get_UrlBase"
                    ? "/chaptarr"
                    : throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class HttpClientProxy : DispatchProxy
        {
            public bool WriteOversizedResponse { get; set; }
            public byte[] ResponseData { get; set; }
            public HttpStatusCode? FailureStatusCode { get; set; }
            public WebExceptionStatus? NetworkFailureStatus { get; set; }
            public HashSet<string> FailureUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Exception> ExceptionsByUrl { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> OversizedUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, byte[]> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> ContentTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> RequestedUrls { get; } = new();
            public NzbDrone.Common.Http.HttpRequest LastRequest { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name != "Get" || args?[0] is not NzbDrone.Common.Http.HttpRequest request)
                {
                    throw new NotImplementedException(targetMethod?.Name);
                }

                LastRequest = request;
                RequestedUrls.Add(request.Url.ToString());

                if (ExceptionsByUrl.TryGetValue(request.Url.ToString(), out var exception))
                {
                    throw exception;
                }

                if (FailureStatusCode.HasValue || FailureUrls.Contains(request.Url.ToString()))
                {
                    var statusCode = FailureStatusCode ?? HttpStatusCode.ServiceUnavailable;
                    throw new HttpException(request, new NzbDrone.Common.Http.HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), statusCode));
                }

                if (NetworkFailureStatus.HasValue)
                {
                    throw new WebException("Network failure", NetworkFailureStatus.Value);
                }

                try
                {
                    var responseData = Responses.TryGetValue(request.Url.ToString(), out var configuredResponse)
                        ? configuredResponse
                        : ResponseData;
                    if (responseData != null)
                    {
                        request.ResponseStream.Write(responseData, 0, responseData.Length);
                    }
                    else
                    {
                        var block = new byte[1024 * 1024];
                        MediaCoverRenditionFixture.BuildPngBytes(2, 2, new SixLabors.ImageSharp.PixelFormats.Rgba32(30, 120, 210)).CopyTo(block, 0);
                        var blocks = WriteOversizedResponse || OversizedUrls.Contains(request.Url.ToString()) ? 21 : 1;
                        for (var i = 0; i < blocks; i++)
                        {
                            request.ResponseStream.Write(block, 0, block.Length);
                        }
                    }
                }
                catch (Exception e)
                {
                    throw new WebException("Failed to read complete http response", e, WebExceptionStatus.ReceiveFailure, null);
                }

                var headers = new HttpHeader();
                if (ContentTypes.TryGetValue(request.Url.ToString(), out var contentType))
                {
                    headers.ContentType = contentType;
                }

                return new NzbDrone.Common.Http.HttpResponse(request, headers, Array.Empty<byte>());
            }
        }

        private class MediaCoverProxyStub : IMediaCoverProxy
        {
            public bool Missing { get; set; }
            public bool Placeholder { get; set; }

            public string RegisterUrl(string url) => throw new NotImplementedException();
            public void ProxyRemoteUrls(IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers) => throw new NotImplementedException();
            public string GetUrl(string hash) => Missing ? throw new KeyNotFoundException() : "https://images.example/cover.jpg";
            public byte[] GetImageFromUrl(string url) => Missing
                ? throw new KeyNotFoundException()
                : Placeholder
                    ? throw new PlaceholderImageException("https://images.example/default.jpg", "known")
                    : new byte[] { 1, 2, 3 };
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }
            public int Calls { get; private set; }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name != nameof(IBookService.GetBook))
                {
                    throw new NotImplementedException(method.Name);
                }

                Calls++;
                return Book ?? throw new ModelNotFoundException(typeof(Book), (int)args[0]);
            }
        }

        private static IBookService BookServiceWithCover(bool monitored = true)
        {
            var service = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)service).Book = new Book
            {
                Id = 31,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Monitored = false, Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                    {
                        new(MediaCoverTypes.Cover, "http://127.0.0.1/unmonitored.jpg")
                    } },
                    new Edition { Id = 2, Monitored = monitored, Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                    {
                        new(MediaCoverTypes.Cover, "https://1.1.1.1/monitored.jpg")
                    } }
                }
            };
            return service;
        }

        [Test]
        public void saved_book_proxy_should_resolve_monitored_art_without_registration_after_restart()
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var books = BookServiceWithCover();
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, books, Logger);

            var result = mapper.GetResponse("/MediaCoverProxy/book/31/cover.jpg?v=previous-image") as FileContentResult;

            Assert.That(result, Is.Not.Null);
            Assert.That(((HttpClientProxy)(object)httpClient).LastRequest.Url.ToString(), Is.EqualTo("https://1.1.1.1/monitored.jpg"));
            Assert.That(((BookServiceProxy)(object)books).Calls, Is.EqualTo(1));
        }

        [TestCase("connection")]
        [TestCase("unexpected")]
        [TestCase("unavailable")]
        [TestCase("oversized")]
        [TestCase("placeholder")]
        [TestCase("empty")]
        [TestCase("html")]
        [TestCase("json")]
        public void saved_book_proxy_should_try_the_next_cover_after_a_download_failure(string failure)
        {
            var firstUrl = $"https://1.1.1.1/{failure}-first.jpg";
            const string secondUrl = "https://1.1.1.1/second.jpg";
            var books = BookServiceWithCover();
            var monitoredEdition = ((BookServiceProxy)(object)books).Book.Editions[1];
            monitoredEdition.Images = new List<NzbDrone.Core.MediaCover.MediaCover>
            {
                new(MediaCoverTypes.Cover, firstUrl),
                new(MediaCoverTypes.Cover, secondUrl)
            };
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var client = (HttpClientProxy)(object)httpClient;
            switch (failure)
            {
                case "connection":
                    client.ExceptionsByUrl[firstUrl] = new HttpRequestException("Connection failed");
                    break;
                case "unexpected":
                    client.ExceptionsByUrl[firstUrl] = new InvalidOperationException("Fetch failed");
                    break;
                case "oversized":
                    client.OversizedUrls.Add(firstUrl);
                    break;
                case "empty":
                    client.Responses[firstUrl] = Array.Empty<byte>();
                    break;
                case "html":
                case "json":
                    client.Responses[firstUrl] = System.Text.Encoding.UTF8.GetBytes(failure == "html" ? "<html>Error</html>" : "{\"error\":true}");
                    client.ContentTypes[firstUrl] = failure == "html" ? "text/html" : "application/json";
                    break;
                case "placeholder":
                    client.Responses[firstUrl] = Convert.FromBase64String(MediaCoverRenditionFixture.MascotWebpBase64);
                    break;
                default:
                    client.FailureUrls.Add(firstUrl);
                    break;
            }

            var expectedImage = MediaCoverRenditionFixture.BuildPngBytes(2, 2, new SixLabors.ImageSharp.PixelFormats.Rgba32(30, 120, 210));
            client.Responses[secondUrl] = expectedImage;
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, books, Logger);

            var result = mapper.GetResponse("/MediaCoverProxy/book/31/cover.jpg") as FileContentResult;

            Assert.That(result, Is.Not.Null);
            Assert.That(result.FileContents, Is.EqualTo(expectedImage));
            Assert.That(client.RequestedUrls, Is.EqualTo(new[] { firstUrl, secondUrl }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void oversized_cover_without_an_alternative_should_return_not_found(bool bookRoute)
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            ((HttpClientProxy)(object)httpClient).WriteOversizedResponse = true;
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, BookServiceWithCover(), Logger);
            var path = bookRoute
                ? "/MediaCoverProxy/book/31/cover.jpg"
                : proxy.RegisterUrl("https://1.1.1.1/oversized.jpg").Substring(config.UrlBase.Length);

            var result = mapper.GetResponse(path) as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void connection_failures_without_an_alternative_should_return_not_found(bool bookRoute)
        {
            const string firstUrl = "https://1.1.1.1/first.jpg";
            const string secondUrl = "https://1.1.1.1/second.jpg";
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var client = (HttpClientProxy)(object)httpClient;
            client.ExceptionsByUrl[firstUrl] = new HttpRequestException("Connection failed");
            client.ExceptionsByUrl[secondUrl] = new HttpRequestException("Connection failed");
            var books = BookServiceWithCover();
            ((BookServiceProxy)(object)books).Book.Editions[1].Images = new List<NzbDrone.Core.MediaCover.MediaCover>
            {
                new(MediaCoverTypes.Cover, firstUrl),
                new(MediaCoverTypes.Cover, secondUrl)
            };
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, books, Logger);
            var path = bookRoute
                ? "/MediaCoverProxy/book/31/cover.jpg"
                : proxy.RegisterUrl(firstUrl).Substring(config.UrlBase.Length);

            var result = mapper.GetResponse(path) as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(client.RequestedUrls, Is.EqualTo(bookRoute ? new[] { firstUrl, secondUrl } : new[] { firstUrl }));
        }

        [TestCase("text/html", false)]
        [TestCase("application/json", false)]
        [TestCase(null, true)]
        public void remote_proxy_should_return_not_found_for_empty_or_error_responses(string contentType, bool empty)
        {
            const string url = "https://1.1.1.1/error.jpg";
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var client = (HttpClientProxy)(object)httpClient;
            client.ResponseData = empty ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes("Error");
            client.ContentTypes[url] = contentType;
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, null, Logger);

            var result = mapper.GetResponse(proxy.RegisterUrl(url).Substring(config.UrlBase.Length)) as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(client.RequestedUrls, Is.EqualTo(new[] { url }));
        }

        [Test]
        public void saved_book_proxy_should_skip_unsafe_candidate_and_try_the_next_cover()
        {
            var books = BookServiceWithCover();
            var monitoredEdition = ((BookServiceProxy)(object)books).Book.Editions[1];
            monitoredEdition.Images = new List<NzbDrone.Core.MediaCover.MediaCover>
            {
                new(MediaCoverTypes.Cover, "http://127.0.0.1/unsafe.jpg"),
                new(MediaCoverTypes.Cover, "https://1.1.1.1/safe.jpg")
            };
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, books, Logger);

            var result = mapper.GetResponse("/MediaCoverProxy/book/31/cover.jpg") as FileContentResult;

            Assert.That(result, Is.Not.Null);
            Assert.That(((HttpClientProxy)(object)httpClient).RequestedUrls,
                Is.EqualTo(new[] { "https://1.1.1.1/safe.jpg" }));
        }

        [Test]
        public void saved_book_proxy_should_return_not_found_when_all_monitored_covers_are_unavailable()
        {
            var books = BookServiceWithCover();
            var monitoredEdition = ((BookServiceProxy)(object)books).Book.Editions[1];
            monitoredEdition.Images = new List<NzbDrone.Core.MediaCover.MediaCover>
            {
                new(MediaCoverTypes.Cover, "https://1.1.1.1/first.jpg"),
                new(MediaCoverTypes.Cover, "https://1.1.1.1/second.jpg")
            };
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            ((HttpClientProxy)(object)httpClient).FailureUrls.UnionWith(new[]
            {
                "https://1.1.1.1/first.jpg",
                "https://1.1.1.1/second.jpg"
            });
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, books, Logger);

            var result = mapper.GetResponse("/MediaCoverProxy/book/31/cover.jpg") as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(((HttpClientProxy)(object)httpClient).RequestedUrls,
                Is.EqualTo(new[] { "https://1.1.1.1/first.jpg", "https://1.1.1.1/second.jpg" }));
        }

        [Test]
        public void saved_book_proxy_should_not_fall_back_to_an_unmonitored_edition()
        {
            var books = BookServiceWithCover();
            var book = ((BookServiceProxy)(object)books).Book;
            book.Editions[0].Images[0].Url = "https://1.1.1.1/unmonitored.jpg";
            book.Editions[1].Images.Clear();
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, books, Logger);

            var result = mapper.GetResponse("/MediaCoverProxy/book/31/cover.jpg") as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(((HttpClientProxy)(object)httpClient).RequestedUrls, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void saved_book_without_monitored_art_or_deleted_should_return_not_found(bool deleted)
        {
            var books = BookServiceWithCover(monitored: false);
            if (deleted)
            {
                ((BookServiceProxy)(object)books).Book = null;
            }

            var mapper = new MediaCoverProxyMapper(new MediaCoverProxyStub(), books, Logger);
            var result = mapper.GetResponse("/MediaCoverProxy/book/31/cover.jpg") as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        [Test]
        public void saved_book_proxy_should_keep_the_existing_private_address_guard()
        {
            var books = BookServiceWithCover();
            ((BookServiceProxy)(object)books).Book.Editions[1].Images[0].Url = "http://127.0.0.1/private.jpg";
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var mapper = new MediaCoverProxyMapper(proxy, books, Logger);

            var result = mapper.GetResponse("/MediaCoverProxy/book/31/cover.jpg") as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(((HttpClientProxy)(object)httpClient).LastRequest, Is.Null);
        }

        [Test]
        public void remote_images_should_be_fronted_without_rewriting_local_images()
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(null, config, null, new CacheManager());
            var remote = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/cover.webp");
            var local = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "/MediaCover/1/poster.jpg");

            proxy.ProxyRemoteUrls(new[] { remote, local });

            Assert.That(remote.Url, Does.StartWith("/chaptarr/MediaCoverProxy/"));
            Assert.That(remote.Url, Does.EndWith("/cover.webp"));
            Assert.That(local.Url, Is.EqualTo("/MediaCover/1/poster.jpg"));
        }

        [Test]
        public void extensionless_remote_images_should_get_a_routable_filename()
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(null, config, null, new CacheManager());

            var result = proxy.RegisterUrl("https://books.example/content?id=123&img=1");

            Assert.That(result, Does.EndWith("/cover.jpg"));
        }

        [Test]
        public void known_amazon_default_author_image_should_not_be_registered_for_proxying()
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(null, config, null, new CacheManager());

            var result = proxy.RegisterUrl("https://m.media-amazon.com/images/I/01Kv-W2ysOL.png?size=500");

            Assert.That(result, Is.Null);
        }

        [Test]
        public void remote_image_download_should_enforce_a_size_limit_while_streaming()
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            ((HttpClientProxy)(object)httpClient).WriteOversizedResponse = true;
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var proxyUrl = proxy.RegisterUrl("https://1.1.1.1/cover.jpg");
            var hash = proxyUrl.Split('/')[3];

            var exception = Assert.Throws<WebException>(() => proxy.GetImageFromUrl(proxy.GetUrl(hash)));
            Assert.That(exception.Status, Is.EqualTo(WebExceptionStatus.ReceiveFailure));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void remote_proxy_should_never_return_known_placeholder_bytes()
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            ((HttpClientProxy)(object)httpClient).ResponseData = Convert.FromBase64String(MediaCoverRenditionFixture.MascotWebpBase64);
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            const string remoteUrl = "https://1.1.1.1/provider-default.jpg";
            var proxyUrl = proxy.RegisterUrl(remoteUrl);
            var hash = proxyUrl.Split('/')[3];

            Assert.Throws<PlaceholderImageException>(() => proxy.GetImageFromUrl(proxy.GetUrl(hash)));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(remoteUrl), Is.True);
            Assert.That(proxy.RegisterUrl(remoteUrl), Is.Null);
        }

        [Test]
        public void expired_proxy_mapping_should_return_not_found()
        {
            var mapper = new MediaCoverProxyMapper(new MediaCoverProxyStub { Missing = true }, null, Logger);

            var result = mapper.GetResponse("/MediaCoverProxy/missing/cover.jpg") as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        [Test]
        public void placeholder_proxy_response_should_return_not_found()
        {
            var mapper = new MediaCoverProxyMapper(new MediaCoverProxyStub { Placeholder = true }, null, Logger);

            var result = mapper.GetResponse("/MediaCoverProxy/placeholder/cover.jpg") as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        [Test]
        public void upstream_http_error_should_return_not_found_without_default_error_logging()
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var client = (HttpClientProxy)(object)httpClient;
            client.FailureStatusCode = HttpStatusCode.Forbidden;
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var proxyUrl = proxy.RegisterUrl("https://1.1.1.1/challenged.jfif");
            var mapper = new MediaCoverProxyMapper(proxy, null, Logger);

            var result = mapper.GetResponse(proxyUrl.Substring(config.UrlBase.Length)) as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(client.LastRequest.LogHttpError, Is.False);
        }

        [Test]
        public void upstream_network_failure_should_return_not_found()
        {
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientProxy>();
            var client = (HttpClientProxy)(object)httpClient;
            client.NetworkFailureStatus = WebExceptionStatus.Timeout;
            var proxy = new NzbDrone.Core.MediaCover.MediaCoverProxy(httpClient, config, null, new CacheManager());
            var proxyUrl = proxy.RegisterUrl("https://1.1.1.1/timeout.jpg");
            var mapper = new MediaCoverProxyMapper(proxy, null, Logger);

            var result = mapper.GetResponse(proxyUrl.Substring(config.UrlBase.Length)) as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(client.LastRequest.LogHttpError, Is.False);
        }

        [TestCase("/MediaCoverProxy/found/cover.jpg")]
        [TestCase("/MediaCoverProxy/book/31/cover.jpg?v=current")]
        public async Task proxied_image_response_should_enable_private_browser_caching(string path)
        {
            var mapper = new MediaCoverProxyMapper(new MediaCoverProxyStub(), BookServiceWithCover(), Logger);
            var result = mapper.GetResponse(path);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllers();
            var httpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider()
            };
            httpContext.Response.Body = new MemoryStream();
            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

            await result.ExecuteResultAsync(actionContext);

            Assert.That(httpContext.Response.Headers.CacheControl.ToString(), Is.EqualTo("private, max-age=86400"));
        }
    }
}
