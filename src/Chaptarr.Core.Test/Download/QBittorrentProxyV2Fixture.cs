using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.QBittorrent;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class QBittorrentProxyV2Fixture
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

                    var response = Responses.Dequeue()(request);
                    if (!request.SuppressHttpError &&
                        response.HasHttpError &&
                        (request.SuppressHttpErrorStatusCodes == null || !request.SuppressHttpErrorStatusCodes.Contains(response.StatusCode)))
                    {
                        throw new HttpException(response);
                    }

                    return response;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IHttpClient).Name}.{targetMethod?.Name}");
            }
        }

        private static (QBittorrentProxyV2 Proxy, HttpClientQueueProxy HttpClient) CreateProxy()
        {
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientQueueProxy>();
            var httpClientProxy = (HttpClientQueueProxy)(object)httpClient;

            return (new QBittorrentProxyV2(httpClient, new CacheManager(), LogManager.GetCurrentClassLogger()), httpClientProxy);
        }

        private static QBittorrentSettings Settings(string username = "user")
        {
            return new QBittorrentSettings
            {
                Host = "qbittorrent",
                Port = 8080,
                Username = username,
                Password = "pass"
            };
        }

        private static QBittorrentSettings ApiKeySettings(string apiKey = "test-api-key")
        {
            return new QBittorrentSettings
            {
                Host = "qbittorrent",
                Port = 8080,
                ApiKey = apiKey
            };
        }

        private static HttpResponse Response(HttpRequest request, string content, HttpStatusCode statusCode = HttpStatusCode.OK, string cookie = null)
        {
            var headers = new HttpHeader();
            headers.ContentType = "text/plain; charset=UTF-8";

            if (cookie != null)
            {
                headers.Add("Set-Cookie", cookie);
            }

            return new HttpResponse(request, headers, content, statusCode);
        }

        [Test]
        public void should_send_bearer_authorization_header_when_api_key_is_set()
        {
            var (proxy, httpClient) = CreateProxy();

            httpClient.Responses.Enqueue(request => Response(request, "v5.2.0"));

            var version = proxy.GetVersion(ApiKeySettings("secret-key"));

            Assert.That(version, Is.EqualTo("5.2.0"));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
            Assert.That(httpClient.Requests[0].Url.FullUri, Does.EndWith("/api/v2/app/version"));
            Assert.That(httpClient.Requests[0].Headers.GetSingleValue("Authorization"), Is.EqualTo("Bearer secret-key"));
        }

        [Test]
        public void should_skip_cookie_auth_when_api_key_is_set()
        {
            var (proxy, httpClient) = CreateProxy();

            httpClient.Responses.Enqueue(request => Response(request, "v5.2.0"));

            proxy.GetVersion(ApiKeySettings());

            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
            Assert.That(httpClient.Requests.Any(request => request.Url.FullUri.EndsWith("/api/v2/auth/login")), Is.False);
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void should_translate_api_key_authentication_failures(HttpStatusCode statusCode)
        {
            var (proxy, httpClient) = CreateProxy();

            httpClient.Responses.Enqueue(request => Response(request, "Unauthorized", statusCode));

            Assert.Throws<DownloadClientAuthenticationException>(() => proxy.GetVersion(ApiKeySettings()));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
            Assert.That(httpClient.Requests[0].Url.FullUri, Does.EndWith("/api/v2/app/version"));
        }

        [Test]
        public void should_fail_validation_when_api_key_and_username_are_both_set()
        {
            var settings = ApiKeySettings();
            settings.Username = "user";

            var result = settings.Validate();

            Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Some.EqualTo("Username must be empty when using API Key."));
        }

        [Test]
        public void should_fail_validation_when_api_key_and_password_are_both_set()
        {
            var settings = ApiKeySettings();
            settings.Password = "pass";

            var result = settings.Validate();

            Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Some.EqualTo("Password must be empty when using API Key."));
        }

        [Test]
        public void should_pass_validation_with_api_key_only()
        {
            var result = ApiKeySettings().Validate();

            Assert.That(result.Errors, Is.Empty);
        }

        [Test]
        public void should_pass_validation_with_username_and_password_only()
        {
            var result = Settings().Validate();

            Assert.That(result.Errors, Is.Empty);
        }

        [Test]
        public void should_accept_empty_login_response_from_qbittorrent_5_2()
        {
            var (proxy, httpClient) = CreateProxy();

            httpClient.Responses.Enqueue(request => Response(request, string.Empty, HttpStatusCode.NoContent, "QBT_SID_8080=abc; path=/"));
            httpClient.Responses.Enqueue(request => Response(request, "v5.2.0"));

            var version = proxy.GetVersion(Settings());

            Assert.That(version, Is.EqualTo("5.2.0"));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(2));
            Assert.That(httpClient.Requests[0].Url.FullUri, Does.EndWith("/api/v2/auth/login"));
            Assert.That(httpClient.Requests[1].Cookies["QBT_SID_8080"], Is.EqualTo("abc"));
            Assert.That(httpClient.Requests[0].StoreRequestCookie, Is.False);
            Assert.That(httpClient.Requests[1].StoreRequestCookie, Is.False);
        }

        [Test]
        public void should_treat_unauthorized_login_as_authentication_failure()
        {
            var (proxy, httpClient) = CreateProxy();

            httpClient.Responses.Enqueue(request => Response(request, "Unauthorized", HttpStatusCode.Unauthorized));

            Assert.Throws<DownloadClientAuthenticationException>(() => proxy.GetVersion(Settings()));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
            Assert.That(httpClient.Requests[0].Url.FullUri, Does.EndWith("/api/v2/auth/login"));
        }

        [Test]
        public void should_reject_failed_login_response_from_older_qbittorrent()
        {
            var (proxy, httpClient) = CreateProxy();

            httpClient.Responses.Enqueue(request => Response(request, "Fails."));

            Assert.Throws<DownloadClientAuthenticationException>(() => proxy.GetVersion(Settings()));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
            Assert.That(httpClient.Requests[0].Url.FullUri, Does.EndWith("/api/v2/auth/login"));
        }

        [Test]
        public void should_reauthenticate_on_unauthorized_api_response()
        {
            var (proxy, httpClient) = CreateProxy();
            var settings = Settings();

            httpClient.Responses.Enqueue(request => Response(request, string.Empty, HttpStatusCode.NoContent, "QBT_SID_8080=stale; path=/"));
            httpClient.Responses.Enqueue(request => Response(request, "v5.2.0"));
            httpClient.Responses.Enqueue(request => Response(request, "Unauthorized", HttpStatusCode.Unauthorized));
            httpClient.Responses.Enqueue(request => Response(request, string.Empty, HttpStatusCode.NoContent, "QBT_SID_8080=fresh; path=/"));
            httpClient.Responses.Enqueue(request => Response(request, "v5.2.1"));

            Assert.That(proxy.GetVersion(settings), Is.EqualTo("5.2.0"));
            Assert.That(proxy.GetVersion(settings), Is.EqualTo("5.2.1"));

            var loginRequests = httpClient.Requests.Where(request => request.Url.FullUri.EndsWith("/api/v2/auth/login")).ToList();
            Assert.That(loginRequests, Has.Count.EqualTo(2));
            Assert.That(httpClient.Requests.Last().Cookies["QBT_SID_8080"], Is.EqualTo("fresh"));
        }

        [Test]
        public void should_cache_auth_cookies_per_username()
        {
            var (proxy, httpClient) = CreateProxy();

            httpClient.Responses.Enqueue(request => Response(request, string.Empty, HttpStatusCode.NoContent, "QBT_SID_8080=user1; path=/"));
            httpClient.Responses.Enqueue(request => Response(request, "v5.2.0"));
            httpClient.Responses.Enqueue(request => Response(request, string.Empty, HttpStatusCode.NoContent, "QBT_SID_8080=user2; path=/"));
            httpClient.Responses.Enqueue(request => Response(request, "v5.2.0"));

            Assert.That(proxy.GetVersion(Settings("user1")), Is.EqualTo("5.2.0"));
            Assert.That(proxy.GetVersion(Settings("user2")), Is.EqualTo("5.2.0"));

            var loginRequests = httpClient.Requests.Where(request => request.Url.FullUri.EndsWith("/api/v2/auth/login")).ToList();
            Assert.That(loginRequests, Has.Count.EqualTo(2));
            Assert.That(httpClient.Requests[1].Cookies["QBT_SID_8080"], Is.EqualTo("user1"));
            Assert.That(httpClient.Requests[3].Cookies["QBT_SID_8080"], Is.EqualTo("user2"));
        }
    }
}
