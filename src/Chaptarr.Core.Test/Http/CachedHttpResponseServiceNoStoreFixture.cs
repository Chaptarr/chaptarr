using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource.Audible;
using NzbDrone.Core.MetadataSource.Goodreads;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class CachedHttpResponseServiceNoStoreFixture
    {
        private class RepositoryProxy : DispatchProxy
        {
            public CachedHttpResponse Cached { get; private set; }
            public int UpsertCount { get; private set; }
            public int DeleteCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "FindByUrl" => Cached,
                    "Upsert" => RecordUpsert((CachedHttpResponse)args[0]),
                    "Delete" => RecordDelete((CachedHttpResponse)args[0]),
                    _ => throw new NotImplementedException($"Repository proxy does not implement {targetMethod?.Name}")
                };
            }

            private CachedHttpResponse RecordUpsert(CachedHttpResponse response)
            {
                UpsertCount++;
                Cached = response;
                return response;
            }

            private object RecordDelete(CachedHttpResponse response)
            {
                DeleteCount++;
                if (ReferenceEquals(Cached, response))
                {
                    Cached = null;
                }

                return null;
            }

            public void Seed(CachedHttpResponse response)
            {
                Cached = response;
            }
        }

        private sealed class RecordingHttpClient : IHttpClient
        {
            private readonly Func<HttpRequest, HttpResponse> _get;

            public RecordingHttpClient(Func<HttpRequest, HttpResponse> get)
            {
                _get = get;
            }

            public int GetCount { get; private set; }

            public HttpResponse Get(HttpRequest request)
            {
                GetCount++;
                return _get(request);
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

        [Test]
        public void should_not_store_no_store_response_or_overwrite_origin_bypass()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
            {
                var headers = new HttpHeader();
                headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                headers["X-Cache-Status"] = "BYPASS";
                return new HttpResponse(request, headers, "null", HttpStatusCode.Accepted);
            });
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var request = new HttpRequest("https://metadata.test/api/v5/author?id=gr%3A123");

            var first = service.Get(request, true, TimeSpan.FromMinutes(30));
            var second = service.Get(request, true, TimeSpan.FromMinutes(30));

            Assert.That(first.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("BYPASS"));
            Assert.That(second.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("BYPASS"));
            Assert.That(repositoryState.UpsertCount, Is.Zero);
            Assert.That(client.GetCount, Is.EqualTo(2));
        }

        [Test]
        public void should_not_store_origin_bypass_without_cache_control()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
            {
                var headers = new HttpHeader();
                headers["X-Cache-Status"] = "bypass";
                return new HttpResponse(request, headers, "null", HttpStatusCode.Accepted);
            });
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var request = new HttpRequest("https://metadata.test/api/v5/author?id=gr%3A456");

            var first = service.Get(request, true, TimeSpan.FromMinutes(30));
            var second = service.Get(request, true, TimeSpan.FromMinutes(30));

            Assert.That(first.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("bypass"));
            Assert.That(second.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("bypass"));
            Assert.That(repositoryState.UpsertCount, Is.Zero);
            Assert.That(client.GetCount, Is.EqualTo(2));
        }

        [Test]
        public void should_delete_legacy_cached_accepted_response_and_fetch_fresh_positive()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            repositoryState.Seed(new CachedHttpResponse
            {
                Url = "https://metadata.test/api/v5/author?id=gr%3A789",
                Value = "null",
                StatusCode = (int)HttpStatusCode.Accepted,
                Expiry = DateTime.UtcNow.AddMinutes(30)
            });
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "{\"id\":789}", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var request = new HttpRequest("https://metadata.test/api/v5/author?id=gr%3A789");

            var response = service.Get(request, true, TimeSpan.FromMinutes(30));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(repositoryState.DeleteCount, Is.EqualTo(1));
            Assert.That(repositoryState.UpsertCount, Is.EqualTo(1));
            Assert.That(client.GetCount, Is.EqualTo(1));
        }

        [Test]
        public void should_continue_to_store_and_reuse_positive_responses()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "{\"ok\":true}", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var request = new HttpRequest("https://metadata.test/positive");

            var first = service.Get(request, true, TimeSpan.FromMinutes(30));
            var second = service.Get(request, true, TimeSpan.FromMinutes(30));

            Assert.That(first.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("MISS"));
            Assert.That(second.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("HIT"));
            Assert.That(repositoryState.UpsertCount, Is.EqualTo(1));
            Assert.That(client.GetCount, Is.EqualTo(1));
        }

        [TestCase("")]

        [TestCase("   ")]

        [TestCase("null")]

        [TestCase("[]")]

        [TestCase("{}")]

        [TestCase("{\"products\":[]}")]

        [TestCase("{\"product\":null}")]

        [TestCase("{\"results\":[]}")]

        [TestCase("{\"matches\":[]}")]
        public void should_not_store_semantic_empty_200_when_endpoint_predicate_rejects_it(string payload)
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), payload, HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var request = new HttpRequest("https://metadata.test/semantic-empty");

            var first = service.Get(request, true, TimeSpan.FromMinutes(30), _ => false);
            var second = service.Get(request, true, TimeSpan.FromMinutes(30), _ => false);

            Assert.That(first.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("BYPASS"));
            Assert.That(second.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("BYPASS"));
            Assert.That(repositoryState.UpsertCount, Is.Zero);
            Assert.That(client.GetCount, Is.EqualTo(2));
        }

        [Test]
        public void should_delete_legacy_semantic_empty_200_before_serving_it()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            repositoryState.Seed(new CachedHttpResponse
            {
                Url = "https://metadata.test/legacy-empty",
                Value = "{\"matches\":[]}",
                StatusCode = (int)HttpStatusCode.OK,
                Expiry = DateTime.UtcNow.AddMinutes(30)
            });
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "{\"matches\":[{\"id\":1}]}", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var request = new HttpRequest("https://metadata.test/legacy-empty");

            var response = service.Get(
                request,
                true,
                TimeSpan.FromMinutes(30),
                candidate => candidate.Content.Contains("\"id\":1", StringComparison.Ordinal));

            Assert.That(response.Content, Does.Contain("\"id\":1"));
            Assert.That(repositoryState.DeleteCount, Is.EqualTo(1));
            Assert.That(repositoryState.UpsertCount, Is.EqualTo(1));
            Assert.That(client.GetCount, Is.EqualTo(1));
        }

        [Test]
        public void should_not_store_positive_when_cache_is_disabled()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "{\"id\":1}", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var request = new HttpRequest("https://metadata.test/cache-disabled");

            var first = service.Get(request, false, TimeSpan.FromMinutes(30), _ => true);
            var second = service.Get(request, false, TimeSpan.FromMinutes(30), _ => true);

            Assert.That(first.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("BYPASS"));
            Assert.That(second.Headers.GetSingleValue("X-Cache-Status"), Is.EqualTo("BYPASS"));
            Assert.That(repositoryState.UpsertCount, Is.Zero);
            Assert.That(client.GetCount, Is.EqualTo(2));
        }
        [Test]
        public void goodreads_empty_array_should_be_requested_again()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "[]", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var proxy = new GoodreadsSearchProxy(service, LogManager.GetCurrentClassLogger());

            Assert.That(proxy.Search("missing"), Is.Empty);
            Assert.That(proxy.Search("missing"), Is.Empty);
            Assert.That(repositoryState.UpsertCount, Is.Zero);
            Assert.That(client.GetCount, Is.EqualTo(2));
        }

        [Test]
        public void audible_empty_products_should_be_requested_again()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "{\"products\":[]}", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var proxy = new AudibleCatalogProxy(service, LogManager.GetCurrentClassLogger());

            Assert.That(proxy.SearchBooks("missing"), Is.Empty);
            Assert.That(proxy.SearchBooks("missing"), Is.Empty);
            Assert.That(repositoryState.UpsertCount, Is.Zero);
            Assert.That(client.GetCount, Is.EqualTo(2));
        }

        [Test]
        public void audible_null_product_should_be_requested_again()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "{\"product\":null}", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var proxy = new AudibleCatalogProxy(service, LogManager.GetCurrentClassLogger());

            Assert.That(proxy.GetBookInfo("B000MISSING"), Is.Null);
            Assert.That(proxy.GetBookInfo("B000MISSING"), Is.Null);
            Assert.That(repositoryState.UpsertCount, Is.Zero);
            Assert.That(client.GetCount, Is.EqualTo(2));
        }
        [Test]
        public void goodreads_positive_array_should_remain_cached()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "[{\"bookId\":1,\"title\":\"Positive\"}]", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var proxy = new GoodreadsSearchProxy(service, LogManager.GetCurrentClassLogger());

            Assert.That(proxy.Search("positive"), Has.Count.EqualTo(1));
            Assert.That(proxy.Search("positive"), Has.Count.EqualTo(1));
            Assert.That(repositoryState.UpsertCount, Is.EqualTo(1));
            Assert.That(client.GetCount, Is.EqualTo(1));
        }

        [Test]
        public void audible_positive_products_should_remain_cached()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "{\"products\":[{\"asin\":\"A1\",\"title\":\"Positive\"}]}", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var proxy = new AudibleCatalogProxy(service, LogManager.GetCurrentClassLogger());

            Assert.That(proxy.SearchBooks("positive"), Has.Count.EqualTo(1));
            Assert.That(proxy.SearchBooks("positive"), Has.Count.EqualTo(1));
            Assert.That(repositoryState.UpsertCount, Is.EqualTo(1));
            Assert.That(client.GetCount, Is.EqualTo(1));
        }

        [Test]
        public void audible_positive_product_should_remain_cached()
        {
            var repository = DispatchProxy.Create<ICachedHttpResponseRepository, RepositoryProxy>();
            var repositoryState = (RepositoryProxy)repository;
            var client = new RecordingHttpClient(request =>
                new HttpResponse(request, new HttpHeader(), "{\"product\":{\"asin\":\"A1\",\"title\":\"Positive\"}}", HttpStatusCode.OK));
            var service = new CachedHttpResponseService(repository, client, LogManager.GetCurrentClassLogger());
            var proxy = new AudibleCatalogProxy(service, LogManager.GetCurrentClassLogger());

            Assert.That(proxy.GetBookInfo("A1"), Is.Not.Null);
            Assert.That(proxy.GetBookInfo("A1"), Is.Not.Null);
            Assert.That(repositoryState.UpsertCount, Is.EqualTo(1));
            Assert.That(client.GetCount, Is.EqualTo(1));
        }
    }
}
