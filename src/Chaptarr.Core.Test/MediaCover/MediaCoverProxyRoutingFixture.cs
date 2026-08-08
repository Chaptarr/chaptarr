using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Http.Frontend.Mappers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverProxyRoutingFixture
    {
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

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name != "Get" || args?[0] is not NzbDrone.Common.Http.HttpRequest request)
                {
                    throw new NotImplementedException(targetMethod?.Name);
                }

                if (ResponseData != null)
                {
                    request.ResponseStream.Write(ResponseData, 0, ResponseData.Length);
                }
                else
                {
                    var block = new byte[1024 * 1024];
                    var blocks = WriteOversizedResponse ? 21 : 1;
                    for (var i = 0; i < blocks; i++)
                    {
                        request.ResponseStream.Write(block, 0, block.Length);
                    }
                }

                return new NzbDrone.Common.Http.HttpResponse(request, new HttpHeader(), Array.Empty<byte>());
            }
        }

        private class MediaCoverProxyStub : IMediaCoverProxy
        {
            public bool Missing { get; set; }
            public bool Placeholder { get; set; }

            public string RegisterUrl(string url) => throw new NotImplementedException();
            public void ProxyRemoteUrls(IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers) => throw new NotImplementedException();
            public string GetUrl(string hash) => throw new NotImplementedException();
            public byte[] GetImage(string hash) => Missing
                ? throw new KeyNotFoundException()
                : Placeholder
                    ? throw new PlaceholderImageException("https://images.example/default.jpg", "known")
                    : new byte[] { 1, 2, 3 };
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

            Assert.Throws<InvalidDataException>(() => proxy.GetImage(hash));
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

            Assert.Throws<PlaceholderImageException>(() => proxy.GetImage(hash));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(remoteUrl), Is.True);
            Assert.That(proxy.RegisterUrl(remoteUrl), Is.Null);
        }

        [Test]
        public void expired_proxy_mapping_should_return_not_found()
        {
            var mapper = new MediaCoverProxyMapper(new MediaCoverProxyStub { Missing = true });

            var result = mapper.GetResponse("/MediaCoverProxy/missing/cover.jpg") as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        [Test]
        public void placeholder_proxy_response_should_return_not_found()
        {
            var mapper = new MediaCoverProxyMapper(new MediaCoverProxyStub { Placeholder = true });

            var result = mapper.GetResponse("/MediaCoverProxy/placeholder/cover.jpg") as StatusCodeResult;

            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        [Test]
        public async Task proxied_image_response_should_enable_private_browser_caching()
        {
            var mapper = new MediaCoverProxyMapper(new MediaCoverProxyStub());
            var result = mapper.GetResponse("/MediaCoverProxy/found/cover.jpg");
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
