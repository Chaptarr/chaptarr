using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using Chaptarr.Http.Frontend;
using Chaptarr.Http.Frontend.Mappers;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class StaticResourceControllerFixture
    {
        private sealed class StubMapper : IMapHttpRequestsToDisk
        {
            public Func<string, bool> CanHandleFunc { get; init; } = _ => false;
            public Func<string, IActionResult> GetResponseFunc { get; init; }
            public int GetResponseCalls { get; private set; }

            public string Map(string resourceUrl) => resourceUrl;

            public bool CanHandle(string resourceUrl) => CanHandleFunc(resourceUrl);

            public IActionResult GetResponse(string resourceUrl)
            {
                GetResponseCalls++;
                return GetResponseFunc?.Invoke(resourceUrl);
            }
        }

        [Test]
        public void index_routes_should_keep_api_and_feed_exclusions_anchored_to_path_segments()
        {
            var indexTemplates = typeof(StaticResourceController)
                .GetMethod(nameof(StaticResourceController.Index))
                .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
                .Cast<HttpGetAttribute>()
                .Select(attribute => attribute.Template)
                .ToArray();

            var contentTemplate = (HttpGetAttribute)typeof(StaticResourceController)
                .GetMethod(nameof(StaticResourceController.IndexContent))
                .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
                .Single();

            Assert.That(indexTemplates, Does.Contain("/{**path:regex(^(?!(api|feed)(/|$)).*)}"));
            Assert.That(contentTemplate.Template, Is.EqualTo("content/{**path:regex(^(?!api(/|$)).*)}"));
        }

        [Test]
        public void should_use_first_matching_mapper_and_disable_cache_for_html_with_charset()
        {
            var first = new StubMapper
            {
                CanHandleFunc = path => string.Equals(path, "/Content/app/index.html", StringComparison.Ordinal),
                GetResponseFunc = _ => new FileContentResult(new byte[] { 1, 2, 3 }, "text/html; charset=utf-8")
            };

            var second = new StubMapper
            {
                CanHandleFunc = path => string.Equals(path, "/Content/app/index.html", StringComparison.Ordinal),
                GetResponseFunc = _ => new FileContentResult(new byte[] { 4, 5, 6 }, "text/plain")
            };

            var controller = CreateController(first, second);

            var result = controller.IndexContent("app/index.html");

            Assert.That(result, Is.TypeOf<FileContentResult>());
            Assert.That(((FileContentResult)result).ContentType, Is.EqualTo("text/html; charset=utf-8"));
            Assert.That(first.GetResponseCalls, Is.EqualTo(1));
            Assert.That(second.GetResponseCalls, Is.EqualTo(0));
            Assert.That(controller.Response.Headers["Cache-Control"].ToString(), Is.EqualTo("no-cache, no-store, must-revalidate"));
            Assert.That(controller.Response.Headers["Pragma"].ToString(), Is.EqualTo("no-cache"));
            Assert.That(controller.Response.Headers["Expires"].ToString(), Is.EqualTo("0"));
        }

        [Test]
        public void should_return_not_found_when_mapper_returns_null_response()
        {
            var mapper = new StubMapper
            {
                CanHandleFunc = _ => true,
                GetResponseFunc = _ => null
            };

            var controller = CreateController(mapper);

            var result = controller.Index("missing.js");

            Assert.That(result, Is.TypeOf<NotFoundResult>());
        }

        private static StaticResourceController CreateController(params IMapHttpRequestsToDisk[] mappers)
        {
            var controller = new StaticResourceController(mappers, LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            return controller;
        }
    }
}
