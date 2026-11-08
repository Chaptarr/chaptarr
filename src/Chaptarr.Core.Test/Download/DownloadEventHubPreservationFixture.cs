using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.TorrentRss;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadEventHubPreservationFixture
    {
        private sealed class StubDownloadImportModeResolver : IDownloadImportModeResolver
        {
            public bool Preserve { get; set; }

            public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem) => requestedMode;
            public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem) => new(requestedMode, Preserve);
            public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem, int fallbackIndexerId = 0, bool includeUnmanaged = true) => Preserve;
        }

        private sealed class StubDownloadClientProvider : IProvideDownloadClient
        {
            private readonly IDownloadClient _downloadClient;

            public StubDownloadClientProvider(IDownloadClient downloadClient)
            {
                _downloadClient = downloadClient;
            }

            public IDownloadClient GetDownloadClient(DownloadProtocol downloadProtocol, BookMediaType mediaType, int indexerId = 0, bool filterBlockedClients = false, HashSet<int> tags = null)
            {
                throw new NotImplementedException();
            }

            public IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false)
            {
                throw new NotImplementedException();
            }

            public IDownloadClient Get(int id) => _downloadClient;
        }

        private sealed class StubDownloadClient : IDownloadClient, IPreserveDownloadClientItemAfterImport
        {
            public int RemoveCalls { get; private set; }
            public bool? LastDeleteData { get; private set; }
            public int MarkImportedCalls { get; private set; }
            public bool PreserveAfterImport { get; set; }
            public int PreserveAfterImportChecks { get; private set; }
            public BookMediaType? LastPreserveAfterImportMediaType { get; private set; }

            public string Name => "Test Client";
            public Type ConfigContract => typeof(object);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Array.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; } = new DownloadClientDefinition
            {
                Id = 7,
                Name = "Test Client",
                RemoveCompletedDownloads = true,
                RemoveFailedDownloads = true
            };

            public DownloadProtocol Protocol => DownloadProtocol.Torrent;

            public Task<string> Download(RemoteBook remoteBook, IIndexer indexer)
            {
                throw new NotImplementedException();
            }

            public IEnumerable<DownloadClientItem> GetItems()
            {
                throw new NotImplementedException();
            }

            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
            {
                throw new NotImplementedException();
            }

            public void RemoveItem(DownloadClientItem item, bool deleteData)
            {
                RemoveCalls++;
                LastDeleteData = deleteData;
            }

            public DownloadClientInfo GetStatus()
            {
                throw new NotImplementedException();
            }

            public void MarkItemAsImported(DownloadClientItem downloadClientItem)
            {
                MarkImportedCalls++;
            }

            public bool ShouldPreserveItemAfterImport(DownloadClientItem downloadClientItem)
            {
                PreserveAfterImportChecks++;
                LastPreserveAfterImportMediaType = downloadClientItem.MediaType;
                return PreserveAfterImport;
            }

            public ValidationResult Test() => new();

            public object RequestAction(string stage, IDictionary<string, string> query)
            {
                throw new NotImplementedException();
            }
        }

        private static TrackedDownload TrackedDownload(BookMediaType? mediaType = null)
        {
            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 7,
                State = TrackedDownloadState.Imported,
                DownloadItem = new DownloadClientItem
                {
                    Title = "Test Download",
                    DownloadId = "ABC",
                    Status = DownloadItemStatus.Completed,
                    CanBeRemoved = true,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 7,
                        Name = "Test Client",
                        Protocol = DownloadProtocol.Torrent
                    }
                }
            };

            if (mediaType.HasValue)
            {
                trackedDownload.RemoteBook = new RemoteBook
                {
                    Books = new List<Book>
                    {
                        new Book { MediaType = mediaType.Value }
                    }
                };
            }

            return trackedDownload;
        }

        private static DownloadEventHub BuildSubject(StubDownloadClient downloadClient, IDownloadImportModeResolver resolver)
        {
            return new DownloadEventHub(
                Proxy<ICompletedDownloadService>((method, args) => true),
                new StubDownloadClientProvider(downloadClient),
                resolver,
                LogManager.GetLogger("DownloadEventHubPreservationFixture"));
        }

        [Test]
        public void should_honor_current_indexer_preservation_after_author_history_is_deleted(
            [Values(false, true)] bool postgres,
            [Values(false, true)] bool removeCompleted,
            [Values(false, true)] bool canBeRemoved)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var downloadClient = new StubDownloadClient();
                var clientDefinition = (DownloadClientDefinition)downloadClient.Definition;
                clientDefinition.RemoveCompletedDownloads = removeCompleted;
                var settings = new TorrentRssIndexerSettings();
                settings.SeedCriteria.NeverMoveOnImport = true;
                var indexer = new IndexerDefinition { Id = 99, Settings = settings };
                DownloadEventHub hub = null;
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    if (args[0] is DownloadCanBeRemovedEvent removable)
                    {
                        hub.Handle(removable);
                    }

                    return null;
                });
                var repository = new DownloadHistoryRepository(database, events);
                var history = new DownloadHistoryService(repository, null);
                var author = new Author { Id = 42, Name = "Test Author" };
                var tracked = TrackedDownload(BookMediaType.Audiobook);
                tracked.DownloadItem.CanBeRemoved = canBeRemoved;
                tracked.Added = DateTime.UtcNow.AddHours(-1);
                tracked.RemoteBook.Author = author;
                tracked.RemoteBook.Release = new ReleaseInfo { IndexerId = indexer.Id };
                repository.Insert(new DownloadHistory
                {
                    AuthorId = author.Id,
                    DownloadId = tracked.DownloadItem.DownloadId,
                    SourceTitle = tracked.DownloadItem.Title,
                    Date = tracked.Added.Value,
                    EventType = DownloadHistoryEventType.DownloadGrabbed,
                    IndexerId = indexer.Id,
                    Protocol = DownloadProtocol.Torrent
                });
                repository.Insert(new DownloadHistory
                {
                    AuthorId = author.Id,
                    DownloadId = tracked.DownloadItem.DownloadId,
                    SourceTitle = tracked.DownloadItem.Title,
                    Date = DateTime.UtcNow,
                    EventType = DownloadHistoryEventType.DownloadImported,
                    Protocol = DownloadProtocol.Torrent
                });
                var cache = new CacheManager();
                var tracker = new TrackedDownloadService(null, cache, null, events, history, null, logger,
                    NoopDownloadClientFileSnapshotService.Instance);
                cache.GetCache<TrackedDownload>(typeof(TrackedDownloadService)).Set(tracked.DownloadItem.DownloadId, tracked);
                var resolver = new DownloadImportModeResolver(history,
                    Proxy<IIndexerFactory>((method, args) => (int)args[0] == indexer.Id ? indexer : null),
                    Proxy<IDownloadClientFactory>((method, args) => clientDefinition), logger);
                hub = BuildSubject(downloadClient, resolver);
                var processor = new DownloadProcessingService(Proxy<IConfigService>((method, args) => false),
                    null, null, tracker, events, logger);

                processor.Execute(new ProcessMonitoredDownloadsCommand());
                Assert.That(downloadClient.RemoveCalls, Is.Zero);

                var deleted = new AuthorDeletedEvent(author, deleteFiles: false, addImportListExclusion: false);
                history.Handle(deleted);
                tracker.Handle(deleted);
                Assert.That(repository.FindByDownloadId(tracked.DownloadItem.DownloadId), Is.Empty);
                Assert.That(tracked.RemoteBook.Author, Is.Null);
                Assert.That(tracked.RemoteBook.Books, Is.Empty);
                var refreshed = tracker.TrackDownload(clientDefinition, tracked.DownloadItem.Clone());
                Assert.That(refreshed, Is.SameAs(tracked));
                Assert.That(refreshed.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(refreshed.RemoteBook.Release.IndexerId, Is.EqualTo(indexer.Id));

                processor.Execute(new ProcessMonitoredDownloadsCommand());
                Assert.That(downloadClient.RemoveCalls, Is.Zero, "Deleting the author must not revoke indexer preservation");

                settings.SeedCriteria.NeverMoveOnImport = false;
                processor.Execute(new ProcessMonitoredDownloadsCommand());
                Assert.That(downloadClient.RemoveCalls, Is.EqualTo(removeCompleted && canBeRemoved ? 1 : 0));
                Assert.That(tracked.DownloadItem.Removed, Is.EqualTo(removeCompleted && canBeRemoved));
            });
        }

        [Test]
        public void should_remove_normal_completed_download_when_client_allows_removal()
        {
            var downloadClient = new StubDownloadClient();
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            var tracked = TrackedDownload();
            subject.Handle(new DownloadCompletedEvent(tracked, 1));
            Assert.That(downloadClient.RemoveCalls, Is.Zero, "Completion publishes before the cleanup pass");
            subject.Handle(new DownloadCanBeRemovedEvent(tracked));

            Assert.That(downloadClient.MarkImportedCalls, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(1));
            Assert.That(downloadClient.LastDeleteData, Is.True);
        }

        [Test]
        public void should_not_remove_preserved_completed_download_when_client_allows_removal()
        {
            var downloadClient = new StubDownloadClient();
            var resolver = new StubDownloadImportModeResolver { Preserve = true };
            var subject = BuildSubject(downloadClient, resolver);

            var tracked = TrackedDownload();
            subject.Handle(new DownloadCompletedEvent(tracked, 1));
            Assert.That(downloadClient.RemoveCalls, Is.Zero, "Completion publishes before the cleanup pass");
            subject.Handle(new DownloadCanBeRemovedEvent(tracked));

            Assert.That(downloadClient.MarkImportedCalls, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_remove_post_import_preserved_completed_download_when_client_allows_removal()
        {
            var downloadClient = new StubDownloadClient { PreserveAfterImport = true };
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            var tracked = TrackedDownload();
            subject.Handle(new DownloadCompletedEvent(tracked, 1));
            Assert.That(downloadClient.RemoveCalls, Is.Zero, "Completion publishes before the cleanup pass");
            subject.Handle(new DownloadCanBeRemovedEvent(tracked));

            Assert.That(downloadClient.MarkImportedCalls, Is.EqualTo(1));
            Assert.That(downloadClient.PreserveAfterImportChecks, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_pass_remote_book_media_type_to_post_import_preserve_check()
        {
            var downloadClient = new StubDownloadClient { PreserveAfterImport = true };
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            var tracked = TrackedDownload(BookMediaType.Ebook);
            subject.Handle(new DownloadCompletedEvent(tracked, 1));
            Assert.That(downloadClient.RemoveCalls, Is.Zero, "Completion publishes before the cleanup pass");
            subject.Handle(new DownloadCanBeRemovedEvent(tracked));

            Assert.That(downloadClient.LastPreserveAfterImportMediaType, Is.EqualTo(BookMediaType.Ebook));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_remove_preserved_download_when_later_removal_event_fires()
        {
            var downloadClient = new StubDownloadClient();
            var resolver = new StubDownloadImportModeResolver { Preserve = true };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadCanBeRemovedEvent(TrackedDownload()));

            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_remove_post_import_preserved_download_when_later_removal_event_fires()
        {
            var downloadClient = new StubDownloadClient { PreserveAfterImport = true };
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadCanBeRemovedEvent(TrackedDownload()));

            Assert.That(downloadClient.PreserveAfterImportChecks, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_remove_preserved_failed_download()
        {
            var downloadClient = new StubDownloadClient();
            var resolver = new StubDownloadImportModeResolver { Preserve = true };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadFailedEvent { TrackedDownload = TrackedDownload() });

            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_use_post_import_preservation_for_failed_downloads()
        {
            var downloadClient = new StubDownloadClient { PreserveAfterImport = true };
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadFailedEvent { TrackedDownload = TrackedDownload() });

            Assert.That(downloadClient.PreserveAfterImportChecks, Is.EqualTo(0));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(1));
        }
    }
}
