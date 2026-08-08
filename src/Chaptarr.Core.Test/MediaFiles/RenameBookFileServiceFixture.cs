using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class RenameBookFileServiceFixture
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

        private sealed class StubAuthorService : IAuthorService
        {
            public Author Author { get; set; }

            public Author GetAuthor(int authorId) => Author;
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => new List<Author> { Author };
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public List<BookFile> Files { get; set; } = new();
            public List<BookFile> Updated { get; } = new();

            public List<BookFile> GetFilesByAuthor(int authorId) => Files;
            public List<BookFile> Get(IEnumerable<int> ids)
            {
                var requested = ids.ToHashSet();
                return Files.Where(f => requested.Contains(f.Id)).ToList();
            }

            public void Update(BookFile bookFile) => Updated.Add(bookFile);
            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class RecordingMoveBookFiles : IMoveBookFiles
        {
            public List<int> MovedFileIds { get; } = new();
            public Func<BookFile, string> DestinationFactory { get; set; }

            public BookFile MoveBookFile(BookFile bookFile, Author author, bool forceRename = false, RenameBatchContext renameBatchContext = null)
            {
                MovedFileIds.Add(bookFile.Id);
                var destination = DestinationFactory?.Invoke(bookFile);
                if (destination != null)
                {
                    bookFile.Path = destination;
                }

                return bookFile;
            }

            public BookFile MoveBookFile(BookFile bookFile, NzbDrone.Core.Parser.Model.LocalBook localBook) => throw new NotImplementedException();
            public BookFile CopyBookFile(BookFile bookFile, NzbDrone.Core.Parser.Model.LocalBook localBook) => throw new NotImplementedException();
            public string GetImportDestinationPath(BookFile bookFile, NzbDrone.Core.Parser.Model.LocalBook localBook) => throw new NotImplementedException();
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FolderExists))
                {
                    return false;
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private static BookFile BookFile(int id, string path, Quality quality, string mediaType)
        {
            return new BookFile
            {
                Id = id,
                Path = path,
                EditionId = 10,
                Quality = new QualityModel(quality),
                MediaType = mediaType,
                CalibreId = 0
            };
        }

        private static RenameBookFileService CreateService(Author author, StubMediaFileService mediaFileService, RecordingMoveBookFiles mover, RecordingEventAggregator eventAggregator)
        {
            return new RenameBookFileService(
                new StubAuthorService { Author = author },
                mediaFileService,
                mover,
                eventAggregator,
                DispatchProxy.Create<IBuildFileNames, ThrowingProxy<IBuildFileNames>>(),
                DispatchProxy.Create<INamingConfigService, ThrowingProxy<INamingConfigService>>(),
                DispatchProxy.Create<IAuthorFolderPathResolver, ThrowingProxy<IAuthorFolderPathResolver>>(),
                DispatchProxy.Create<IEbookColocationPlanner, ThrowingProxy<IEbookColocationPlanner>>(),
                DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_not_publish_rename_events_when_mover_leaves_path_unchanged()
        {
            var author = new Author { Id = 1, Name = "Joe Abercrombie" };
            var mediaFileService = new StubMediaFileService
            {
                Files = new List<BookFile>
                {
                    BookFile(1, "/books/Joe/file.epub", Quality.EPUB, "ebook")
                }
            };

            var eventAggregator = new RecordingEventAggregator();
            var mover = new RecordingMoveBookFiles();
            var service = CreateService(author, mediaFileService, mover, eventAggregator);

            service.Execute(new RenameFilesCommand(author.Id, new List<int> { 1 }));

            Assert.That(mediaFileService.Updated.Select(f => f.Id), Is.EqualTo(new[] { 1 }));
            Assert.That(eventAggregator.Events.OfType<BookFileRenamedEvent>(), Is.Empty);
            Assert.That(eventAggregator.Events.OfType<AuthorRenamedEvent>(), Is.Empty);
        }

        [Test]
        public void should_scope_bulk_rename_to_requested_media_type()
        {
            var author = new Author { Id = 1, Name = "Joe Abercrombie" };
            var mediaFileService = new StubMediaFileService
            {
                Files = new List<BookFile>
                {
                    BookFile(1, "/books/Joe/audio.mp3", Quality.MP3, "audiobook"),
                    BookFile(2, "/books/Joe/book.epub", Quality.EPUB, "ebook")
                }
            };

            var eventAggregator = new RecordingEventAggregator();
            var mover = new RecordingMoveBookFiles
            {
                DestinationFactory = file => file.Path + ".renamed"
            };

            var service = CreateService(author, mediaFileService, mover, eventAggregator);

            service.Execute(new RenameAuthorCommand
            {
                AuthorIds = new List<int> { author.Id },
                MediaType = "ebook"
            });

            Assert.That(mover.MovedFileIds, Is.EqualTo(new[] { 2 }));
            Assert.That(eventAggregator.Events.OfType<BookFileRenamedEvent>().Single().BookFile.Id, Is.EqualTo(2));
            Assert.That(eventAggregator.Events.OfType<AuthorRenamedEvent>().Single().RenamedFiles.Single().BookFile.Id, Is.EqualTo(2));
        }

        [Test]
        public void should_format_all_success_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 10,
                AttemptedCount = 10,
                RenamedCount = 10
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Renamed 10 of 10 files for Joe Abercrombie."));
        }

        [Test]
        public void should_format_partial_collision_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 10,
                AttemptedCount = 10,
                RenamedCount = 5,
                CollisionSkippedCount = 5
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Renamed 5 of 10 files for Joe Abercrombie; 5 skipped because destination already exists."));
        }

        [Test]
        public void should_format_singular_collision_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 1,
                AttemptedCount = 1,
                CollisionSkippedCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Renamed 0 of 1 file for Joe Abercrombie; 1 skipped because destination already exists."));
        }

        [Test]
        public void should_format_already_in_place_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 2,
                AttemptedCount = 2,
                AlreadyInPlaceCount = 2
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Renamed 0 of 2 files for Joe Abercrombie; 2 already in place."));
        }

        [Test]
        public void should_format_singular_already_in_place_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 1,
                AttemptedCount = 1,
                AlreadyInPlaceCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Renamed 0 of 1 file for Joe Abercrombie; 1 already in place."));
        }

        [Test]
        public void should_format_failed_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 1,
                AttemptedCount = 1,
                FailedCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Renamed 0 of 1 file for Joe Abercrombie; 1 failed."));
        }

        [Test]
        public void should_format_not_eligible_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 10,
                AttemptedCount = 9,
                RenamedCount = 4,
                CollisionSkippedCount = 5
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Renamed 4 of 10 files for Joe Abercrombie; 5 skipped because destination already exists; 1 not eligible for rename."));
        }

        [Test]
        public void should_format_mixed_result_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 11,
                AttemptedCount = 11,
                RenamedCount = 5,
                CollisionSkippedCount = 3,
                AlreadyInPlaceCount = 2,
                FailedCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Renamed 5 of 11 files for Joe Abercrombie; 3 skipped because destination already exists; 2 already in place; 1 failed."));
        }

        [Test]
        public void should_format_zero_selected_message()
        {
            var result = new RenameFilesResult();

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("No files selected to rename for Joe Abercrombie."));
        }
    }
}
