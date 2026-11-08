using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RemotePathMappings;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class SabnzbdImportInventoryFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static string Listing(string directory, params (string Name, bool Directory)[] children) =>
            JsonConvert.SerializeObject(new
            {
                paths = new object[] { new { current_path = directory }, new { name = "..", path = new OsPath(directory).Directory.FullPath, dir = true } }
                    .Concat(children.Select(child => (object)new { name = child.Name, path = (new OsPath(directory).AsDirectory() + new OsPath(child.Name)).FullPath, dir = child.Directory }))
            });

        private static Sabnzbd CreateClient(Func<HttpRequest, string> response,
            IDownloadClientFileSnapshotService snapshots = null, SabnzbdSettings settings = null, int clientId = 1)
        {
            var transport = Proxy<IHttpClient>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IHttpClient.Execute)));
                var request = (HttpRequest)args[0];
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, response(request), HttpStatusCode.OK);
            });
            var mapping = Proxy<IRemotePathMappingService>((method, args) =>
            {
                var path = ((OsPath)args.Last()).FullPath;
                return new OsPath(method.Name == nameof(IRemotePathMappingService.RemapLocalToRemote)
                    ? path.Replace("/local/", "/remote/") : path.Replace("/remote/", "/local/"));
            });
            return new Sabnzbd(new SabnzbdProxy(transport, Logger), null, null, null, mapping, null,
                snapshots ?? NoopDownloadClientFileSnapshotService.Instance, Logger, DownloadClientTestRoots.Empty)
            {
                Definition = new DownloadClientDefinition
                {
                    Id = clientId, Name = "SABnzbd", Settings = settings ?? new SabnzbdSettings { Host = "localhost", Port = 8080, ApiKey = "fixture-key" }
                }
            };
        }

        private static DownloadClientItem Item(string path = "/local/Dune Messiah", int clientId = 1) => new()
        {
            DownloadId = "sab-job", Title = "Dune Messiah", OutputPath = new OsPath(path), Status = DownloadItemStatus.Completed,
            DownloadClientInfo = new DownloadClientItemClientInfo { Id = clientId }
        };

        [Test]
        public void should_capture_final_nested_output_once_and_remap_each_file()
        {
            var requests = new List<string>();
            var client = CreateClient(request =>
            {
                var query = HttpUtility.ParseQueryString(new Uri(request.Url.ToString()).Query);
                Assert.That(query["mode"], Is.EqualTo("browse"));
                Assert.That(query["show_files"], Is.EqualTo("1"));
                Assert.That(query["show_hidden_folders"], Is.EqualTo("1"));
                requests.Add(query["name"]);
                return query["name"] == "/remote/Dune Messiah"
                    ? Listing(query["name"], ("Disc 1", true), ("Disc 2", true), ("companion.epub", false), ("cover.jpg", false), ("._track.mp3", false))
                    : Listing(query["name"], ("track.mp3", false));
            });

            var result = client.GetImportItem(Item(), null);
            Assert.That(result.FilePaths, Is.EquivalentTo(new[]
            {
                "/local/Dune Messiah/Disc 1/track.mp3", "/local/Dune Messiah/Disc 2/track.mp3",
                "/local/Dune Messiah/companion.epub", "/local/Dune Messiah/cover.jpg", "/local/Dune Messiah/._track.mp3"
            }));
            Assert.That(result.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
            Assert.That(client.GetImportItem(Item(), result).FilePaths, Is.EqualTo(result.FilePaths));
            Assert.That(requests, Has.Count.EqualTo(3), "A retry must retain the inventory, not replace it with the files still on the source");
        }

        [TestCase(DownloadItemStatus.Downloading)]
        [TestCase(DownloadItemStatus.Paused)]
        [TestCase(DownloadItemStatus.Failed)]
        public void should_not_browse_unfinished_downloads(DownloadItemStatus status)
        {
            var item = Item();
            item.Status = status;
            var result = CreateClient(_ => throw new AssertionException("Unexpected browse")).GetImportItem(item, null);
            Assert.That(result.FileListConfidence, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_accept_only_the_exact_single_file_from_its_parent(bool windows)
        {
            var root = windows ? @"C:\Downloads" : "/remote";
            var item = Item(windows ? @"C:\Downloads\Dune.m4b" : "/local/Dune.m4b");
            var result = CreateClient(_ => Listing(root, ("Dune.m4b", false), ("Other.m4b", false))).GetImportItem(item, null);
            Assert.That(result.FilePaths, Is.EqualTo(new[] { item.OutputPath.FullPath }));
            Assert.That(result.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [TestCase("parent")]
        [TestCase("missing-child")]
        [TestCase("escaped-child")]
        [TestCase("error")]
        [TestCase("malformed")]
        [TestCase("network")]
        [TestCase("empty")]
        [TestCase("old-directories-only")]
        public void should_not_publish_an_incomplete_or_unavailable_inventory(string failure)
        {
            var calls = 0;
            var client = CreateClient(_ =>
            {
                calls++;
                return failure switch
                {
                    "parent" => Listing("/remote", ("Unrelated.m4b", false)),
                    "missing-child" => calls == 1
                        ? Listing("/remote/Dune Messiah", ("part1.mp3", false), ("Disc 2", true))
                        : Listing("/remote/Dune Messiah", ("part1.mp3", false)),
                    "escaped-child" => Listing("/remote/Dune Messiah", ("../other/Unrelated.m4b", false)),
                    "error" => "{\"status\":false,\"error\":\"API access denied\"}",
                    "malformed" => "not JSON",
                    "network" => throw new WebException("Offline"),
                    "old-directories-only" => calls == 1
                        ? Listing("/remote/Dune Messiah", ("Disc 1", true)) : Listing("/remote/Dune Messiah/Disc 1"),
                    _ => Listing("/remote/Dune Messiah")
                };
            });
            var item = Item();
            item.FilePaths = new List<string> { "/local/Dune Messiah/previous.mp3" };
            item.FileListConfidence = DownloadClientFileListConfidence.Disk;
            var result = client.GetImportItem(item, null);
            Assert.That(result.FilePaths, Is.EqualTo(item.FilePaths));
            Assert.That(result.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Disk));
            Assert.That(calls, Is.InRange(1, 2));
        }

        [TestCase("id")]
        [TestCase("client")]
        [TestCase("path")]
        public void should_not_reuse_another_downloads_inventory(string change)
        {
            var previous = Item();
            previous.FilePaths = new List<string> { "/wrong/file.mp3" };
            previous.FileListConfidence = DownloadClientFileListConfidence.Authoritative;
            if (change == "id") previous.DownloadId = "another-job";
            if (change == "client") previous.DownloadClientInfo.Id = 2;
            if (change == "path") previous.OutputPath = new OsPath("/another/folder");
            var result = CreateClient(_ => Listing("/remote/Dune Messiah", ("Book.m4b", false))).GetImportItem(Item(), previous);
            Assert.That(result.FilePaths, Is.EqualTo(new[] { "/local/Dune Messiah/Book.m4b" }));
        }

        [Test]
        public void should_not_capture_a_shared_sorted_folder_as_one_download()
        {
            var calls = 0;
            var client = CreateClient(_ =>
            {
                calls++;
                return Listing("/remote/books", ("Other book", true), ("Another.m4b", false));
            });
            var result = client.GetImportItem(Item("/local/books"), null);
            Assert.That(result.FileListConfidence, Is.Null);
            Assert.That(calls, Is.EqualTo(1), "Do not recursively browse a shared category or sorted output directory");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_restore_inventory_after_restart_even_when_import_has_moved_source_files(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var definition = new DownloadClientRepository(database, events).Insert(new DownloadClientDefinition
                {
                    Name = "SABnzbd", Implementation = "Sabnzbd", ConfigContract = "SabnzbdSettings", Settings = new SabnzbdSettings(), Enable = true
                });
                var repository = new DownloadClientFileSnapshotRepository(database, events);
                var snapshot = new DownloadClientFileSnapshotService(repository, null, Logger);
                var client = CreateClient(_ => Listing("/remote/Dune Messiah", ("part1.mp3", false), ("part2.mp3", false)), snapshot, clientId: definition.Id);
                var captured = client.GetImportItem(Item(clientId: definition.Id), null);
                Assert.That(repository.All(), Has.Count.EqualTo(1));

                var reopened = new DownloadClientFileSnapshotService(new DownloadClientFileSnapshotRepository(database, events), null, Logger);
                var requestsAfterRestart = 0;
                var restarted = CreateClient(_ =>
                {
                    requestsAfterRestart++;
                    return Listing("/remote/Dune Messiah", ("part2.mp3", false));
                }, reopened, clientId: definition.Id);
                var restored = restarted.GetImportItem(Item(clientId: definition.Id), null);
                Assert.That(restored.FilePaths, Is.EqualTo(captured.FilePaths));
                Assert.That(restored.FilePaths, Has.Count.EqualTo(2));
                Assert.That(restored.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
                Assert.That(requestsAfterRestart, Is.Zero, "Must not replace the saved list with remaining files");
            });
        }

        [Test, Explicit("Read-only live SAB browse; requires an explicitly supplied completed output path and settings")]
        public void live_completed_output_listing()
        {
            var settings = JsonConvert.DeserializeObject<SabnzbdSettings>(Environment.GetEnvironmentVariable("CHAPTARR_SAB_TEST_SETTINGS") ?? throw new InvalidOperationException("Missing live settings"));
            var path = Environment.GetEnvironmentVariable("CHAPTARR_SAB_TEST_PATH") ?? throw new InvalidOperationException("Missing live path");
            var expected = JsonConvert.DeserializeObject<string[]>(Environment.GetEnvironmentVariable("CHAPTARR_SAB_TEST_FILES") ?? throw new InvalidOperationException("Missing expected filenames"));
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var calls = 0;
            var client = CreateClient(request =>
            {
                Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
                Assert.That(HttpUtility.ParseQueryString(new Uri(request.Url.ToString()).Query)["mode"], Is.EqualTo("browse"));
                calls++;
                using var response = http.GetAsync(request.Url.ToString()).GetAwaiter().GetResult();
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }, settings: settings);
            var item = Item(path);
            item.Title = new OsPath(path).FileName;
            var result = client.GetImportItem(item, null);
            Assert.That(result.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
            Assert.That(result.FilePaths.Select(file => new OsPath(file).FileName), Is.EquivalentTo(expected));
            TestContext.Progress.WriteLine($"SAB live inventory: {result.FilePaths.Count} files, {calls} read-only directory requests");
        }
    }
}
