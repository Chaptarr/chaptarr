using System.Threading.Tasks;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class ServarrMediaTypeScopeMiddlewareFixture
    {
        [TestCase("/ebook/api/v1/book/lookup", "ebook")]
        [TestCase("/ebooks/api/v1/book/lookup", "ebook")]
        [TestCase("/audiobook/api/v1/book/lookup", "audiobook")]
        [TestCase("/audiobooks/api/v1/book/lookup", "audiobook")]
        [TestCase("/audioboks/api/v1/book/lookup", "audiobook")]
        public async Task should_rewrite_scoped_api_paths_and_inject_mediaType(string path, string expectedMediaType)
        {
            var sawPath = string.Empty;
            var sawMediaType = string.Empty;
            ReadarrFacadeContext sawFacade = null;

            var middleware = new ServarrMediaTypeScopeMiddleware(context =>
            {
                sawPath = context.Request.Path.Value;
                sawMediaType = context.Request.Query["mediaType"];
                sawFacade = context.GetReadarrFacadeContext();
                return Task.CompletedTask;
            });

            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.QueryString = new QueryString("?apikey=abc&term=work:123");

            await middleware.InvokeAsync(context);

            Assert.That(sawPath, Is.EqualTo("/api/v1/book/lookup"));
            Assert.That(sawMediaType, Is.EqualTo(expectedMediaType));
            Assert.That(sawFacade?.Dialect, Is.EqualTo("hc"));
            Assert.That(sawFacade?.MediaType, Is.EqualTo(expectedMediaType));
        }

        [TestCase("/readarr/gr/ebook/api/v1/book/lookup", "gr", "ebook", "/readarr/gr/ebook")]
        [TestCase("/readarr/hc/audiobook/api/v1/book/lookup", "hc", "audiobook", "/readarr/hc/audiobook")]
        public async Task should_rewrite_explicit_readarr_facade_paths_and_capture_dialect(string path, string dialect, string expectedMediaType, string expectedPrefix)
        {
            var sawPath = string.Empty;
            var sawMediaType = string.Empty;
            ReadarrFacadeContext sawFacade = null;

            var middleware = new ServarrMediaTypeScopeMiddleware(context =>
            {
                sawPath = context.Request.Path.Value;
                sawMediaType = context.Request.Query["mediaType"];
                sawFacade = context.GetReadarrFacadeContext();
                return Task.CompletedTask;
            });

            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.QueryString = new QueryString("?apikey=abc&term=work:123");

            await middleware.InvokeAsync(context);

            Assert.That(sawPath, Is.EqualTo("/api/v1/book/lookup"));
            Assert.That(sawMediaType, Is.EqualTo(expectedMediaType));
            Assert.That(sawFacade?.Dialect, Is.EqualTo(dialect));
            Assert.That(sawFacade?.MediaType, Is.EqualTo(expectedMediaType));
            Assert.That(sawFacade?.Prefix, Is.EqualTo(expectedPrefix));
        }

        [Test]
        public async Task should_not_rewrite_non_api_scoped_paths()
        {
            var sawPath = string.Empty;

            var middleware = new ServarrMediaTypeScopeMiddleware(context =>
            {
                sawPath = context.Request.Path.Value;
                return Task.CompletedTask;
            });

            var context = new DefaultHttpContext();
            context.Request.Path = "/ebook";

            await middleware.InvokeAsync(context);

            Assert.That(sawPath, Is.EqualTo("/ebook"));
        }

        [Test]
        public async Task should_not_override_explicit_mediaType_query_param()
        {
            var sawQuery = string.Empty;

            var middleware = new ServarrMediaTypeScopeMiddleware(context =>
            {
                sawQuery = context.Request.QueryString.Value;
                return Task.CompletedTask;
            });

            var context = new DefaultHttpContext();
            context.Request.Path = "/ebook/api/v1/book/lookup";
            context.Request.QueryString = new QueryString("?mediaType=audiobook&apikey=abc");

            await middleware.InvokeAsync(context);

            Assert.That(sawQuery, Does.Contain("mediaType=audiobook"));
            Assert.That(sawQuery, Does.Not.Contain("mediaType=ebook"));
        }
    }
}
