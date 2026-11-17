using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.MediaCover;
using HttpClient = NzbDrone.Common.Http.HttpClient;
using NetClient = System.Net.Http.HttpClient;

namespace Chaptarr.Core.Test.MediaCover
{
    public partial class MediaCoverTransportFixture
    {
        private sealed class ResolvingFactory : ICreateManagedSocketsHttpHandler
        {
            private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
            private readonly ManagedSocketsHttpHandlerFactory _factory;
            public readonly List<string> Resolved = new();

            public ResolvingFactory(Func<string, CancellationToken, Task<IPAddress[]>> resolve, Logger logger = null)
            {
                _resolve = resolve;
                _factory = Factory(logger);
            }

            public SocketsHttpHandler CreateHandler(HttpProxySettings settings, bool redirects, bool cookies,
                ICredentials credentials, bool preAuthenticate, int maxConnectionsPerServer, HttpRequestTargetPolicy targetPolicy = null)
            {
                var handler = _factory.CreateHandler(settings, redirects, cookies, credentials, preAuthenticate, maxConnectionsPerServer);
                if (targetPolicy != null)
                {
                    _factory.ApplyTargetPolicy(handler, targetPolicy, (host, token) =>
                    {
                        Resolved.Add(host);
                        return _resolve(host, token);
                    });
                }

                return handler;
            }
        }

        [TestCase(true, "0.0.0.0", false)]
        [TestCase(true, "0.0.0.0", true)]
        [TestCase(false, "127.0.0.1", false)]
        [TestCase(false, "0.0.0.0", false)]
        [TestCase(false, "0.0.0.0", true)]
        [TestCase(true, null, false)]
        [TestCase(false, null, false)]
        public async Task refresh_skips_unsafe_DNS_quietly_but_warns_on_lookup_failure_and_tries_next_cover(bool authorRefresh, string address, bool redirect)
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}|${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
            logs.Configuration = logging;
            const string blocked = "http://blocked-cover.invalid/cover.jpg";
            using var server = new Server();
            var received = new List<string>();
            var serving = Task.Run(async () =>
            {
                for (var i = 0; i < 1 + (redirect ? 1 : 0); i++)
                {
                    using var socket = await server.Listener.AcceptTcpClientAsync(server.Deadline.Token);
                    received.Add(await ReadHeaders(socket.GetStream(), server.Deadline.Token));
                    await Reply(socket.GetStream(), server.Deadline.Token,
                        redirect && i == 0 ? $"Location: {blocked}\r\n" : "", body: !redirect || i != 0);
                }
            });
            var factory = new ResolvingFactory((_, _) => Task.FromResult(address == null
                ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(address) }));
            var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.Name switch
            {
                "get_MetadataServerUrl" => server.Url,
                "get_CoverDownloadScope" => CoverDownloadScope.AllBooks,
                _ => throw new AssertionException("Unexpected setting: " + method.Name)
            });
            var saved = new List<byte[]>();
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IDiskProvider.FileExists):
                    case nameof(IDiskProvider.FolderExists):
                        return false;
                    case nameof(IDiskProvider.GetFiles):
                        return Array.Empty<string>();
                    case nameof(IDiskProvider.WriteFileAtomically):
                        ((Action<string>)args[1])((string)args[0]);
                        return null;
                    case nameof(IDiskProvider.SaveStream):
                        using (var bytes = new MemoryStream())
                        {
                            ((Stream)args[0]).CopyTo(bytes);
                            saved.Add(bytes.ToArray());
                        }
                        return null;
                    case nameof(IDiskProvider.WriteAllText):
                    case nameof(IDiskProvider.EnsureFolder):
                    case nameof(IDiskProvider.MoveFile):
                    case nameof(IDiskProvider.FileSetLastWriteTime):
                        return null;
                    default:
                        throw new AssertionException("Unexpected disk call: " + method.Name);
                }
            });
            var appFolder = InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => Path.GetTempPath());
            var deferred = InterfaceProxy.Proxy<IDeferredCoverService>((_, _) => false);
            var subject = new MediaCoverService(null, null, null, Client(factory: factory), disk, appFolder, config, null, null, deferred, logs.GetLogger("RefreshCoverFixture"), imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
            var author = new Author { Id = 123, Name = "Test author", Images = new()
            {
                new(MediaCoverTypes.Poster, redirect ? server.Url + "?redirect" : blocked), new(MediaCoverTypes.Banner, server.Url)
            } };
            if (authorRefresh)
            {
                subject.EnsureAuthorCovers(author);
            }
            else
            {
                var book = new Book { Id = 456, Title = "Test book", Author = author };
                book.Editions = new List<Edition> { new() { Id = 789, BookId = book.Id, Book = book, Monitored = true, Images = new()
                {
                    new(MediaCoverTypes.Cover, redirect ? server.Url + "?redirect" : blocked), new(MediaCoverTypes.Cover, server.Url)
                } } };
                subject.EnsureBookCovers(book);
            }

            await serving;
            Assert.That(received, Has.Count.EqualTo(1 + (redirect ? 1 : 0)));
            Assert.That(received, Has.All.StartWith($"GET {new Uri(server.Url).PathAndQuery}"));
            Assert.That(received.Last(), Does.StartWith($"GET {new Uri(server.Url).PathAndQuery} HTTP/1.1"));
            Assert.That(factory.Resolved, Is.EqualTo(new[] { "blocked-cover.invalid" }));
            Assert.That(saved, Has.Count.EqualTo(1), "The next cover must reach the normal writer");
            Assert.That(saved.Single(), Is.EqualTo(Image));
            Assert.That(messages.Logs.Where(line => line.StartsWith("Error|")), Is.Empty);
            Assert.That(messages.Logs.Count(line => line.StartsWith("Warn|")), Is.EqualTo(address == null ? 1 : 0));
            if (address != null)
            {
                var reason = $"Blocked unsafe image {(redirect ? "redirect target" : "URL")} '{blocked}': private_or_local_address";
                var skip = authorRefresh
                    ? $"Debug|Skipping unsafe author cover for Test author (ID: 123): {reason}"
                    : $"Debug|Skipping unsafe monitored-edition cover for book Test book: {reason}";
                Assert.That(messages.Logs.Count(line => line == skip), Is.EqualTo(1));
            }
            else
            {
                Assert.That(messages.Logs.Single(line => line.StartsWith("Warn|")), Does.Contain("Couldn't resolve the image host"));
            }
        }

        private static (HttpClient Client, IConfigService Config) ModeClient(ProxyMode mode, int proxyPort,
            string metadataUrl = null, string bypass = "", ResolvingFactory sockets = null)
        {
            var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.Name switch
            {
                "get_ProxyMode" => mode,
                "get_GlobalProxyId" => (int?)7,
                "get_ProxyBypassFilter" => bypass,
                "get_ProxyBypassLocalAddresses" => false,
                "get_MetadataServerUrl" => metadataUrl,
                _ => throw new AssertionException("Unexpected configuration read: " + method.Name)
            });
            var proxies = InterfaceProxy.Proxy<IProxyService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IProxyService.Find)));
                Assert.That(args[0], Is.EqualTo(7));
                return new ProxyDefinition { Id = 7, Name = "Fixture proxy", ProxyType = ProxyType.Http, Hostname = "127.0.0.1", Port = proxyPort };
            });
            var provider = new StandardProxySettingsProvider(config, new GlobalProxySettingsResolver(config, proxies, Logger));
            return (Client(provider: provider, factory: sockets), config);
        }

        private sealed class DefaultProxyScope : IDisposable
        {
            private readonly IWebProxy _old = NetClient.DefaultProxy;
            public DefaultProxyScope() => NetClient.DefaultProxy = new WebProxy();
            public void Dispose() => NetClient.DefaultProxy = _old;
        }

        [TestCase(ProxyMode.Disabled)]
        [TestCase(ProxyMode.IndexerOnly)]
        [TestCase(ProxyMode.ProxyEverything)]
        public async Task real_setting_routes_covers_and_honors_non_loopback_bypass(ProxyMode mode)
        {
            using var environment = new DefaultProxyScope();
            using var origin = new Server();
            using var proxy = new Server();
            var url = $"http://cover.example-cdn.test:{origin.Port}/cover.png";
            var sockets = new ResolvingFactory((_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
            // The hostname is non-loopback. The configured metadata trust permits
            // the injected loopback test endpoint, without binding a public IP.
            var setup = ModeClient(mode, proxy.Port, url, "*.example-cdn.test", sockets);
            var serving = Task.Run(async () =>
            {
                using var socket = await origin.Listener.AcceptTcpClientAsync(origin.Deadline.Token);
                var headers = await ReadHeaders(socket.GetStream(), origin.Deadline.Token);
                await Reply(socket.GetStream(), origin.Deadline.Token);
                return headers;
            });
            Assert.That(MediaCoverHttpRequest.Get(setup.Client, setup.Config, url).ResponseData, Is.EqualTo(Image));
            Assert.That(await serving, Does.Contain("Host: cover.example-cdn.test:" + origin.Port));
            Assert.That(sockets.Resolved, Is.EqualTo(new[] { "cover.example-cdn.test" }));
            Assert.That(proxy.Listener.Pending(), Is.False);

            // Removing metadata trust must reject that same direct private answer.
            var untrusted = ModeClient(mode, proxy.Port, bypass: "*.example-cdn.test", sockets: sockets);
            var error = Assert.Throws<UnsafeImageUrlException>(() => MediaCoverHttpRequest.Get(untrusted.Client, untrusted.Config, url));
            Assert.That(error.Message, Does.StartWith("Blocked unsafe image URL '"));
            Assert.That(error.Message, Does.Contain("private_or_local_address").And.Contains(url));
            Assert.That(origin.Listener.Pending(), Is.False);
            Assert.That(proxy.Listener.Pending(), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task real_proxy_everything_never_falls_back_to_origin(bool proxyDown)
        {
            using var environment = new DefaultProxyScope();
            using var origin = new Server();
            using var proxy = new Server();
            var port = proxy.Port;
            var url = $"http://cover.example-cdn.test:{origin.Port}/cover.png";
            var sockets = new ResolvingFactory((_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
            var setup = ModeClient(ProxyMode.ProxyEverything, port, url, sockets: sockets);
            if (proxyDown)
            {
                proxy.Listener.Stop();
                Assert.Catch(() => MediaCoverHttpRequest.Get(setup.Client, setup.Config, url));
            }
            else
            {
                var serving = Task.Run(async () =>
                {
                    using var socket = await proxy.Listener.AcceptTcpClientAsync(proxy.Deadline.Token);
                    var headers = await ReadHeaders(socket.GetStream(), proxy.Deadline.Token);
                    await Reply(socket.GetStream(), proxy.Deadline.Token);
                    return headers;
                });
                Assert.That(MediaCoverHttpRequest.Get(setup.Client, setup.Config, url).ResponseData, Is.EqualTo(Image));
                Assert.That(await serving, Does.StartWith("GET " + url + " "));
            }

            Assert.That(sockets.Resolved, Is.Empty, "Proxy route must not resolve the origin locally");
            Assert.That(origin.Listener.Pending(), Is.False, "Even proxy failure must never trigger a direct request");
        }

        [Test]
        public async Task direct_HTTPS_keeps_hostname_for_SNI_and_Host()
        {
            using var environment = new DefaultProxyScope();
            using var origin = new Server();
            const string host = "direct-cover.invalid";
            var url = $"https://{host}:{origin.Port}/cover.png";
            using var key = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            string sni = null;
            var serving = Task.Run(async () =>
            {
                using var socket = await origin.Listener.AcceptTcpClientAsync(origin.Deadline.Token);
                using var tls = new SslStream(socket.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { sni = name; return certificate; }
                }, origin.Deadline.Token);
                var headers = await ReadHeaders(tls, origin.Deadline.Token);
                await Reply(tls, origin.Deadline.Token);
                return headers;
            });
            var sockets = new ResolvingFactory((_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
            var setup = ModeClient(ProxyMode.Disabled, 1, url, sockets: sockets);
            Assert.That(MediaCoverHttpRequest.Get(setup.Client, setup.Config, url).ResponseData, Is.EqualTo(Image));
            Assert.That(await serving, Does.Contain("Host: " + host + ":" + origin.Port));
            Assert.That(sni, Is.EqualTo(host));
            Assert.That(sockets.Resolved, Is.EqualTo(new[] { host }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DNS_deadline_has_phase_diagnostic_but_caller_cancellation_stays_cancellation(bool callerCancel)
        {
            using var logFactory = new LogFactory();
            var logs = new MemoryTarget { Layout = "${level}|${message}" };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logs);
            logFactory.Configuration = config;
            var pending = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sockets = new ResolvingFactory((_, _) => pending.Task);
            using var caller = new CancellationTokenSource();
            if (callerCancel)
            {
                caller.CancelAfter(100);
            }

            try
            {
                var error = Assert.Catch(() => MediaCoverHttpRequest.Get(Client(factory: sockets, logger: logFactory.GetLogger("CoverDNS")), null, "http://dns-deadline.invalid/image",
                    cancellationToken: caller.Token, timeout: TimeSpan.FromMilliseconds(callerCancel ? 5000 : 200)));
                if (callerCancel)
                {
                    Assert.That(error, Is.InstanceOf<OperationCanceledException>());
                }
                else
                {
                    Assert.That(error, Is.TypeOf<WebException>());
                    Assert.That(((WebException)error).Status, Is.EqualTo(WebExceptionStatus.NameResolutionFailure));
                    Assert.That(error.Message, Is.EqualTo("Couldn't resolve the image host 'dns-deadline.invalid'."));
                }

                Assert.That(logs.Logs.Count(message => message.StartsWith("Debug|Couldn't resolve the image host 'dns-deadline.invalid' after ") &&
                    message.Contains(" ms (") && message.Contains("CanceledException")), Is.EqualTo(1));
                Assert.That(string.Join("\n", logs.Logs), Does.Not.Contain("Blocked unsafe"));
            }
            finally
            {
                pending.TrySetResult(Array.Empty<IPAddress>());
            }
        }

        [Test]
        public async Task indexer_only_uses_environment_proxy_in_fresh_process()
        {
            const string marker = "CHAPTARR_COVER_PROXY_TEST";
            var childPorts = Environment.GetEnvironmentVariable(marker);
            if (childPorts != null)
            {
                var ports = childPorts.Split(',').Select(int.Parse).ToArray();
                var setup = ModeClient(ProxyMode.IndexerOnly, ports[1]);
                var url = $"http://environment-cover.invalid:{ports[0]}/cover.png";
                Assert.That(MediaCoverHttpRequest.Get(setup.Client, setup.Config, url).ResponseData, Is.EqualTo(Image));
                return;
            }

            using var origin = new Server();
            using var appProxy = new Server();
            using var environmentProxy = new Server(TimeSpan.FromSeconds(30));
            var serving = Task.Run(async () =>
            {
                using var socket = await environmentProxy.Listener.AcceptTcpClientAsync(environmentProxy.Deadline.Token);
                var headers = await ReadHeaders(socket.GetStream(), environmentProxy.Deadline.Token);
                await Reply(socket.GetStream(), environmentProxy.Deadline.Token);
                return headers;
            });
            var directory = Path.Combine(Path.GetTempPath(), "cover-env-proxy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var process = new Process();
            var started = false;
            try
            {
                var settings = Path.Combine(directory, "tests.runsettings");
                var selection = "FullyQualifiedName=" + GetType().FullName + "." + nameof(indexer_only_uses_environment_proxy_in_fresh_process);
                new XDocument(new XElement("RunSettings",
                    new XElement("RunConfiguration", new XElement("MaxCpuCount", 1), new XElement("TestCaseFilter", selection)),
                    new XElement("NUnit", new XElement("NumberOfTestWorkers", 0), new XElement("UseNUnitFilter", true),
                        new XElement("AssemblySelectLimit", 1), new XElement("WorkDirectory", directory)))).Save(settings);
                process.StartInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
                foreach (var arg in new[] { "vstest", GetType().Assembly.Location, "--Settings:" + settings,
                    "--ResultsDirectory:" + directory, "--Logger:trx;LogFileName=result.trx" })
                {
                    process.StartInfo.ArgumentList.Add(arg);
                }

                foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" })
                {
                    process.StartInfo.Environment.Remove(key);
                    process.StartInfo.Environment.Remove(key.ToLowerInvariant());
                }

                process.StartInfo.Environment["HTTP_PROXY"] = "http://127.0.0.1:" + environmentProxy.Port;
                process.StartInfo.Environment[marker] = origin.Port + "," + appProxy.Port;
                started = process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(environmentProxy.Deadline.Token);
                Assert.That(process.ExitCode, Is.Zero, await output + await errors);
                XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                var results = XDocument.Load(Path.Combine(directory, "result.trx")).Descendants(ns + "UnitTestResult").ToArray();
                Assert.That(results, Has.Length.EqualTo(1), "Child must run exactly this test, never the whole assembly");
                Assert.That((string)results[0].Attribute("outcome"), Is.EqualTo("Passed"));
                Assert.That(await serving, Does.StartWith($"GET http://environment-cover.invalid:{origin.Port}/cover.png "));
                Assert.That(origin.Listener.Pending(), Is.False);
                Assert.That(appProxy.Listener.Pending(), Is.False);
            }
            finally
            {
                if (started && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                environmentProxy.Deadline.Cancel();
                try { await serving; }
                catch (OperationCanceledException) when (environmentProxy.Deadline.IsCancellationRequested) { }
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
