using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadCleanupContextFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void missing_outputs_share_parent_read_but_only_for_one_pass(bool inaccessible)
        {
            WithFolder(root =>
            {
                var reads = new Dictionary<string, int>();
                var blocked = Path.Combine(root, "blocked");
                var healthy = Path.Combine(root, "healthy");
                Directory.CreateDirectory(blocked);
                Directory.CreateDirectory(healthy);
                var disk = Proxy<IDiskProvider>((method, args) =>
                {
                    if (method.Name == nameof(IDiskProvider.FileExists)) return false;
                    Assert.That(method.Name, Is.EqualTo(nameof(IDiskProvider.GetDirectoryInfo)));
                    var path = (string)args[0];
                    reads[path] = reads.GetValueOrDefault(path) + 1;
                    if (path == blocked && inaccessible) throw new UnauthorizedAccessException("Fixture parent inaccessible");
                    return new DirectoryInfoWrapper(new FileSystem(), new DirectoryInfo(path));
                });
                var service = new DownloadClientFileSnapshotService(Proxy<IDownloadClientFileSnapshotRepository>((_, _) => null), disk, LogManager.GetCurrentClassLogger());
                var pass = new DownloadCleanupContext();
                for (var i = 0; i < 100; i++)
                {
                    var item = Item(Path.Combine(blocked, "missing-" + i));
                    if (inaccessible) Assert.Throws<DownloadOutputReadException>(() => service.CaptureCompletedOutput(item, true, pass));
                    else Assert.DoesNotThrow(() => service.CaptureCompletedOutput(item, true, pass));
                }
                Assert.DoesNotThrow(() => service.CaptureCompletedOutput(Item(Path.Combine(healthy, "missing")), true, pass));
                Assert.That(reads[blocked], Is.EqualTo(1));
                Assert.That(reads[healthy], Is.EqualTo(1));
                Assert.That(pass.ParentProbes, Is.EqualTo(2));
                Assert.That(pass.ReusedParentProbes, Is.EqualTo(99));
                inaccessible = false;
                Assert.DoesNotThrow(() => service.CaptureCompletedOutput(Item(Path.Combine(blocked, "missing-0")), true, new DownloadCleanupContext()));
                Assert.That(reads[blocked], Is.EqualTo(2), "A later pass must re-read a recovered mount");
            });
        }

        [Test]
        public void unreadable_child_does_not_skip_healthy_sibling()
        {
            WithFolder(root =>
            {
                var blocked = Path.Combine(root, "blocked");
                var healthy = Path.Combine(root, "healthy");
                Directory.CreateDirectory(blocked);
                Directory.CreateDirectory(healthy);
                var file = Path.Combine(healthy, "Dune.mp3");
                File.WriteAllText(file, "fixture");
                var disk = Proxy<IDiskProvider>((method, args) =>
                {
                    if (method.Name == nameof(IDiskProvider.FileExists)) return false;
                    Assert.That(method.Name, Is.EqualTo(nameof(IDiskProvider.GetDirectoryInfo)));
                    if ((string)args[0] == blocked) throw new UnauthorizedAccessException("Fixture child inaccessible");
                    return new DirectoryInfoWrapper(new FileSystem(), new DirectoryInfo((string)args[0]));
                });
                var writes = 0;
                var repository = Proxy<IDownloadClientFileSnapshotRepository>((method, args) =>
                {
                    if (method.Name == nameof(IDownloadClientFileSnapshotRepository.Find)) return null;
                    writes++;
                    return args[0];
                });
                var service = new DownloadClientFileSnapshotService(repository, disk, LogManager.GetCurrentClassLogger());
                var pass = new DownloadCleanupContext();
                Assert.Throws<DownloadOutputReadException>(() => service.CaptureCompletedOutput(Item(blocked), true, pass));
                var item = Item(healthy);
                service.CaptureCompletedOutput(item, true, pass);
                Assert.That(item.FilePaths, Is.EqualTo(new[] { file }));
                Assert.That(writes, Is.EqualTo(1));
                Assert.That(pass.ParentProbes, Is.Zero);
            });
        }

        [Test]
        public void processing_owns_one_cleanup_context_per_pass()
        {
            var downloads = Enumerable.Range(1, 2).Select(id => new TrackedDownload
            {
                State = TrackedDownloadState.Imported,
                DownloadItem = new DownloadClientItem { DownloadId = id.ToString(), CanBeRemoved = true }
            }).ToList();
            var contexts = new List<DownloadCleanupContext>();
            var service = new DownloadProcessingService(
                Proxy<IConfigService>((_, _) => true), null, null,
                Proxy<ITrackedDownloadService>((_, _) => downloads),
                Proxy<IEventAggregator>((_, args) =>
                {
                    if (args[0] is DownloadCanBeRemovedEvent removable) contexts.Add(removable.Cleanup);
                    return null;
                }), LogManager.GetCurrentClassLogger());
            service.Execute(new ProcessMonitoredDownloadsCommand());
            service.Execute(new ProcessMonitoredDownloadsCommand());
            Assert.That(contexts, Has.Count.EqualTo(4));
            Assert.That(contexts[0], Is.Not.Null.And.SameAs(contexts[1]));
            Assert.That(contexts[2], Is.SameAs(contexts[3]).And.Not.SameAs(contexts[0]));
        }

        private static DownloadClientItem Item(string path) => new()
        {
            DownloadId = "fixture",
            DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1 },
            OutputPath = new OsPath(path)
        };

        private static void WithFolder(Action<string> check)
        {
            var root = Path.Combine(Path.GetTempPath(), "chaptarr-cleanup-context-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { check(root); }
            finally { Directory.Delete(root, true); }
        }
    }
}
