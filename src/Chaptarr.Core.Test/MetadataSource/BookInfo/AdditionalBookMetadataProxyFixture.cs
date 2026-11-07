using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class AdditionalBookMetadataProxyFixture
    {
        private sealed class CachedResponseStub : ICachedHttpResponseService
        {
            private readonly Func<HttpRequest, string> _handler;
            public List<bool> PersistentCacheUses { get; } = new();

            public CachedResponseStub(Func<HttpRequest, string> handler)
            {
                _handler = handler;
            }

            public HttpResponse Get(HttpRequest request, bool useCache, TimeSpan ttl)
            {
                PersistentCacheUses.Add(useCache);
                return Json(request);
            }

            public HttpResponse Get(HttpRequest request, bool useCache, TimeSpan ttl, Func<HttpResponse, bool> isPositive) => Get(request, useCache, ttl);

            public HttpResponse<T> Get<T>(HttpRequest request, bool useCache, TimeSpan ttl) where T : new()
            {
                PersistentCacheUses.Add(useCache);
                return new HttpResponse<T>(Json(request));
            }

            public HttpResponse<T> Get<T>(HttpRequest request, bool useCache, TimeSpan ttl, Func<HttpResponse, bool> isPositive) where T : new() => Get<T>(request, useCache, ttl);

            private HttpResponse Json(HttpRequest request) => new(
                request,
                new HttpHeader { ContentType = "application/json" },
                _handler(request),
                HttpStatusCode.OK);
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "IsDefined")
                {
                    return false;
                }

                if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    var propertyName = targetMethod.Name.Substring("get_".Length);
                    if (Values.TryGetValue(propertyName, out var value))
                    {
                        return value;
                    }

                    return targetMethod.ReturnType.IsValueType
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null;
                }

                throw new NotImplementedException($"Test config does not implement {targetMethod.Name}");
            }
        }

        private string _previousEuropeanaKey;
        private string _previousOpenLibraryEmail;
        private string _previousChaptarrSources;
        private string _previousBookshelfSources;

        [SetUp]
        public void SetUp()
        {
            _previousEuropeanaKey = Environment.GetEnvironmentVariable("EUROPEANA_API_KEY");
            _previousOpenLibraryEmail = Environment.GetEnvironmentVariable("OPEN_LIBRARY_CONTACT_EMAIL");
            _previousChaptarrSources = Environment.GetEnvironmentVariable("CHAPTARR_METADATA_SOURCES");
            _previousBookshelfSources = Environment.GetEnvironmentVariable("BOOKSHELF_METADATA_SOURCES");
            Environment.SetEnvironmentVariable("EUROPEANA_API_KEY", null);
            Environment.SetEnvironmentVariable("OPEN_LIBRARY_CONTACT_EMAIL", null);
            Environment.SetEnvironmentVariable("CHAPTARR_METADATA_SOURCES", null);
            Environment.SetEnvironmentVariable("BOOKSHELF_METADATA_SOURCES", null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("EUROPEANA_API_KEY", _previousEuropeanaKey);
            Environment.SetEnvironmentVariable("OPEN_LIBRARY_CONTACT_EMAIL", _previousOpenLibraryEmail);
            Environment.SetEnvironmentVariable("CHAPTARR_METADATA_SOURCES", _previousChaptarrSources);
            Environment.SetEnvironmentVariable("BOOKSHELF_METADATA_SOURCES", _previousBookshelfSources);
        }

        [Test]
        public void book_lookup_tuple_should_return_the_author_provider_id()
        {
            var config = CreateConfig();
            ((ConfigServiceProxy)config).Values["EuropeanaApiKey"] = "test-europeana-key";
            var cachedResponse = new CachedResponseStub(_ => EuropeanaDetailResponse());
            var proxy = CreateProxy(config, cachedResponse);
            var recordId = Base64Url("/202/record-1");

            var result = proxy.GetBook("europeana:" + recordId);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2.AdditionalMetadataProviderId, Does.StartWith("meta:"));
                Assert.That(result.Item1, Is.EqualTo(result.Item2.Author.AdditionalMetadataProviderId));
                Assert.That(result.Item1, Is.Not.EqualTo(result.Item2.AdditionalMetadataProviderId));
                Assert.That(AdditionalMetadataSources.TryParseProviderId(result.Item1, out var provider, out _), Is.True);
                Assert.That(provider, Is.EqualTo("europeana-author"));
                Assert.That(cachedResponse.PersistentCacheUses, Is.EqualTo(new[] { false }));
            });
        }

        [Test]
        public void open_library_edition_lookup_should_return_author_id_and_select_the_requested_edition()
        {
            var sourceId = AdditionalMetadataSources.CreateProviderId(AdditionalMetadataSources.OpenLibrary, "OL9999M");
            var proxy = CreateProxy(CreateConfig(), OpenLibraryResponse);

            var result = proxy.GetBook(sourceId);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item1, Is.EqualTo(result.Item2.Author.AdditionalMetadataProviderId));
                Assert.That(result.Item1, Is.Not.EqualTo(result.Item2.AdditionalMetadataProviderId));
                Assert.That(result.Item2.Editions, Has.Count.EqualTo(2));
                Assert.That(result.Item2.Editions.Single(x => x.OpenLibraryEditionId == "OL9999M").Monitored, Is.True);
                Assert.That(result.Item2.Editions.Single(x => x.OpenLibraryEditionId == "OL7394331M").Monitored, Is.False);
            });
        }

        [Test]
        public void provider_search_should_not_reuse_mutable_books_between_requests()
        {
            var config = CreateConfig();
            ((ConfigServiceProxy)config).Values["GoogleBooksApiKey"] = "fixture-key";
            var cachedResponse = new CachedResponseStub(_ => GoogleBooksSearchResponse());
            var proxy = CreateProxy(config, cachedResponse);

            var first = proxy.SearchProvider("fixture query", "googlebooks").Single();
            first.Title = "Caller mutation";
            first.Editions[0].Overview = "Caller mutation";

            var second = proxy.SearchProvider("fixture query", "googlebooks").Single();

            Assert.Multiple(() =>
            {
                Assert.That(second, Is.Not.SameAs(first));
                Assert.That(second.Title, Is.EqualTo("A Fixture Book"));
                Assert.That(second.Editions[0].Overview, Is.EqualTo("A Fixture description."));
                Assert.That(cachedResponse.PersistentCacheUses, Is.EqualTo(new[] { false }));
            });
        }

        [Test]
        public void metadata_publisher_preference_should_require_an_exact_isbn_match()
        {
            var config = CreateConfig();
            var configProxy = (ConfigServiceProxy)config;
            configProxy.Values["GoogleBooksApiKey"] = "fixture-key";
            configProxy.Values["MetadataPublisherSourcePreference"] = "googlebooks";
            var cachedResponse = new CachedResponseStub(_ => GoogleBooksResponse("9783161484100", "Supplemental Press"));
            var proxy = CreateProxy(config, cachedResponse);
            var target = CreateTargetBook("9783161484100");

            proxy.ApplyFieldSourcePreferences(target);

            Assert.That(target.Editions[0].Publisher, Is.EqualTo("Supplemental Press"));
        }

        [Test]
        public void metadata_publisher_preference_should_ignore_a_different_isbn()
        {
            var config = CreateConfig();
            var configProxy = (ConfigServiceProxy)config;
            configProxy.Values["GoogleBooksApiKey"] = "fixture-key";
            configProxy.Values["MetadataPublisherSourcePreference"] = "googlebooks";
            var cachedResponse = new CachedResponseStub(_ => GoogleBooksResponse("9781850812333", "Wrong Edition Press"));
            var proxy = CreateProxy(config, cachedResponse);
            var target = CreateTargetBook("9783161484100");

            proxy.ApplyFieldSourcePreferences(target);

            Assert.That(target.Editions[0].Publisher, Is.EqualTo("Original Press"));
        }

        [Test]
        public void open_library_search_hints_should_not_reuse_mutable_author_or_metadata_objects()
        {
            var cachedResponse = new CachedResponseStub(OpenLibrarySearchAndDetailResponse);
            var proxy = new OpenLibraryMetadataProxy(
                cachedResponse,
                new CacheManager(),
                CreateConfig(),
                LogManager.GetCurrentClassLogger());

            var searchResult = proxy.Search("fixture query").Single();
            searchResult.Author.Name = "Caller mutation";
            searchResult.Genres.Clear();

            var result = proxy.GetBook("openlibrary:OL82586W");

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2.Author.Name, Is.EqualTo("A Fixture Author"));
                Assert.That(result.Item2.Genres, Does.Contain("Fantasy"));
                Assert.That(result.Item2.Author.AdditionalMetadataProviderId, Does.StartWith("meta:"));
            });
        }

        private static AdditionalBookMetadataProxy CreateProxy(IConfigService config, Func<HttpRequest, string> response)
        {
            return CreateProxy(config, new CachedResponseStub(response));
        }

        private static AdditionalBookMetadataProxy CreateProxy(IConfigService config, CachedResponseStub cachedResponse)
        {
            return new AdditionalBookMetadataProxy(
                null,
                cachedResponse,
                new CacheManager(),
                config,
                LogManager.GetCurrentClassLogger());
        }

        private static IConfigService CreateConfig() =>
            DispatchProxy.Create<IConfigService, ConfigServiceProxy>();

        private static Book CreateTargetBook(string isbn) => new()
        {
            Title = "A Fixture Book",
            Author = new Author { Name = "A Fixture Author" },
            Editions = new List<Edition>
            {
                new()
                {
                    Isbn13 = isbn,
                    Publisher = "Original Press",
                    Title = "A Fixture Book"
                }
            }
        };

        private static string Base64Url(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string EuropeanaDetailResponse() => new JObject
        {
            ["object"] = new JObject
            {
                ["about"] = "/202/record-1",
                ["type"] = "TEXT",
                ["title"] = new JArray("A Fixture Book"),
                ["proxies"] = new JArray
                {
                    new JObject
                    {
                        ["europeanaProxy"] = false,
                        ["dcCreator"] = new JArray("A Fixture Author"),
                        ["dcIdentifier"] = new JArray("urn:isbn:978-3-16-148410-0")
                    }
                },
                ["aggregations"] = new JArray
                {
                    new JObject { ["edmPreview"] = "https://api.europeana.eu/thumbnail/record-1.jpg" }
                }
            }
        }.ToString();

        private static string GoogleBooksSearchResponse() => new JObject
        {
            ["items"] = new JArray
            {
                new JObject
                {
                    ["id"] = "fixture-volume",
                    ["volumeInfo"] = new JObject
                    {
                        ["title"] = "A Fixture Book",
                        ["authors"] = new JArray("A Fixture Author"),
                        ["description"] = "A Fixture description.",
                        ["industryIdentifiers"] = new JArray
                        {
                            new JObject { ["type"] = "ISBN_13", ["identifier"] = "9783161484100" }
                        }
                    }
                }
            }
        }.ToString();

        private static string GoogleBooksResponse(string isbn, string publisher)
        {
            var volume = new JObject
            {
                ["id"] = "fixture-volume",
                ["volumeInfo"] = new JObject
                {
                    ["title"] = "A Fixture Book",
                    ["authors"] = new JArray("A Fixture Author"),
                    ["publisher"] = publisher,
                    ["description"] = "A Fixture description.",
                    ["industryIdentifiers"] = new JArray
                    {
                        new JObject { ["type"] = "ISBN_13", ["identifier"] = isbn }
                    }
                }
            };
            var response = new JObject
            {
                ["items"] = new JArray(volume.DeepClone()),
                ["volumeInfo"] = volume["volumeInfo"].DeepClone()
            };
            return response.ToString();
        }

        private static string OpenLibrarySearchAndDetailResponse(HttpRequest request)
        {
            if (request.Url.Path == "/search.json")
            {
                return new JObject
                {
                    ["docs"] = new JArray
                    {
                        new JObject
                        {
                            ["key"] = "/works/OL82586W",
                            ["title"] = "A Fixture Book",
                            ["author_name"] = new JArray("A Fixture Author"),
                            ["author_key"] = new JArray("OL1A"),
                            ["first_publish_year"] = 1999,
                            ["subject"] = new JArray("Fantasy"),
                            ["editions"] = new JArray
                            {
                                new JObject
                                {
                                    ["key"] = "/books/OL7394331M",
                                    ["title"] = "A Fixture Book",
                                    ["isbn_13"] = new JArray("9781850812333")
                                }
                            }
                        }
                    }
                }.ToString();
            }

            if (request.Url.Path == "/works/OL82586W.json")
            {
                return new JObject
                {
                    ["title"] = "A Fixture Book",
                    ["description"] = "A fixture description."
                }.ToString();
            }

            if (request.Url.Path == "/works/OL82586W/editions.json")
            {
                return new JObject
                {
                    ["entries"] = new JArray
                    {
                        new JObject { ["key"] = "/books/OL7394331M", ["title"] = "A Fixture Book" }
                    }
                }.ToString();
            }

            if (request.Url.Path == "/authors/OL1A.json")
            {
                return new JObject { ["name"] = "A Fixture Author" }.ToString();
            }

            if (request.Url.Path == "/authors/OL1A/works.json")
            {
                return new JObject { ["entries"] = new JArray() }.ToString();
            }

            throw new AssertionException($"Unexpected Open Library request: {request.Url}");
        }

        private static string OpenLibraryResponse(HttpRequest request)
        {
            var path = request.Url.Path;
            if (path == "/works/OL82586W.json")
            {
                return new JObject
                {
                    ["title"] = "A Fixture Book",
                    ["authors"] = new JArray
                    {
                        new JObject { ["author"] = new JObject { ["key"] = "/authors/OL1A" } }
                    },
                    ["description"] = new JObject { ["value"] = "A fixture description." },
                    ["subjects"] = new JArray("Fantasy"),
                    ["covers"] = new JArray(98765)
                }.ToString();
            }

            if (path == "/works/OL82586W/editions.json")
            {
                return new JObject
                {
                    ["entries"] = new JArray
                    {
                        new JObject
                        {
                            ["key"] = "/books/OL7394331M",
                            ["title"] = "A Fixture Book",
                            ["isbn_13"] = new JArray("9781850812333"),
                            ["publishers"] = new JArray("Fixture Press"),
                            ["covers"] = new JArray(98765)
                        }
                    }
                }.ToString();
            }

            if (path == "/books/OL9999M.json")
            {
                return new JObject
                {
                    ["key"] = "/books/OL9999M",
                    ["works"] = new JArray(new JObject { ["key"] = "/works/OL82586W" }),
                    ["title"] = "Requested Fixture Edition",
                    ["isbn_13"] = new JArray("9783161484100"),
                    ["publishers"] = new JArray("Requested Press")
                }.ToString();
            }

            if (path == "/authors/OL1A.json")
            {
                return new JObject { ["name"] = "A Fixture Author" }.ToString();
            }

            if (path == "/authors/OL1A/works.json")
            {
                return new JObject { ["entries"] = new JArray() }.ToString();
            }

            throw new AssertionException($"Unexpected Open Library request: {request.Url}");
        }
    }
}
