using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using NLog;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Transmission;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TransmissionProxyFixture
    {
        private class HttpClientQueueProxy : DispatchProxy
        {
            public Queue<Func<HttpRequest, HttpResponse>> Responses { get; } = new();
            public List<HttpRequest> Requests { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHttpClient.Execute) && args?.Length == 1 && args[0] is HttpRequest request)
                {
                    Requests.Add(request);

                    if (Responses.Count == 0)
                    {
                        throw new InvalidOperationException("Test IHttpClient has no queued responses.");
                    }

                    return Responses.Dequeue()(request);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IHttpClient).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_retry_on_multiple_session_id_conflicts()
        {
            var cacheManager = new CacheManager();

            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientQueueProxy>();
            var httpClientProxy = (HttpClientQueueProxy)(object)httpClient;

            HttpResponse Conflict(HttpRequest request, string sessionId)
            {
                var headers = new HttpHeader();
                headers.ContentType = "text/html; charset=ISO-8859-1";
                headers.Add("X-Transmission-Session-Id", sessionId);
                return new HttpResponse(request, headers, "<html>409</html>", HttpStatusCode.Conflict);
            }

            HttpResponse Success(HttpRequest request)
            {
                var headers = new HttpHeader();
                headers.ContentType = "application/json; charset=utf-8";
                return new HttpResponse(request, headers, "{\"result\":\"success\",\"arguments\":{}}", HttpStatusCode.OK);
            }

            // AuthenticateClient (GET) -> 409 with session id
            httpClientProxy.Responses.Enqueue(request => Conflict(request, "session-auth"));
            // First POST -> 409 (stale session id)
            httpClientProxy.Responses.Enqueue(request => Conflict(request, "session-1"));
            // Second POST -> 409 (rotated again)
            httpClientProxy.Responses.Enqueue(request => Conflict(request, "session-2"));
            // Third POST -> OK
            httpClientProxy.Responses.Enqueue(Success);

            var proxy = new TransmissionProxy(cacheManager, httpClient, LogManager.GetCurrentClassLogger());

            var settings = new TransmissionSettings
            {
                Host = "transmission",
                Port = 9091,
                UseSsl = false,
                UrlBase = "/transmission",
                Username = "user",
                Password = "pass"
            };

            var response = proxy.ProcessRequest("session-get", null, settings);

            Assert.That(response.Result, Is.EqualTo("success"));

            // Requests: GET auth + 3 POST attempts
            Assert.That(httpClientProxy.Requests, Has.Count.EqualTo(4));
            Assert.That(httpClientProxy.Requests[0].Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(httpClientProxy.Requests[1].Method, Is.EqualTo(HttpMethod.Post));

            // First POST uses the auth session id, subsequent POSTs use the ids returned by 409s.
            Assert.That(httpClientProxy.Requests[1].Headers.GetSingleValue("X-Transmission-Session-Id"), Is.EqualTo("session-auth"));
            Assert.That(httpClientProxy.Requests[2].Headers.GetSingleValue("X-Transmission-Session-Id"), Is.EqualTo("session-1"));
            Assert.That(httpClientProxy.Requests[3].Headers.GetSingleValue("X-Transmission-Session-Id"), Is.EqualTo("session-2"));
        }

        [Test]
        public void should_sanitize_session_id_from_headers()
        {
            var cacheManager = new CacheManager();

            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientQueueProxy>();
            var httpClientProxy = (HttpClientQueueProxy)(object)httpClient;

            HttpResponse Conflict(HttpRequest request, string sessionId)
            {
                var headers = new HttpHeader();
                headers.ContentType = "text/html; charset=ISO-8859-1";
                headers.Add("X-Transmission-Session-Id", sessionId);
                return new HttpResponse(request, headers, "<html>409</html>", HttpStatusCode.Conflict);
            }

            HttpResponse Success(HttpRequest request)
            {
                var headers = new HttpHeader();
                headers.ContentType = "application/json; charset=utf-8";
                return new HttpResponse(request, headers, "{\"result\":\"success\",\"arguments\":{}}", HttpStatusCode.OK);
            }

            // AuthenticateClient (GET) -> 409 with quoted session id
            httpClientProxy.Responses.Enqueue(request => Conflict(request, "\"session-auth\""));
            // First POST -> 409 with coalesced session id
            httpClientProxy.Responses.Enqueue(request => Conflict(request, "session-1; session-ignored"));
            // Second POST -> OK
            httpClientProxy.Responses.Enqueue(Success);

            var proxy = new TransmissionProxy(cacheManager, httpClient, LogManager.GetCurrentClassLogger());

            var settings = new TransmissionSettings
            {
                Host = "transmission",
                Port = 9091,
                UseSsl = false,
                UrlBase = "/transmission",
                Username = "user",
                Password = "pass"
            };

            var response = proxy.ProcessRequest("session-get", null, settings);

            Assert.That(response.Result, Is.EqualTo("success"));
            Assert.That(httpClientProxy.Requests, Has.Count.EqualTo(3));
            Assert.That(httpClientProxy.Requests[1].Headers.GetSingleValue("X-Transmission-Session-Id"), Is.EqualTo("session-auth"));
            Assert.That(httpClientProxy.Requests[2].Headers.GetSingleValue("X-Transmission-Session-Id"), Is.EqualTo("session-1"));
        }

        [Test]
        public void should_request_torrent_file_details_by_hash()
        {
            var cacheManager = new CacheManager();

            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientQueueProxy>();
            var httpClientProxy = (HttpClientQueueProxy)(object)httpClient;

            HttpResponse Conflict(HttpRequest request)
            {
                var headers = new HttpHeader();
                headers.ContentType = "text/html; charset=ISO-8859-1";
                headers.Add("X-Transmission-Session-Id", "session-auth");
                return new HttpResponse(request, headers, "<html>409</html>", HttpStatusCode.Conflict);
            }

            HttpResponse Success(HttpRequest request)
            {
                var headers = new HttpHeader();
                headers.ContentType = "application/json; charset=utf-8";
                return new HttpResponse(request, headers, @"{
                    ""result"": ""success"",
                    ""arguments"": {
                        ""torrents"": [
                            {
                                ""hashString"": ""ABCDEF1234"",
                                ""downloadDir"": ""/downloads"",
                                ""files"": [
                                    { ""name"": ""Author - Book/part1.m4b"", ""length"": 12345, ""bytesCompleted"": 12345 }
                                ],
                                ""fileStats"": [
                                    { ""bytesCompleted"": 12345, ""wanted"": true, ""priority"": 0 }
                                ]
                            }
                        ]
                    }
                }", HttpStatusCode.OK);
            }

            httpClientProxy.Responses.Enqueue(Conflict);
            httpClientProxy.Responses.Enqueue(Success);

            var proxy = new TransmissionProxy(cacheManager, httpClient, LogManager.GetCurrentClassLogger());

            var settings = new TransmissionSettings
            {
                Host = "transmission",
                Port = 9091,
                UseSsl = false,
                UrlBase = "/transmission",
                Username = "user",
                Password = "pass"
            };

            var details = proxy.GetTorrentDetails("abcdef1234", settings);
            var requestBody = JObject.Parse(Encoding.UTF8.GetString(httpClientProxy.Requests[1].ContentData));

            Assert.That(details.HashString, Is.EqualTo("ABCDEF1234"));
            Assert.That(details.DownloadDir, Is.EqualTo("/downloads"));
            Assert.That(details.Files[0].Name, Is.EqualTo("Author - Book/part1.m4b"));
            Assert.That((string)requestBody["method"], Is.EqualTo("torrent-get"));
            Assert.That(requestBody["arguments"]["ids"].ToObject<string[]>(), Is.EqualTo(new[] { "abcdef1234" }));
            Assert.That(requestBody["arguments"]["fields"].ToObject<string[]>(), Is.EqualTo(new[]
            {
                "hashString",
                "name",
                "downloadDir",
                "files",
                "fileStats"
            }));
        }
    }
}
