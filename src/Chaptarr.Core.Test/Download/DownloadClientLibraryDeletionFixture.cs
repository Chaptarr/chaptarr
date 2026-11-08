using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Download.Clients.Aria2;
using NzbDrone.Core.Download.Clients.Blackhole;
using NzbDrone.Core.Download.Clients.DownloadStation;
using NzbDrone.Core.Download.Clients.Nzbget;
using NzbDrone.Core.Download.Clients.RTorrent;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Download.Clients;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadClientLibraryDeletionFixture
    {
        private static IEnumerable<TestCaseData> OverlappingPaths()
        {
            foreach (var type in new[] { typeof(Sabnzbd), typeof(Nzbget), typeof(RTorrent), typeof(Aria2),
                typeof(UsenetDownloadStation), typeof(TorrentDownloadStation), typeof(UsenetBlackhole), typeof(TorrentBlackhole) })
            foreach (var location in new[] { "root", "child", "parent" })
                yield return new TestCaseData(type, location);
        }

        [TestCaseSource(nameof(OverlappingPaths))]
        public void local_data_removers_refuse_library_overlap_before_client_or_disk_actions(Type clientType, string location)
        {
            var temp = Directory.CreateTempSubdirectory("chaptarr-download-root-").FullName;
            try
            {
                var root = Path.Combine(temp, "library");
                var child = Path.Combine(root, "Frank Herbert");
                Directory.CreateDirectory(child);
                var file = Path.Combine(child, "Dune.epub");
                File.WriteAllText(file, "library book");
                var roots = Proxy<IRootFolderService>((m, a) => new List<RootFolder> { new() { Path = root } });
                var constructor = clientType.GetConstructors().Single();
                var client = (IDownloadClient)constructor.Invoke(constructor.GetParameters().Select(parameter =>
                    parameter.ParameterType == typeof(IRootFolderService) ? (object)roots :
                    parameter.ParameterType == typeof(Logger) ? LogManager.GetCurrentClassLogger() : null).ToArray());
                var path = location == "root" ? root : location == "child" ? child : temp;

                var error = Assert.Throws<DownloadDataDeletionRefusedException>(() => client.RemoveItem(new DownloadClientItem
                { Title = "Dune", OutputPath = new OsPath(path), Status = DownloadItemStatus.Completed }, true));

                Assert.That(error.Message, Does.Contain("library root").And.Contain(root));
                Assert.That(File.ReadAllText(file), Is.EqualTo("library book"));
            }
            finally { Directory.Delete(temp, true); }
        }

        [TestCase(TrackedDownloadState.Imported)]
        [TestCase(TrackedDownloadState.DownloadFailed)]
        public void cleanup_refusal_is_visible_warns_once_and_recovers_without_deleting_library(TrackedDownloadState state)
        {
            var temp = Directory.CreateTempSubdirectory("chaptarr-cleanup-refusal-").FullName;
            using var logs = new LogFactory();
            var target = new MemoryTarget { Layout = "${level}|${message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
            logs.Configuration = configuration;
            var logger = logs.GetLogger(nameof(DownloadClientLibraryDeletionFixture));
            try
            {
                var root = Path.Combine(temp, "library");
                var download = Path.Combine(root, "Dune");
                Directory.CreateDirectory(download);
                var libraryFile = Path.Combine(download, "Dune.m4b");
                File.WriteAllText(libraryFile, "library recording");
                var clientCalls = 0;
                var removes = 0;
                var proxy = Proxy<ISabnzbdProxy>((method, args) =>
                {
                    clientCalls++;
                    if (method.Name == nameof(ISabnzbdProxy.GetQueue)) return new SabnzbdQueue { Items = new() };
                    Assert.That(method.Name, Is.EqualTo(nameof(ISabnzbdProxy.RemoveFromHistory)));
                    removes++;
                    return null;
                });
                var roots = Proxy<IRootFolderService>((_, _) => new List<RootFolder> { new() { Path = root } });
                var client = CreateSab(roots, proxy, logger);
                var tracked = new TrackedDownload
                {
                    DownloadClient = 1, State = state,
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = "DUNE", Title = "Dune", OutputPath = new OsPath(download),
                        CanBeRemoved = true, Status = state == TrackedDownloadState.Imported ? DownloadItemStatus.Completed : DownloadItemStatus.Failed,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Name = "SABnzbd" }
                    },
                    RemoteBook = new RemoteBook { Books = new(), ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) } }
                };
                var completed = Proxy<ICompletedDownloadService>((_, _) => true);
                var hub = new DownloadEventHub(completed, Proxy<IProvideDownloadClient>((_, _) => client),
                    Proxy<IDownloadImportModeResolver>((_, _) => false), logger);
                var queue = new QueueService(Proxy<IEventAggregator>((_, _) => null),
                    Proxy<IHistoryService>((_, _) => new List<EntityHistory>()), null);
                var events = Proxy<IEventAggregator>((_, args) =>
                {
                    if (args[0] is TrackedDownloadRefreshedEvent refreshed) queue.Handle(refreshed);
                    return null;
                });
                var monitor = new DownloadMonitoringService(null, null, events, null,
                    Proxy<IConfigService>((_, _) => true), null, completed,
                    Proxy<ITrackedDownloadService>((_, _) => new List<TrackedDownload> { tracked }), logger);
                void Remove()
                {
                    if (state == TrackedDownloadState.Imported) hub.Handle(new DownloadCanBeRemovedEvent(tracked));
                    else hub.Handle(new DownloadFailedEvent { TrackedDownload = tracked });
                    monitor.Handle(new DownloadsProcessedEvent());
                }

                for (var i = 0; i < 3; i++)
                {
                    Remove();
                    Assert.That(tracked.State, Is.EqualTo(state));
                    Assert.That(tracked.DownloadItem.Removed, Is.False);
                    Assert.That(tracked.CleanupDeletionRefused, Is.True);
                    var row = queue.GetQueue(false).Single();
                    Assert.That(row.TrackedDownloadStatus, Is.EqualTo(TrackedDownloadStatus.Warning));
                    Assert.That(row.StatusMessages.SelectMany(message => message.Messages), Has.Some.Contains("overlaps library root"));
                    Assert.That(row.StatusMessages.SelectMany(message => message.Messages), Has.Some.Contains("without deleting data"));
                }
                Assert.That(clientCalls, Is.Zero, "Refuse before calling SAB's deletion API");
                Assert.That(target.Logs, Has.Count.EqualTo(1));
                Assert.That(target.Logs.Single(), Does.StartWith("Warn|"));
                Assert.That(File.ReadAllText(libraryFile), Is.EqualTo("library recording"));

                // A corrected mapping now points at the actual download, never at the library.
                var actualDownload = Path.Combine(temp, "downloads", "Dune");
                Directory.CreateDirectory(actualDownload);
                File.WriteAllText(Path.Combine(actualDownload, "Dune.m4b"), "download recording");
                tracked.DownloadItem.OutputPath = new OsPath(actualDownload);
                Remove();
                Assert.That(removes, Is.EqualTo(1));
                Assert.That(tracked.DownloadItem.Removed, Is.True);
                Assert.That(tracked.CleanupDeletionRefused, Is.False);
                Assert.That(tracked.StatusMessages, Is.Empty);
                Assert.That(queue.GetQueue(false), Is.Empty);
                Assert.That(File.ReadAllText(libraryFile), Is.EqualTo("library recording"));
                if (state == TrackedDownloadState.Imported) Assert.That(Directory.Exists(actualDownload), Is.False);
            }
            finally { Directory.Delete(temp, true); }
        }

        private static Sabnzbd CreateSab(IRootFolderService roots, ISabnzbdProxy proxy, Logger logger)
        {
            var constructor = typeof(Sabnzbd).GetConstructors().Single();
            var client = (Sabnzbd)constructor.Invoke(constructor.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(IRootFolderService) ? (object)roots :
                parameter.ParameterType == typeof(ISabnzbdProxy) ? proxy :
                parameter.ParameterType == typeof(IDiskProvider) ? RealDiskProvider.Create(logger) :
                parameter.ParameterType == typeof(Logger) ? logger : null).ToArray());
            client.Definition = new DownloadClientDefinition { Settings = new SabnzbdSettings() };
            return client;
        }

        [Test]
        public void sab_cleanup_outside_library_still_removes_completed_download()
        {
            var temp = Directory.CreateTempSubdirectory("chaptarr-download-cleanup-").FullName;
            try
            {
                var download = Path.Combine(temp, "downloads", "Dune");
                Directory.CreateDirectory(download);
                File.WriteAllText(Path.Combine(download, "Dune.m4b"), "download");
                var removed = 0;
                var proxy = Proxy<ISabnzbdProxy>((m, a) =>
                {
                    if (m.Name == nameof(ISabnzbdProxy.GetQueue)) return new SabnzbdQueue { Items = new() };
                    Assert.That(m.Name, Is.EqualTo(nameof(ISabnzbdProxy.RemoveFromHistory)));
                    removed++;
                    return null;
                });
                var logger = LogManager.GetCurrentClassLogger();
                var roots = Proxy<IRootFolderService>((m, a) => new List<RootFolder> { new() { Path = Path.Combine(temp, "library") } });
                var client = CreateSab(roots, proxy, logger);
                client.RemoveItem(new DownloadClientItem { DownloadId = "ABC", OutputPath = new OsPath(download), Status = DownloadItemStatus.Completed }, true);
                Assert.That(Directory.Exists(download), Is.False);
                Assert.That(removed, Is.EqualTo(1));
            }
            finally { Directory.Delete(temp, true); }
        }
    }
}
