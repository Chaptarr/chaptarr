using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    public partial class CompletedDownloadWaitingFixture
    {
        [Test]
        public void cleanup_health_check_subscribes_to_cleanup_transitions_not_every_import_update()
        {
            var events = typeof(DownloadCleanupCheck).GetCustomAttributes(typeof(CheckOnAttribute), true)
                .Cast<CheckOnAttribute>().Select(attribute => attribute.EventType).ToList();
            Assert.That(events, Does.Contain(typeof(DownloadCleanupStatusChangedEvent)));
            Assert.That(events, Does.Not.Contain(typeof(TrackedDownloadUpdatedEvent)));
            Assert.That(events, Does.Contain(typeof(TrackedDownloadsRemovedEvent)));
        }

        [Test]
        public void cleanup_read_failure_should_retry_through_polling_and_restart(
            [Values(false, true)] bool postgres, [Values(false, true)] bool restart,
            [Values(false, true)] bool lateFile, [Values(false, true)] bool sab)
        {
            Run(postgres, 1, 1, (subject, initial, counters) =>
            {
                Assert.That(subject.VerifyImport(initial, new List<ImportResult>()), Is.True);
                initial.DownloadItem.CanBeRemoved = true;
                var calls = new RemovalCalls();
                IDownloadClient removalClient = sab ? new RecordingSabnzbd(calls) : new RecordingNzbget(calls);
                var definition = (DownloadClientDefinition)removalClient.Definition;
                var hub = new DownloadEventHub(subject, Proxy<IProvideDownloadClient>((_, _) => removalClient),
                    Proxy<IDownloadImportModeResolver>((_, _) => false), LogManager.GetCurrentClassLogger());
                var tracker = counters.NewTracker();
                var healthCheck = new DownloadCleanupCheck(
                    Proxy<ITrackedDownloadService>((_, _) => tracker.GetTrackedDownloads()),
                    Proxy<ILocalizationService>((_, _) => "Cleanup pending: {0}"));
                var client = Proxy<IDownloadClient>((method, args) => method.Name switch
                {
                    "get_Definition" => definition,
                    nameof(IDownloadClient.GetItems) => new List<DownloadClientItem> { initial.DownloadItem.Clone() },
                    _ => throw new InvalidOperationException(method.Name)
                });
                var config = Proxy<IConfigService>((method, args) => true);
                var updateEvents = 0;
                var cleanupHealthEvents = 0;
                counters.OnEvent = message =>
                {
                    if (message is DownloadCleanupStatusChangedEvent) cleanupHealthEvents++;
                    if (message is TrackedDownloadUpdatedEvent updated)
                    {
                        updateEvents++;
                        counters.Queue.Handle(updated);
                    }
                    if (message is TrackedDownloadRefreshedEvent refreshed) counters.Queue.Handle(refreshed);
                    if (message is DownloadCanBeRemovedEvent removable) hub.Handle(removable);
                };
                var events = Proxy<IEventAggregator>((_, args) => { counters.OnEvent((IEvent)args[0]); return null; });
                void Poll()
                {
                    var monitor = new DownloadMonitoringService(
                        Proxy<IDownloadClientStatusService>((method, args) => method.Name == "GetBlockedProviders" ? new List<DownloadClientStatus>() : null),
                        Proxy<IDownloadClientFactory>((_, _) => new List<IDownloadClient> { client }), events,
                        Proxy<IManageCommandQueue>((_, _) => null), config, null, subject, tracker, LogManager.GetCurrentClassLogger());
                    monitor.Execute(new RefreshMonitoredDownloadsCommand());
                    // A refresh must not temporarily hide an outstanding cleanup warning.
                    if (tracker.Find("waiting")?.CleanupCheckFailed == true)
                    {
                        Assert.That(counters.Queue.GetQueue(false), Has.Count.EqualTo(1));
                    }
                    new DownloadProcessingService(config, subject, null, tracker, events, LogManager.GetCurrentClassLogger())
                        .Execute(new ProcessMonitoredDownloadsCommand());
                }

                counters.FailFileListing = true;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    Poll();
                    Assert.That(tracker.Find("waiting").State, Is.EqualTo(TrackedDownloadState.Imported));
                    var row = counters.Queue.GetQueue(false).Single();
                    Assert.That(row.TrackedDownloadStatus, Is.EqualTo(TrackedDownloadStatus.Warning));
                    Assert.That(row.StatusMessages.SelectMany(m => m.Messages), Has.Some.Contains("retry automatically"));
                    Assert.That(calls.RemoveItem, Is.Zero);
                    Assert.That(calls.DeleteItemData, Is.Zero);
                    Assert.That(counters.IncompleteEvents, Is.Zero);
                    Assert.That(healthCheck.Check().Type, Is.EqualTo(HealthCheckResult.Warning));
                    Assert.That(healthCheck.Check().Message, Does.Contain(initial.DownloadItem.OutputPath.FullPath));
                    Assert.That(counters.DownloadHistory.GetLatestDownloadHistoryItem("waiting").EventType,
                        Is.EqualTo(DownloadHistoryEventType.DownloadImported));
                    Assert.That(updateEvents, Is.EqualTo(restart && attempt > 0 ? 2 : 1),
                        "Only the first failed check per tracked instance publishes an update");
                    Assert.That(cleanupHealthEvents, Is.EqualTo(restart && attempt > 0 ? 2 : 1),
                        "Only a changed cleanup failure flag triggers the health check");
                    if (restart && attempt == 0) tracker = counters.NewTracker();
                }

                var latePath = Path.Combine(initial.DownloadItem.OutputPath.FullPath, "Dune Part 002.mp3");
                if (lateFile) File.WriteAllText(latePath, "fixture");
                counters.FailFileListing = false;
                Poll();
                var tracked = tracker.Find("waiting");
                Assert.That(updateEvents, Is.EqualTo(restart ? 3 : 2), "Recovery publishes even when a late file blocks cleanup");
                Assert.That(cleanupHealthEvents, Is.EqualTo(restart ? 3 : 2));
                Assert.That(tracked.CleanupCheckFailed, Is.False);
                Assert.That(healthCheck.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
                Assert.That(tracked.StatusMessages.SelectMany(m => m.Messages), Has.None.Contains("Unable to read"));
                if (lateFile)
                {
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                    Assert.That(counters.Queue.GetQueue(false), Has.Count.EqualTo(1));
                    Assert.That(tracked.StatusMessages.Select(m => m.Title), Has.Member(Path.GetFileName(latePath)));
                    Assert.That(tracked.StatusMessages.Select(m => m.SourcePath), Has.Member(latePath));
                    Assert.That(counters.IncompleteEvents, Is.EqualTo(1));
                    Poll();
                    Assert.That(calls.RemoveItem, Is.Zero);
                    Assert.That(calls.DeleteItemData, Is.Zero);
                }
                else
                {
                    Assert.That(calls.RemoveItem, Is.EqualTo(1));
                    Assert.That(calls.DeleteItemData, Is.EqualTo(1));
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                    Assert.That(tracked.StatusMessages, Is.Empty);
                    Assert.That(counters.Queue.GetQueue(false), Is.Empty);
                }
            }, diskInventory: true);
        }

        [Test]
        public void cleanup_should_verify_saved_snapshot_after_history_failure_and_restart(
            [Values(false, true)] bool postgres, [Values(false, true)] bool restart,
            [Values(false, true)] bool ioError)
        {
            Run(postgres, 1, 1, (subject, tracked, counters) =>
            {
                Assert.That(subject.VerifyImport(tracked, new List<ImportResult>()), Is.True);
                var late = Path.Combine(tracked.DownloadItem.OutputPath.FullPath, "Dune Part 002.mp3");
                File.WriteAllText(late, "fixture");
                counters.FailHistoryReads = true;
                counters.HistoryReadError = ioError ? new IOException("History storage unavailable") : null;
                Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(tracked.StatusMessages.SelectMany(m => m.Messages), Has.Some.Contains("import evidence"));
                Assert.That(tracked.StatusMessages.SelectMany(m => m.Messages), Has.None.Contains("Unable to read"));
                counters.FailHistoryReads = false;
                if (restart)
                {
                    var item = tracked.DownloadItem.Clone();
                    item.FilePaths = null;
                    item.FileListConfidence = null;
                    tracked = counters.NewTracker().TrackDownload(new DownloadClientDefinition { Id = 1 }, item);
                    Assert.That(tracked, Is.Not.Null);
                }
                Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(tracked.StatusMessages.Select(m => m.Title), Has.Member(Path.GetFileName(late)));
                Assert.That(tracked.StatusMessages.Select(m => m.SourcePath), Has.Member(late));
            }, diskInventory: true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cleanup_partial_listing_must_not_authorize_removal(bool postgres)
        {
            Run(postgres, 1, 1, (subject, tracked, counters) =>
            {
                Assert.That(subject.VerifyImport(tracked, new List<ImportResult>()), Is.True);
                counters.FailDuringListing = true;
                Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(counters.IncompleteEvents, Is.Zero);
                counters.FailDuringListing = false;
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                Assert.That(tracked.StatusMessages, Is.Empty);
            }, diskInventory: true);
        }

        [Test]
        public void cleanup_missing_output_requires_a_readable_parent(
            [Values(false, true)] bool postgres, [Values(false, true)] bool parentMissing)
        {
            Run(postgres, 1, 1, (subject, tracked, counters) =>
            {
                Assert.That(subject.VerifyImport(tracked, new List<ImportResult>()), Is.True);
                if (parentMissing)
                {
                    tracked.DownloadItem.OutputPath = new OsPath(Path.Combine(tracked.DownloadItem.OutputPath.FullPath, "offline", "Dune"));
                }
                else
                {
                    Directory.Delete(tracked.DownloadItem.OutputPath.FullPath, true);
                }
                Assert.That(subject.CanRemoveDownload(tracked), Is.EqualTo(!parentMissing));
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(counters.IncompleteEvents, Is.Zero);
            }, diskInventory: true);
        }
    }
}
