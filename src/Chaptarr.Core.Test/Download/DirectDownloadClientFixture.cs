using System;
using System.Collections.Generic;
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
    public class DirectDownloadClientFixture
    {
        [Test]
        public async Task should_download_to_local_staging_and_report_exact_completed_file_path()
        {
            using var scenario = new DirectDownloadClientScenario();
            scenario.RegisterBinary("https://downloads.example/dune.epub", "application/epub+zip", "ebook-body");
            var client = scenario.BuildClient();

            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(item.OutputPath.FullPath, Is.EqualTo(Path.Combine(scenario.StagingFolder, $"client-42/{downloadId}/Frank Herbert - Dune [epub].epub")));
            Assert.That(item.FilePaths, Is.EqualTo(new[] { item.OutputPath.FullPath }));
            Assert.That(item.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
            Assert.That(File.Exists(item.OutputPath.FullPath), Is.True);
            Assert.That(File.ReadAllText(item.OutputPath.FullPath), Is.EqualTo("ebook-body"));
        }

        [Test]
        public async Task should_retry_transient_failure_and_complete_without_leaking_partial_path()
        {
            using var scenario = new DirectDownloadClientScenario();
            var attempts = 0;
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                request =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        throw new WebException("Http request timed out", WebExceptionStatus.Timeout);
                    }

                    return scenario.WriteBinaryResponse(request, "application/epub+zip", "retried-success");
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(File.Exists(item.OutputPath.FullPath + ".part"), Is.False);
        }

        [Test]
        public async Task should_restart_stale_partial_download_from_tracked_state_after_client_recreation()
        {
            using var scenario = new DirectDownloadClientScenario();
            var downloadId = "DIRECT-RESTART-TEST";
            var downloadFolder = Path.Combine(scenario.StagingFolder, $"client-42/{downloadId}");
            Directory.CreateDirectory(downloadFolder);
            File.WriteAllText(Path.Combine(downloadFolder, "Frank Herbert - Dune [epub].epub.part"), "partial");
            File.WriteAllText(Path.Combine(downloadFolder, "direct-download-state.json"), scenario.SerializeState(downloadId, DownloadItemStatus.Downloading));
            scenario.RegisterBinary("https://downloads.example/dune.epub", "application/epub+zip", "fresh-body");

            var client = scenario.BuildClient();

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Completed);

            var item = scenario.SingleItem(client, downloadId);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(File.ReadAllText(item.OutputPath.FullPath), Is.EqualTo("fresh-body"));
        }

        [Test]
        public async Task should_leave_failed_partial_data_until_remove_item_decides_whether_to_delete_it()
        {
            using var scenario = new DirectDownloadClientScenario();
            scenario.Transport.AddRoute(
                url => url == "https://downloads.example/dune.epub",
                async request =>
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes("partial-body");
                    await request.ResponseStream.WriteAsync(bytes, 0, bytes.Length);
                    throw new WebException("connection reset", WebExceptionStatus.ReceiveFailure);
                });

            var client = scenario.BuildClient();
            var downloadId = await client.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);

            await scenario.WaitForStatus(client, downloadId, DownloadItemStatus.Failed);

            var failedItem = scenario.SingleItem(client, downloadId);
            Assert.That(File.Exists(failedItem.OutputPath.FullPath), Is.True, "failed downloads should preserve their exact partial path until cleanup policy runs");

            client.RemoveItem(failedItem, deleteData: false);
            Assert.That(File.Exists(failedItem.OutputPath.FullPath), Is.True);

            var secondClient = scenario.BuildClient();
            var redownloadId = await secondClient.Download(BuildRemoteBook("https://downloads.example/dune.epub"), indexer: null);
            Assert.That(redownloadId, Is.EqualTo(downloadId));
        }

        private static RemoteBook BuildRemoteBook(string downloadUrl)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "Direct-Catalog-1234567890ABCDEF123456",
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

        private sealed class DirectDownloadClientScenario : IDisposable
        {
            public DirectDownloadClientScenario()
            {
                StagingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-direct-client-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(StagingFolder);
                Transport = new Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp();
            }

            public string StagingFolder { get; }
            public Chaptarr.Core.Test.Indexers.DirectDownloadTestHttp Transport { get; }

            public DirectDownloadClient BuildClient()
            {
                return new DirectDownloadClient(Transport.CreateClient(), new TestDiskProvider(), null, LogManager.GetCurrentClassLogger())
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

            public async Task WaitForStatus(DirectDownloadClient client, string downloadId, DownloadItemStatus status)
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
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

            public string SerializeState(string downloadId, DownloadItemStatus status)
            {
                return $"{{\"downloadId\":\"{downloadId}\",\"title\":\"Frank Herbert - Dune [epub]\",\"downloadUrl\":\"https://downloads.example/dune.epub\",\"status\":{(int)status},\"outputFilePath\":\"{Path.Combine(StagingFolder, $"client-42/{downloadId}/Frank Herbert - Dune [epub].epub").Replace("\\", "\\\\")}\",\"partFilePath\":\"{Path.Combine(StagingFolder, $"client-42/{downloadId}/Frank Herbert - Dune [epub].epub.part").Replace("\\", "\\\\")}\",\"createdAtUtc\":\"{DateTime.UtcNow:O}\",\"updatedAtUtc\":\"{DateTime.UtcNow:O}\"}}";
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

                return new HttpResponse(request, headers, Array.Empty<byte>(), HttpStatusCode.OK);
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
            public override bool TryCreateHardLink(string source, string destination) => false; public override long? GetTotalSize(string path) => null;
        }
    }
}
