using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectCompletedDownloadImportFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private sealed class RecordingProvideImportItemService : IProvideImportItemService
        {
            private readonly string _filePath;

            public RecordingProvideImportItemService(string filePath)
            {
                _filePath = filePath;
            }

            public DownloadClientItem ProvideImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
            {
                var clone = item.Clone();
                clone.OutputPath = new OsPath(_filePath);
                clone.FilePaths = new List<string> { _filePath };
                clone.FileListConfidence = DownloadClientFileListConfidence.Authoritative;
                return clone;
            }
        }

        private sealed class RecordingDownloadedBooksImportService : IDownloadedBooksImportService
        {
            public string LastPath { get; private set; }
            public DownloadClientItem LastDownloadClientItem { get; private set; }
            public RemoteBook LastRemoteBook { get; private set; }

            public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
            {
                LastPath = path;
                LastDownloadClientItem = downloadClientItem;
                LastRemoteBook = remoteBook;
                return new List<ImportResult>
                {
                    CreateImportedResult(501, 77, "A Civil Campaign")
                };
            }

            public List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw new NotImplementedException();

            public List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw new NotImplementedException();
        }

        private sealed class PassthroughDownloadImportModeResolver : IDownloadImportModeResolver
        {
            public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem) => requestedMode;
            public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem) => new(requestedMode, false);
            public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem) => false;
        }

        private sealed class StubTrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
        {
            public bool IsImported(TrackedDownload trackedDownload, List<EntityHistory> historyItems) => false;
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> HistoryItems { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    return HistoryItems;
                }

                if (targetMethod?.Name == nameof(IHistoryService.MostRecentForDownloadId))
                {
                    return HistoryItems.OrderByDescending(item => item.Date).FirstOrDefault();
                }

                throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_import_completed_direct_download_through_existing_file_path_pipeline()
        {
            var eventAggregator = new RecordingEventAggregator();
            var importService = new RecordingDownloadedBooksImportService();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "direct-import-1",
                    AuthorId = 77,
                    BookId = 501,
                    Date = DateTime.UtcNow
                }
            };

            var stagedFilePath = Path.Combine(Path.GetTempPath(), "chaptarr-direct-import", Guid.NewGuid().ToString("N"), "A Civil Campaign.epub");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedFilePath));
            File.WriteAllText(stagedFilePath, "ebook-body");
            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new RecordingProvideImportItemService(stagedFilePath),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported(),
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 7,
                Protocol = DownloadProtocol.Direct,
                State = TrackedDownloadState.ImportPending,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "direct-import-1",
                    Title = "Lois McMaster Bujold - A Civil Campaign [epub]",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 7,
                        Name = "Direct Download",
                        Protocol = DownloadProtocol.Direct
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 77, Name = "Lois McMaster Bujold" },
                    Books = new List<Book>
                    {
                        new()
                        {
                            Id = 501,
                            AuthorId = 77,
                            Title = "A Civil Campaign",
                            MediaType = BookMediaType.Ebook,
                            AnyEditionOk = true
                        }
                    },
                    Release = new ReleaseInfo
                    {
                        DownloadProtocol = DownloadProtocol.Direct,
                        Title = "Lois McMaster Bujold - A Civil Campaign [epub]"
                    }
                }
            };

            try
            {
                subject.Import(trackedDownload);

                Assert.That(importService.LastPath, Is.EqualTo(stagedFilePath));
                Assert.That(importService.LastDownloadClientItem, Is.SameAs(trackedDownload.DownloadItem));
                Assert.That(importService.LastRemoteBook, Is.SameAs(trackedDownload.RemoteBook));
                Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(eventAggregator.Events, Has.Some.InstanceOf<DownloadCompletedEvent>());
                Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());
            }
            finally
            {
                var directory = Path.GetDirectoryName(stagedFilePath);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        private static ImportResult CreateImportedResult(int bookId, int authorId, string title)
        {
            var author = new Author { Id = authorId, Name = "Test Author" };
            var book = new Book { Id = bookId, AuthorId = authorId, Author = author, Title = title, MediaType = BookMediaType.Ebook };
            var localBook = new LocalBook
            {
                Author = author,
                Book = book
            };

            return new ImportResult(new ImportDecision<LocalBook>(localBook));
        }
    }
}
