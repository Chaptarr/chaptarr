using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectDownloadClientInterruptionFixture
    {
        [Test]
        public async Task should_cancel_stalled_request_when_item_is_removed_before_first_bytes_arrive()
        {
            using var scenario = new DirectDownloadClientCancellationScenario();
            var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/stalled.epub",
                async request =>
                {
                    requestStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        requestCanceled.TrySetResult(true);
                        throw;
                    }

                    return new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), HttpStatusCode.OK);
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/stalled.epub"), indexer: null);
            await requestStarted.Task;

            var item = scenario.WaitForItem(client, downloadId);
            client.RemoveItem(item, deleteData: true);

            await Task.WhenAny(requestCanceled.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.That(requestCanceled.Task.IsCompleted, Is.True, "stalled request should observe client cancellation before timeout elapses");
            Assert.That(scenario.ContainsItem(client, downloadId), Is.False);
        }

        [Test]
        public void should_fail_fast_when_staging_folder_is_missing()
        {
            using var scenario = new DirectDownloadClientCancellationScenario(createStagingFolder: false);
            var client = scenario.BuildClient();

            var exception = Assert.ThrowsAsync<ReleaseDownloadException>(async () => await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null));

            Assert.That(exception.Message, Does.Contain("staging folder"));
        }

        [Test]
        public async Task should_mark_download_failed_when_source_returns_html_instead_of_file()
        {
            using var scenario = new DirectDownloadClientCancellationScenario();
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/html.epub",
                request => Task.FromResult(new HttpResponse(request, new HttpHeader { ContentType = "text/html; charset=utf-8" }, "<html>not a file</html>", HttpStatusCode.OK)));

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/html.epub"), indexer: null);

            var item = await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed);
            Assert.That(item.Message, Does.Contain("HTML"));
        }

        [Test]
        public async Task should_mark_download_failed_when_source_returns_empty_body()
        {
            using var scenario = new DirectDownloadClientCancellationScenario();
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/empty.epub",
                request => Task.FromResult(new HttpResponse(request, new HttpHeader { ContentType = "application/epub+zip" }, Array.Empty<byte>(), HttpStatusCode.OK)));

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/empty.epub"), indexer: null);

            var item = await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed);
            Assert.That(item.Message, Does.Contain("empty file"));
        }

        [Test]
        public void should_ignore_malformed_persisted_state_file()
        {
            using var scenario = new DirectDownloadClientCancellationScenario();
            scenario.WriteStateFile("BROKEN-STATE", "{ definitely-not-json }");

            var client = scenario.BuildClient();

            Assert.That(client.GetItems(), Is.Empty);
        }

        [Test]
        public void should_downgrade_completed_state_to_failed_when_output_file_is_missing()
        {
            using var scenario = new DirectDownloadClientCancellationScenario();
            scenario.WriteStateFile("MISSING-FILE", scenario.BuildStateJson("MISSING-FILE", DownloadItemStatus.Completed));

            var client = scenario.BuildClient();
            var item = scenario.WaitForItem(client, "MISSING-FILE");

            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Failed));
            Assert.That(item.Message, Does.Contain("missing"));
        }

        [Test]
        public async Task should_complete_deferred_playwright_state_with_browser_fallback_on_restart()
        {
            using var scenario = new DirectDownloadClientCancellationScenario();
            scenario.BrowserResolver.SlowDownloadUrl = "https://slow.example/restart-resolved.epub";
            scenario.RegisterBinary("https://slow.example/restart-resolved.epub", "application/epub+zip", "restart-fallback-body");

            var downloadId = "DEFERRED-RESTART-INTERRUPT";
            var stateDir = Path.Combine(scenario.StagingFolder, $"client-42/{downloadId}");
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(
                Path.Combine(stateDir, "direct-download-state.json"),
                scenario.BuildStateJson(downloadId, DownloadItemStatus.Downloading,
                    downloadUrl: "https://catalog.example/md5/abc123def456abc123def456abc123de",
                    fallbackMode: "deferredPlaywright"));

            var client = scenario.BuildClient();
            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            Assert.That(scenario.BrowserResolver.ResolveCalls, Is.GreaterThan(0), "Browser should be invoked on restart for DeferredPlaywright state");
            var item = scenario.WaitForItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
        }

        [Test]
        public async Task should_handle_html_response_as_transient_failure_not_permanent()
        {
            using var scenario = new DirectDownloadClientCancellationScenario();
            var attempts = 0;
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/challenge.epub",
                request =>
                {
                    attempts++;
                    if (attempts <= 2)
                    {
                        var headers = new HttpHeader();
                        headers.ContentType = "text/html; charset=utf-8";
                        var bytes = System.Text.Encoding.UTF8.GetBytes("<html>DDoS-Guard protection</body></html>");
                        return Task.FromResult(new HttpResponse(request, headers, bytes, HttpStatusCode.OK));
                    }

                    var successHeaders = new HttpHeader();
                    successHeaders.ContentType = "application/epub+zip";
                    var successBytes = System.Text.Encoding.UTF8.GetBytes("success-after-challenge");
                    return scenario.WriteBinaryResponse(request, "application/epub+zip", "success-after-challenge");
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/challenge.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed, timeoutSeconds: 30);

            var item = scenario.WaitForItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(attempts, Is.EqualTo(3));
        }

        [Test]
        public async Task should_retry_after_transient_failure_then_fail_permanently_on_html_only_source()
        {
            using var scenario = new DirectDownloadClientCancellationScenario();
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/ddos-only.epub",
                request =>
                {
                    var headers = new HttpHeader();
                    headers.ContentType = "text/html; charset=utf-8";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("<html>Just a moment... checking your browser</html>");
                    return Task.FromResult(new HttpResponse(request, headers, bytes, (HttpStatusCode)403));
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/ddos-only.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed, timeoutSeconds: 30);

            var item = scenario.WaitForItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Failed));
            Assert.That(item.Message, Does.Contain("3/3"), "Should exhaust all retry attempts");
        }

        private static RemoteBook BuildRemoteBook(string downloadUrl)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = $"Direct-Catalog-{Math.Abs(downloadUrl.GetHashCode())}",
                    Title = "Frank Herbert - Dune [epub]",
                    Author = "Frank Herbert",
                    Book = "Dune",
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = downloadUrl,
                    InfoUrl = "https://info.example/dune",
                    Container = "epub",
                    Size = 9
                }
            };
        }

        private sealed class DirectDownloadClientCancellationScenario : IDisposable
        {
            public DirectDownloadClientCancellationScenario(bool createStagingFolder = true)
            {
                StagingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-direct-client-tests", Guid.NewGuid().ToString("N"));
                if (createStagingFolder)
                {
                    Directory.CreateDirectory(StagingFolder);
                }

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

            public DownloadClientItem WaitForItem(DirectDownloadClient client, string downloadId)
            {
                for (var i = 0; i < 40; i++)
                {
                    foreach (var item in client.GetItems())
                    {
                        if (item.DownloadId == downloadId)
                        {
                            return item;
                        }
                    }

                    Thread.Sleep(25);
                }

                Assert.Fail($"Download item '{downloadId}' was not found.");
                return null;
            }

            public bool ContainsItem(DirectDownloadClient client, string downloadId)
            {
                foreach (var item in client.GetItems())
                {
                    if (item.DownloadId == downloadId)
                    {
                        return true;
                    }
                }

                return false;
            }

            public async Task<DownloadClientItem> WaitForStatus(DirectDownloadClient client, string downloadId, DownloadItemStatus status, int timeoutSeconds = 30)
            {
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    var item = WaitForItem(client, downloadId);
                    if (item.Status == status)
                    {
                        return item;
                    }

                    await Task.Delay(50);
                }

                Assert.Fail($"Timed out waiting for {status} for {downloadId}");
                return null;
            }

            public void Dispose()
            {
                if (Directory.Exists(StagingFolder))
                {
                    Directory.Delete(StagingFolder, recursive: true);
                }
            }

            public void WriteStateFile(string downloadId, string content)
            {
                var stateDirectory = Path.Combine(StagingFolder, $"client-42/{downloadId}");
                Directory.CreateDirectory(stateDirectory);
                File.WriteAllText(Path.Combine(stateDirectory, "direct-download-state.json"), content);
            }

            public string BuildStateJson(string downloadId, DownloadItemStatus status, string downloadUrl = null, string fallbackMode = null)
            {
                var url = downloadUrl ?? "https://downloads.example/dune.epub";
                var mode = fallbackMode ?? "none";
                var outputPath = Path.Combine(StagingFolder, $"client-42/{downloadId}/Frank Herbert - Dune [epub].epub");
                var partPath = outputPath + ".part";
                return $"{{\"downloadId\":\"{downloadId}\",\"title\":\"Frank Herbert - Dune [epub]\",\"downloadUrl\":\"{url}\"," +
                       $"\"status\":{(int)status},\"fallbackMode\":\"{mode}\"," +
                       $"\"outputFilePath\":\"{outputPath.Replace("\\", "\\\\")}\",\"partFilePath\":\"{partPath.Replace("\\", "\\\\")}\"," +
                       $"\"createdAtUtc\":\"{DateTime.UtcNow:O}\",\"updatedAtUtc\":\"{DateTime.UtcNow:O}\"}}";
            }

            private static async Task<HttpResponse> WriteResponseAsync(HttpRequest request, HttpHeader headers, byte[] bytes)
            {
                if (request.ResponseStream != null)
                {
                    await request.ResponseStream.WriteAsync(bytes, 0, bytes.Length);
                }

                return new HttpResponse(request, headers, Array.Empty<byte>(), HttpStatusCode.OK);
            }
        }

        public sealed class StubBrowserResolver : IBrowserDownloadResolver
        {
            public string SlowDownloadUrl { get; set; }
            public bool ShouldFail { get; set; }
            public int ResolveCalls;

            public Task<bool> IsAvailableAsync() => Task.FromResult(!ShouldFail);

            public Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl)
            {
                Interlocked.Increment(ref ResolveCalls);
                return ShouldFail ? Task.FromResult<string>(null) : Task.FromResult(SlowDownloadUrl);
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
            public override bool TryCreateHardLink(string source, string destination) => false; public override long? GetTotalSize(string path) => null;
        }
    }
}
