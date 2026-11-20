using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using HttpClient = NzbDrone.Common.Http.HttpClient;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    [NonParallelizable]
    public partial class MediaCoverTransportFixture
    {
        private static readonly byte[] Image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static IConfigService Config(string url) => InterfaceProxy.Proxy<IConfigService>((m, _) =>
            m.Name == "get_MetadataServerUrl" ? url : m.ReturnType == typeof(bool) ? false : null);

        private static ManagedSocketsHttpHandlerFactory Factory(Logger logger = null) => new(new ManagedWebProxyFactory(new CacheManager()),
            InterfaceProxy.Proxy<ICertificateValidationService>((_, _) => true), logger ?? Logger);

        private static HttpClient Client(HttpProxySettings proxy = null, CacheManager cache = null, Action<HttpRequest> onRequest = null,
            IHttpProxySettingsProvider provider = null, ICreateManagedSocketsHttpHandler factory = null, Logger logger = null)
        {
            cache ??= new CacheManager();
            var dispatcher = new ManagedHttpDispatcher(
                provider ?? InterfaceProxy.Proxy<IHttpProxySettingsProvider>((_, _) => proxy ?? HttpProxySettings.DirectConnection),
                factory ?? Factory(), InterfaceProxy.Proxy<IUserAgentBuilder>((_, _) => "CoverFixture/1.0"), cache, logger ?? Logger);
            var interceptors = onRequest == null ? Array.Empty<IHttpRequestInterceptor>() : new[]
            {
                InterfaceProxy.Proxy<IHttpRequestInterceptor>((method, args) =>
                {
                    if (method.Name == nameof(IHttpRequestInterceptor.PreRequest))
                    {
                        onRequest((HttpRequest)args[0]);
                    }

                    return args[0];
                })
            };
            return new HttpClient(interceptors, cache,
                InterfaceProxy.Proxy<IRateLimitService>((_, _) => Task.CompletedTask), dispatcher, Logger);
        }

        private static async Task<string> ReadHeaders(Stream stream, CancellationToken token)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (bytes.Count < 16384)
            {
                await stream.ReadExactlyAsync(one, token);
                bytes.Add(one[0]);
                if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
            }

            throw new InvalidDataException("Fixture header limit exceeded");
        }

        private static async Task Reply(Stream stream, CancellationToken token, string extra = "", bool body = true)
        {
            var status = extra.StartsWith("Location:", StringComparison.Ordinal) ? "302 Found" : "200 OK";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extra}Content-Type: image/png\r\nContent-Length: {(body ? Image.Length : 0)}\r\nConnection: close\r\n\r\n"), token);
            if (body)
            {
                await stream.WriteAsync(Image, token);
            }
        }

        private sealed class Server : IDisposable
        {
            public readonly TcpListener Listener = new(IPAddress.Loopback, 0);
            public readonly CancellationTokenSource Deadline;
            public int Port => ((IPEndPoint)Listener.LocalEndpoint).Port;
            public string Url => $"http://127.0.0.1:{Port}/cover.png";
            public Server()
            {
                Deadline = new(TimeSpan.FromSeconds(5));
                Listener.Start();
            }
            public void Dispose()
            {
                Deadline.Cancel();
                Listener.Stop();
                Deadline.Dispose();
            }
        }

        private static X509Certificate2 CreateServerCertificate(string host)
        {
            using var key = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            // Schannel needs a named private key to serve the certificate.
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
        }

        [Test]
        public async Task configured_local_metadata_server_works_and_generic_HTTP_stays_unrestricted()
        {
            using var server = new Server();
            var serving = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                    await ReadHeaders(socket.GetStream(), server.Deadline.Token);
                    await Reply(socket.GetStream(), server.Deadline.Token);
                }
            });
            var logging = new List<bool>();
            var client = Client(onRequest: request => logging.Add(request.LogHttpError));
            Assert.That(client.Get(new HttpRequest(server.Url)).ResponseData, Is.EqualTo(Image));
            Assert.That(MediaCoverHttpRequest.Get(client, Config(server.Url), server.Url).ResponseData, Is.EqualTo(Image));
            await serving;
            Assert.That(logging, Is.EqualTo(new[] { true, true }));
            var error = Assert.Throws<UnsafeImageUrlException>(() => MediaCoverHttpRequest.Get(client, null, server.Url));
            Assert.That(error.Message, Does.StartWith("Blocked unsafe image URL '"));
            Assert.That(error.Message, Does.Contain("private_or_local_address"));
            Assert.That(server.Listener.Pending(), Is.False, "A generic or formerly trusted pool must not bypass image restrictions");
        }

        [TestCase(ProxyType.Http, false)]
        [TestCase(ProxyType.Http, true)]
        [TestCase(ProxyType.Socks5, false)]
        [TestCase(ProxyType.Socks5, true)]
        public async Task proxy_receives_origin_hostname_without_local_origin_DNS(ProxyType type, bool tls)
        {
            using var server = new Server();
            var host = "cover-cannot-resolve.invalid";
            var url = $"{(tls ? "https" : "http")}://{host}/cover.png";
            string proxyTarget = null;
            string headers = null;
            using var certificate = tls ? CreateServerCertificate(host) : null;
            var serving = Task.Run(async () =>
            {
                using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                Stream stream = socket.GetStream();
                if (type == ProxyType.Socks5)
                {
                    var greeting = new byte[2];
                    await stream.ReadExactlyAsync(greeting, server.Deadline.Token);
                    var methods = new byte[greeting[1]];
                    await stream.ReadExactlyAsync(methods, server.Deadline.Token);
                    await stream.WriteAsync(new byte[] { 5, 0 }, server.Deadline.Token);
                    var connect = new byte[5];
                    await stream.ReadExactlyAsync(connect, server.Deadline.Token);
                    Assert.That(connect[3], Is.EqualTo(3), "SOCKS5 destination must be a hostname");
                    var destination = new byte[connect[4] + 2];
                    await stream.ReadExactlyAsync(destination, server.Deadline.Token);
                    proxyTarget = Encoding.ASCII.GetString(destination, 0, destination.Length - 2);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, server.Deadline.Token);
                }
                else if (tls)
                {
                    proxyTarget = await ReadHeaders(stream, server.Deadline.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), server.Deadline.Token);
                }

                if (tls)
                {
                    using var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, server.Deadline.Token);
                    headers = await ReadHeaders(ssl, server.Deadline.Token);
                    await Reply(ssl, server.Deadline.Token);
                }
                else
                {
                    headers = await ReadHeaders(stream, server.Deadline.Token);
                    await Reply(stream, server.Deadline.Token);
                }
            });
            var client = Client(new HttpProxySettings(type, "127.0.0.1", server.Port, "", false),
                onRequest: request => Assert.That(request.LogHttpError, Is.False));
            var imageProxy = new MediaCoverProxy(client, null, null, new CacheManager());
            Assert.That(imageProxy.GetImageFromUrl(url), Is.EqualTo(Image));
            await serving;
            Assert.That(headers, Does.Contain("Host: " + host));
            if (type == ProxyType.Socks5)
            {
                Assert.That(proxyTarget, Is.EqualTo(host));
            }
            else
            {
                Assert.That(tls ? proxyTarget : headers, Does.StartWith(tls ? $"CONNECT {host}:443 " : $"GET {url} "));
            }
        }

        [Test]
        public async Task configured_proxy_bypass_still_uses_the_direct_metadata_route()
        {
            using var server = new Server();
            using var unusedProxy = new Server();
            var serving = Task.Run(async () =>
            {
                using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                await ReadHeaders(socket.GetStream(), server.Deadline.Token);
                await Reply(socket.GetStream(), server.Deadline.Token);
            });
            var client = Client(new HttpProxySettings(ProxyType.Http, "127.0.0.1", unusedProxy.Port, "", true));
            Assert.That(MediaCoverHttpRequest.Get(client, Config(server.Url), server.Url).ResponseData, Is.EqualTo(Image));
            await serving;
            Assert.That(unusedProxy.Listener.Pending(), Is.False);
        }

        [TestCase("http://192.168.1.1/private", "private_or_local_address")]
        [TestCase("http://169.254.169.254/latest", "private_or_local_address")]
        [TestCase("http://metadata.google.internal/latest", "cloud_metadata_host")]
        [TestCase("http://user:secret@example.invalid/", "userinfo_not_allowed")]
        public async Task proxy_redirects_cannot_escape_URL_or_cloud_metadata_checks(string target, string reason)
        {
            using var server = new Server();
            var serving = Task.Run(async () =>
            {
                using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                await ReadHeaders(socket.GetStream(), server.Deadline.Token);
                await Reply(socket.GetStream(), server.Deadline.Token, $"Location: {target}\r\n", body: false);
            });
            var error = Assert.Throws<UnsafeImageUrlException>(() => MediaCoverHttpRequest.Get(
                Client(new HttpProxySettings(ProxyType.Http, "127.0.0.1", server.Port, "", false)), null, "http://origin.invalid/cover"));
            await serving;
            Assert.That(error.Message, Is.EqualTo($"Blocked unsafe image redirect target '{NzbDrone.Common.Instrumentation.CleanseLogMessage.Cleanse(target)}': {reason}"));
            Assert.That(error.Message, Does.Contain(new Uri(target).Host));
            Assert.That(error.Message, Does.Not.Contain("user:secret"));
            Assert.That(server.Listener.Pending(), Is.False, "Rejected redirect must not make another request");
        }

        [Test]
        public async Task direct_DNS_is_resolved_once_and_checked_before_any_socket()
        {
            using var server = new Server();
            using var handler = Factory().CreateHandler(HttpProxySettings.DirectConnection, false, false, null, false, 12);
            var calls = 0;
            Factory().ApplyTargetPolicy(handler, MediaCoverUrlSafetyPolicy.CreateTargetPolicy(null),
                (_, _) => { calls++; return Task.FromResult(new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Loopback }); });
            using var http = new System.Net.Http.HttpClient(handler);
            var error = Assert.ThrowsAsync<HttpRequestException>(async () => await http.GetAsync($"http://test.invalid:{server.Port}/"));
            Assert.That(error.InnerException, Is.TypeOf<UnsafeImageUrlException>());
            Assert.That(error.InnerException.Message, Does.Contain("private_or_local_address"));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(server.Listener.Pending(), Is.False);
        }

        [Test]
        public async Task direct_socket_uses_the_validated_DNS_result_without_resolving_again()
        {
            using var server = new Server();
            var serving = Task.Run(async () =>
            {
                using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                var headers = await ReadHeaders(socket.GetStream(), server.Deadline.Token);
                await Reply(socket.GetStream(), server.Deadline.Token);
                return headers;
            });
            using var handler = Factory().CreateHandler(HttpProxySettings.DirectConnection, false, false, null, false, 12);
            var calls = 0;
            Factory().ApplyTargetPolicy(handler, MediaCoverUrlSafetyPolicy.CreateTargetPolicy(Config("http://test.invalid")),
                (_, _) => { calls++; return Task.FromResult(new[] { IPAddress.Loopback }); });
            using var http = new System.Net.Http.HttpClient(handler);
            var response = await http.GetByteArrayAsync($"http://test.invalid:{server.Port}/image", server.Deadline.Token);
            Assert.That(response, Is.EqualTo(Image));
            Assert.That(await serving, Does.Contain("Host: test.invalid:" + server.Port));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public async Task image_policy_cannot_reuse_an_unrestricted_keepalive_connection()
        {
            using var server = new Server();
            var requests = 0;
            var serving = Task.Run(async () =>
            {
                using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                var stream = socket.GetStream();
                try
                {
                    while (true)
                    {
                        await ReadHeaders(stream, server.Deadline.Token);
                        Interlocked.Increment(ref requests);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"), server.Deadline.Token);
                    }
                }
                catch (OperationCanceledException) when (server.Deadline.IsCancellationRequested)
                {
                }
                catch (EndOfStreamException)
                {
                }
            });
            try
            {
                var url = server.Url;
                var client = Client();
                Assert.That(client.Get(new HttpRequest(url)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                // Isolate the connection-pool boundary from the earlier URL check.
                var request = new HttpRequest(url)
                {
                    TargetPolicy = new HttpRequestTargetPolicy("fixture-image", _ => { },
                        MediaCoverUrlSafetyPolicy.CreateTargetPolicy(null).ValidateAddress)
                };
                var error = Assert.Throws<UnsafeImageUrlException>(() => client.Get(request));
                Assert.That(error.Message, Does.Contain("private_or_local_address"));
                Assert.That(requests, Is.EqualTo(1));
            }
            finally
            {
                server.Deadline.Cancel();
                try
                {
                    await serving;
                }
                catch (OperationCanceledException) when (server.Deadline.IsCancellationRequested)
                {
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DNS_failure_and_empty_answers_are_network_failures_without_fallback(bool empty)
        {
            var factory = new ResolvingFactory((_, _) => empty
                ? Task.FromResult(Array.Empty<IPAddress>())
                : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));
            var error = Assert.Throws<WebException>(() => MediaCoverHttpRequest.Get(Client(factory: factory), null, "http://test.invalid/"));
            Assert.That(error.Status, Is.EqualTo(WebExceptionStatus.NameResolutionFailure));
            Assert.That(error.Message, Is.EqualTo("Couldn't resolve the image host 'test.invalid'."));
            Assert.That(error.Message, Does.Not.Contain("Blocked unsafe"));
            Assert.That(factory.Resolved, Is.EqualTo(new[] { "test.invalid" }));
        }

        [Test]
        public void unresolved_DNS_obeys_the_request_token_without_waiting_for_the_native_resolver()
        {
            using var handler = Factory().CreateHandler(HttpProxySettings.DirectConnection, false, false, null, false, 12);
            var pending = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            Factory().ApplyTargetPolicy(handler, MediaCoverUrlSafetyPolicy.CreateTargetPolicy(null), (_, _) =>
            {
                cancellation.Cancel();
                return pending.Task;
            });
            using var http = new System.Net.Http.HttpClient(handler);
            Assert.CatchAsync<OperationCanceledException>(async () => await http.GetAsync("http://test.invalid/", cancellation.Token));
            pending.SetResult(Array.Empty<IPAddress>());
        }

        [Test]
        public async Task initial_IPv6_probe_falls_back_without_spending_the_whole_attempt_deadline()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var useV6 = typeof(ManagedSocketsHttpHandlerFactory).GetField("_useIPv6", flags);
            var resolvedV6 = typeof(ManagedSocketsHttpHandlerFactory).GetField("_hasResolvedIPv6Availability", flags);
            var oldUse = useV6.GetValue(null);
            var oldResolved = resolvedV6.GetValue(null);
            try
            {
                useV6.SetValue(null, true);
                resolvedV6.SetValue(null, false);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var families = new List<AddressFamily>();
                using var result = await Factory().ConnectWithFallback(async (family, token) =>
                {
                    families.Add(family);
                    if (family == AddressFamily.InterNetworkV6)
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }

                    return new MemoryStream(Image);
                }, deadline.Token);
                Assert.That(families, Is.EqualTo(new[] { AddressFamily.InterNetworkV6, AddressFamily.InterNetwork }));
                Assert.That(deadline.IsCancellationRequested, Is.False);
                Assert.That(result.Length, Is.EqualTo(Image.Length));
            }
            finally
            {
                useV6.SetValue(null, oldUse);
                resolvedV6.SetValue(null, oldResolved);
            }
        }

        [Test]
        public async Task redirect_and_response_body_share_the_attempt_cancellation_token()
        {
            using var server = new Server();
            using var caller = new CancellationTokenSource();
            var body = new Pipe();
            using var received = body.Reader.AsStream();
            using var destination = body.Writer.AsStream();
            var serving = Task.Run(async () =>
            {
                using (var first = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token))
                {
                    await ReadHeaders(first.GetStream(), server.Deadline.Token);
                    await Reply(first.GetStream(), server.Deadline.Token, "Location: /final\r\n", body: false);
                }

                using var last = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                await ReadHeaders(last.GetStream(), server.Deadline.Token);
                await last.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\na"), server.Deadline.Token);
                var one = new byte[1];
                return await last.GetStream().ReadAsync(one, server.Deadline.Token);
            });
            var tokens = new List<CancellationToken>();
            var client = Client(onRequest: request => tokens.Add(request.CancellationToken));
            var fetch = Task.Run(() => MediaCoverHttpRequest.Get(client, Config(server.Url), server.Url,
                responseStream: destination, cancellationToken: caller.Token));
            try
            {
                await received.ReadExactlyAsync(new byte[1], server.Deadline.Token);
            }
            finally
            {
                caller.Cancel();
            }

            Assert.CatchAsync<OperationCanceledException>(async () => await fetch);
            Assert.That(tokens.Count, Is.EqualTo(2));
            Assert.That(tokens[0].CanBeCanceled, Is.True);
            Assert.That(tokens[0], Is.Not.EqualTo(caller.Token));
            Assert.That(tokens[1], Is.EqualTo(tokens[0]), "Redirects must not replace the attempt deadline");
            Assert.That(tokens[1].IsCancellationRequested, Is.True);
            Assert.That(await serving, Is.Zero, "Cancellation must close the pending body read");
        }

        [Test]
        public async Task stalled_response_body_reports_the_cover_attempt_timeout()
        {
            using var server = new Server();
            using var body = new MemoryStream();
            var fetch = Task.Run(() => MediaCoverHttpRequest.Get(Client(), Config(server.Url), server.Url,
                responseStream: body, timeout: TimeSpan.FromSeconds(2)));

            using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
            var stream = socket.GetStream();
            await ReadHeaders(stream, server.Deadline.Token);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\na"u8.ToArray(), server.Deadline.Token);

            var error = Assert.ThrowsAsync<WebException>(async () => await fetch.WaitAsync(server.Deadline.Token));
            Assert.That(body.ToArray(), Is.EqualTo(new byte[] { (byte)'a' }));
            Assert.That(error.Status, Is.EqualTo(WebExceptionStatus.Timeout));
        }

        [Test]
        public void attempt_deadline_expires_without_caller_cancellation()
        {
            var client = InterfaceProxy.Proxy<IHttpClient>((_, args) =>
            {
                var token = ((HttpRequest)args[0]).CancellationToken;
                Assert.That(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)), Is.True, "Attempt deadline never fired");
                token.ThrowIfCancellationRequested();
                return null;
            });
            var error = Assert.Throws<WebException>(() => MediaCoverHttpRequest.Get(client, null,
                "https://cover.invalid/image", timeout: TimeSpan.FromMilliseconds(25)));
            Assert.That(error.Status, Is.EqualTo(WebExceptionStatus.Timeout));
        }

        [Test]
        [Repeat(8)]
        public async Task redirects_refresh_Goodreads_headers_and_allow_five_hops()
        {
            using var server = new Server();
            var requests = new List<string>();
            var serving = Task.Run(async () =>
            {
                for (var hop = 0; hop < 6; hop++)
                {
                    using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                    requests.Add(await ReadHeaders(socket.GetStream(), server.Deadline.Token));
                    var next = hop % 2 == 0 ? "http://i.gr-assets.com/image" : "http://cover.invalid/image";
                    await Reply(socket.GetStream(), server.Deadline.Token, hop < 5 ? $"Location: {next}\r\n" : "", body: hop == 5);
                }
            });
            var client = Client(new HttpProxySettings(ProxyType.Http, "127.0.0.1", server.Port, "", false));
            Assert.That(MediaCoverHttpRequest.Get(client, null, "http://cover.invalid/image", userAgent: "Mozilla/5.0 Fixture").ResponseData, Is.EqualTo(Image));
            await serving;
            for (var i = 0; i < requests.Count; i++)
            {
                Assert.That(requests[i].Contains("Referer: https://www.goodreads.com/"), Is.EqualTo(i % 2 == 1), requests[i]);
                Assert.That(requests[i], Does.Not.Contain("Range:"));
                Assert.That(requests[i].Contains("Sec-Fetch-"), Is.EqualTo(i % 2 == 0), requests[i]);
                Assert.That(requests[i], Does.Contain(i % 2 == 1 ? "User-Agent: Dalvik/" : "User-Agent: Mozilla/5.0 Fixture"));
                Assert.That(requests[i].Split("\r\n").Where(line => line.StartsWith("Accept:")),
                    Is.EqualTo(new[] { "Accept: image/webp, image/apng, image/svg+xml, image/*, */*; q=0.8" }),
                    "Values must not accumulate even when the dispatcher combines them onto one line");
            }

            var goodreadsAgents = requests.Where((_, index) => index % 2 == 1)
                .Select(request => request.Split("\r\n").Single(line => line.StartsWith("User-Agent:")));
            Assert.That(goodreadsAgents.Distinct().Count(), Is.EqualTo(1), "One Android identity per fetch, including redirects");
        }

        [TestCase("http://www.goodreads.com/image", true)]
        [TestCase("http://images.gr-assets.com/image", true)]
        [TestCase("http://goodreads.com/image", true)]
        [TestCase("http://gr-assets.com/image", true)]
        [TestCase("http://IMAGES.GR-ASSETS.COM./image", true)]
        [TestCase("http://goodreads.com.example/image", false)]
        [TestCase("http://cdn.example/gr-assets.com/image", false)]
        public async Task shared_builder_selects_Android_headers_only_for_Goodreads_hosts(string url, bool goodreads)
        {
            using var server = new Server();
            var serving = Task.Run(async () =>
            {
                using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                var headers = await ReadHeaders(socket.GetStream(), server.Deadline.Token);
                await Reply(socket.GetStream(), server.Deadline.Token);
                return headers;
            });
            var client = Client(new HttpProxySettings(ProxyType.Http, "127.0.0.1", server.Port, "", false));
            Assert.That(MediaCoverHttpRequest.Get(client, null, url, userAgent: "Mozilla/5.0 Fixture").ResponseData, Is.EqualTo(Image));
            var received = await serving;
            Assert.That(received, Does.Contain(goodreads ? "User-Agent: Dalvik/" : "User-Agent: Mozilla/5.0 Fixture"));
            Assert.That(received.Contains("Sec-Fetch-"), Is.EqualTo(!goodreads));
            Assert.That(received.Contains("Referer: https://www.goodreads.com/"), Is.EqualTo(goodreads));
        }

        [Test]
        public async Task private_DNS_answer_after_redirect_retains_redirect_wording()
        {
            using var server = new Server();
            var serving = Task.Run(async () =>
            {
                using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                await ReadHeaders(socket.GetStream(), server.Deadline.Token);
                await Reply(socket.GetStream(), server.Deadline.Token, "Location: http://private-cover.invalid/image\r\n", body: false);
            });
            var factory = new ResolvingFactory((_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
            var error = Assert.Throws<UnsafeImageUrlException>(() => MediaCoverHttpRequest.Get(
                Client(factory: factory), Config(server.Url), server.Url));
            await serving;
            Assert.That(error.Message, Is.EqualTo("Blocked unsafe image redirect target 'http://private-cover.invalid/image': private_or_local_address"));
            Assert.That(server.Listener.Pending(), Is.False);
        }

        [Test]
        public void legacy_SOCKS4_retains_its_local_address_check()
        {
            using var server = new Server();
            using var handler = Factory().CreateHandler(new HttpProxySettings(ProxyType.Socks4, "127.0.0.1", server.Port, "", false),
                false, false, null, false, 12);
            var calls = 0;
            Factory().ApplyTargetPolicy(handler, MediaCoverUrlSafetyPolicy.CreateTargetPolicy(null),
                (_, _) => { calls++; return Task.FromResult(new[] { IPAddress.Loopback }); });
            using var http = new System.Net.Http.HttpClient(handler);
            var error = Assert.ThrowsAsync<HttpRequestException>(async () => await http.GetAsync("http://origin.invalid/image"));
            Assert.That(error.InnerException.Message, Does.Contain("private_or_local_address"));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(server.Listener.Pending(), Is.False);
        }

        [Test]
        public void caller_cancellation_during_the_lookup_is_not_a_failed_lookup()
        {
            var pending = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var factory = new ResolvingFactory((_, _) =>
            {
                cancellation.Cancel();
                return pending.Task;
            });
            Assert.Catch<OperationCanceledException>(() => MediaCoverHttpRequest.Get(Client(factory: factory), null,
                "http://slow-lookup.invalid/", cancellationToken: cancellation.Token));
            pending.SetResult(Array.Empty<IPAddress>());
        }

        [Test]
        public void caller_cancellation_is_not_labelled_as_unsafe_or_timeout()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Catch<OperationCanceledException>(() => MediaCoverHttpRequest.Get(Client(), null,
                "http://cancelled.invalid/", cancellationToken: cancellation.Token));
        }
    }
}
