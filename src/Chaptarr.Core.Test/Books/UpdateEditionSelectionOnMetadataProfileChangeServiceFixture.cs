using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Metadata.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class UpdateEditionSelectionOnMetadataProfileChangeServiceFixture
    {
        private sealed class StubAuthorService : IAuthorService
        {
            private readonly List<Author> _authors;

            public StubAuthorService(params Author[] authors)
            {
                _authors = (authors ?? Array.Empty<Author>()).ToList();
            }

            public List<Author> GetAllAuthors(bool bypassCache = false) => _authors;
            public Author GetAuthor(int authorId) => _authors.FirstOrDefault(a => a.Id == authorId);
            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false, bool deleteOtherAuthorsFiles = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> beforeSave) => throw new System.NotSupportedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public List<KeyValuePair<int, string>> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void SetAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            public List<Book> GetImportListBookIdentities() => GetAllBooks();

            private readonly Dictionary<int, List<Book>> _booksByAuthorId;
            public List<Book> UpdatedBooks { get; } = new List<Book>();
            public List<int> QueuedCoverIds { get; } = new();
            public List<int> CoverPageSizes { get; } = new();
            public Action BeforeCoverQueue { get; set; }
            public int? FailNotification { get; set; }
            public List<BookEditedEvent> Changes { get; } = new();
            public List<BooksEditedEvent> Batches { get; } = new();
            public Action<Book> OnUpdate { get; set; }

            public StubBookService(params Book[] books)
            {
                _booksByAuthorId = (books ?? Array.Empty<Book>())
                    .GroupBy(b => b.AuthorId)
                    .ToDictionary(g => g.Key, g => g.ToList());
            }

            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false) =>
                _booksByAuthorId.TryGetValue(authorId, out var books) ? books : new List<Book>();

            public Book UpdateBook(Book book, List<Edition> editionUpdates, string monitoredMediaType = null, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> writeRelated = null) => throw new System.NotSupportedException();
            public Book UpdateBook(Book book)
            {
                OnUpdate?.Invoke(book);
                UpdatedBooks.Add(book);
                return book;
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => GetBooksByAuthor(authorId);
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public List<Book> FindBySlug(string titleSlug) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => _booksByAuthorId.Values.SelectMany(x => x).ToList();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books, bool filesAlreadyDeleted = false, List<NzbDrone.Core.MediaFiles.BookFile> deletedBookFiles = null) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public (Book Book, bool ExistingCopy) AddWantedEditionCopy(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            private readonly Dictionary<int, List<Edition>> _editionsByBookId;
            private readonly EditionRepository _repository;
            public List<Edition> UpdatedEditions { get; private set; }
            public Action<List<Edition>> BeforeSelection { get; set; }

            public StubEditionService(Dictionary<int, List<Edition>> editionsByBookId, EditionRepository repository = null)
            {
                _editionsByBookId = editionsByBookId;
                _repository = repository;
            }

            public void AttachEditions(IEnumerable<Book> books)
            {
                foreach (var book in books)
                    book.Editions = _editionsByBookId.GetValueOrDefault(book.Id, new List<Edition>());
            }

            public List<Edition> GetEditionsByBook(int bookId) => throw new InvalidOperationException("Editions were already batch-loaded by GetBooksByAuthor");

            public void UpdateMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Edition selectedEdition = null) => throw new System.NotSupportedException();
            public void UpdateMany(List<Edition> editions)
            {
                UpdatedEditions = editions;
                _repository?.UpdateMany(editions);
            }

            public Edition GetEdition(int id) => throw new NotImplementedException();
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => throw new NotImplementedException();
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId, bool onlyMonitored = false) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();

            public void RefreshProviderAliases(List<Edition> editions) { }
            public void UpdateSelection(List<Edition> editions)
            {
                BeforeSelection?.Invoke(editions);
                UpdatedEditions = editions;
                _repository?.UpdateSelection(editions);
            }
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public System.Collections.Generic.List<NzbDrone.Core.MediaFiles.BookFile> GetExisting(System.Collections.Generic.IEnumerable<int> ids) => throw new System.NotImplementedException();

            public void ValidateReplacementFiles(System.Collections.Generic.List<BookFile> replacedFiles,
                System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles)
                => throw new System.NotSupportedException("This test adapter does not support replacement validation.");

            public void ReplaceMany(System.Collections.Generic.List<BookFile> bookFiles, System.Collections.Generic.List<BookFile> replacedFiles,
                DeleteMediaFileReason reason, System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles,
                System.Action<System.Action<System.Data.IDbConnection, System.Data.IDbTransaction>> persist)
                => throw new System.NotSupportedException("This test adapter does not support transactional replacement.");

            private readonly Dictionary<int, List<BookFile>> _filesByBookId;

            public StubMediaFileService(Dictionary<int, List<BookFile>> filesByBookId = null)
            {
                _filesByBookId = filesByBookId ?? new Dictionary<int, List<BookFile>>();
            }

            public List<List<int>> FileReads { get; } = new();
            public List<BookFile> GetFilesByBook(int bookId) => throw new InvalidOperationException("Files must be read in a batch");

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, NzbDrone.Core.MediaFiles.AudioProductionCorrection correction, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();

            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds)
            {
                FileReads.Add(bookIds.ToList());
                return _filesByBookId.Where(pair => bookIds.Contains(pair.Key))
                    .SelectMany(pair => pair.Value.Select(file =>
                    {
                        file.Edition = new Edition { BookId = pair.Key };
                        return file;
                    })).ToList();
            }
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private static UpdateEditionSelectionOnMetadataProfileChangeService CreateSut(
            StubAuthorService authorService,
            StubBookService bookService,
            StubEditionService editionService,
            StubMediaFileService mediaFileService)
        {
            editionService.AttachEditions(bookService.GetAllBooks());
            return new UpdateEditionSelectionOnMetadataProfileChangeService(
                authorService,
                bookService,
                editionService,
                mediaFileService,
                new EditionSelector(LogManager.GetCurrentClassLogger()),
                new EditionMetadataProfileFilter(new TestTermMatcherService()),
                LogManager.GetCurrentClassLogger(), new NzbDrone.Core.MediaCover.DeferredCoverService(NLog.LogManager.GetCurrentClassLogger(), null),
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Messaging.Commands.IManageCommandQueue>((_, args) =>
                {
                    bookService.BeforeCoverQueue?.Invoke();
                    var command = (NzbDrone.Core.MediaCover.Commands.DownloadBookMediaCommand)args[0];
                    bookService.QueuedCoverIds.AddRange(command.BookIds);
                    bookService.CoverPageSizes.Add(command.BookIds.Count);
                    return new NzbDrone.Core.Messaging.Commands.CommandModel();
                }), InterfaceProxy.Proxy<IEventAggregator>((_, args) =>
                {
                    if (args[0] is BookEditedEvent change)
                    {
                        if (change.Book.Id == bookService.FailNotification) throw new InvalidOperationException("Fixture notification refused");
                        bookService.Changes.Add(change);
                    }
                    if (args[0] is BooksEditedEvent batch) bookService.Batches.Add(batch);
                    return null;
                }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void profile_change_batches_across_authors_and_continues_after_a_failed_page(bool failFirstPage)
        {
            var profile = new MetadataProfile { Id = 7, AllowedLanguages = "eng" };
            var authors = Enumerable.Range(1, 40).Select(id => new Author { Id = id, AudiobookMetadataProfileId = 7 }).ToArray();
            var books = Enumerable.Range(1, 320).Select(id => new Book { Id = id, AuthorId = (id - 1) / 8 + 1,
                MediaType = BookMediaType.Audiobook, ForeignEditionId = "fra-" + id }).ToArray();
            var editions = books.ToDictionary(b => b.Id, b => new List<Edition>
            {
                new() { Id = b.Id * 2, BookId = b.Id, ForeignEditionId = b.ForeignEditionId, Language = "fra", ReadingFormatId = 2, Monitored = true },
                new() { Id = b.Id * 2 + 1, BookId = b.Id, ForeignEditionId = "eng-" + b.Id, Language = "eng", ReadingFormatId = 2 }
            });
            var service = new StubBookService(books);
            var attempts = 0;
            service.BeforeCoverQueue = () =>
            {
                Assert.That(service.Changes, Has.Count.EqualTo(320), "Collect across the whole profile operation");
                if (++attempts == 1 && failFirstPage) throw new InvalidOperationException("Fixture queue unavailable");
            };
            CreateSut(new StubAuthorService(authors), service, new StubEditionService(editions), new StubMediaFileService())
                .Handle(new MetadataProfileUpdatedEvent(profile));
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(service.CoverPageSizes, Is.EqualTo(failFirstPage ? new[] { 70 } : new[] { 250, 70 }));
            Assert.That(service.QueuedCoverIds, Is.EqualTo(failFirstPage ? Enumerable.Range(251, 70) : Enumerable.Range(1, 320)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void profile_selection_preserves_metadata_changed_after_batch_load(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Fixture Author", CleanName = "fixtureauthor", SortName = "fixtureauthor",
                    TitleSlug = Guid.NewGuid().ToString(), Path = "/fixture", Added = DateTime.UtcNow
                });
                author.AudiobookMetadataProfileId = 7;
                var statements = new List<string>();
                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) => method.Name == nameof(IDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => statements.Add(command.CommandText), includeWrites: true)
                    : method.Invoke(database, args));
                var bookRepository = new BookRepository(observed, events);
                var book = bookRepository.Insert(new Book
                {
                    AuthorId = author.Id, Title = "Fixture Book", CleanTitle = "fixturebook",
                    TitleSlug = Guid.NewGuid().ToString(), Added = DateTime.UtcNow, MediaType = BookMediaType.Audiobook
                });
                var repository = new EditionRepository(observed, events);
                var editions = new[] { "fra", "eng" }.Select(language => repository.Insert(new Edition
                {
                    BookId = book.Id, Title = "Fixture Edition", TitleSlug = Guid.NewGuid().ToString(),
                    Language = language, ForeignEditionId = language == "fra" ? "gr:12344" : "gr:12345",
                    ReadingFormatId = 2, Monitored = language == "fra", Publisher = "Original publisher"
                })).ToList();
                book.Author = author;
                var bookService = new StubBookService(book) { OnUpdate = changed => bookRepository.Update(changed) };
                var sut = CreateSut(new StubAuthorService(author), bookService,
                    new StubEditionService(new() { [book.Id] = editions }, repository), new StubMediaFileService());

                // A metadata refresh commits after this operation loaded the editions.
                var refreshed = repository.Get(editions[1].Id);
                refreshed.Publisher = "Updated publisher";
                repository.Update(refreshed);
                var updatedBook = bookRepository.Get(book.Id);
                updatedBook.Overview = "New description from concurrent refresh";
                updatedBook.AudiobookMonitored = true;
                bookRepository.Update(updatedBook);
                statements.Clear();
                sut.Handle(new MetadataProfileUpdatedEvent(new MetadataProfile { Id = 7, AllowedLanguages = "eng" }));

                Assert.That(bookService.UpdatedBooks, Is.Empty);
                Assert.That(statements.Where(sql => sql.StartsWith("UPDATE \"Books\"", StringComparison.OrdinalIgnoreCase)), Is.Empty,
                    "Profile selection must issue no Book UPDATE, including unchanged writes");
                Assert.That(bookRepository.Get(book.Id).Overview, Is.EqualTo(updatedBook.Overview));
                Assert.That(bookRepository.Get(book.Id).AudiobookMonitored, Is.True);
                Assert.That(bookService.Changes.Single().Book.Id, Is.EqualTo(book.Id));
                Assert.That(bookService.Changes.Single().OldBook.AuthorId, Is.EqualTo(author.Id));
                Assert.That(bookService.Batches.Single().Changes.Single(), Is.SameAs(bookService.Changes.Single()));
                var apiBook = bookRepository.Get(book.Id);
                apiBook.Author = author;
                apiBook.Editions = repository.FindByBook(new[] { book.Id });
                apiBook.SeriesLinks = new List<SeriesBookLink>();
                Assert.That(BookEditionIdentity.GetMonitoredEdition(apiBook).Id, Is.EqualTo(editions[1].Id));
                Assert.That(apiBook.ToResource(BookResourceMappingOptions.Lean()).ForeignEditionId, Is.EqualTo("gr:12345"));

                Assert.That(repository.Get(editions[0].Id).Monitored, Is.False);
                Assert.That(repository.Get(editions[1].Id).Monitored, Is.True);
                Assert.That(repository.Get(editions[1].Id).Publisher, Is.EqualTo("Updated publisher"),
                    "Changing the monitored edition must not write the earlier metadata snapshot back");
            });
        }

        [Test]
        public void profile_reads_files_once_for_affected_media_and_reuses_loaded_editions()
        {
            var profile = new MetadataProfile { Id = 7, AllowedLanguages = "eng" };
            var author = new Author { Id = 5, AudiobookMetadataProfileId = 7, EbookMetadataProfileId = 8 };
            var books = Enumerable.Range(1, 100).Select(id => new Book { Id = id, AuthorId = 5,
                MediaType = id <= 80 ? BookMediaType.Audiobook : BookMediaType.Ebook, ForeignEditionId = "fra-" + id }).ToArray();
            var editions = books.ToDictionary(b => b.Id, b => new List<Edition>
            {
                new() { Id = b.Id * 2, BookId = b.Id, ForeignEditionId = b.ForeignEditionId, Language = "fra", ReadingFormatId = 2, Monitored = true },
                new() { Id = b.Id * 2 + 1, BookId = b.Id, ForeignEditionId = "eng-" + b.Id, Language = "eng", ReadingFormatId = 2 }
            });
            var files = new StubMediaFileService(new() { [1] = new() { new BookFile() } });
            var service = new StubBookService(books);
            CreateSut(new StubAuthorService(author), service, new StubEditionService(editions), files)
                .Handle(new MetadataProfileUpdatedEvent(profile));
            Assert.That(files.FileReads, Has.Count.EqualTo(1));
            Assert.That(files.FileReads[0], Is.EqualTo(Enumerable.Range(1, 80)));
            Assert.That(service.QueuedCoverIds, Is.EqualTo(Enumerable.Range(2, 79)), "Owned files and the other media side retain their selection");
            Assert.That(books[0].ForeignEditionId, Is.EqualTo("fra-1"));
            Assert.That(books.Skip(80).Select(b => b.ForeignEditionId), Is.EqualTo(Enumerable.Range(81, 20).Select(id => "fra-" + id)));
        }

        [TestCase("none")]
        [TestCase("queue")]
        [TestCase("notification")]
        public void changed_selections_are_saved_before_paged_cover_commands(string failure)
        {
            var profile = new MetadataProfile { Id = 7, Name = "English", AllowedLanguages = "eng" };
            var author = new Author { Id = 5, AudiobookMetadataProfileId = profile.Id };
            var books = Enumerable.Range(1, 300).Select(id => new Book { Id = id, AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook, ForeignEditionId = "fra-" + id }).ToArray();
            var editions = books.ToDictionary(b => b.Id, b => new List<Edition>
            {
                new() { Id = b.Id * 2, BookId = b.Id, ForeignEditionId = b.ForeignEditionId, Language = "fra", ReadingFormatId = 2, Monitored = true },
                new() { Id = b.Id * 2 + 1, BookId = b.Id, ForeignEditionId = "eng-" + b.Id, Language = "eng", ReadingFormatId = 2 }
            });
            var bookService = new StubBookService(books) { FailNotification = failure == "notification" ? 44 : null };
            var savedBeforeQueue = new List<int>();
            bookService.BeforeCoverQueue = () =>
            {
                savedBeforeQueue.Add(bookService.Changes.Count);
                if (failure == "queue") throw new InvalidOperationException("Fixture queue unavailable");
            };
            CreateSut(new StubAuthorService(author), bookService, new StubEditionService(editions), new StubMediaFileService())
                .Handle(new MetadataProfileUpdatedEvent(profile));
            Assert.That(savedBeforeQueue, Is.Not.Empty);
            Assert.That(savedBeforeQueue, Is.All.EqualTo(failure == "notification" ? 299 : 300));
            Assert.That(books.All(b => b.ForeignEditionId == "eng-" + b.Id), Is.True);
            if (failure != "queue")
            {
                Assert.That(bookService.CoverPageSizes, Is.EqualTo(new[] { 250, 50 }));
                Assert.That(bookService.QueuedCoverIds, Is.EqualTo(Enumerable.Range(1, 300)));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_book_selection_does_not_skip_later_books_but_cancellation_stops(bool cancelled)
        {
            var profile = new MetadataProfile { Id = 7, AllowedLanguages = "eng" };
            var author = new Author { Id = 5, AudiobookMetadataProfileId = profile.Id };
            var books = Enumerable.Range(1, 3).Select(id => new Book { Id = id, AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook, ForeignEditionId = "fra-" + id }).ToArray();
            var editions = books.ToDictionary(book => book.Id, book => new List<Edition>
            {
                new() { Id = book.Id * 2, BookId = book.Id, ForeignEditionId = book.ForeignEditionId, Language = "fra", ReadingFormatId = 2, Monitored = true },
                new() { Id = book.Id * 2 + 1, BookId = book.Id, ForeignEditionId = "eng-" + book.Id, Language = "eng", ReadingFormatId = 2 }
            });
            Exception failure = cancelled ? new OperationCanceledException("Fixture cancelled") : new TimeoutException("Fixture selection save timed out");
            var saves = new List<int>();
            var editionService = new StubEditionService(editions)
            {
                BeforeSelection = batch =>
                {
                    if (batch[0].BookId == 2) throw failure;
                    saves.Add(batch[0].BookId);
                }
            };
            var bookService = new StubBookService(books);
            var sut = CreateSut(new StubAuthorService(author), bookService, editionService, new StubMediaFileService());
            if (cancelled) Assert.That(Assert.Catch(() => sut.Handle(new MetadataProfileUpdatedEvent(profile))), Is.SameAs(failure));
            else sut.Handle(new MetadataProfileUpdatedEvent(profile));
            var expected = cancelled ? new[] { 1 } : new[] { 1, 3 };
            Assert.Multiple(() =>
            {
                Assert.That(saves, Is.EqualTo(expected));
                Assert.That(bookService.Changes.Select(change => change.Book.Id), Is.EqualTo(expected));
                Assert.That(bookService.Batches.SelectMany(batch => batch.Changes).Select(change => change.Book.Id), Is.EqualTo(expected));
                Assert.That(bookService.QueuedCoverIds, Is.EqualTo(expected));
                Assert.That(books[1].ForeignEditionId, Is.EqualTo("fra-2"), "A failed save must not be announced as a new selection");
            });
        }

        [Test]
        public void should_reselect_to_allowed_audiobook_and_update_book_foreign_edition_id()
        {
            var profile = new MetadataProfile { Id = 7, Name = "English", AllowedLanguages = "eng" };
            var author = new Author { Id = 5, Name = "Frank Herbert", AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "fra-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = false },
                new Edition { Id = 3, BookId = book.Id, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3, Monitored = false }
            };

            var bookService = new StubBookService(book);
            var sut = CreateSut(
                new StubAuthorService(author),
                bookService,
                new StubEditionService(new Dictionary<int, List<Edition>> { [book.Id] = editions }),
                new StubMediaFileService());

            sut.Handle(new MetadataProfileUpdatedEvent(profile));

            Assert.That(editions.Single(e => e.ForeignEditionId == "eng-audio").Monitored, Is.True);
            Assert.That(editions.Single(e => e.ForeignEditionId == "fra-audio").Monitored, Is.False);
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
            Assert.That(bookService.UpdatedBooks, Is.Empty, "Selection must not rewrite the Book row");
            Assert.That(bookService.Changes.Single().Book, Is.SameAs(book));
            Assert.That(bookService.Changes.Single().OldBook, Is.Not.SameAs(book));
            Assert.That(bookService.Batches.Single().Changes.Single(), Is.SameAs(bookService.Changes.Single()));
            Assert.That(bookService.QueuedCoverIds, Is.EqualTo(new[] { book.Id }));
            sut.Handle(new MetadataProfileUpdatedEvent(profile));
            Assert.That(bookService.QueuedCoverIds, Is.EqualTo(new[] { book.Id }), "Unchanged selection must not queue again");
            Assert.That(bookService.Changes, Has.Count.EqualTo(1), "Unchanged selection must not notify again");
        }

        [Test]
        public void should_treat_null_language_as_allowed_bucket_when_reselecting()
        {
            var profile = new MetadataProfile { Id = 8, Name = "Unknown", AllowedLanguages = "null" };
            var author = new Author { Id = 6, Name = "Frank Herbert", AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 12,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "eng-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "null-audio", Title = "Dune Audio Unknown", Language = null, ReadingFormatId = 2, Monitored = false },
                new Edition { Id = 3, BookId = book.Id, ForeignEditionId = "null-ebook", Title = "Dune", Language = null, ReadingFormatId = 3, Monitored = false }
            };

            var bookService = new StubBookService(book);
            var sut = CreateSut(
                new StubAuthorService(author),
                bookService,
                new StubEditionService(new Dictionary<int, List<Edition>> { [book.Id] = editions }),
                new StubMediaFileService());

            sut.Handle(new MetadataProfileUpdatedEvent(profile));

            Assert.That(editions.Single(e => e.ForeignEditionId == "null-audio").Monitored, Is.True);
            Assert.That(editions.Single(e => e.ForeignEditionId == "eng-audio").Monitored, Is.False);
            Assert.That(book.ForeignEditionId, Is.EqualTo("null-audio"));
            Assert.That(bookService.UpdatedBooks, Is.Empty, "Selection must not rewrite the Book row");
            Assert.That(bookService.Changes.Single().Book, Is.SameAs(book));
            Assert.That(bookService.Changes.Single().OldBook, Is.Not.SameAs(book));
            Assert.That(bookService.Batches.Single().Changes.Single(), Is.SameAs(bookService.Changes.Single()));
        }

        [Test]
        public void should_skip_reselection_when_allowed_languages_are_unchanged()
        {
            var previousProfile = new MetadataProfile { Id = 7, Name = "English", AllowedLanguages = "eng" };
            var profile = new MetadataProfile { Id = 7, Name = "English Restored", AllowedLanguages = "eng" };
            var author = new Author { Id = 5, Name = "Frank Herbert", AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "fra-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = false }
            };

            var bookService = new StubBookService(book);
            var editionService = new StubEditionService(new Dictionary<int, List<Edition>> { [book.Id] = editions });
            var sut = CreateSut(
                new StubAuthorService(author),
                bookService,
                editionService,
                new StubMediaFileService());

            sut.Handle(new MetadataProfileUpdatedEvent(profile, previousProfile));

            Assert.That(editions.Single(e => e.ForeignEditionId == "fra-audio").Monitored, Is.True);
            Assert.That(editions.Single(e => e.ForeignEditionId == "eng-audio").Monitored, Is.False);
            Assert.That(book.ForeignEditionId, Is.EqualTo("fra-audio"));
            Assert.That(editionService.UpdatedEditions, Is.Null);
            Assert.That(bookService.UpdatedBooks, Is.Empty);
        }

        [Test]
        public void should_skip_reselection_when_allowed_languages_are_equivalent()
        {
            var previousProfile = new MetadataProfile { Id = 7, Name = "English", AllowedLanguages = "eng, null" };
            var profile = new MetadataProfile { Id = 7, Name = "English Restored", AllowedLanguages = "unknown, ENG" };
            var author = new Author { Id = 5, Name = "Frank Herbert", AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                ForeignEditionId = "fra-audio"
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2, Monitored = true },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2, Monitored = false }
            };

            var bookService = new StubBookService(book);
            var editionService = new StubEditionService(new Dictionary<int, List<Edition>> { [book.Id] = editions });
            var sut = CreateSut(
                new StubAuthorService(author),
                bookService,
                editionService,
                new StubMediaFileService());

            sut.Handle(new MetadataProfileUpdatedEvent(profile, previousProfile));

            Assert.That(editionService.UpdatedEditions, Is.Null);
            Assert.That(bookService.UpdatedBooks, Is.Empty);
        }
    }
}
