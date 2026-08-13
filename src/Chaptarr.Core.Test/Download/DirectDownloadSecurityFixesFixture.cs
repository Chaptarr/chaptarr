using System;
using System.IO;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.DirectDownload;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectDownloadSecurityFixesFixture
    {
        // ── Blocker 4: downloadId path-segment validation ──

        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("   ", false)]
        [TestCase("../etc/passwd", false)]
        [TestCase("..", false)]
        [TestCase(".", false)]
        [TestCase("foo/bar", false)]
        [TestCase("foo\\bar", false)]
        [TestCase("foo\0bar", false)]
        [TestCase("/absolute", false)]
        [TestCase("\\unc", false)]
        [TestCase("normal-download-id-123", true)]
        [TestCase("ABCDEF1234567890", true)]
        [TestCase("a.b.c", true)]
        [TestCase("with-dashes_and_underscores", true)]
        public void is_valid_path_segment_accepts_safe_ids_and_rejects_unsafe(string id, bool expected)
        {
            Assert.That(DirectDownloadClient.IsValidPathSegment(id), Is.EqualTo(expected));
        }

        [Test]
        public void download_rejects_traversal_download_id_before_creating_state()
        {
            using var scenario = new SecurityFixesScenario();
            var client = scenario.BuildClient();

            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "../../etc/passwd",
                    Title = "Evil Book",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = "https://downloads.example/evil.epub",
                    Container = "epub",
                    Size = 100
                }
            };

            var ex = Assert.ThrowsAsync<ReleaseDownloadException>(
                async () => await client.Download(remoteBook, indexer: null));

            Assert.That(ex.Message, Does.Contain("invalid characters"));
            Assert.That(client.GetItems(), Is.Empty, "No state should be created for traversal ID");
        }

        [Test]
        public void download_rejects_dot_dot_download_id()
        {
            using var scenario = new SecurityFixesScenario();
            var client = scenario.BuildClient();

            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "..",
                    Title = "Evil Book",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = "https://downloads.example/evil.epub",
                    Container = "epub",
                    Size = 100
                }
            };

            var ex = Assert.ThrowsAsync<ReleaseDownloadException>(
                async () => await client.Download(remoteBook, indexer: null));

            Assert.That(ex.Message, Does.Contain("invalid characters"));
        }

        [Test]
        public void download_rejects_backslash_traversal_id()
        {
            using var scenario = new SecurityFixesScenario();
            var client = scenario.BuildClient();

            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "..\\..\\windows\\system32",
                    Title = "Evil Book",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = "https://downloads.example/evil.epub",
                    Container = "epub",
                    Size = 100
                }
            };

            var ex = Assert.ThrowsAsync<ReleaseDownloadException>(
                async () => await client.Download(remoteBook, indexer: null));

            Assert.That(ex.Message, Does.Contain("invalid characters"));
        }

        // ── Blocker 4: staging containment defense-in-depth ──

        [Test]
        public void state_store_get_download_directory_rejects_path_traversal()
        {
            var store = new DirectDownloadClientStateStore(
                new TestDiskProvider(), LogManager.GetCurrentClassLogger());

            var stagingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-containment-test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingFolder);

            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => store.GetDownloadDirectory(stagingFolder, 1, "../../escape"));
            }
            finally
            {
                if (Directory.Exists(stagingFolder))
                {
                    Directory.Delete(stagingFolder, recursive: true);
                }
            }
        }

        [Test]
        public void state_store_get_download_directory_allows_normal_ids()
        {
            var store = new DirectDownloadClientStateStore(
                new TestDiskProvider(), LogManager.GetCurrentClassLogger());

            var stagingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-containment-test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingFolder);

            try
            {
                var dir = store.GetDownloadDirectory(stagingFolder, 1, "normal-id-123");
                Assert.That(dir, Does.Contain("normal-id-123"));
                Assert.That(dir, Does.StartWith(stagingFolder));
            }
            finally
            {
                if (Directory.Exists(stagingFolder))
                {
                    Directory.Delete(stagingFolder, recursive: true);
                }
            }
        }

        // ── Blocker 5: API key redaction ──

        [TestCase("https://example.com/api?key=SECRET123&other=val", "https://example.com/api?key=[redacted]&other=val")]
        [TestCase("https://example.com/api?other=val&key=SECRET123", "https://example.com/api?other=val&key=[redacted]")]
        [TestCase("https://example.com/api?key=SECRET123", "https://example.com/api?key=[redacted]")]
        [TestCase("https://example.com/api?md5=abc&key=MY-KEY", "https://example.com/api?md5=abc&key=[redacted]")]
        [TestCase("no key here", "no key here")]
        [TestCase(null, null)]
        [TestCase("", "")]
        public void redact_api_key_from_url_redacts_key_parameter(string input, string expected)
        {
            Assert.That(DirectDownloadGrabUrlResolver.RedactApiKeyFromUrl(input), Is.EqualTo(expected));
        }

        [Test]
        public void redact_preserves_url_structure_when_key_is_present()
        {
            var url = "https://catalog.example/dyn/api/fast_download.json?md5=abc123&key=SUPER-SECRET&path_index=0";
            var redacted = DirectDownloadGrabUrlResolver.RedactApiKeyFromUrl(url);

            Assert.That(redacted, Does.Contain("md5=abc123"));
            Assert.That(redacted, Does.Contain("key=[redacted]"));
            Assert.That(redacted, Does.Contain("path_index=0"));
            Assert.That(redacted, Does.Not.Contain("SUPER-SECRET"));
        }

        [Test]
        public void failure_message_does_not_leak_api_key_in_state()
        {
            using var scenario = new SecurityFixesScenario();
            scenario.Transport.AddRoute(
                url => url.Contains("/dyn/api/fast_download.json", StringComparison.OrdinalIgnoreCase) &&
                       url.Contains("key=SECRET-KEY-LEAK-TEST", StringComparison.OrdinalIgnoreCase),
                request =>
                {
                    throw new System.Net.WebException(
                        $"Connection to https://catalog.example/dyn/api/fast_download.json?key=SECRET-KEY-LEAK-TEST failed",
                        System.Net.WebExceptionStatus.ConnectFailure);
                });

            var resolver = new DirectDownloadGrabUrlResolver(scenario.Transport.CreateClient());

            var result = resolver.TryResolveCatalogGrabAsync_ThroughPublicApi(
                "https://catalog.example/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "SECRET-KEY-LEAK-TEST").GetAwaiter().GetResult();

            Assert.That(result.Outcome, Is.EqualTo(GrabResolutionOutcome.Unavailable));
            Assert.That(result.Reason, Does.Not.Contain("SECRET-KEY-LEAK-TEST"), "API key must not leak into GrabResolution reason");
        }

        // ── Blocker 1: InternalDirectClientProvider wires browser resolver ──

        [Test]
        public void internal_direct_client_provider_passes_browser_resolver_through()
        {
            var stubResolver = new StubBrowserResolver();
            var provider = new TestableInternalDirectClientProvider(stubResolver);

            var client = provider.GetClient();

            Assert.That(client, Is.Not.Null);
            Assert.That(clientBrowserResolver(client), Is.SameAs(stubResolver));
        }

        [Test]
        public void internal_direct_client_provider_gracefully_handles_null_browser_resolver()
        {
            var provider = new TestableInternalDirectClientProvider(null);

            var client = provider.GetClient();

            Assert.That(client, Is.Not.Null);
        }

        // ── Helpers ──

        private static IBrowserDownloadResolver clientBrowserResolver(DirectDownloadClient client)
        {
            var field = typeof(DirectDownloadClient).GetField("_browserResolver",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (IBrowserDownloadResolver)field?.GetValue(client);
        }

        private sealed class SecurityFixesScenario : IDisposable
        {
            public SecurityFixesScenario()
            {
                StagingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-security-fixes", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(StagingFolder);
                Transport = new Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp();
            }

            public string StagingFolder { get; }
            public Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp Transport { get; }

            public DirectDownloadClient BuildClient()
            {
                return new DirectDownloadClient(
                    Transport.CreateClient(),
                    new TestDiskProvider(),
                    null,
                    LogManager.GetCurrentClassLogger())
                {
                    Definition = new DownloadClientDefinition
                    {
                        Id = 42,
                        Name = "Direct Download",
                        Protocol = DownloadProtocol.Direct,
                        Settings = new DirectDownloadClientSettings { StagingFolder = StagingFolder }
                    }
                };
            }

            public void Dispose()
            {
                if (Directory.Exists(StagingFolder))
                {
                    Directory.Delete(StagingFolder, recursive: true);
                }
            }
        }

        private sealed class StubBrowserResolver : IBrowserDownloadResolver
        {
            public Task<bool> IsAvailableAsync() => Task.FromResult(true);
            public Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl) => Task.FromResult<string>(null);
        }

        private sealed class TestableInternalDirectClientProvider
        {
            private readonly IBrowserDownloadResolver _browserResolver;

            public TestableInternalDirectClientProvider(IBrowserDownloadResolver browserResolver)
            {
                _browserResolver = browserResolver;
            }

            public DirectDownloadClient GetClient()
            {
                var stagingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-internal-test", Guid.NewGuid().ToString("N"));
                var http = new Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp();
                var grabResolver = new DirectDownloadGrabUrlResolver(http.CreateClient(), _browserResolver);
                return new DirectDownloadClient(
                    http.CreateClient(),
                    new TestDiskProvider(),
                    null,
                    LogManager.GetCurrentClassLogger(),
                    grabResolver,
                    browserResolver: _browserResolver)
                {
                    Definition = new DownloadClientDefinition
                    {
                        Id = -1,
                        Name = "Direct Download",
                        ImplementationName = nameof(DirectDownloadClient),
                        Enable = true,
                        Protocol = DownloadProtocol.Direct,
                        RemoveCompletedDownloads = true,
                        RemoveFailedDownloads = true,
                        Settings = new DirectDownloadClientSettings { StagingFolder = stagingFolder }
                    }
                };
            }
        }

        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider() : base(new System.IO.Abstractions.FileSystem()) { }
            public override long? GetAvailableSpace(string path) => null;
            public override void InheritFolderPermissions(string filename) { }
            public override void SetEveryonePermissions(string filename) { }
            public override void SetFilePermissions(string path, string mask, string group) { }
            public override void SetPermissions(string path, string mask, string group) { }
            public override void CopyPermissions(string sourcePath, string targetPath) { }
            public override bool TryCreateHardLink(string source, string destination) => false;
            public override long? GetTotalSize(string path) => null;
        }
    }

    internal static class GrabUrlResolverTestExtensions
    {
        public static async Task<GrabResolution> TryResolveCatalogGrabAsync_ThroughPublicApi(
            this DirectDownloadGrabUrlResolver resolver, string url, string apiKey)
        {
            return await resolver.TryResolveGrabAsync(url, apiKey, "CatalogPage");
        }
    }
}
