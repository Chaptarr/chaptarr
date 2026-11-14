using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Http.Middleware;
using NzbDrone.Common.EnvironmentInfo;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class CacheHeaderMiddlewareFixture
    {
        // Exercise the resource policy without depending on the test runner's build mode.
        private sealed class ResourceCacheSpecification : ICacheableSpecification
        {
            private static readonly MethodInfo ResourcePolicy = typeof(CacheableSpecification).GetMethod(
                "IsCacheableResource", BindingFlags.NonPublic | BindingFlags.Static);

            public bool IsCacheable(HttpRequest request) => (bool)ResourcePolicy.Invoke(null, new object[] { request });
        }

        private sealed class NeverCacheSpecification : ICacheableSpecification
        {
            public bool IsCacheable(HttpRequest request) => false;
        }

        [TestCase("/MediaCover/12/poster-250.jpg", "?v=photo-hash")]
        [TestCase("/MediaCover/Books/31/cover-500.jpg", "?v=cover-hash")]
        [TestCase("/mediacover/Books/31/cover.jpg", "?v=cover-hash")]
        [TestCase("/MediaCover/12/poster.jpg", "?h=photo-hash")]
        [TestCase("/MediaCover/12/poster.jpg", "?lastWrite=1234")]
        [TestCase("/Content/styles.css", "?cb=build-hash")]
        public void versioned_images_and_assets_should_allow_browser_caching(string path, string query)
        {
            var context = ImageResponse(path, query);
            var middleware = new CacheHeaderMiddleware(_ => System.Threading.Tasks.Task.CompletedTask, new ResourceCacheSpecification());

            middleware.ApplyCacheHeaders(context);

            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("max-age=31536000, public"));
        }

        [TestCase("/MediaCover/12/poster.jpg", "")]
        [TestCase("/MediaCover/12/poster-1122334455667788.jpg", "")]
        [TestCase("/MediaCover/Books/31/cover.jpg", "?v=")]
        [TestCase("/MediaCover/12/poster.jpg", "?v=%20")]
        [TestCase("/MediaCoverOther/12/poster.jpg", "?v=hash")]
        [TestCase("/MediaCoverProxy/hash/author.jpg", "?v=hash")]
        [TestCase("/Content/styles.css", "?v=hash")]
        [TestCase("/signalr", "?v=hash")]
        public void unversioned_images_and_unrelated_v_parameters_should_remain_uncacheable(string path, string query)
        {
            var context = ImageResponse(path, query);
            var middleware = new CacheHeaderMiddleware(_ => System.Threading.Tasks.Task.CompletedTask, new ResourceCacheSpecification());

            middleware.ApplyCacheHeaders(context);

            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-cache, no-store, must-revalidate"));
        }

        [TestCase(404, "image/jpeg")]
        [TestCase(500, "image/jpeg")]
        [TestCase(200, "text/html; charset=utf-8")]
        public void missing_images_and_error_pages_must_not_be_cached(int status, string contentType)
        {
            var context = ImageResponse("/MediaCover/12/poster.jpg", "?v=hash");
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            var middleware = new CacheHeaderMiddleware(_ => System.Threading.Tasks.Task.CompletedTask, new ResourceCacheSpecification());

            middleware.ApplyCacheHeaders(context);

            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-cache, no-store, must-revalidate"));
        }

        [Test]
        public void media_version_must_not_enable_api_caching()
        {
            var context = ImageResponse("/api/v1/mediacover/12/poster.jpg", "?v=hash");
            var middleware = new CacheHeaderMiddleware(_ => System.Threading.Tasks.Task.CompletedTask, new ResourceCacheSpecification());

            middleware.ApplyCacheHeaders(context);

            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        }

        [Test]
        public async Task unversioned_selected_photo_must_not_return_a_false_not_modified()
        {
            var context = ImageResponse("/MediaCover/12/poster-1122334455667788.jpg", "");
            context.Request.Method = HttpMethods.Get;
            context.Request.Headers.IfModifiedSince = "Sat, 07 Nov 2026 00:00:00 GMT";
            var served = false;
            var policy = new ResourceCacheSpecification();
            var conditional = new IfModifiedMiddleware(_ =>
            {
                served = true;
                return Task.CompletedTask;
            }, policy);

            await conditional.InvokeAsync(context);
            new CacheHeaderMiddleware(_ => Task.CompletedTask, policy).ApplyCacheHeaders(context);

            Assert.That(served, Is.True);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-cache, no-store, must-revalidate"));
            Assert.That(context.Response.Headers.ContainsKey("Last-Modified"), Is.False);
        }

        [Test]
        public void versioned_cover_caching_must_follow_the_production_gate()
        {
            // The shard runner copies the assembly out of _tests, so both environments are valid.
            Assert.That(new CacheableSpecification().IsCacheable(ImageResponse("/MediaCover/12/poster.jpg", "?v=hash").Request),
                Is.EqualTo(RuntimeInfo.IsProduction));
        }

        private static DefaultHttpContext ImageResponse(string path, string query)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.QueryString = new QueryString(query);
            context.Response.ContentType = "image/jpeg";
            return context;
        }

        [Test]
        public void should_preserve_an_explicit_private_cache_policy_for_non_api_images()
        {
            var middleware = new CacheHeaderMiddleware(_ => System.Threading.Tasks.Task.CompletedTask, new NeverCacheSpecification());
            var context = new DefaultHttpContext();
            context.Request.Path = "/MediaCoverProxy/hash/author.jpg";
            context.Response.ContentType = "image/jpeg";
            context.Response.Headers.CacheControl = "private, max-age=86400";

            middleware.ApplyCacheHeaders(context);

            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("private, max-age=86400"));
        }

        [Test]
        public void api_responses_should_remain_no_store_even_if_the_endpoint_sets_a_cache_header()
        {
            var middleware = new CacheHeaderMiddleware(_ => System.Threading.Tasks.Task.CompletedTask, new NeverCacheSpecification());
            var context = new DefaultHttpContext();
            context.Request.Path = "/api/v1/author";
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "public, max-age=86400";

            middleware.ApplyCacheHeaders(context);

            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        }
    }
}
