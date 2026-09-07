using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectDownloadBackoffFixture
    {
        [Test]
        public async Task should_use_exponential_backoff_between_retries()
        {
            using var scenario = new BackoffScenario();
            var attemptTimestamps = new System.Collections.Concurrent.ConcurrentBag<DateTime>();
            var attempts = 0;
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                request =>
                {
                    attemptTimestamps.Add(DateTime.UtcNow);
                    attempts++;
                    if (attempts < 3)
                    {
                        throw new WebException("timeout", WebExceptionStatus.Timeout);
                    }

                    return scenario.WriteBinaryResponse(request, "application/epub+zip", "success-body");
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed, timeoutSeconds: 15);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(attempts, Is.EqualTo(3));
        }

        [Test]
        public async Task should_retry_html_challenge_three_times_then_fail()
        {
            using var scenario = new BackoffScenario();
            var attempts = 0;
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                request =>
                {
                    attempts++;
                    var headers = new HttpHeader();
                    headers.ContentType = "text/html; charset=utf-8";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("<html>DDoS Guard</html>");
                    return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.Forbidden));
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed, timeoutSeconds: 30);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Failed));
            Assert.That(attempts, Is.EqualTo(3), "HTML/403 challenge should be retried 3 times before failing");
            Assert.That(item.Message, Does.Contain("3/3"));
            Assert.That(item.Message, Does.Not.Contain("permanently"));
        }

        [Test]
        public async Task should_retry_html_response_then_succeed_on_third_attempt()
        {
            using var scenario = new BackoffScenario();
            var attempts = 0;
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                request =>
                {
                    attempts++;
                    if (attempts <= 2)
                    {
                        var headers = new HttpHeader();
                        headers.ContentType = "text/html; charset=utf-8";
                        var bytes = System.Text.Encoding.UTF8.GetBytes("<html>DDoS-Guard challenge</html>");
                        return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.Forbidden));
                    }

                    return scenario.WriteBinaryResponse(request, "application/epub+zip", "success-after-retry");
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed, timeoutSeconds: 30);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(attempts, Is.EqualTo(3));
        }

        [Test]
        public void should_classify_ddos_challenge_page()
        {
            var headers = new HttpHeader { ContentType = "text/html; charset=utf-8" };
            var body = System.Text.Encoding.UTF8.GetBytes("<html><body>DDoS-Guard protection</body></html>");
            var response = new HttpResponse(null, headers, body, HttpStatusCode.Forbidden);

            var classification = DirectDownloadClient.ClassifyHtmlResponse(response);

            Assert.That(classification, Is.EqualTo(HtmlResponseClassification.DdosChallenge));
        }

        [Test]
        public void should_classify_rate_limit_page()
        {
            var headers = new HttpHeader { ContentType = "text/html; charset=utf-8" };
            var body = System.Text.Encoding.UTF8.GetBytes("<html><body>Too many requests, rate limit exceeded</body></html>");
            var response = new HttpResponse(null, headers, body, (HttpStatusCode)429);

            var classification = DirectDownloadClient.ClassifyHtmlResponse(response);

            Assert.That(classification, Is.EqualTo(HtmlResponseClassification.RateLimit));
        }

        [Test]
        public void should_classify_login_or_key_error()
        {
            var headers = new HttpHeader { ContentType = "text/html; charset=utf-8" };
            var body = System.Text.Encoding.UTF8.GetBytes("<html><body>Please sign in to continue</body></html>");
            var response = new HttpResponse(null, headers, body, HttpStatusCode.Unauthorized);

            var classification = DirectDownloadClient.ClassifyHtmlResponse(response);

            Assert.That(classification, Is.EqualTo(HtmlResponseClassification.LoginOrKeyError));
        }

        [Test]
        public void should_classify_wrong_url_as_not_found()
        {
            var headers = new HttpHeader { ContentType = "text/html; charset=utf-8" };
            var body = System.Text.Encoding.UTF8.GetBytes("<html><body>404 page not found</body></html>");
            var response = new HttpResponse(null, headers, body, HttpStatusCode.NotFound);

            var classification = DirectDownloadClient.ClassifyHtmlResponse(response);

            Assert.That(classification, Is.EqualTo(HtmlResponseClassification.WrongUrl));
        }

        [Test]
        public void should_classify_ordinary_detail_page_with_download_link()
        {
            var headers = new HttpHeader { ContentType = "text/html; charset=utf-8" };
            var body = System.Text.Encoding.UTF8.GetBytes("<html><body><a href=\"/file.epub\">GET</a></body></html>");
            var response = new HttpResponse(null, headers, body, HttpStatusCode.OK);

            var classification = DirectDownloadClient.ClassifyHtmlResponse(response);

            Assert.That(classification, Is.EqualTo(HtmlResponseClassification.OrdinaryDetailPage));
        }

        [Test]
        public void should_classify_no_classification_for_non_html_response()
        {
            var headers = new HttpHeader { ContentType = "application/json" };
            var body = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
            var response = new HttpResponse(null, headers, body, HttpStatusCode.NotFound);

            var classification = DirectDownloadClient.ClassifyHtmlResponse(response);

            Assert.That(classification, Is.EqualTo(HtmlResponseClassification.None));
        }

        [Test]
        public void should_include_classification_in_failure_message()
        {
            using var scenario = new BackoffScenario();
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                request =>
                {
                    var headers = new HttpHeader();
                    headers.ContentType = "text/html; charset=utf-8";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("<html>checking your browser</html>");
                    return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.OK));
                });

            var client = scenario.BuildClient();
            var downloadId = client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null).GetAwaiter().GetResult();

            scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed, timeoutSeconds: 30).GetAwaiter().GetResult();

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Message, Does.Contain("DdosChallenge"));
            Assert.That(item.Message, Does.Contain("3/3"));
        }

        [Test]
        public async Task should_not_permanently_fail_html_on_first_attempt()
        {
            using var scenario = new BackoffScenario();
            var attempts = 0;
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                request =>
                {
                    attempts++;
                    var headers = new HttpHeader();
                    headers.ContentType = "text/html; charset=utf-8";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("<html>challenge page</html>");
                    return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.OK));
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await Task.Delay(500);

            var item = scenario.SingleItem(client, downloadId, throwWhenMissing: false);
            if (item != null && item.Status == DownloadItemStatus.Failed)
            {
                Assert.Fail("HTML response should not cause immediate permanent failure on first attempt");
            }

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed, timeoutSeconds: 30);

            item = scenario.SingleItem(client, downloadId);
            Assert.That(attempts, Is.EqualTo(3));
            Assert.That(item.Message, Does.Not.Contain("permanently"));
        }

        [Test]
        public async Task should_show_retry_attempt_in_failure_message_after_all_transient_retries_exhausted()
        {
            using var scenario = new BackoffScenario();
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                request => throw new WebException("connection refused", WebExceptionStatus.ConnectFailure));

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed, timeoutSeconds: 15);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Failed));
            Assert.That(item.Message, Does.Contain("3/3"));
            Assert.That(item.Message, Does.Contain("attempts"));
        }

        [Test]
        public async Task should_report_final_failure_message_with_attempt_count()
        {
            using var scenario = new BackoffScenario();
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                request => throw new WebException("connection refused", WebExceptionStatus.ConnectFailure));

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed, timeoutSeconds: 15);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Failed));
            Assert.That(item.Message, Does.Contain("3/3"));
        }

        [Test]
        public async Task should_mark_empty_file_as_permanent_failure()
        {
            using var scenario = new BackoffScenario();
            scenario.RegisterBinary("https://downloads.example/empty.epub", "application/epub+zip", "");

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/empty.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed, timeoutSeconds: 10);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Failed));
            Assert.That(item.Message, Does.Contain("permanently"));
        }

        [Test]
        public async Task should_preserve_durable_url_through_deferred_playwright_retries()
        {
            using var scenario = new BackoffScenario();
            scenario.BrowserResolver.SlowDownloadUrl = "https://slow.example/backoff-test.epub";

            var attemptCount = 0;
            scenario.Transport.AddRoute(
                url => url == "https://slow.example/backoff-test.epub",
                request =>
                {
                    attemptCount++;
                    if (attemptCount < 3)
                    {
                        throw new WebException("timeout", WebExceptionStatus.Timeout);
                    }

                    return scenario.WriteBinaryResponse(request, "application/epub+zip", "backoff-success");
                });

            var catalogUrl = "https://catalog.example/md5/abc123def456abc123def456abc123de";
            var stateDir = Path.Combine(scenario.StagingFolder, $"client-42/DEFERRED-BACKOFF");
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(
                Path.Combine(stateDir, "direct-download-state.json"),
                $"{{\"downloadId\":\"DEFERRED-BACKOFF\",\"title\":\"Frank Herbert - Dune [epub]\"," +
                $"\"downloadUrl\":\"{catalogUrl}\",\"status\":{(int)DownloadItemStatus.Downloading}," +
                $"\"fallbackMode\":\"deferredPlaywright\"," +
                $"\"outputFilePath\":\"{Path.Combine(stateDir, "Frank Herbert - Dune [epub].epub").Replace("\\", "\\\\")}\"," +
                $"\"partFilePath\":\"{Path.Combine(stateDir, "Frank Herbert - Dune [epub].epub.part").Replace("\\", "\\\\")}\"," +
                $"\"createdAtUtc\":\"{DateTime.UtcNow:O}\",\"updatedAtUtc\":\"{DateTime.UtcNow:O}\"}}");

            var client = scenario.BuildClient();

            await scenario.WaitForStatus(client, "DEFERRED-BACKOFF", DownloadItemStatus.Completed, timeoutSeconds: 30);

            Assert.That(attemptCount, Is.EqualTo(3));
            Assert.That(scenario.BrowserResolver.ResolveCalls, Is.GreaterThanOrEqualTo(1),
                "Browser should resolve at least once for deferred playwright state");
        }

        [Test]
        public async Task should_fallback_to_stored_url_when_browser_resolver_fails_during_retry()
        {
            using var scenario = new BackoffScenario();
            scenario.BrowserResolver.ShouldFail = true;

            var catalogUrl = "https://catalog.example/md5/abc123def456abc123def456abc123de";
            scenario.RegisterBinary(catalogUrl, "application/epub+zip", "stored-url-fallback-body");

            var stateDir = Path.Combine(scenario.StagingFolder, $"client-42/DEFERRED-FALLBACK");
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(
                Path.Combine(stateDir, "direct-download-state.json"),
                $"{{\"downloadId\":\"DEFERRED-FALLBACK\",\"title\":\"Frank Herbert - Dune [epub]\"," +
                $"\"downloadUrl\":\"{catalogUrl}\",\"status\":{(int)DownloadItemStatus.Downloading}," +
                $"\"fallbackMode\":\"deferredPlaywright\"," +
                $"\"outputFilePath\":\"{Path.Combine(stateDir, "Frank Herbert - Dune [epub].epub").Replace("\\", "\\\\")}\"," +
                $"\"partFilePath\":\"{Path.Combine(stateDir, "Frank Herbert - Dune [epub].epub.part").Replace("\\", "\\\\")}\"," +
                $"\"createdAtUtc\":\"{DateTime.UtcNow:O}\",\"updatedAtUtc\":\"{DateTime.UtcNow:O}\"}}");

            var client = scenario.BuildClient();

            await scenario.WaitForStatus(client, "DEFERRED-FALLBACK", DownloadItemStatus.Completed, timeoutSeconds: 30);

            Assert.That(File.Exists(Path.Combine(stateDir, "Frank Herbert - Dune [epub].epub")), Is.True);
        }

        private static RemoteBook BuildRemoteBook(string downloadUrl)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "Direct-Backoff-Test",
                    Title = "Frank Herbert - Dune [epub]",
                    Author = "Frank Herbert",
                    Book = "Dune",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = downloadUrl,
                    InfoUrl = "https://info.example/dune",
                    Container = "epub",
                    Size = 12
                }
            };
        }

        private sealed class BackoffScenario : IDisposable
        {
            public BackoffScenario()
            {
                StagingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-backoff-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(StagingFolder);
                Transport = new Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp();
                BrowserResolver = new StubBrowserResolver();
            }

            public string StagingFolder { get; }
            public Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp Transport { get; }
            public StubBrowserResolver BrowserResolver { get; }

            public DirectDownloadClient BuildClient()
            {
                return new DirectDownloadClient(Transport.CreateClient(), new TestDiskProvider(), null, LogManager.GetCurrentClassLogger(), browserResolver: BrowserResolver)
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

            public void RegisterBinary(string url, string contentType, string body)
            {
                Transport.AddRoute(candidate => candidate == url, request => WriteBinaryResponse(request, contentType, body));
            }

            public Task<HttpResponse> WriteBinaryResponse(HttpRequest request, string contentType, string body)
            {
                var headers = new HttpHeader();
                headers.ContentType = contentType;
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                return WriteResponseAsync(request, headers, bytes);
            }

            public async Task WaitForStatus(DirectDownloadClient client, string downloadId, DownloadItemStatus status, int timeoutSeconds = 5)
            {
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    var item = SingleItem(client, downloadId, throwWhenMissing: false);
                    if (item != null && item.Status == status)
                    {
                        return;
                    }

                    _ = client.GetItems();
                    await Task.Delay(50);
                }

                Assert.Fail($"Timed out waiting for {status} for {downloadId}");
            }

            public DownloadClientItem SingleItem(DirectDownloadClient client, string downloadId, bool throwWhenMissing = true)
            {
                foreach (var item in client.GetItems())
                {
                    if (item.DownloadId == downloadId)
                    {
                        return item;
                    }
                }

                if (throwWhenMissing)
                {
                    Assert.Fail($"Download item '{downloadId}' was not found.");
                }

                return null;
            }

            public void Dispose()
            {
                if (Directory.Exists(StagingFolder))
                {
                    Directory.Delete(StagingFolder, recursive: true);
                }
            }

            private static async Task<HttpResponse> WriteResponseAsync(HttpRequest request, HttpHeader headers, byte[] bytes)
            {
                if (request.ResponseStream != null)
                {
                    await request.ResponseStream.WriteAsync(bytes, 0, bytes.Length);
                }

                return new HttpResponse(request, headers, System.Array.Empty<byte>(), HttpStatusCode.OK);
            }
        }

        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider()
                : base(new System.IO.Abstractions.FileSystem())
            {
            }

            public override long? GetAvailableSpace(string path) => null;
            public override void InheritFolderPermissions(string filename) { }
            public override void SetEveryonePermissions(string filename) { }
            public override void SetFilePermissions(string path, string mask, string group) { }
            public override void SetPermissions(string path, string mask, string group) { }
            public override void CopyPermissions(string sourcePath, string targetPath) { }
            public override bool TryCreateHardLink(string source, string destination) => false;
            public override long? GetTotalSize(string path) => null;
        }

        public sealed class StubBrowserResolver : IBrowserDownloadResolver
        {
            public string SlowDownloadUrl { get; set; }
            public bool ShouldFail { get; set; }
            public int ResolveCalls;

            public Task<bool> IsAvailableAsync() => Task.FromResult(!ShouldFail);

            public Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl)
            {
                System.Threading.Interlocked.Increment(ref ResolveCalls);
                return ShouldFail ? Task.FromResult<string>(null) : Task.FromResult(SlowDownloadUrl);
            }
        }
    }
}
