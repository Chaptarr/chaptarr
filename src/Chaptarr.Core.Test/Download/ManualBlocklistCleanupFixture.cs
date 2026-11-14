using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class ManualBlocklistCleanupFixture
    {
        [Test]
        public void manual_blocklist_does_not_grant_client_cleanup_permission([Values] bool postgres, [Values] bool removeFromClient)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var removals = 0;
                var stopped = 0;
                DownloadFailedEvent failure = null;
                DownloadHistoryService downloadHistory = null;
                DownloadEventHub hub = null;
                var events = Proxy<IEventAggregator>((m, a) =>
                {
                    if (a[0] is DownloadFailedEvent failed) { failure = failed; downloadHistory.Handle(failed); hub.Handle(failed); }
                    if (a[0] is DownloadIgnoredEvent ignored) downloadHistory.Handle(ignored);
                    return null;
                });
                var history = new HistoryService(new HistoryRepository(db, events), logger);
                var repository = new DownloadHistoryRepository(db, events);
                downloadHistory = new DownloadHistoryService(repository, history);
                var tracked = new TrackedDownload
                {
                    DownloadClient = 7,
                    State = TrackedDownloadState.Downloading,
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = "ABC", Title = "Dune", CanBeRemoved = true,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7, Protocol = DownloadProtocol.Usenet }
                    }
                };
                new HistoryRepository(db, events).Insert(new EntityHistory
                {
                    DownloadId = "ABC", SourceTitle = "Dune", AuthorId = 1, BookId = 1,
                    Date = DateTime.UtcNow, EventType = EntityHistoryEventType.Grabbed, Quality = new QualityModel(Quality.M4B)
                });
                var tracker = Proxy<ITrackedDownloadService>((m, a) =>
                {
                    if (m.Name == nameof(ITrackedDownloadService.Find)) return tracked;
                    if (m.Name == nameof(ITrackedDownloadService.StopTracking)) { stopped++; return null; }
                    throw new AssertionException(m.Name);
                });
                var client = Proxy<IDownloadClient>((m, a) =>
                {
                    if (m.Name == "get_Definition") return new DownloadClientDefinition { Id = 7, RemoveFailedDownloads = true };
                    if (m.Name == nameof(IDownloadClient.RemoveItem)) { removals++; return null; }
                    throw new AssertionException(m.Name);
                });
                var provider = Proxy<IProvideDownloadClient>((m, a) => client);
                var resolver = Proxy<IDownloadImportModeResolver>((m, a) => false);
                hub = new DownloadEventHub(null, provider, resolver, logger);
                var failures = new FailedDownloadService(history, tracker, events);
                var profiles = new QualityProfileService(Proxy<IProfileRepository>((m, a) => new List<QualityProfile>
                { new() { Id = 1, Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.M4B, Allowed = true } } } }), null, null, null, null, null, logger);
                var queue = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), resolver, null,
                    Proxy<IQueueService>((m, a) => new NzbDrone.Core.Queue.Queue { Id = 1, DownloadId = "ABC" }),
                    Proxy<IPendingReleaseService>((m, a) => null), profiles, tracker, failures,
                    new IgnoredDownloadService(events, logger), provider, null, null);

                queue.RemoveAction(1, removeFromClient, blocklist: true, skipRedownload: true);

                Assert.That(failure, Is.Not.Null, "Blocklisting must still publish the failure/history event");
                Assert.That(failure.SkipRedownload, Is.True);
                Assert.That(removals, Is.EqualTo(removeFromClient ? 1 : 0));
                Assert.That(stopped, Is.EqualTo(1));
                var restartedHistory = new DownloadHistoryService(new DownloadHistoryRepository(db, events), history);
                Assert.That(restartedHistory.GetLatestDownloadHistoryItem("ABC").EventType,
                    Is.EqualTo(removeFromClient ? DownloadHistoryEventType.DownloadFailed : DownloadHistoryEventType.DownloadIgnored));

                if (!removeFromClient)
                {
                    // A genuinely failed automatic download still obeys RemoveFailedDownloads.
                    tracked.State = TrackedDownloadState.DownloadFailedPending;
                    failures.ProcessFailed(tracked);
                    Assert.That(removals, Is.EqualTo(1));
                }
            });
        }
    }
}
