using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http.Proxy;

namespace NzbDrone.Common.Http.Dispatchers
{
    public class ManagedHttpDispatcher : IHttpDispatcher
    {
        private const string NO_PROXY_KEY = "no-proxy";
        private const string TransmissionSessionHeader = "X-Transmission-Session-Id";

        private readonly IHttpProxySettingsProvider _proxySettingsProvider;
        private readonly ICreateManagedSocketsHttpHandler _socketsHttpHandlerFactory;
        private readonly IUserAgentBuilder _userAgentBuilder;
        private readonly ICached<System.Net.Http.HttpClient> _httpClientCache;
        private readonly ICached<CredentialCache> _credentialCache;
        private readonly object _credentialCacheLock = new object();

        public ManagedHttpDispatcher(IHttpProxySettingsProvider proxySettingsProvider,
            ICreateManagedSocketsHttpHandler socketsHttpHandlerFactory,
            IUserAgentBuilder userAgentBuilder,
            ICacheManager cacheManager,
            Logger logger)
        {
            _proxySettingsProvider = proxySettingsProvider;
            _socketsHttpHandlerFactory = socketsHttpHandlerFactory;
            _userAgentBuilder = userAgentBuilder;

            _httpClientCache = cacheManager.GetCache<System.Net.Http.HttpClient>(typeof(ManagedHttpDispatcher), "httpclient");
            _credentialCache = cacheManager.GetCache<CredentialCache>(typeof(ManagedHttpDispatcher), "credentialcache");
        }

        public virtual async Task<HttpResponse> GetResponseAsync(HttpRequest request, CookieContainer cookies)
        {
            var isTransmissionRpcSessionRequest = request.Headers != null && request.Headers.ContainsKey(TransmissionSessionHeader);

            using var requestMessage = new HttpRequestMessage(request.Method, (Uri)request.Url)
            {
                Version = isTransmissionRpcSessionRequest ? HttpVersion.Version11 : HttpVersion.Version20,
                VersionPolicy = isTransmissionRpcSessionRequest ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower
            };
            requestMessage.Headers.UserAgent.ParseAdd(_userAgentBuilder.GetUserAgent(request.UseSimplifiedUserAgent));
            requestMessage.Headers.ConnectionClose = !request.ConnectionKeepAlive;

            var cookieHeader = cookies.GetCookieHeader((Uri)request.Url);
            if (cookieHeader.IsNotNullOrWhiteSpace())
            {
                requestMessage.Headers.Add("Cookie", cookieHeader);
            }

            if (request.Credentials != null)
            {
                if (request.Credentials is BasicNetworkCredential bc)
                {
                    // Manually set header to avoid initial challenge response
                    var authInfo = bc.UserName + ":" + bc.Password;
                    authInfo = Convert.ToBase64String(Encoding.GetEncoding("ISO-8859-1").GetBytes(authInfo));
                    requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Basic", authInfo);
                }
                else if (request.Credentials is NetworkCredential nc)
                {
                    foreach (var authtype in new[] { "Basic", "Digest" })
                    {
                        EnsureCredentialCached((Uri)request.Url, authtype, nc);
                    }
                }
            }

            using var cts = new CancellationTokenSource();
            if (request.RequestTimeout != TimeSpan.Zero)
            {
                cts.CancelAfter(request.RequestTimeout);
            }
            else
            {
                // The default for System.Net.Http.HttpClient
                cts.CancelAfter(TimeSpan.FromSeconds(100));
            }

            if (request.ContentData != null)
            {
                requestMessage.Content = new ByteArrayContent(request.ContentData);
            }

            if (request.Headers != null)
            {
                AddRequestHeaders(requestMessage, request.Headers);
            }

            var httpClient = GetClient(request.Url);

            try
            {
                if (isTransmissionRpcSessionRequest && request.Headers != null)
                {
                    // Defensive: ensure the header is set on the outgoing message even if a previous add was dropped.
                    var sessionId = request.Headers.GetSingleValue(TransmissionSessionHeader);
                    if (sessionId.IsNotNullOrWhiteSpace())
                    {
                        requestMessage.Headers.Remove(TransmissionSessionHeader);
                        requestMessage.Headers.TryAddWithoutValidation(TransmissionSessionHeader, sessionId);
                    }
                }

                using var responseMessage = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                byte[] data = null;

                try
                {
                    if (request.ResponseStream != null && responseMessage.StatusCode == HttpStatusCode.OK)
                    {
                        await responseMessage.Content.CopyToAsync(request.ResponseStream, null, cts.Token);
                    }
                    else
                    {
                        data = await responseMessage.Content.ReadAsByteArrayAsync(cts.Token);
                    }
                }
                catch (Exception ex)
                {
                    throw new WebException("Failed to read complete http response", ex, WebExceptionStatus.ReceiveFailure, null);
                }

                var headers = responseMessage.Headers.ToNameValueCollection();
                headers.Add(responseMessage.Content.Headers.ToNameValueCollection());

                return new HttpResponse(request, new HttpHeader(headers), data, responseMessage.StatusCode, responseMessage.Version);
            }
            catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
            {
                throw new WebException("Http request timed out", ex, WebExceptionStatus.Timeout, null);
            }
        }

        protected virtual System.Net.Http.HttpClient GetClient(HttpUri uri)
        {
            var proxySettings = _proxySettingsProvider.GetProxySettings(uri);
            var key = proxySettings?.Key ?? NO_PROXY_KEY;

            return _httpClientCache.Get(key, () => CreateHttpClient(proxySettings));
        }

        protected virtual System.Net.Http.HttpClient CreateHttpClient(HttpProxySettings proxySettings)
        {
            var handler = _socketsHttpHandlerFactory.CreateHandler(proxySettings,
                allowAutoRedirect: false,
                useCookies: false,
                credentials: GetCredentialCache(),
                preAuthenticate: true,
                maxConnectionsPerServer: 12);

            var client = new System.Net.Http.HttpClient(handler)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Timeout = Timeout.InfiniteTimeSpan
            };

            return client;
        }

        protected virtual void AddRequestHeaders(HttpRequestMessage webRequest, HttpHeader headers)
        {
            foreach (var header in headers)
            {
                switch (header.Key)
                {
                    case "Accept":
                        webRequest.Headers.Accept.ParseAdd(header.Value);
                        break;
                    case "Connection":
                        webRequest.Headers.Connection.Clear();
                        webRequest.Headers.Connection.Add(header.Value);
                        break;
                    case "Content-Length":
                        AddContentHeader(webRequest, "Content-Length", header.Value);
                        break;
                    case "Content-Type":
                        AddContentHeader(webRequest, "Content-Type", header.Value);
                        break;
                    case "Content-Encoding":
                        AddContentHeader(webRequest, "Content-Encoding", header.Value);
                        break;
                    case "Date":
                        webRequest.Headers.Remove("Date");
                        webRequest.Headers.Date = HttpHeader.ParseDateTime(header.Value);
                        break;
                    case "Expect":
                        webRequest.Headers.Expect.ParseAdd(header.Value);
                        break;
                    case "Host":
                        webRequest.Headers.Host = header.Value;
                        break;
                    case "If-Modified-Since":
                        webRequest.Headers.IfModifiedSince = HttpHeader.ParseDateTime(header.Value);
                        break;
                    case "Referer":
                        webRequest.Headers.Add("Referer", header.Value);
                        break;
                    case "Transfer-Encoding":
                        webRequest.Headers.TransferEncoding.ParseAdd(header.Value);
                        break;
                    case "User-Agent":
                        webRequest.Headers.UserAgent.Clear();
                        if (header.Value.IsNotNullOrWhiteSpace())
                        {
                            webRequest.Headers.UserAgent.ParseAdd(header.Value);
                        }
                        break;
                    case "Proxy-Connection":
                        throw new NotImplementedException();
                    default:
                        if (header.Key.Equals(TransmissionSessionHeader, StringComparison.OrdinalIgnoreCase))
                        {
                            if (header.Value.IsNotNullOrWhiteSpace())
                            {
                                webRequest.Headers.Remove(TransmissionSessionHeader);
                                webRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                        }
                        else
                        {
                            webRequest.Headers.Add(header.Key, header.Value);
                        }
                        break;
                }
            }
        }

        private static void AddContentHeader(HttpRequestMessage request, string header, string value)
        {
            var headers = request.Content?.Headers;
            if (headers == null)
            {
                return;
            }

            headers.Remove(header);
            headers.Add(header, value);
        }

        private CredentialCache GetCredentialCache()
        {
            return _credentialCache.Get("credentialCache", () => new CredentialCache());
        }

        private void EnsureCredentialCached(Uri uri, string authType, NetworkCredential credential)
        {
            var creds = GetCredentialCache();

            lock (_credentialCacheLock)
            {
                var existing = creds.GetCredential(uri, authType);

                // CredentialCache uses URI-prefix matching; a broader matching entry with the same credential is enough.
                if (CredentialsEqual(existing, credential))
                {
                    return;
                }

                if (existing != null)
                {
                    creds.Remove(uri, authType);
                }

                creds.Add(uri, authType, credential);
            }
        }

        private static bool CredentialsEqual(NetworkCredential left, NetworkCredential right)
        {
            if (left == null || right == null)
            {
                return left == null && right == null;
            }

            return string.Equals(left.UserName, right.UserName, StringComparison.Ordinal) &&
                   string.Equals(left.Password, right.Password, StringComparison.Ordinal) &&
                   string.Equals(left.Domain, right.Domain, StringComparison.Ordinal);
        }
    }
}
