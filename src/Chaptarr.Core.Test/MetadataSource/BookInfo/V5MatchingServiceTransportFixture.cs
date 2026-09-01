using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class V5MatchingServiceTransportFixture
    {
        private sealed class RecordingHttpClient : IHttpClient
        {
            public List<HttpRequest> Requests { get; } = new();

            public HttpResponse Execute(HttpRequest request)
            {
                Requests.Add(request);
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, "{\"matches\":[]}", HttpStatusCode.OK);
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse Get(HttpRequest request) => throw new NotImplementedException();
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

        private sealed class MetadataServerHealthGateStub : IMetadataServerHealthGate
        {
            public string SourceName => "test";

            public bool TryBeginRequest(out TimeSpan retryAfter)
            {
                retryAfter = TimeSpan.Zero;
                return true;
            }

            public bool CanAttemptWithoutProbe(out TimeSpan retryAfter)
            {
                retryAfter = TimeSpan.Zero;
                return true;
            }

            public void ReportResponse(HttpResponse response)
            {
            }

            public void ReportException(Exception exception)
            {
            }

            public void Reset()
            {
            }
        }

        private sealed class MatchingLoggerStub : IMatchingUploadLogger
        {
            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null)
            {
            }

            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null)
            {
            }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null)
            {
            }

            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000) => new();

            public void ClearLogs()
            {
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_MetadataServerUrl" => "https://metadata.test",
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private static V5MatchingService CreateService(IHttpClient httpClient, IConfigService configService = null)
        {
            configService ??= DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            return new V5MatchingService(
                httpClient,
                null,
                configService,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                new MatchingLoggerStub(),
                LogManager.GetCurrentClassLogger(),
                new MetadataServerHealthGateStub());
        }

        [Test]
        public void should_send_v5_match_as_query_with_json_body()
        {
            var httpClient = new RecordingHttpClient();
            var service = CreateService(httpClient);
            var matches = service.SearchV5Matching(
                "Piranesi",
                new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Piranesi" },
                    ["ARTIST"] = new() { "Susanna Clarke" }
                },
                "audio",
                "/audiobooks/Piranesi.m4b");

            Assert.That(matches, Is.Empty);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));

            var request = httpClient.Requests.Single();
            Assert.Multiple(() =>
            {
                Assert.That(request.Method, Is.EqualTo(HttpMethod.Query));
                Assert.That(request.Url.FullUri, Is.EqualTo("https://metadata.test/api/v5/match"));
                Assert.That(request.Headers.ContentType, Is.EqualTo("application/json"));
                Assert.That(request.ContentData, Is.Not.Null.And.Not.Empty);
            });
        }

        [Test]
        public void should_remove_embedded_nulls_from_every_outgoing_match_evidence_field()
        {
            var httpClient = new RecordingHttpClient();
            var service = CreateService(httpClient);

            service.SearchV5Matching(
                "Pi\0ranesi",
                new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Existing" },
                    ["TI\0TLE"] = new() { "Pi\0ranesi" },
                    ["ALBUM"] = new() { "Pi\0ranesi" },
                    ["CUS\0TOM"] = new() { "Susanna\0Clarke" },
                    ["CO\0MMENT"] = new() { "excluded after key sanitation" }
                },
                "ebook",
                "/ebooks/Pi\0ranesi.epub");

            var request = httpClient.Requests.Single();
            var requestJson = Encoding.UTF8.GetString(request.ContentData);
            var body = JsonConvert.DeserializeObject<V5MatchRequest>(requestJson);

            Assert.Multiple(() =>
            {
                Assert.That(requestJson, Does.Not.Contain("\\u0000"));
                Assert.That(body.q, Is.EqualTo("Pi ranesi"));
                Assert.That(body.tags["TITLE"], Is.EquivalentTo(new[] { "Existing", "Pi ranesi" }));
                Assert.That(body.tags["ALBUM"], Is.EqualTo(new[] { "Pi ranesi" }));
                Assert.That(body.tags["CUSTOM"], Is.EqualTo(new[] { "Susanna Clarke" }));
                Assert.That(body.tags["file_name"], Is.EqualTo(new[] { "Pi ranesi.epub" }));
                Assert.That(body.tags.ContainsKey("COMMENT"), Is.False);
                Assert.That(body.tags.Keys.Any(key => key.Contains('\0')), Is.False);
                Assert.That(body.tags.Values.SelectMany(values => values).Any(value => value.Contains('\0')), Is.False);
            });
        }

        [Test]
        public void should_remove_embedded_nulls_from_a_query_derived_from_tags()
        {
            var httpClient = new RecordingHttpClient();
            var service = CreateService(httpClient);

            service.SearchV5Matching(
                string.Empty,
                new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Pi\0ranesi" },
                    ["ARTIST"] = new() { "Susanna Clarke" }
                },
                "ebook",
                null);

            var request = httpClient.Requests.Single();
            var requestJson = Encoding.UTF8.GetString(request.ContentData);
            var body = JsonConvert.DeserializeObject<V5MatchRequest>(requestJson);

            Assert.Multiple(() =>
            {
                Assert.That(requestJson, Does.Not.Contain("\\u0000"));
                Assert.That(body.q, Is.EqualTo("pi ranesi susanna clarke"));
            });
        }

        [Test]
        public void should_not_truncate_the_effective_query()
        {
            var httpClient = new RecordingHttpClient();
            var service = CreateService(httpClient);
            var query = $"{new string('a', 300)}\0{new string('b', 300)}";

            service.SearchV5Matching(
                query,
                new Dictionary<string, List<string>> { ["TITLE"] = new() { "Piranesi" } },
                "ebook",
                null);

            var request = httpClient.Requests.Single();
            var requestJson = Encoding.UTF8.GetString(request.ContentData);
            var body = JsonConvert.DeserializeObject<V5MatchRequest>(requestJson);

            Assert.Multiple(() =>
            {
                Assert.That(requestJson, Does.Not.Contain("\\u0000"));
                Assert.That(body.q, Is.EqualTo($"{new string('a', 300)} {new string('b', 300)}"));
                Assert.That(body.q, Has.Length.EqualTo(601));
            });
        }

        [Test]
        public void should_omit_tags_whose_sanitized_key_or_values_are_empty()
        {
            var httpClient = new RecordingHttpClient();
            var service = CreateService(httpClient);

            service.SearchV5Matching(
                "Piranesi",
                new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Piranesi" },
                    ["EMPTY"] = new() { "\0 \t" },
                    ["\0\t"] = new() { "must not survive" }
                },
                "ebook",
                null);

            var request = httpClient.Requests.Single();
            var requestJson = Encoding.UTF8.GetString(request.ContentData);
            var body = JsonConvert.DeserializeObject<V5MatchRequest>(requestJson);

            Assert.Multiple(() =>
            {
                Assert.That(requestJson, Does.Not.Contain("\\u0000"));
                Assert.That(body.tags.Keys, Is.EqualTo(new[] { "TITLE" }));
                Assert.That(body.tags["TITLE"], Is.EqualTo(new[] { "Piranesi" }));
            });
        }
    }
}
