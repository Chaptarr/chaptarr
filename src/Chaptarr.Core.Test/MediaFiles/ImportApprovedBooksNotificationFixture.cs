using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportApprovedBooksNotificationFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            private int _nextId = 1000;

            public List<BookFile> AddedFiles { get; } = new();
            public List<BookFile> UpdatedFiles { get; } = new();
            public List<BookFile> DeletedFiles { get; } = new();
            public bool ClearEditionOnAdd { get; set; }
            public BookFile FileAtPath { get; set; }
            public List<string> OperationOrder { get; set; }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();

            public void AddMany(List<BookFile> bookFiles)
            {
                foreach (var bookFile in bookFiles)
                {
                    bookFile.Id = ++_nextId;

                    if (ClearEditionOnAdd)
                    {
                        bookFile.Edition = null;
                    }

                    AddedFiles.Add(bookFile);
                }
            }

            public void Update(BookFile bookFile)
            {
                UpdatedFiles.Add(bookFile);
                OperationOrder?.Add("update");
            }

            public void Update(List<BookFile> bookFiles) { }
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason)
            {
                DeletedFiles.Add(bookFile);

                if (FileAtPath?.Id == bookFile.Id)
                {
                    FileAtPath = null;
                }
            }

            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) { }
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => new();
            public List<BookFile> GetFilesByBook(int bookId) => new();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => new();
            public List<BookFile> GetFilesByEdition(int editionId) => new();
            public List<BookFile> GetUnmappedFiles() => new();
            public BookFile Get(int id) => null;
            public List<BookFile> Get(IEnumerable<int> ids) => new();
            public List<BookFile> GetFilesWithBasePath(string path) => new();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => new();
            public List<BookFile> GetFileWithPath(List<string> path) => new();
            public BookFile GetFileWithPath(string path)
            {
                return string.Equals(FileAtPath?.Path, path, StringComparison.OrdinalIgnoreCase) ? FileAtPath : null;
            }
            public void UpdateMediaInfo(List<BookFile> bookFiles) { }
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => new();
        }

        private sealed class StubMetadataTagService : IMetadataTagService
        {
            public bool ThrowOnWrite { get; set; }
            public List<string> OperationOrder { get; set; }

            public Dictionary<string, List<string>> ReadAllTags(System.IO.Abstractions.IFileInfo file) => new();
            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(System.IO.Abstractions.IFileInfo file) => (new Dictionary<string, List<string>>(), null);
            public string ReadAllTagsAsJson(System.IO.Abstractions.IFileInfo file) => "{}";
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false)
            {
                OperationOrder?.Add("tags");

                if (ThrowOnWrite)
                {
                    throw new InvalidOperationException("synthetic tag write failure");
                }
            }

            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int authorId) => throw new NotImplementedException();
        }

        private sealed class StubMediaInfoExtractor : IMediaInfoExtractor
        {
            public MediaInfoModel ExtractMediaInfo(string filePath) => new();
            public TimeSpan GetDuration(string filePath) => TimeSpan.Zero;
            public bool IsAudiobookFile(string filePath, MediaInfoModel mediaInfo = null) => false;
        }

        private sealed class StubMoveBookFiles : IMoveBookFiles
        {
            public string DestinationPath { get; set; }
            public int MoveCalls { get; private set; }

            public BookFileMovePlan GetOrganizeDestination(BookFile bookFile, Author author, bool moveToCanonicalAuthorFolder, RenameBatchContext renameBatchContext = null) => throw new NotImplementedException();
            public BookFile MoveBookFile(BookFile bookFile, Author author, BookFileMovePlan plan, RenameBatchContext renameBatchContext = null) => throw new NotImplementedException();

            public BookFile MoveBookFile(BookFile bookFile, LocalBook localBook)
            {
                MoveCalls++;
                bookFile.Path = DestinationPath;
                return bookFile;
            }

            public BookFile CopyBookFile(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
            public string GetImportDestinationPath(BookFile bookFile, LocalBook localBook) => DestinationPath;
        }

        private sealed class CapturingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();
            public List<string> OperationOrder { get; set; }

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);

                if (@event is BookFileAddedEvent)
                {
                    OperationOrder?.Add("event");
                }
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Book;
                }

                throw new NotImplementedException($"Unexpected call to IBookService.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return Author;
                }

                throw new NotImplementedException($"Unexpected call to IAuthorService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Edition Edition { get; set; }
            public List<Edition> EditionsByBook { get; set; } = new();
            public List<(Edition Edition, bool IsManualSelection)> SetMonitoredCalls { get; } = new();
            public List<string> OperationOrder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    return Edition;
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook))
                {
                    return EditionsByBook;
                }

                if (targetMethod?.Name == nameof(IEditionService.SetMonitored))
                {
                    SetMonitoredCalls.Add(((Edition)args[0], (bool)args[1]));
                    OperationOrder?.Add("monitor");
                    return null;
                }

                throw new NotImplementedException($"Unexpected call to IEditionService.{targetMethod?.Name}");
            }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            private readonly List<CustomFormat> _formats;

            public StubCustomFormatCalculationService(params CustomFormat[] formats)
            {
                _formats = formats.ToList();
            }

            public int LocalBookCalls { get; private set; }

            public List<CustomFormat> ParseCustomFormat(LocalBook localBook)
            {
                LocalBookCalls++;
                return _formats;
            }

            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => throw new NotImplementedException();
        }

        private static T Proxy<T>() where T : class
        {
            return DispatchProxy.Create<T, ThrowingProxy<T>>();
        }

        private static (Author Author, Book Book, Edition Edition, string Path) CreateExistingEbookContext(bool monitored = true)
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Riley Sager",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Riley Sager",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "The House Across the Lake",
                CleanTitle = "thehouseacrossthelake",
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = book,
                Title = "The House Across the Lake",
                Monitored = monitored,
                IsEbook = true
            };

            return (author, book, edition, "/ebooks/Riley Sager/The House Across the Lake.epub");
        }

        private static (List<ImportResult> Results, EditionServiceProxy EditionService) ImportExistingEbook(
            StubMediaFileService mediaFileService,
            CapturingEventAggregator eventAggregator,
            Author author,
            Book book,
            Edition edition,
            string path,
            bool existingFile = true,
            IReadOnlyCollection<Edition> editionsByBook = null,
            IMoveBookFiles bookFileMover = null,
            StubMetadataTagService metadataTagService = null,
            List<string> operationOrder = null,
            ImportMode importMode = ImportMode.Copy)
        {
            mediaFileService.OperationOrder = operationOrder;
            eventAggregator.OperationOrder = operationOrder;
            metadataTagService ??= new StubMetadataTagService();
            metadataTagService.OperationOrder = operationOrder;

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            var editionServiceProxy = (EditionServiceProxy)(object)editionService;
            editionServiceProxy.Edition = edition;
            editionServiceProxy.EditionsByBook = editionsByBook?.Any() == true ? editionsByBook.ToList() : new List<Edition> { edition };
            editionServiceProxy.OperationOrder = operationOrder;

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                new StubMediaInfoExtractor(),
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                bookFileMover ?? Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"));

            var localBook = new LocalBook
            {
                Path = path,
                ExistingFile = existingFile,
                IsManualImport = true,
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["title"] = new() { book.Title }
                    }
                },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: importMode,
                cancellationToken: CancellationToken.None);

            return (results, editionServiceProxy);
        }

        private static (
            List<ImportResult> Results,
            StubMediaFileService MediaFiles,
            StubMoveBookFiles Mover)
            ImportMixedEbookPayload(bool downloadForced)
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "EPUB only",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = false, Quality = Quality.MOBI },
                    new() { Allowed = true, Quality = Quality.EPUB },
                    new() { Allowed = false, Quality = Quality.AZW3 }
                }
            };

            var author = new Author
            {
                Id = 38,
                Name = "Joe Abercrombie",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Joe Abercrombie",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 5792,
                AuthorId = author.Id,
                Author = author,
                Title = "Best Served Cold",
                CleanTitle = "bestservedcold",
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true
            };

            var edition = new Edition
            {
                Id = 15629,
                BookId = book.Id,
                Book = book,
                Title = book.Title,
                Monitored = true,
                IsEbook = true
            };

            var mediaFileService = new StubMediaFileService();
            var eventAggregator = new CapturingEventAggregator();
            var mover = new StubMoveBookFiles
            {
                DestinationPath = "/ebooks/Joe Abercrombie/Best Served Cold/Best Served Cold.epub"
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                new StubMetadataTagService(),
                new StubMediaInfoExtractor(),
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                mover,
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"));

            var decisions = new[]
            {
                (Quality: Quality.AZW3, Extension: "azw3"),
                (Quality: Quality.EPUB, Extension: "epub"),
                (Quality: Quality.MOBI, Extension: "mobi")
            }
            .Select(candidate => new ImportDecision<LocalBook>(new LocalBook
            {
                Path = $"/downloads/complete/Best Served Cold.{candidate.Extension}",
                ExistingFile = false,
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = candidate.Quality, Revision = new Revision() },
                Part = 1,
                PartCount = 1
            }))
            .ToList();

            var results = service.Import(
                decisions,
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "mixed-ebook-download",
                    DownloadForced = downloadForced,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: ImportMode.Move,
                cancellationToken: CancellationToken.None);

            return (results, mediaFileService, mover);
        }

        [Test]
        public void interactive_grab_should_import_profile_allowed_ebook_from_multi_format_payload()
        {
            var import = ImportMixedEbookPayload(downloadForced: true);

            Assert.That(import.Results, Has.Count.EqualTo(3));
            Assert.That(import.Results.Count(result => result.Result == ImportResultType.Imported), Is.EqualTo(1));
            Assert.That(import.Results.Count(result => result.Result == ImportResultType.Rejected), Is.EqualTo(2));

            var imported = import.Results.Single(result => result.Result == ImportResultType.Imported);
            Assert.That(imported.ImportDecision.Item.Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(import.MediaFiles.AddedFiles, Has.Count.EqualTo(1));
            Assert.That(import.MediaFiles.AddedFiles.Single().Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(import.Mover.MoveCalls, Is.EqualTo(1));

            var rejected = import.Results.Where(result => result.Result == ImportResultType.Rejected).ToList();
            Assert.That(rejected.All(result =>
                result.ImportDecision.Rejections.Single().Reason.Contains("Skipped duplicate ebook format")), Is.True);
            Assert.That(rejected.All(result =>
                !result.ImportDecision.Rejections.Single().IsQualityFilter), Is.True);
        }

        [Test]
        public void automatic_grab_should_reject_disallowed_ebook_alternatives_before_selection()
        {
            var import = ImportMixedEbookPayload(downloadForced: false);

            Assert.That(import.Results, Has.Count.EqualTo(3));
            Assert.That(import.Results.Count(result => result.Result == ImportResultType.Imported), Is.EqualTo(1));
            Assert.That(import.Results.Count(result => result.Result == ImportResultType.Rejected), Is.EqualTo(2));

            var imported = import.Results.Single(result => result.Result == ImportResultType.Imported);
            Assert.That(imported.ImportDecision.Item.Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(import.MediaFiles.AddedFiles, Has.Count.EqualTo(1));
            Assert.That(import.MediaFiles.AddedFiles.Single().Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(import.Mover.MoveCalls, Is.EqualTo(1));

            var rejected = import.Results.Where(result => result.Result == ImportResultType.Rejected).ToList();
            Assert.That(rejected.All(result =>
                result.ImportDecision.Rejections.Single().Type == RejectionType.Permanent), Is.True);
            Assert.That(rejected.All(result =>
                result.ImportDecision.Rejections.Single().IsQualityFilter), Is.True);
            Assert.That(rejected.All(result =>
                !result.ImportDecision.Rejections.Single().CanBypass), Is.True);
        }

        [TestCase(false, true, ImportResultType.Skipped)]
        [TestCase(true, true, ImportResultType.Imported)]
        [TestCase(true, false, ImportResultType.Imported)]
        public void manual_grab_override_should_survive_profile_import_gates(bool downloadForced, bool hasQualityProfile, ImportResultType expectedResult)
        {
            var format = new CustomFormat
            {
                Id = 77,
                Name = "No Audio Plays",
                BuiltInKey = BuiltInCustomFormats.DramatizedAudioKey
            };
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Reject Dramatized",
                ProfileType = ProfileType.Audiobook,
                MinFormatScore = 0,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = !downloadForced, Quality = Quality.M4B }
                },
                FormatItems = new List<ProfileFormatItem>
                {
                    new() { Format = format, Score = -100 }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Jim Butcher",
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookPath = "/audiobooks/Jim Butcher",
                AudiobookQualityProfileId = hasQualityProfile ? qualityProfile.Id : null,
                AudiobookQualityProfile = hasQualityProfile ? qualityProfile : null
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "Storm Front",
                CleanTitle = "stormfront",
                MediaType = BookMediaType.Audiobook
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = book,
                Title = "Storm Front",
                Monitored = true,
                IsEbook = false,
                ReadingFormatId = 2,
                NarratorNames = new List<string> { "James Marsters" }
            };

            var mediaFileService = new StubMediaFileService();
            var metadataTagService = new StubMetadataTagService();
            var mediaInfoExtractor = new StubMediaInfoExtractor();
            var eventAggregator = new CapturingEventAggregator();
            var formatCalculationService = new StubCustomFormatCalculationService(format);
            var mover = new StubMoveBookFiles { DestinationPath = "/audiobooks/Jim Butcher/Storm Front/Storm Front.m4b" };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                mediaInfoExtractor,
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                downloadForced ? mover : Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"),
                customFormatCalculationService: formatCalculationService);

            var localBook = new LocalBook
            {
                Path = "/downloads/complete/Storm Front.m4b",
                ExistingFile = false,
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.M4B, Revision = new Revision() },
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["description"] = new() { "A GraphicAudio production" }
                    }
                },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadForced = downloadForced,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: downloadForced ? ImportMode.Move : ImportMode.Copy,
                cancellationToken: CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(expectedResult));

            if (downloadForced)
            {
                Assert.That(formatCalculationService.LocalBookCalls, Is.EqualTo(0));
                Assert.That(mediaFileService.AddedFiles, Has.Count.EqualTo(1));
                Assert.That(eventAggregator.Events.OfType<BookImportedEvent>(), Has.Exactly(1).Items);
            }
            else
            {
                Assert.That(results[0].Errors.Single(), Does.Contain("matched No Audio Plays custom format on file tags"));
                Assert.That(formatCalculationService.LocalBookCalls, Is.EqualTo(1));
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
                Assert.That(eventAggregator.Events.OfType<BookImportedEvent>(), Is.Empty);
            }
        }

        [Test]
        public void should_publish_book_imported_event_for_new_download_when_edition_book_navigation_is_missing()
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Riley Sager",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Riley Sager",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "The House Across the Lake",
                CleanTitle = "thehouseacrossthelake",
                MediaType = BookMediaType.Ebook
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = null,
                Title = "The House Across the Lake",
                Monitored = true,
                IsEbook = true
            };

            var mediaFileService = new StubMediaFileService();
            var metadataTagService = new StubMetadataTagService();
            var mediaInfoExtractor = new StubMediaInfoExtractor();
            var eventAggregator = new CapturingEventAggregator();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                mediaInfoExtractor,
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"));

            var localBook = new LocalBook
            {
                Path = "/downloads/complete/The House Across the Lake.epub",
                ExistingFile = true,
                Book = book,
                Author = author,
                Edition = new Edition
                {
                    Id = edition.Id,
                    BookId = book.Id,
                    Book = null,
                    Title = edition.Title,
                    Monitored = true,
                    IsEbook = true
                },
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: ImportMode.Copy,
                cancellationToken: CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));

            var importedEvents = eventAggregator.Events.OfType<BookImportedEvent>().ToList();
            Assert.That(importedEvents, Has.Count.EqualTo(1));
            Assert.That(importedEvents[0].Book.Id, Is.EqualTo(book.Id));
            Assert.That(importedEvents[0].Author.Id, Is.EqualTo(author.Id));
            Assert.That(importedEvents[0].NewDownload, Is.True);
            Assert.That(importedEvents[0].ImportedBooks, Has.Count.EqualTo(1));

            Assert.That(eventAggregator.Events.OfType<BookAddedEvent>(), Is.Empty);
        }

        [Test]
        public void should_publish_book_imported_event_for_new_download_when_book_file_edition_navigation_is_missing()
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Riley Sager",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Riley Sager",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "The House Across the Lake",
                CleanTitle = "thehouseacrossthelake",
                MediaType = BookMediaType.Ebook
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = null,
                Title = "The House Across the Lake",
                Monitored = true,
                IsEbook = true
            };

            var mediaFileService = new StubMediaFileService
            {
                ClearEditionOnAdd = true
            };
            var metadataTagService = new StubMetadataTagService();
            var mediaInfoExtractor = new StubMediaInfoExtractor();
            var eventAggregator = new CapturingEventAggregator();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                mediaInfoExtractor,
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"));

            var localBook = new LocalBook
            {
                Path = "/downloads/complete/The House Across the Lake.epub",
                ExistingFile = true,
                Book = book,
                Author = author,
                Edition = new Edition
                {
                    Id = edition.Id,
                    BookId = book.Id,
                    Book = null,
                    Title = edition.Title,
                    Monitored = true,
                    IsEbook = true
                },
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: ImportMode.Copy,
                cancellationToken: CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));

            var importedEvents = eventAggregator.Events.OfType<BookImportedEvent>().ToList();
            Assert.That(importedEvents, Has.Count.EqualTo(1));
            Assert.That(importedEvents[0].Book.Id, Is.EqualTo(book.Id));
            Assert.That(importedEvents[0].Author.Id, Is.EqualTo(author.Id));
            Assert.That(importedEvents[0].NewDownload, Is.True);
            Assert.That(importedEvents[0].ImportedBooks, Has.Count.EqualTo(1));

            Assert.That(eventAggregator.Events.OfType<BookAddedEvent>(), Is.Empty);
        }

        [Test]
        public void should_publish_book_file_added_event_when_adopting_existing_unmapped_file()
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Riley Sager",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Riley Sager",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "The House Across the Lake",
                CleanTitle = "thehouseacrossthelake",
                MediaType = BookMediaType.Ebook
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = book,
                Title = "The House Across the Lake",
                Monitored = true,
                IsEbook = true
            };

            var path = "/ebooks/Riley Sager/The House Across the Lake.epub";
            var originalDateAdded = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var orphan = new BookFile
            {
                Id = 8001,
                Path = path,
                EditionId = 0,
                DateAdded = originalDateAdded,
                CalibreId = 41
            };

            var mediaFileService = new StubMediaFileService
            {
                FileAtPath = orphan
            };
            var metadataTagService = new StubMetadataTagService();
            var mediaInfoExtractor = new StubMediaInfoExtractor();
            var eventAggregator = new CapturingEventAggregator();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                mediaInfoExtractor,
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"));

            var localBook = new LocalBook
            {
                Path = path,
                ExistingFile = true,
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["title"] = new() { "The House Across the Lake" }
                    }
                },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: ImportMode.Copy,
                cancellationToken: CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.FileAtPath, Is.SameAs(orphan));
            Assert.That(orphan.DateAdded, Is.GreaterThan(originalDateAdded));
            Assert.That(orphan.CalibreId, Is.EqualTo(41));

            var addedEvents = eventAggregator.Events.OfType<BookFileAddedEvent>().ToList();
            Assert.That(addedEvents, Has.Count.EqualTo(1));
            Assert.That(addedEvents[0].BookFile.Id, Is.EqualTo(orphan.Id));
            Assert.That(addedEvents[0].BookFile.EditionId, Is.EqualTo(edition.Id));
            Assert.That(addedEvents[0].BookFile.Edition.Book.Id, Is.EqualTo(book.Id));
        }

        [Test]
        public void should_not_delete_or_emit_added_event_when_reimporting_tracked_file_to_same_edition()
        {
            var context = CreateExistingEbookContext();
            var originalDateAdded = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            var trackedFile = new BookFile
            {
                Id = 8002,
                Path = context.Path,
                EditionId = context.Edition.Id,
                Edition = context.Edition,
                DateAdded = originalDateAdded,
                CalibreId = 42,
                LastMatchAttempt = DateTime.UtcNow,
                MatchDetails = "APPLY_FAILED:OLD"
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                context.Path);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.UpdatedFiles[0], Is.SameAs(trackedFile));
            Assert.That(mediaFileService.FileAtPath, Is.SameAs(trackedFile));
            Assert.That(trackedFile.Id, Is.EqualTo(8002));
            Assert.That(trackedFile.EditionId, Is.EqualTo(context.Edition.Id));
            Assert.That(trackedFile.DateAdded, Is.EqualTo(originalDateAdded));
            Assert.That(trackedFile.CalibreId, Is.EqualTo(42));
            Assert.That(trackedFile.LastMatchAttempt, Is.Null);
            Assert.That(trackedFile.MatchDetails, Is.Null);
            Assert.That(trackedFile.MatchProvenance.Mode, Is.EqualTo("Manual"));
            Assert.That(trackedFile.MatchProvenance.Route, Is.EqualTo("manual_selection"));
            Assert.That(trackedFile.MatchProvenance.SupportingSignals.Single().Type, Is.EqualTo("manual_selection"));
            Assert.That(eventAggregator.Events.OfType<BookFileAddedEvent>(), Is.Empty);
            Assert.That(import.EditionService.SetMonitoredCalls, Is.Empty);
        }

        [Test]
        public void should_update_in_place_and_emit_added_event_when_relinking_tracked_file_to_another_edition_of_same_book()
        {
            var context = CreateExistingEbookContext(monitored: false);
            var previousEdition = new Edition
            {
                Id = 12386,
                BookId = context.Book.Id,
                Book = context.Book,
                Title = context.Book.Title,
                Monitored = true,
                IsEbook = true
            };
            var originalDateAdded = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            var trackedFile = new BookFile
            {
                Id = 8003,
                Path = context.Path,
                EditionId = previousEdition.Id,
                Edition = previousEdition,
                DateAdded = originalDateAdded,
                CalibreId = 43
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();
            var operationOrder = new List<string>();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                context.Path,
                editionsByBook: new[] { previousEdition, context.Edition },
                operationOrder: operationOrder);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.UpdatedFiles[0], Is.SameAs(trackedFile));
            Assert.That(mediaFileService.FileAtPath, Is.SameAs(trackedFile));
            Assert.That(trackedFile.Id, Is.EqualTo(8003));
            Assert.That(trackedFile.EditionId, Is.EqualTo(context.Edition.Id));
            Assert.That(trackedFile.Edition, Is.SameAs(context.Edition));
            Assert.That(trackedFile.DateAdded, Is.EqualTo(originalDateAdded));
            Assert.That(trackedFile.CalibreId, Is.EqualTo(43));

            var addedEvents = eventAggregator.Events.OfType<BookFileAddedEvent>().ToList();
            Assert.That(addedEvents, Has.Count.EqualTo(1));
            Assert.That(addedEvents[0].BookFile, Is.SameAs(trackedFile));
            Assert.That(import.EditionService.SetMonitoredCalls, Has.Count.EqualTo(1));
            Assert.That(import.EditionService.SetMonitoredCalls[0].Edition, Is.SameAs(context.Edition));
            Assert.That(import.EditionService.SetMonitoredCalls[0].IsManualSelection, Is.False);
            Assert.That(operationOrder, Is.EqualTo(new[] { "update", "monitor", "event", "tags" }));
        }

        [Test]
        public void should_complete_relink_and_emit_event_when_tag_write_fails()
        {
            var context = CreateExistingEbookContext(monitored: false);
            var previousEdition = new Edition
            {
                Id = 12386,
                BookId = context.Book.Id,
                Book = context.Book,
                Title = context.Book.Title,
                Monitored = true,
                IsEbook = true
            };
            var trackedFile = new BookFile
            {
                Id = 8004,
                Path = context.Path,
                EditionId = previousEdition.Id,
                Edition = previousEdition,
                DateAdded = new DateTime(2024, 4, 5, 6, 7, 8, DateTimeKind.Utc),
                CalibreId = 44
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();
            var metadataTagService = new StubMetadataTagService { ThrowOnWrite = true };
            var operationOrder = new List<string>();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                context.Path,
                editionsByBook: new[] { previousEdition, context.Edition },
                metadataTagService: metadataTagService,
                operationOrder: operationOrder);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.UpdatedFiles[0], Is.SameAs(trackedFile));
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(eventAggregator.Events.OfType<BookFileAddedEvent>().ToList(), Has.Count.EqualTo(1));
            Assert.That(import.EditionService.SetMonitoredCalls, Has.Count.EqualTo(1));
            Assert.That(import.EditionService.SetMonitoredCalls[0].Edition, Is.SameAs(context.Edition));
            Assert.That(operationOrder, Is.EqualTo(new[] { "update", "monitor", "event", "tags" }));
        }

        [Test]
        public void should_preserve_same_book_relocation_when_existing_file_is_false()
        {
            var context = CreateExistingEbookContext();
            var originalPath = context.Path;
            var destinationPath = "/ebooks/Riley Sager/The House Across the Lake (Renamed).epub";
            var originalDateAdded = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var trackedFile = new BookFile
            {
                Id = 8005,
                Path = originalPath,
                EditionId = context.Edition.Id,
                Edition = context.Edition,
                DateAdded = originalDateAdded,
                CalibreId = 45
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();
            var mover = new StubMoveBookFiles { DestinationPath = destinationPath };

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                originalPath,
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Move);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mover.MoveCalls, Is.EqualTo(1));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.UpdatedFiles[0], Is.SameAs(trackedFile));
            Assert.That(trackedFile.Id, Is.EqualTo(8005));
            Assert.That(trackedFile.Path, Is.EqualTo(destinationPath));
            Assert.That(trackedFile.DateAdded, Is.EqualTo(originalDateAdded));
            Assert.That(trackedFile.CalibreId, Is.EqualTo(45));
            Assert.That(eventAggregator.Events.OfType<BookFileAddedEvent>(), Is.Empty);

            var renamedEvent = eventAggregator.Events.OfType<BookFileRenamedEvent>().Single();
            Assert.That(renamedEvent.BookFile, Is.SameAs(trackedFile));
            Assert.That(renamedEvent.OriginalPath, Is.EqualTo(originalPath));
        }

        [Test]
        public void should_reject_file_tracked_to_a_different_book_without_mutating_it()
        {
            var context = CreateExistingEbookContext();
            var otherBook = new Book
            {
                Id = 9999,
                AuthorId = context.Author.Id,
                Author = context.Author,
                Title = "A Different Book",
                CleanTitle = "adifferentbook",
                MediaType = BookMediaType.Ebook
            };
            var otherEdition = new Edition
            {
                Id = 19999,
                BookId = otherBook.Id,
                Book = otherBook,
                Title = otherBook.Title,
                Monitored = true,
                IsEbook = true
            };
            var trackedFile = new BookFile
            {
                Id = 8006,
                Path = context.Path,
                EditionId = otherEdition.Id,
                Edition = otherEdition,
                DateAdded = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc),
                CalibreId = 46
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                context.Path);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Skipped));
            Assert.That(import.Results[0].Errors.Single(), Does.Contain("linked to a different book"));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Is.Empty);
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.FileAtPath, Is.SameAs(trackedFile));
            Assert.That(trackedFile.Id, Is.EqualTo(8006));
            Assert.That(trackedFile.EditionId, Is.EqualTo(otherEdition.Id));
            Assert.That(trackedFile.CalibreId, Is.EqualTo(46));
            Assert.That(eventAggregator.Events, Is.Empty);
            Assert.That(import.EditionService.SetMonitoredCalls, Is.Empty);
        }
    }
}
