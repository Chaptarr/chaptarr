using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.MetadataSource.Hardcover;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyAuthorSearchSummaryFixture
    {
        private sealed class SummaryHttpClient : IHttpClient
        {
            public string BodyOverride { get; set; }

            public Func<HttpRequest, HttpResponse> ReplayResponse { get; set; }

            public List<HttpRequest> Requests { get; } = new List<HttpRequest>();

            public HashSet<string> MissingAuthorIds { get; } = new HashSet<string>();

            public HashSet<string> TimedOutAuthorIds { get; } = new HashSet<string>();

            public HttpResponse Get(HttpRequest request)
            {
                Requests.Add(request);

                if (ReplayResponse != null)
                {
                    return ReplayResponse(request);
                }

                if (TimedOutAuthorIds.Any(id => request.Url.FullUri.Contains(Uri.EscapeDataString(id))))
                {
                    throw new TimeoutException("summary request timed out");
                }

                if (MissingAuthorIds.Any(id => request.Url.FullUri.Contains(Uri.EscapeDataString(id))))
                {
                    return new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.NotFound);
                }

                const string body = @"{
                  ""name"": ""Canonical Author"",
                  ""description"": ""Golden description"",
                  ""birthDate"": ""1950-01-02"",
                  ""deathDate"": ""2020-03-04"",
                  ""bookCount"": 42,
                  ""providerUrls"": {
                    ""hardcover"": ""https://hardcover.app/authors/canonical-author"",
                    ""goodreads"": ""https://www.goodreads.com/author/show/123.Canonical_Author"",
                    ""invalid"": ""javascript:alert(1)""
                  },
                  ""provider_ids_all"": {
                    ""gr"": [""gr:123"", ""gr:456""],
                    ""hc"": [""hc:123""]
                  },
                  ""photos"": [
                    { ""url"": ""https://images.example/goodreads-primary.jpg"", ""provider"": ""goodreads"", ""isPrimary"": true },
                    { ""url"": ""https://images.example/hardcover.jpg"", ""provider"": ""hardcover"", ""isPrimary"": false },
                    { ""url"": ""https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/nophoto/user/u_700x933.png"", ""provider"": ""goodreads"", ""isPrimary"": false }
                  ]
                }";
                return new HttpResponse(request, new HttpHeader(), BodyOverride ?? body, HttpStatusCode.OK);
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse Execute(HttpRequest request) => Get(request);
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        private sealed class GoodreadsSearchClient : IGoodreadsSearchProxy
        {
            public List<SearchJsonResource> Search(string query)
            {
                return new List<SearchJsonResource>
                {
                    new SearchJsonResource
                    {
                        BookId = 1,
                        WorkId = 2,
                        Title = "The Old Man and the Sea",
                        Author = new AuthorJsonResource { Id = 70940350, Name = "Ernest Hemingway" }
                    }
                };
            }
        }

        private sealed class HardcoverSearchClient : IHardcoverSearchClient
        {
            private readonly int _authorCount;

            public HardcoverSearchClient(int authorCount = 1)
            {
                _authorCount = authorCount;
            }

            public List<object> Search(string query)
            {
                return Enumerable.Range(1, _authorCount)
                    .Select(index => (object)new HardcoverAuthorResult
                    {
                        Id = (122 + index).ToString(),
                        Name = index == 1 ? "Provider Candidate" : $"Provider Candidate {index}",
                        Bio = "Provider description",
                        ImageUrl = $"https://images.example/exact-search-candidate-{index}.jpg",
                        Slug = $"provider-candidate-{index}",
                        AlternateNames = Array.Empty<string>()
                    })
                    .ToList();
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_HardcoverEnabled" => true,
                    "get_HardcoverApiToken" => "test-token",
                    "get_MetadataServerUrl" => "https://metadata.test",
                    _ => throw new NotImplementedException($"Config proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        [TestCase("https://www.goodreads.com/author/show/1455.Ernest_Hemingway")]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("javascript:alert(1)")]
        public void goodreads_search_should_prefer_valid_summary_url_without_changing_lookup_identity(string summaryUrl)
        {
            const string fallback = "https://www.goodreads.com/author/show/70940350.Ernest_Hemingway";
            var httpClient = new SummaryHttpClient
            {
                BodyOverride = JsonConvert.SerializeObject(new
                {
                    name = "Ernest Hemingway",
                    providerUrls = new Dictionary<string, string> { ["goodreads"] = summaryUrl }
                })
            };
            var results = CreateGoodreadsProxy(httpClient).SearchForNewEntity("Hemingway", "goodreads");
            var author = results.OfType<Author>().Single();
            var expectedUrl = summaryUrl?.StartsWith("https://", StringComparison.Ordinal) == true ? summaryUrl : fallback;

            Assert.That(author.Links.Select(link => (link.Name, link.Url)),
                Is.EqualTo(new[] { ("goodreads", expectedUrl) }));
            Assert.That(author.GoodreadsAuthorId, Is.EqualTo("gr:70940350"));
            Assert.That(httpClient.Requests.Single().Url.FullUri, Does.Contain("/api/v5/author/summary/gr%3A70940350"));

            var ensureLink = typeof(BookInfoProxy).GetMethod("EnsureGoodreadsAuthorLink", BindingFlags.NonPublic | BindingFlags.Static);
            ensureLink.Invoke(null, new object[] { author });
            Assert.That(author.Links.Select(link => (link.Name, link.Url)),
                Is.EqualTo(new[] { ("goodreads", expectedUrl) }), "fallback must preserve an existing valid provider URL");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void goodreads_search_should_keep_generated_link_when_summary_is_unavailable(bool timeout)
        {
            var httpClient = new SummaryHttpClient();
            (timeout ? httpClient.TimedOutAuthorIds : httpClient.MissingAuthorIds).Add("gr:70940350");

            var author = CreateGoodreadsProxy(httpClient).SearchForNewEntity("Hemingway", "goodreads").OfType<Author>().Single();

            Assert.That(author.GoodreadsAuthorId, Is.EqualTo("gr:70940350"));
            Assert.That(author.Links.Select(link => (link.Name, link.Url)), Is.EqualTo(new[]
            {
                ("goodreads", "https://www.goodreads.com/author/show/70940350.Ernest_Hemingway")
            }));
        }

        private static BookInfoProxy CreateGoodreadsProxy(SummaryHttpClient httpClient)
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var logger = LogManager.GetCurrentClassLogger();
            return new BookInfoProxy(httpClient, null, new GoodreadsSearchClient(), null, null, null, null, null,
                configService, new MetadataRequestBuilder(configService), logger, new CacheManager(),
                new MetadataServerHealthGate(configService, new MetadataServerHealthService(logger), logger));
        }

        [Test]
        public void hardcover_author_search_should_use_slim_golden_summary_and_preserve_exact_candidate_photo_first()
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var httpClient = new SummaryHttpClient();
            var logger = LogManager.GetCurrentClassLogger();
            var proxy = new BookInfoProxy(
                httpClient,
                null,
                null,
                new HardcoverSearchClient(),
                null,
                null,
                null,
                null,
                configService,
                new MetadataRequestBuilder(configService),
                logger,
                new CacheManager(),
                new MetadataServerHealthGate(configService, new MetadataServerHealthService(logger), logger));

            var author = proxy.SearchForNewEntity("Provider Candidate", "hardcover").Single() as Author;

            Assert.That(author, Is.Not.Null);
            Assert.That(author.Name, Is.EqualTo("Canonical Author"));
            Assert.That(author.Overview, Is.EqualTo("Golden description"));
            Assert.That(author.MetadataBookCount, Is.EqualTo(42));
            Assert.That(author.Status, Is.EqualTo(AuthorStatusType.Ended));
            Assert.That(author.Images.Select(image => image.Url), Is.EqualTo(new[]
            {
                "https://images.example/exact-search-candidate-1.jpg",
                "https://images.example/hardcover.jpg",
                "https://images.example/goodreads-primary.jpg"
            }));
            Assert.That(author.Links.Select(link => (link.Name, link.Url)), Is.EqualTo(new[]
            {
                ("hardcover", "https://hardcover.app/authors/canonical-author"),
                ("goodreads", "https://www.goodreads.com/author/show/123.Canonical_Author")
            }));
            Assert.That(author.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:123", "gr:456", "hc:123" }));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
            Assert.That(httpClient.Requests[0].Url.FullUri, Does.Contain("/api/v5/author/summary/hc%3A123"));
            Assert.That(httpClient.Requests[0].Url.FullUri, Does.Not.Contain("/api/v5/author?id="));
            Assert.That(httpClient.Requests[0].RequestTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void summary_failure_should_not_stop_later_candidates_or_open_the_shared_circuit()
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var httpClient = new SummaryHttpClient();
            httpClient.TimedOutAuthorIds.Add("hc:124");
            var logger = LogManager.GetCurrentClassLogger();
            var healthService = new MetadataServerHealthService(logger);
            var healthGate = new MetadataServerHealthGate(configService, healthService, logger);
            var proxy = new BookInfoProxy(
                httpClient,
                null,
                null,
                new HardcoverSearchClient(3),
                null,
                null,
                null,
                null,
                configService,
                new MetadataRequestBuilder(configService),
                logger,
                new CacheManager(),
                healthGate);

            var authors = proxy.SearchForNewEntity("Provider Candidate", "hardcover").OfType<Author>().ToList();

            Assert.That(httpClient.Requests, Has.Count.EqualTo(3));
            Assert.That(httpClient.Requests.Last().RequestTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(authors, Has.Count.EqualTo(3));
            Assert.That(authors[0].Name, Is.EqualTo("Canonical Author"));
            Assert.That(authors[1].Name, Is.EqualTo("Provider Candidate 2"));
            Assert.That(authors[2].Name, Is.EqualTo("Canonical Author"));
            Assert.That(healthService.GetStatus(healthGate.SourceName).IsHealthy, Is.True);
            Assert.That(healthService.GetStatus(healthGate.SourceName).ConsecutiveFailures, Is.Zero);
        }

        [Test]
        public void open_circuit_should_skip_optional_summary_without_claiming_the_recovery_probe()
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var httpClient = new SummaryHttpClient();
            var logger = LogManager.GetCurrentClassLogger();
            var healthService = new MetadataServerHealthService(logger);
            var healthGate = new MetadataServerHealthGate(configService, healthService, logger);
            healthService.ReportFailure(healthGate.SourceName, new TimeoutException("metadata server unavailable"));
            var status = healthService.GetStatus(healthGate.SourceName);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            var proxy = new BookInfoProxy(
                httpClient,
                null,
                null,
                new HardcoverSearchClient(),
                null,
                null,
                null,
                null,
                configService,
                new MetadataRequestBuilder(configService),
                logger,
                new CacheManager(),
                healthGate);

            var author = proxy.SearchForNewEntity("Provider Candidate", "hardcover").Single() as Author;

            Assert.That(author, Is.Not.Null);
            Assert.That(author.Name, Is.EqualTo("Provider Candidate"));
            Assert.That(httpClient.Requests, Is.Empty);
            Assert.That(status.ProbeInProgress, Is.False);
            Assert.That(healthGate.TryBeginRequest(out _), Is.True,
                "load-bearing metadata work must retain the recovery probe");
        }

        [Test]
        public void audible_search_should_recognize_audnexus_photos_as_provider_photos()
        {
            var method = typeof(BookInfoProxy).GetMethod("IsPhotoFromSearchProvider", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { "audnexus", "audible" }), Is.True);
            Assert.That(method.Invoke(null, new object[] { "hardcover", "audible" }), Is.False);
        }

        [TestCase("goodreads", false, "gr:113603", "gr:874602", false)]
        [TestCase("hardcover", false, "hc:427401", "hc:213830", false)]
        [TestCase("hardcover", true, null, "hc:213830", false)]
        [TestCase("hardcover", true, null, "hc:213830", true)]
        [TestCase("audible", false, "az:B002VA3CDO", "az:B0GZZ57KFW", false)]
        public void captured_provider_search_should_preserve_author_name_and_id_when_enrichment_is_unavailable(
            string provider, bool authorQuery, string bookId, string authorId, bool primaryUnavailable)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var config = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            string Capture(string name) => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
                "Fixtures", "ProviderSearch", name));
            var http = new SummaryHttpClient
            {
                ReplayResponse = request =>
                {
                    var url = request.Url.FullUri;
                    string body;
                    var status = HttpStatusCode.OK;
                    if (url.StartsWith("https://www.goodreads.com/book/auto_complete", StringComparison.Ordinal))
                    {
                        var parameters = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
                        Assert.That(parameters["q"], Is.EqualTo("a wizard of earthsea"));
                        Assert.That(parameters["format"], Is.EqualTo("json"));
                        body = Capture("upstream-goodreads-normalized.json");
                    }
                    else if (url.StartsWith("https://api.audible.com/1.0/catalog/products?", StringComparison.Ordinal))
                    {
                        var parameters = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
                        Assert.That(parameters["keywords"], Is.EqualTo("A Wizard of Earthsea"));
                        Assert.That(parameters["num_results"], Is.EqualTo("20"));
                        Assert.That(parameters["products_sort_by"], Is.EqualTo("Relevance"));
                        Assert.That(parameters["response_groups"], Is.EqualTo("product_desc,contributors,media,product_attrs"));
                        body = Capture("upstream-audible.json");
                    }
                    else if (url == "https://api.hardcover.app/v1/graphql")
                    {
                        using var payload = JsonDocument.Parse(request.ContentData);
                        var variables = payload.RootElement.GetProperty("variables");
                        if (variables.TryGetProperty("query_type", out var queryType))
                        {
                            using var capturedRequest = JsonDocument.Parse(Capture(authorQuery
                                ? "upstream-hardcover-author-request.json" : "upstream-hardcover-book-request.json"));
                            var expected = capturedRequest.RootElement;
                            Assert.That(payload.RootElement.GetProperty("query").GetString().ReplaceLineEndings("\n"), Is.EqualTo(expected.GetProperty("query").GetString().ReplaceLineEndings("\n")));
                            foreach (var field in new[] { "q", "page", "limit" })
                                Assert.That(variables.GetProperty(field).ToString(), Is.EqualTo(expected.GetProperty("variables").GetProperty(field).ToString()));
                            body = queryType.GetString() switch
                            {
                                "Author" when authorQuery => Capture("upstream-hardcover-author.json"),
                                "Book" when !authorQuery => Capture("upstream-hardcover-book.json"),
                                _ => "{\"data\":{\"search\":{\"results\":{\"hits\":[]}}}}"
                            };
                        }
                        else if (payload.RootElement.GetProperty("query").GetString().Contains("SearchEnrichment", StringComparison.Ordinal))
                        {
                            body = "{\"data\":{\"authors\":[],\"series\":[]}}";
                        }
                        else
                        {
                            var query = payload.RootElement.GetProperty("query").GetString();
                            var batch = Enumerable.Range(0, 2).Single(index =>
                            {
                                using var capturedRequest = JsonDocument.Parse(Capture($"hardcover-primary-books-request-{index}.json"));
                                return capturedRequest.RootElement.GetProperty("query").GetString().ReplaceLineEndings("\n") == query.ReplaceLineEndings("\n");
                            });
                            using var capturedPrimaryRequest = JsonDocument.Parse(Capture($"hardcover-primary-books-request-{batch}.json"));
                            Assert.That(variables.GetProperty("limit").GetInt32(),
                                Is.EqualTo(capturedPrimaryRequest.RootElement.GetProperty("variables").GetProperty("limit").GetInt32()));
                            status = primaryUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
                            body = primaryUnavailable ? "{}" : Capture($"upstream-hardcover-primary-{batch}.json");
                        }
                    }
                    else if (url.StartsWith("https://metadata.test/api/v5/author/summary/", StringComparison.Ordinal))
                    {
                        status = HttpStatusCode.NotFound;
                        body = "{}";
                    }
                    else
                    {
                        throw new InvalidOperationException("Unexpected replay request: " + url);
                    }

                    return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, body, status);
                }
            };
            var cacheRows = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Http.ICachedHttpResponseRepository>((method, args) => null);
            var cache = new NzbDrone.Core.Http.CachedHttpResponseService(cacheRows, http, logger);
            var proxy = new BookInfoProxy(http, null, new NzbDrone.Core.MetadataSource.Goodreads.GoodreadsSearchProxy(cache, logger),
                new NzbDrone.Core.MetadataSource.Hardcover.HardcoverSearchClient(http, config),
                new NzbDrone.Core.MetadataSource.Audible.AudibleCatalogProxy(cache, logger), null, null, null,
                config, new MetadataRequestBuilder(config), logger, new CacheManager(),
                new MetadataServerHealthGate(config, new MetadataServerHealthService(logger), logger));
            var controller = new Chaptarr.Api.V1.Search.SearchController(proxy, null,
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.MediaCover.IMapCoversToLocal>((method, args) => null),
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.MediaCover.IMediaCoverProxy>((method, args) => args[0]),
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IAuthorService>((method, args) => null), null);
            var results = controller.Search(authorQuery ? "Ursula Le Guin" : "A Wizard of Earthsea", provider).Value;
            var author = authorQuery
                ? results.Single(row => row.Author?.ForeignAuthorId == authorId).Author
                : results.Single(row => row.Book?.ForeignBookId == bookId).Book.Author;

            Assert.Multiple(() =>
            {
                Assert.That(author.AuthorName, Is.EqualTo("Ursula K. Le Guin"));
                Assert.That(author.ForeignAuthorId, Is.EqualTo(authorId));
                Assert.That(author.AuthorName, Is.Not.EqualTo("Pending Import"));
                Assert.That(http.Requests, Is.Not.Empty);
                Assert.That(http.Requests.Any(request => request.Url.FullUri.Contains("/api/v5/search", StringComparison.Ordinal)), Is.False);
            });
            if (provider == "audible")
            {
                var missingId = results.Single(row => row.Book?.ForeignBookId == "az:B09G7B8XXY").Book.Author;
                Assert.That(missingId.AuthorName, Is.EqualTo("Ursula K. Le Guin"));
                Assert.That(missingId.ForeignAuthorId, Is.Null.Or.Empty);
            }

            if (provider == "goodreads")
            {
                var differentAuthor = results.Single(row => row.Book?.ForeignBookId == "gr:220778298").Book.Author;
                Assert.That(differentAuthor.AuthorName, Is.EqualTo("Fred Fordham"));
                Assert.That(differentAuthor.ForeignAuthorId, Is.EqualTo("gr:15502566"));
            }

            if (authorQuery)
            {
                var primaryRequests = http.Requests.Where(request => request.ContentData?.Length > 0 &&
                    System.Text.Encoding.UTF8.GetString(request.ContentData).Contains("BooksByAuthors", StringComparison.Ordinal)).ToList();
                Assert.That(primaryRequests, Has.Count.EqualTo(2));
                Assert.That(results.Single(row => row.Author?.ForeignAuthorId == authorId).ProviderId, Is.EqualTo(authorId));
            }

            if (provider == "hardcover")
            {
                var searchTypes = http.Requests.Where(request => request.ContentData?.Length > 0).Select(request =>
                {
                    using var payload = JsonDocument.Parse(request.ContentData);
                    return payload.RootElement.GetProperty("variables").TryGetProperty("query_type", out var type) ? type.GetString() : null;
                }).Where(type => type != null);
                Assert.That(searchTypes, Is.EqualTo(new[] { "Author", "Book", "Series" }));
            }
        }
    }
}
