using System;
using System.Linq;
using System.Net;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource.RanobeDb;

namespace Chaptarr.Core.Test.MetadataSource.RanobeDb
{
    [TestFixture]
    public class RanobeDbProxyFixture
    {
        private sealed class CachedResponseStub : ICachedHttpResponseService
        {
            private readonly Func<HttpRequest, HttpResponse> _handler;

            public CachedResponseStub(Func<HttpRequest, HttpResponse> handler)
            {
                _handler = handler;
            }

            public HttpResponse Get(HttpRequest request, bool useCache, TimeSpan ttl) => _handler(request);

            public HttpResponse Get(HttpRequest request, bool useCache, TimeSpan ttl, Func<HttpResponse, bool> isPositive) => _handler(request);

            public HttpResponse<T> Get<T>(HttpRequest request, bool useCache, TimeSpan ttl) where T : new() => throw new NotImplementedException();

            public HttpResponse<T> Get<T>(HttpRequest request, bool useCache, TimeSpan ttl, Func<HttpResponse, bool> isPositive) where T : new() => throw new NotImplementedException();
        }

        [Test]
        public void should_map_direct_book_responses_to_ebook_work_and_release_ids()
        {
            var proxy = CreateProxy(request =>
            {
                if (new Uri(request.Url.ToString()).AbsolutePath == "/api/v0/book/2787")
                {
                    return Json(request, """
                    {
                      "id": 2787,
                      "title": "Sample Light Novel",
                      "description": "Must not be imported",
                      "image": { "id": 1, "filename": "cover.jpg" },
                      "editions": [
                        {
                          "eid": 72,
                          "staff": [
                            { "role_type": "author", "staff_id": 42, "name": "Novel Author", "romaji": "Novel Author" }
                          ]
                        }
                      ],
                      "releases": [
                        { "id": 901, "title": "Sample Light Novel eBook", "lang": "en", "format": "digital", "isbn13": "9781234567890", "pages": 240 },
                        { "id": 902, "title": "Sample Light Novel Print", "lang": "en", "format": "print", "pages": 250 },
                        { "id": 903, "title": "Sample Light Novel Audio", "lang": "en", "format": "audio" }
                      ]
                    }
                    """);
                }

                throw new AssertionException($"Unexpected RanobeDB request: {request.Url}");
            });

            var result = proxy.GetBookInfo("rn:2787");

            Assert.Multiple(() =>
            {
                Assert.That(result.Item1, Is.EqualTo("rn:42"));
                Assert.That(result.Item2.RanobeDbBookId, Is.EqualTo("rn:2787"));
                Assert.That(result.Item2.MediaType, Is.EqualTo(BookMediaType.Ebook));
                Assert.That(result.Item2.Editions, Has.Count.EqualTo(1));
                Assert.That(result.Item2.Editions[0].ForeignEditionId, Is.EqualTo("rnr:901"));
                Assert.That(result.Item2.Editions[0].IsEbook, Is.True);
                Assert.That(result.Item2.Overview, Is.Null);
                Assert.That(result.Item2.Images, Is.Null.Or.Empty);
            });
        }

        [Test]
        public void should_resolve_only_digital_release_ids_to_their_book()
        {
            var proxy = CreateProxy(request =>
            {
                if (new Uri(request.Url.ToString()).AbsolutePath == "/api/v0/release/901")
                {
                    return Json(request, """
                    {
                      "release": {
                        "id": 901,
                        "format": "digital",
                        "title": "Sample Light Novel eBook",
                        "books": [{ "id": 2787 }]
                      }
                    }
                    """);
                }

                if (new Uri(request.Url.ToString()).AbsolutePath == "/api/v0/book/2787")
                {
                    return Json(request, """
                    {
                      "book": {
                        "id": 2787,
                        "title": "Sample Light Novel",
                        "editions": [
                          {
                            "staff": [
                              { "role_type": "author", "staff_id": 42, "name": "Novel Author", "romaji": "Novel Author" }
                            ]
                          }
                        ],
                        "releases": [
                          { "id": 901, "title": "Sample Light Novel eBook", "lang": "en", "format": "digital" }
                        ]
                      }
                    }
                    """);
                }

                throw new AssertionException($"Unexpected RanobeDB request: {request.Url}");
            });

            var result = proxy.GetEditionInfo("rnr:901");

            Assert.Multiple(() =>
            {
                Assert.That(result.Item1, Is.EqualTo("rn:42"));
                Assert.That(result.Item2.RanobeDbBookId, Is.EqualTo("rn:2787"));
                Assert.That(result.Item2.Editions.Single().ForeignEditionId, Is.EqualTo("rnr:901"));
                Assert.That(result.Item2.Editions.Single().Monitored, Is.True);
            });
        }

        [Test]
        public void should_not_resolve_print_release_as_an_ebook()
        {
            var proxy = CreateProxy(request => Json(request, """
                {
                  "release": {
                    "id": 902,
                    "format": "print",
                    "books": [{ "id": 2787 }]
                  }
                }
                """));

            Assert.That(proxy.GetEditionInfo("rnr:902"), Is.Null);
        }

        [Test]
        public void should_not_map_a_print_only_book_as_an_ebook()
        {
            var proxy = CreateProxy(request => Json(request, """
                {
                  "id": 2787,
                  "title": "Print Only",
                  "editions": [
                    {
                      "staff": [
                        { "role_type": "author", "staff_id": 42, "name": "Novel Author", "romaji": "Novel Author" }
                      ]
                    }
                  ],
                  "releases": [
                    { "id": 902, "title": "Print Only", "format": "print" }
                  ]
                }
                """));

            Assert.That(proxy.GetBookInfo("rn:2787"), Is.Null);
        }

        [Test]
        public void should_accept_direct_release_responses()
        {
            var proxy = CreateProxy(request =>
            {
                if (new Uri(request.Url.ToString()).AbsolutePath == "/api/v0/release/901")
                {
                    return Json(request, """
                    {
                      "id": 901,
                      "format": "digital",
                      "title": "Sample Light Novel eBook",
                      "books": [{ "id": 2787 }]
                    }
                    """);
                }

                if (new Uri(request.Url.ToString()).AbsolutePath == "/api/v0/book/2787")
                {
                    return Json(request, """
                    {
                      "id": 2787,
                      "title": "Sample Light Novel",
                      "editions": [
                        {
                          "staff": [
                            { "role_type": "author", "staff_id": 42, "name": "Novel Author", "romaji": "Novel Author" }
                          ]
                        }
                      ],
                      "releases": [
                        { "id": 901, "title": "Sample Light Novel eBook", "lang": "en", "format": "digital" }
                      ]
                    }
                    """);
                }

                throw new AssertionException($"Unexpected RanobeDB request: {request.Url}");
            });

            Assert.That(proxy.GetEditionInfo("rnr:901")?.Item2?.RanobeDbBookId, Is.EqualTo("rn:2787"));
        }

        [Test]
        public void should_not_guess_when_a_release_maps_to_multiple_books()
        {
            var proxy = CreateProxy(request => Json(request, """
                {
                  "release": {
                    "id": 904,
                    "format": "digital",
                    "books": [{ "id": 2787 }, { "id": 2788 }]
                  }
                }
                """));

            Assert.That(proxy.GetEditionInfo("rnr:904"), Is.Null);
        }

        private static RanobeDbProxy CreateProxy(Func<HttpRequest, HttpResponse> handler)
        {
            return new RanobeDbProxy(new CachedResponseStub(handler), LogManager.GetCurrentClassLogger());
        }

        private static HttpResponse Json(HttpRequest request, string content)
        {
            return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content, HttpStatusCode.OK);
        }
    }
}
