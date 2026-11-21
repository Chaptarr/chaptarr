using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TorrentPreservationLifecycleFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static RemoteBook Remote(bool preserve)
        {
            return new RemoteBook
            {
                Author = new Author { Id = 42, Name = "Test Author" },
                Books = new List<Book> { new() { Id = 77, AuthorId = 42, MediaType = BookMediaType.Audiobook } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) },
                Release = new TorrentInfo { IndexerId = 9, Indexer = "Private Tracker", Title = "Test Author - Test Book" },
                SeedConfiguration = TorrentPreservationNativeFixture.SeedProvider(preserve).GetSeedConfiguration(9, false)
            };
        }

        private static DownloadClientItem Item(string hash)
        {
            return new DownloadClientItem
            {
                DownloadId = hash, Title = "Test Author - Test Book", CanBeRemoved = true, CanMoveFiles = true,
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 5, Protocol = DownloadProtocol.Torrent }
            };
        }

        private static void Grab(DownloadHistoryService history, string hash, bool preserve)
        {
            history.Handle(new BookGrabbedEvent(Remote(preserve))
            {
                DownloadId = hash, DownloadClientId = 5, DownloadClient = "Test Client", DownloadClientName = "Test Client"
            });
        }

        private static DownloadImportModeResolver Resolver(DownloadHistoryService history, bool unmanaged = false)
        {
            return new DownloadImportModeResolver(history, Proxy<IIndexerFactory>((method, args) => null),
                Proxy<IDownloadClientFactory>((method, args) => new DownloadClientDefinition { CopyUnmanagedDownloads = unmanaged }), Logger);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void regrab_should_keep_latest_target_without_losing_earlier_preservation(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new DownloadHistoryRepository(database, Proxy<IEventAggregator>((method, args) => null));
                var history = new DownloadHistoryService(repository, null);
                Grab(history, "KEEP", true);
                Grab(history, "KEEP", false);
                Grab(history, "ORDINARY", false);
                var grabs = repository.FindByDownloadId("KEEP");
                var latest = grabs.First();
                latest.BookId = 88;
                latest.IndexerId = 10;
                latest.Date = grabs.Last().Date; // Equal timestamps must still keep the newest identity.
                repository.Update(latest);

                var reloaded = new DownloadHistoryService(new DownloadHistoryRepository(database, Proxy<IEventAggregator>((method, args) => null)), null);
                var effective = reloaded.GetLatestGrab("keep");
                Assert.That(effective.Id, Is.EqualTo(latest.Id));
                Assert.That(effective.BookId, Is.EqualTo(88));
                Assert.That(effective.IndexerId, Is.EqualTo(10));
                Assert.That(effective.ShouldKeepSeeding(), Is.True);
                Assert.That(Resolver(reloaded).ShouldPreserveDownloadClientItem(Item("KEEP")), Is.True);
                Assert.That(Resolver(reloaded).ShouldPreserveDownloadClientItem(Item("ORDINARY")), Is.False);
                var seeds = new DownloadSeedConfigProvider(reloaded,
                    Proxy<ISeedConfigProvider>((method, args) => throw new AssertionException("Earlier preservation must survive a new indexer")), new CacheManager(), Logger);
                Assert.That(seeds.GetSeedConfiguration("keep").KeepSeeding, Is.True);
                Assert.That(repository.Get(latest.Id).ShouldKeepSeeding(), Is.False, "Reading effective protection must not rewrite history");
            });
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void stored_choice_should_survive_restart_and_author_indexer_deletion(bool postgres, bool deleteAuthor)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var repository = new DownloadHistoryRepository(database, events);
                var history = new DownloadHistoryService(repository, null);
                Grab(history, "KEEP", true);
                Grab(history, "ORDINARY", false);
                if (deleteAuthor) history.Handle(new AuthorDeletedEvent(Remote(true).Author, false, false));

                var reloaded = new DownloadHistoryService(new DownloadHistoryRepository(database, events), null);
                var saved = reloaded.GetLatestGrab("KEEP");
                var resolver = Resolver(reloaded);
                var seeds = new DownloadSeedConfigProvider(reloaded,
                    Proxy<ISeedConfigProvider>((method, args) => throw new AssertionException("The deleted indexer must not be needed")), new CacheManager(), Logger);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.ShouldKeepSeeding(), Is.True);
                    Assert.That(saved.AuthorId, Is.EqualTo(deleteAuthor ? 0 : 42));
                    Assert.That(saved.BookId, Is.EqualTo(deleteAuthor ? 0 : 77));
                    Assert.That(resolver.Resolve(ImportMode.Auto, Item("KEEP")), Is.EqualTo(ImportMode.Copy));
                    Assert.That(resolver.Resolve(ImportMode.Move, Item("KEEP")), Is.EqualTo(ImportMode.Copy));
                    Assert.That(resolver.ShouldPreserveDownloadClientItem(Item("KEEP")), Is.True);
                    Assert.That(resolver.ShouldPreserveDownloadClientItem(Item("ORDINARY")), Is.False);
                    Assert.That(seeds.GetSeedConfiguration("keep").KeepSeeding, Is.True);
                    Assert.That(seeds.GetSeedConfiguration("keep").SeedTime, Is.Null);
                    Assert.That(repository.All(), Has.Count.EqualTo(deleteAuthor ? 1 : 2));
                });
                var removals = 0;
                var markedImported = 0;
                var cleanupChecks = 0;
                var client = Proxy<IDownloadClient>((method, args) =>
                {
                    if (method.Name == "get_Definition") return new DownloadClientDefinition { RemoveCompletedDownloads = true, RemoveFailedDownloads = true };
                    if (method.Name == nameof(IDownloadClient.RemoveItem)) removals++;
                    if (method.Name == nameof(IDownloadClient.MarkItemAsImported)) markedImported++;
                    return null;
                });
                var completion = Proxy<ICompletedDownloadService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(ICompletedDownloadService.CanRemoveDownload)));
                    cleanupChecks++;
                    return true;
                });
                var hub = new DownloadEventHub(completion, Proxy<IProvideDownloadClient>((method, args) => client), resolver, Logger);
                var tracked = new TrackedDownload { DownloadClient = 5, DownloadItem = Item("keep"), State = TrackedDownloadState.Imported };
                tracked.DownloadItem.Status = DownloadItemStatus.Completed;
                hub.Handle(new DownloadCompletedEvent(tracked, 0));
                Assert.That(markedImported, Is.EqualTo(1));
                hub.Handle(new DownloadCanBeRemovedEvent(tracked));
                hub.Handle(new DownloadFailedEvent { TrackedDownload = tracked });
                Assert.That(cleanupChecks, Is.Zero, "Preservation stops cleanup before import evidence is consulted");
                Assert.That(removals, Is.Zero);

                if (deleteAuthor)
                {
                    var readded = Remote(false);
                    readded.Author.Id = 43;
                    readded.Author.Name = "Frank Herbert";
                    readded.Books.Single().Id = 78;
                    readded.Books.Single().AuthorId = 43;
                    readded.Books.Single().Title = "Dune";
                    var tracker = new TrackedDownloadService(
                        Proxy<NzbDrone.Core.Parser.IParsingService>((method, args) => readded), new CacheManager(),
                        Proxy<NzbDrone.Core.History.IHistoryService>((method, args) => new List<NzbDrone.Core.History.EntityHistory>()),
                        events, reloaded,
                        Proxy<NzbDrone.Core.CustomFormats.ICustomFormatCalculationService>((method, args) => new List<NzbDrone.Core.CustomFormats.CustomFormat>()),
                        Logger, Proxy<IDownloadClientFileSnapshotService>((method, args) => null));
                    var returnedItem = Item("KEEP");
                    returnedItem.Title = "Frank Herbert - Dune [M4B]";
                    Assert.That(NzbDrone.Core.Parser.Parser.ParseBookTitle(returnedItem.Title), Is.Not.Null);
                    var restored = tracker.TrackDownload(new DownloadClientDefinition { Id = 5 }, returnedItem);
                    Assert.That(restored, Is.Not.Null);
                    Assert.That(restored.RemoteBook.Books.Single().Id, Is.EqualTo(78),
                        "The newly matched row is independent of the preserved grab's deleted BookId");
                    Assert.That(restored.State, Is.EqualTo(TrackedDownloadState.Downloading));
                    Assert.That(resolver.Resolve(ImportMode.Auto, restored.DownloadItem), Is.EqualTo(ImportMode.Copy));
                    hub.Handle(new DownloadCanBeRemovedEvent(restored));
                    Assert.That(removals, Is.Zero, "A restart and new local row must not make the preserved source disposable");
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void purge_readd_without_retained_files_should_keep_only_preserved_grab(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var repository = new DownloadHistoryRepository(database, events);
                var entityHistory = new NzbDrone.Core.History.HistoryService(
                    new NzbDrone.Core.History.HistoryRepository(database, events), Logger, repository);
                var history = new DownloadHistoryService(repository, entityHistory);
                Grab(history, "KEEP", true);
                Grab(history, "ORDINARY", false);
                var deletion = new AuthorDeletedEvent(Remote(true).Author, false, false, true, Array.Empty<int>());
                entityHistory.Handle(deletion);
                history.Handle(deletion);
                var reloaded = new DownloadHistoryService(new DownloadHistoryRepository(database, events), null);
                Assert.That(repository.All(), Has.Count.EqualTo(1));
                Assert.That(Resolver(reloaded).ShouldPreserveDownloadClientItem(Item("keep")), Is.True);
            });
        }

        [Test]
        public void queue_removal_should_preflight_protected_torrents_before_any_mutation(
            [Values(false, true)] bool postgres, [Values(false, true)] bool bulk)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new DownloadHistoryRepository(database, Proxy<IEventAggregator>((method, args) => null));
                var history = new DownloadHistoryService(repository, null);
                Grab(history, "KEEP", true);
                Grab(history, "ORDINARY", false);
                var tracked = new Dictionary<string, TrackedDownload>
                {
                    ["KEEP"] = new() { DownloadClient = 5, DownloadItem = Item("KEEP"), RemoteBook = Remote(true) },
                    ["ORDINARY"] = new() { DownloadClient = 5, DownloadItem = Item("ORDINARY"), RemoteBook = Remote(false) }
                };
                foreach (var download in tracked.Values)
                {
                    download.DownloadItem.DownloadClientInfo.Type = nameof(Deluge);
                    download.DownloadItem.DownloadClientInfo.Name = "Home torrent client";
                }
                var mutations = new List<string>();
                var client = Proxy<IDownloadClient>((method, args) => { mutations.Add(method.Name); return null; });
                var pendingRow = new NzbDrone.Core.Queue.Queue { Id = 1 };
                var pending = Proxy<IPendingReleaseService>((method, args) => method.Name switch
                {
                    nameof(IPendingReleaseService.FindPendingQueueItem) => null,
                    nameof(IPendingReleaseService.GetPendingQueue) => new List<NzbDrone.Core.Queue.Queue> { pendingRow },
                    nameof(IPendingReleaseService.RemovePendingQueueItems) => VerifyNoPendingRemoval(args),
                    _ => throw new AssertionException("Pending work changed before protection was checked")
                });
                object VerifyNoPendingRemoval(object[] args)
                {
                    Assert.That((IEnumerable<NzbDrone.Core.Queue.Queue>)args[0], Is.Empty);
                    return null;
                }
                var queue = Proxy<IQueueService>((method, args) => new NzbDrone.Core.Queue.Queue
                {
                    Id = (int)args[0], DownloadId = (int)args[0] == 3 ? "KEEP" : "ORDINARY"
                });
                var tracking = Proxy<ITrackedDownloadService>((method, args) =>
                {
                    if (method.Name == nameof(ITrackedDownloadService.Find)) return tracked[(string)args[0]];
                    mutations.Add(method.Name);
                    return null;
                });
                var profiles = new QualityProfileService(Proxy<IProfileRepository>((method, args) => new List<QualityProfile>
                    { new() { Id = 1, Items = new() { new() { Quality = Quality.M4B, Allowed = true } } } }),
                    null, null, null, null, null, Logger);
                var ignores = Proxy<IIgnoredDownloadService>((method, args) => { mutations.Add("ignore"); return true; });
                var conversions = Proxy<IConversionJobService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IConversionJobService.PrepareArtifactRemoval)));
                    mutations.Add("prepare-cleanup");
                    return (Action)(() => mutations.Add("cleanup"));
                });
                var failures = Proxy<IFailedDownloadService>((method, args) => { mutations.Add("blocklist"); return null; });
                var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Resolver(history), null, queue, pending, profiles, tracking,
                    failures, ignores, Proxy<IProvideDownloadClient>((method, args) => client), null, null, conversions);

                var error = Assert.Throws<Chaptarr.Http.REST.BadRequestException>(() =>
                {
                    if (bulk) controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 1, 2, 3 } },
                        removeFromClient: true, blocklist: true);
                    else controller.RemoveAction(3, removeFromClient: true, blocklist: true);
                });
                Assert.That(error.Message, Does.Contain("Ignore Download"));
                Assert.That(error.Message, Does.Contain(tracked["KEEP"].DownloadItem.Title ?? "KEEP"));
                Assert.That(mutations, Is.Empty);
                controller.RemoveAction(3, removeFromClient: false);
                Assert.That(mutations, Is.EqualTo(new[] { "prepare-cleanup", "ignore", "cleanup", nameof(ITrackedDownloadService.StopTracking) }));
                mutations.Clear();
                controller.RemoveAction(2, removeFromClient: true);
                Assert.That(mutations, Is.EqualTo(new[] { "prepare-cleanup",
                    nameof(IDownloadClient.RemoveItem),
                    "cleanup", nameof(ITrackedDownloadService.StopTracking) }));
                mutations.Clear();
                if (bulk) controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 2, 3 } }, removeFromClient: false, changeCategory: true);
                else controller.RemoveAction(3, removeFromClient: false, changeCategory: true);
                var expectedSwap = new[] { "prepare-cleanup", nameof(IDownloadClient.MarkItemAsImported), "cleanup", nameof(ITrackedDownloadService.StopTracking) };
                Assert.That(mutations, Is.EqualTo(bulk ? expectedSwap.Concat(expectedSwap) : expectedSwap),
                    "Preserved and ordinary torrents must reuse the client's own safe category change");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void tracker_only_guard_should_not_expand_unmanaged_client_policy(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var history = new DownloadHistoryService(new DownloadHistoryRepository(database, Proxy<IEventAggregator>((method, args) => null)), null);
                var resolver = Resolver(history, unmanaged: true);
                Assert.That(resolver.ShouldPreserveDownloadClientItem(Item("UNMANAGED")), Is.True);
                Assert.That(resolver.ShouldPreserveDownloadClientItem(Item("UNMANAGED"), includeUnmanaged: false), Is.False);
            });
        }
    }
}
