using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.ThingiProvider;
using AbsNotification = NzbDrone.Core.Notifications.AudioBookShelf.AudioBookShelf;

namespace Chaptarr.Core.Test.Notifications.AudioBookShelf
{
    [TestFixture]
    public class AudioBookShelfOAuthErrorFixture
    {
        private const string ResponseBody = "{\"error\":\"synthetic response detail\",\"apiKey\":\"synthetic-api-key\",\"Authorization\":\"Bearer synthetic-token\"}";

        [Test]
        public void should_keep_oidc_start_error_to_status_summary()
        {
            var httpClient = new QueueHttpClient();
            httpClient.Enqueue((request) => ErrorResponse(request, ResponseBody));

            var exception = Assert.Throws<BadRequestException>(() => CreateSubject(httpClient).RequestAction("startOAuth", new Dictionary<string, string>
            {
                ["callbackUrl"] = "http://localhost:8787/oauth/callback"
            }));

            AssertSafeSummary(exception, "AudioBookShelf OIDC start failed");
        }

        [Test]
        public void should_keep_oidc_callback_error_to_status_summary()
        {
            var httpClient = new QueueHttpClient();
            httpClient.Enqueue(RedirectResponse);
            httpClient.Enqueue((request) => ErrorResponse(request, ResponseBody));
            var subject = CreateSubject(httpClient);
            var state = StartOAuth(subject, httpClient);

            var exception = Assert.Throws<BadRequestException>(() => subject.RequestAction("getOAuthToken", new Dictionary<string, string>
            {
                ["code"] = "synthetic-code",
                ["state"] = state
            }));

            AssertSafeSummary(exception, "AudioBookShelf OIDC callback failed");
        }

        [Test]
        public void should_keep_api_key_creation_error_to_status_summary()
        {
            var httpClient = new QueueHttpClient();
            httpClient.Enqueue(RedirectResponse);
            httpClient.Enqueue((request) => JsonResponse(request, "{\"user\":{\"id\":\"synthetic-user\",\"accessToken\":\"synthetic-access-token\"}}"));
            httpClient.Enqueue((request) => ErrorResponse(request, ResponseBody));
            var subject = CreateSubject(httpClient);
            var state = StartOAuth(subject, httpClient);

            var exception = Assert.Throws<BadRequestException>(() => subject.RequestAction("getOAuthToken", new Dictionary<string, string>
            {
                ["code"] = "synthetic-code",
                ["state"] = state
            }));

            AssertSafeSummary(exception, "Failed to create AudioBookShelf API key");
        }

        [Test]
        public void should_preserve_invalid_redirect_diagnosis_without_response_body()
        {
            var httpClient = new QueueHttpClient();
            httpClient.Enqueue((request) => ErrorResponse(request, "{\"error\":\"Invalid redirect_uri\",\"apiKey\":\"synthetic-api-key\"}"));
            var callbackUrl = "http://localhost:8787/oauth/callback";

            var exception = Assert.Throws<BadRequestException>(() => CreateSubject(httpClient).RequestAction("startOAuth", new Dictionary<string, string>
            {
                ["callbackUrl"] = callbackUrl
            }));

            Assert.That(exception.Message, Does.Contain("rejected the callback URL"));
            Assert.That(exception.Message, Does.Contain(callbackUrl));
            Assert.That(exception.ToString(), Does.Not.Contain("Invalid redirect_uri"));
            Assert.That(exception.ToString(), Does.Not.Contain("synthetic-api-key"));
        }

        private static AbsNotification CreateSubject(QueueHttpClient httpClient)
        {
            return new AbsNotification(
                proxy: null,
                httpClient: httpClient,
                pendingProviderSecretService: new PendingProviderSecretService(new CacheManager()),
                cacheManager: new CacheManager(),
                rootFolderService: null,
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new NotificationDefinition
                {
                    Settings = new AudioBookShelfSettings
                    {
                        Host = "abs.example",
                        Port = 13378,
                        ApiKey = "configured-api-key"
                    }
                }
            };
        }

        private static string StartOAuth(AbsNotification subject, QueueHttpClient httpClient)
        {
            subject.RequestAction("startOAuth", new Dictionary<string, string>
            {
                ["callbackUrl"] = "http://localhost:8787/oauth/callback"
            });

            foreach (var item in httpClient.LastRequest.Url.Query.Split('&'))
            {
                if (item.StartsWith("state=", StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(item.Substring("state=".Length));
                }
            }

            throw new AssertionException("The OIDC start request did not include a state value.");
        }

        private static void AssertSafeSummary(Exception exception, string operation)
        {
            Assert.That(exception, Is.Not.Null);
            Assert.That(exception.Message, Does.Contain(operation));
            Assert.That(exception.Message, Does.Contain("400 BadRequest"));
            Assert.That(exception.ToString(), Does.Not.Contain("synthetic response detail"));
            Assert.That(exception.ToString(), Does.Not.Contain("synthetic-api-key"));
            Assert.That(exception.ToString(), Does.Not.Contain("synthetic-token"));
        }

        private static HttpResponse RedirectResponse(HttpRequest request)
        {
            var headers = new HttpHeader
            {
                ContentType = "text/html"
            };
            headers.Add("Location", "https://abs.example/auth?code=synthetic-code");
            headers.Add("Set-Cookie", "session=synthetic-session; Path=/");

            return new HttpResponse(request, headers, string.Empty, HttpStatusCode.Found);
        }

        private static HttpResponse JsonResponse(HttpRequest request, string content)
        {
            return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content);
        }

        private static HttpResponse ErrorResponse(HttpRequest request, string content)
        {
            return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content, HttpStatusCode.BadRequest);
        }

        private sealed class QueueHttpClient : IHttpClient
        {
            private readonly Queue<Func<HttpRequest, HttpResponse>> _responses = new();

            public HttpRequest LastRequest { get; private set; }

            public void Enqueue(Func<HttpRequest, HttpResponse> response)
            {
                _responses.Enqueue(response);
            }

            public HttpResponse Execute(HttpRequest request)
            {
                LastRequest = request;
                return _responses.Dequeue()(request);
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
    }
}
