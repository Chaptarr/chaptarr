using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
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

            public List<HttpRequest> Requests { get; } = new List<HttpRequest>();

            public HashSet<string> MissingAuthorIds { get; } = new HashSet<string>();

            public HashSet<string> TimedOutAuthorIds { get; } = new HashSet<string>();

            public HttpResponse Get(HttpRequest request)
            {
                Requests.Add(request);

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
            public HttpResponse Execute(HttpRequest request) => throw new NotImplementedException();
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
    }
}
