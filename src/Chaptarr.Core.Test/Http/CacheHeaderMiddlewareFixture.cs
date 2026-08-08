using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class CacheHeaderMiddlewareFixture
    {
        private sealed class NeverCacheSpecification : ICacheableSpecification
        {
            public bool IsCacheable(HttpRequest request) => false;
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
