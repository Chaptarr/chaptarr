using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileServiceDeleteFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public readonly List<IEvent> Events = new List<IEvent>();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private sealed class RecordingIngestQueueRepository : IIngestQueueRepository
        {
            public readonly List<string> PurgedPrefixes = new List<string>();

            public void BeginSession(int commandId) => throw new NotImplementedException();
            public void InsertBatch(List<IngestQueueItem> items) => throw new NotImplementedException();
            public List<IngestQueueItem> GetQueuedItems(int limit = 100) => throw new NotImplementedException();
            public List<IngestQueueItem> GetQueuedItemsUnderPath(string pathPrefix, int limit = 100, int afterId = 0) => throw new NotImplementedException();
            public int GetActiveCountUnderPath(string pathPrefix) => throw new NotImplementedException();
            public List<IngestQueueStatusCount> GetActiveStatusCountsUnderPath(string pathPrefix) => throw new NotImplementedException();
            public List<IngestQueueItem> GetActiveItemsUnderPath(string pathPrefix, int limit = 20) => throw new NotImplementedException();
            public List<IngestQueueItem> GetActiveItems(int limit = 1000, int afterId = 0) => throw new NotImplementedException();
            public List<IngestQueueItem> GetActiveItemsForSweepUnderPath(string pathPrefix, int limit = 1000, int afterId = 0) => throw new NotImplementedException();

            public int RecoverStaleInProgress(string pathPrefix, int staleMinutes = 10) => throw new NotImplementedException();
            public int RecoverInProgressUpdatedBefore(string pathPrefix, long updatedBefore, string error = null) => throw new NotImplementedException();
            public bool TryClaimItem(int id, out IngestQueueItem item) => throw new NotImplementedException();
            public List<IngestQueueItem> TryClaimUnit(string folderPath) => throw new NotImplementedException();
            public void UpdateStatus(int id, string status, string error = null) => throw new NotImplementedException();
            public void UpdateBatchTagsJson(IEnumerable<(int Id, string TagsJson)> items) => throw new NotImplementedException();
            public void UpdateBatchTagsAndDuration(IEnumerable<(int Id, string TagsJson, int? DurationSeconds)> items) => throw new NotImplementedException();
            public void UpdateBatchStatus(List<int> ids, string status) => throw new NotImplementedException();
            public void RequeueInProgress(List<int> ids, string error = null) => throw new NotImplementedException();
            public int GetQueueCount() => throw new NotImplementedException();
            public int RequeueFailedOrUnmappedUnderPath(string pathPrefix) => throw new NotImplementedException();
            public int RequeueFailedPaths(IEnumerable<string> paths) => throw new NotImplementedException();
            public int PurgeUnderPath(string pathPrefix)
            {
                PurgedPrefixes.Add(pathPrefix);
                return 1;
            }

            public int PurgePaths(IEnumerable<string> paths) => throw new NotImplementedException();
            public void PurgeOldCompleted(int daysToKeep = 14) => throw new NotImplementedException();
            public void RecordImportResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null) => throw new NotImplementedException();
            public void CompleteItemWithResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null, string statusError = null) => throw new NotImplementedException();
            public List<ImportResult> GetImportResults(int? commandId = null) => throw new NotImplementedException();
        }

        private class MediaFileRepositoryProxy : DispatchProxy
        {
            public List<BookFile> DeletedMany { get; } = new List<BookFile>();
            public List<BookFile> ReplacementAdditions { get; } = new List<BookFile>();
            public List<BookFile> ReplacementRemovals { get; } = new List<BookFile>();
            public BookFile DeletedSingle { get; private set; }
            public Func<int, BookFile> GetByIdHandler { get; set; }
            public Func<IEnumerable<int>, IEnumerable<BookFile>> GetByIdsHandler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IMediaFileRepository.Delete):
                        DeletedSingle = (BookFile)args[0];
                        return null;

                    case nameof(IMediaFileRepository.DeleteMany):
                        DeletedMany.Clear();
                        DeletedMany.AddRange((IEnumerable<BookFile>)args[0]);
                        return null;

                    case nameof(IMediaFileRepository.ReplaceMany):
                        ReplacementAdditions.Clear();
                        ReplacementAdditions.AddRange((IEnumerable<BookFile>)args[0]);
                        ReplacementRemovals.Clear();
                        ReplacementRemovals.AddRange((IEnumerable<BookFile>)args[1]);
                        foreach (var file in ReplacementAdditions.Where(file => file.Id == 0))
                        {
                            file.Id = 1000 + ReplacementAdditions.IndexOf(file);
                        }

                        return null;

                    case nameof(IMediaFileRepository.UnlinkFilesByBook):
                    case nameof(IMediaFileRepository.DeleteFilesByBook):
                        return null;

                    case nameof(IMediaFileRepository.Get):
                        if (args?.Length == 1 && args[0] is int id)
                        {
                            return GetByIdHandler?.Invoke(id);
                        }

                        if (args?.Length == 1 && args[0] is IEnumerable<int> ids)
                        {
                            return GetByIdsHandler?.Invoke(ids) ?? Enumerable.Empty<BookFile>();
                        }

                        break;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IMediaFileRepository).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void delete_should_purge_ingest_queue_for_unmapped_files_without_publishing_delete_event()
        {
            var repo = DispatchProxy.Create<IMediaFileRepository, MediaFileRepositoryProxy>();
            var ingestQueue = new RecordingIngestQueueRepository();
            var events = new RecordingEventAggregator();
            var sut = new MediaFileService(repo, events, ingestQueue, LogManager.GetLogger("test"));

            var file = new BookFile
            {
                Id = 42,
                Path = "/books/books/Test Author/Test Book/Test Book.epub",
                EditionId = 0
            };

            sut.Delete(file, DeleteMediaFileReason.MissingFromDisk);

            Assert.That(ingestQueue.PurgedPrefixes, Is.EqualTo(new[] { file.Path }));
            Assert.That(events.Events.OfType<BookFileDeletedEvent>(), Is.Empty);
        }

        [Test]
        public void delete_many_should_publish_hydrated_delete_events_for_mapped_files()
        {
            var repo = DispatchProxy.Create<IMediaFileRepository, MediaFileRepositoryProxy>();
            var repoProxy = (MediaFileRepositoryProxy)(object)repo;
            repoProxy.GetByIdsHandler = ids =>
            {
                return ids.Select(id => new BookFile
                {
                    Id = id,
                    Path = $"/books/books/Test Author/Test Book {id}/Test Book {id}.epub",
                    EditionId = 100 + id,
                    Author = new Author { Id = 7, Name = "Test Author", Path = "/books/books/Test Author" },
                    Edition = new Edition
                    {
                        Id = 100 + id,
                        BookId = 200 + id,
                        Book = new Book { Id = 200 + id, Title = $"Test Book {id}" }
                    }
                });
            };

            var ingestQueue = new RecordingIngestQueueRepository();
            var events = new RecordingEventAggregator();
            var sut = new MediaFileService(repo, events, ingestQueue, LogManager.GetLogger("test"));

            var files = new List<BookFile>
            {
                new BookFile { Id = 1, Path = "/books/books/Test Author/Test Book 1/Test Book 1.epub", EditionId = 11 },
                new BookFile { Id = 2, Path = "/books/books/Test Author/Test Book 2/Test Book 2.epub", EditionId = 12 }
            };

            sut.DeleteMany(files, DeleteMediaFileReason.MissingFromDisk);

            var deleteEvents = events.Events.OfType<BookFileDeletedEvent>().ToList();

            Assert.That(ingestQueue.PurgedPrefixes, Is.EquivalentTo(files.Select(f => f.Path)));
            Assert.That(deleteEvents, Has.Count.EqualTo(2));
            Assert.That(deleteEvents.All(e => e.BookFile.Author?.Id == 7), Is.True);
            Assert.That(deleteEvents.Select(e => e.BookFile.Edition?.Book?.Title), Is.EquivalentTo(new[] { "Test Book 1", "Test Book 2" }));
        }

        [Test]
        public void replace_many_should_publish_events_only_after_repository_swap_succeeds()
        {
            var repo = DispatchProxy.Create<IMediaFileRepository, MediaFileRepositoryProxy>();
            var repoProxy = (MediaFileRepositoryProxy)(object)repo;
            var oldFile = new BookFile
            {
                Id = 42,
                Path = "/books/Test Author/Test Book/old.epub",
                EditionId = 142
            };
            repoProxy.GetByIdsHandler = _ => new[]
            {
                new BookFile
                {
                    Id = oldFile.Id,
                    Path = oldFile.Path,
                    EditionId = oldFile.EditionId,
                    Author = new Author { Id = 7, Name = "Test Author" },
                    Edition = new Edition
                    {
                        Id = oldFile.EditionId,
                        BookId = 242,
                        Book = new Book { Id = 242, Title = "Test Book" }
                    }
                }
            };

            var replacement = new BookFile
            {
                Path = "/books/Test Author/Test Book/new.epub",
                EditionId = 143
            };
            var ingestQueue = new RecordingIngestQueueRepository();
            var events = new RecordingEventAggregator();
            var sut = new MediaFileService(repo, events, ingestQueue, LogManager.GetLogger("test"));

            sut.ReplaceMany(
                new List<BookFile> { replacement },
                new List<BookFile> { oldFile },
                DeleteMediaFileReason.Upgrade);

            Assert.That(repoProxy.ReplacementAdditions, Is.EqualTo(new[] { replacement }));
            Assert.That(repoProxy.ReplacementRemovals, Is.EqualTo(new[] { oldFile }));
            Assert.That(ingestQueue.PurgedPrefixes, Is.EqualTo(new[] { oldFile.Path }));
            Assert.That(events.Events.OfType<BookFileDeletedEvent>().Single().BookFile.Edition.Book.Title, Is.EqualTo("Test Book"));
            Assert.That(events.Events.OfType<BookFilesAddedEvent>().Single().BookFiles, Is.EqualTo(new[] { replacement }));
        }

        [Test]
        public void author_delete_should_purge_ingest_queue_under_author_paths()
        {
            var repo = DispatchProxy.Create<IMediaFileRepository, MediaFileRepositoryProxy>();
            var ingestQueue = new RecordingIngestQueueRepository();
            var events = new RecordingEventAggregator();
            var sut = new MediaFileService(repo, events, ingestQueue, LogManager.GetLogger("test"));

            var author = new Author
            {
                Id = 7,
                Name = "Test Author",
                Path = "/books/books/Test Author",
                AudiobookPath = "/books/books/Test Author",
                EbookPath = "/ebooks/books/Test Author"
            };

            sut.Handle(new AuthorDeletedEvent(author, deleteFiles: false, addImportListExclusion: false));

            Assert.That(ingestQueue.PurgedPrefixes, Is.EquivalentTo(new[]
            {
                "/books/books/Test Author",
                "/ebooks/books/Test Author"
            }));
        }

        [Test]
        public void book_delete_should_purge_ingest_queue_for_left_behind_book_files()
        {
            var repo = DispatchProxy.Create<IMediaFileRepository, MediaFileRepositoryProxy>();
            var ingestQueue = new RecordingIngestQueueRepository();
            var events = new RecordingEventAggregator();
            var sut = new MediaFileService(repo, events, ingestQueue, LogManager.GetLogger("test"));

            var book = new Book
            {
                Id = 10,
                Title = "Test Book",
                BookFiles = new List<BookFile>
                {
                    new BookFile { Id = 1, Path = "/books/books/Test Author/Test Book/Test Book.m4b", EditionId = 100 }
                }
            };

            sut.HandleAsync(new BookDeletedEvent(book, deleteFiles: false, addImportListExclusion: false));

            Assert.That(ingestQueue.PurgedPrefixes, Is.EqualTo(new[] { "/books/books/Test Author/Test Book/Test Book.m4b" }));
        }
    }
}
