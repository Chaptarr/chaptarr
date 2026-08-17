using System;
using System.Collections.Generic;
using System.Data;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Releases;

namespace Chaptarr.Core.Test.Profiles.Metadata
{
    [TestFixture]
    public class MetadataProfileServiceMediaTypeFilteringFixture
    {
        private sealed class StubMetadataProfileRepository : IMetadataProfileRepository
        {
            private readonly Dictionary<int, MetadataProfile> _profiles;

            public StubMetadataProfileRepository(IEnumerable<MetadataProfile> profiles)
            {
                _profiles = profiles.ToDictionary(p => p.Id);
            }

            public bool Exists(int id) => _profiles.ContainsKey(id);

            public IEnumerable<MetadataProfile> All() => _profiles.Values;

            public int Count() => _profiles.Count;

            public MetadataProfile Find(int id) => _profiles.TryGetValue(id, out var profile) ? profile : null;

            public MetadataProfile Get(int id) => _profiles[id];

            public MetadataProfile Insert(MetadataProfile model) => throw new NotImplementedException();

            public MetadataProfile Update(MetadataProfile model) => throw new NotImplementedException();

            public MetadataProfile Upsert(MetadataProfile model) => throw new NotImplementedException();

            public void SetFields(MetadataProfile model, params System.Linq.Expressions.Expression<Func<MetadataProfile, object>>[] properties) => throw new NotImplementedException();

            public void Delete(MetadataProfile model) => throw new NotImplementedException();

            public void Delete(int id) => throw new NotImplementedException();

            public IEnumerable<MetadataProfile> Get(IEnumerable<int> ids) => throw new NotImplementedException();

            public void InsertMany(IList<MetadataProfile> model) => throw new NotImplementedException();

            public void InsertMany(IList<MetadataProfile> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();

            public void UpdateMany(IList<MetadataProfile> model) => throw new NotImplementedException();

            public void SetFields(IList<MetadataProfile> models, params System.Linq.Expressions.Expression<Func<MetadataProfile, object>>[] properties) => throw new NotImplementedException();

            public void DeleteMany(List<MetadataProfile> model) => throw new NotImplementedException();

            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();

            public void Purge(bool vacuum = false) => throw new NotImplementedException();

            public bool HasItems() => throw new NotImplementedException();

            public MetadataProfile Single() => throw new NotImplementedException();

            public MetadataProfile SingleOrDefault() => throw new NotImplementedException();

            public PagingSpec<MetadataProfile> GetPaged(PagingSpec<MetadataProfile> pagingSpec) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;

            public StubAuthorService(Author author)
            {
                _author = author;
            }

            public Author GetAuthor(int authorId) => authorId == _author.Id ? _author : null;
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
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
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            private readonly List<Book> _books;

            public StubBookService(List<Book> books)
            {
                _books = books ?? new List<Book>();
            }

            public List<Book> GetBooksByAuthor(int authorId) => _books.Where(b => b.AuthorId == authorId).ToList();

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public Book FindBySlug(string titleSlug) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => new List<Book>();
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => throw new NotImplementedException();
            public Book UpdateBook(Book book) => throw new NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId, bool asNewVariant = false) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            private readonly List<Edition> _editions;

            public StubEditionService(List<Edition> editions)
            {
                _editions = editions ?? new List<Edition>();
            }

            public List<Edition> GetEditionsByAuthor(int authorId) => _editions;

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
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly List<BookFile> _files;

            public StubMediaFileService(List<BookFile> files)
            {
                _files = files ?? new List<BookFile>();
            }

            public List<BookFile> GetFilesByAuthor(int authorId) => _files;

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
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

        private sealed class StubTermMatcherService : ITermMatcherService
        {
            public bool IsMatch(string term, string value) => false;
            public string MatchingTerm(string term, string value) => null;
        }

        private static MetadataProfile CreateProfile(int id, string allowedLanguages = null, bool skipOmnibus = false, bool skipMissingIdentifierOmnibus = false, int minPages = 0)
        {
            return new MetadataProfile
            {
                Id = id,
                Name = "Test",
                ProfileType = MetadataProfileType.General,
                MinPopularity = 0,
                SkipMissingDate = false,
                SkipMissingIsbn = false,
                SkipMissingAsin = false,
                SkipPartsAndSets = false,
                SkipSeriesSecondary = false,
                SkipMissingIdentifierOmnibus = skipMissingIdentifierOmnibus,
                SkipOmnibus = skipOmnibus,
                AllowedLanguages = allowedLanguages,
                MinPages = minPages,
                Ignored = new List<string>()
            };
        }

        [Test]
        public void should_keep_book_when_page_count_equals_minimum_pages()
        {
            var profile = CreateProfile(id: 1, minPages: 300);
            var remoteBook = new Book
            {
                Title = "Exact Pages",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "edition-1", PageCount = 300 }
                }
            };
            var remoteAuthor = new Author { Books = new List<Book> { remoteBook } };
            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: null,
                bookService: null,
                editionService: null,
                mediaFileService: new StubMediaFileService(new List<BookFile>()),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_skip_omnibus_without_identifier_when_loose_omnibus_filter_is_enabled()
        {
            var profile = CreateProfile(id: 1, skipMissingIdentifierOmnibus: true);
            var remoteBook = new Book
            {
                Title = "Collected Stories",
                MediaType = BookMediaType.Ebook,
                IsOmnibus = true,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "edition-1" }
                }
            };
            var remoteAuthor = new Author { Books = new List<Book> { remoteBook } };
            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: null,
                bookService: null,
                editionService: null,
                mediaFileService: new StubMediaFileService(new List<BookFile>()),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_keep_omnibus_with_identifier_when_only_loose_omnibus_filter_is_enabled()
        {
            var profile = CreateProfile(id: 1, skipMissingIdentifierOmnibus: true);
            var remoteBook = new Book
            {
                Title = "Collected Stories",
                MediaType = BookMediaType.Ebook,
                IsOmnibus = true,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "edition-1", Isbn13 = "9780000000000" }
                }
            };
            var remoteAuthor = new Author { Books = new List<Book> { remoteBook } };
            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: null,
                bookService: null,
                editionService: null,
                mediaFileService: new StubMediaFileService(new List<BookFile>()),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_not_pin_audiobook_book_due_to_ebook_files_when_all_editions_filtered_out()
        {
            var profile = CreateProfile(id: 1, allowedLanguages: "eng");

            var dbAuthor = new Author { Id = 1, Name = "Test Author" };

            var localEbookBook = new Book
            {
                Id = 10,
                AuthorId = dbAuthor.Id,
                Title = "Test Book",
                GoodreadsBookId = "gr:1",
                MediaType = BookMediaType.Ebook
            };

            var localEbookEdition = new Edition
            {
                Id = 100,
                BookId = localEbookBook.Id,
                ForeignEditionId = "local-ebook-1",
                Language = "eng"
            };

            var localFiles = new List<BookFile>
            {
                new BookFile
                {
                    Id = 1000,
                    EditionId = localEbookEdition.Id,
                    Edition = new Edition
                    {
                        Id = localEbookEdition.Id,
                        BookId = localEbookBook.Id,
                        ForeignEditionId = localEbookEdition.ForeignEditionId,
                        Language = localEbookEdition.Language,
                        Book = localEbookBook
                    }
                }
            };

            var remoteAudiobook = new Book
            {
                Title = "Test Book",
                GoodreadsBookId = "gr:1",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "remote-audio-1", Language = "fra" },
                    new Edition { ForeignEditionId = "remote-audio-2", Language = "deu" }
                }
            };

            var remoteAuthor = new Author
            {
                Id = dbAuthor.Id,
                Name = dbAuthor.Name,
                Books = new List<Book> { remoteAudiobook }
            };

            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: new StubAuthorService(dbAuthor),
                bookService: new StubBookService(new List<Book> { localEbookBook }),
                editionService: new StubEditionService(new List<Edition> { localEbookEdition }),
                mediaFileService: new StubMediaFileService(localFiles),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_not_pin_audiobook_omnibus_due_to_ebook_files()
        {
            var profile = CreateProfile(id: 1, allowedLanguages: null, skipOmnibus: true);

            var dbAuthor = new Author { Id = 1, Name = "Test Author" };

            var localEbookBook = new Book
            {
                Id = 10,
                AuthorId = dbAuthor.Id,
                Title = "Test Book",
                GoodreadsBookId = "gr:1",
                MediaType = BookMediaType.Ebook
            };

            var localFiles = new List<BookFile>
            {
                new BookFile
                {
                    Id = 1000,
                    EditionId = 100,
                    Edition = new Edition
                    {
                        Id = 100,
                        BookId = localEbookBook.Id,
                        ForeignEditionId = "local-ebook-1",
                        Language = "eng",
                        Book = localEbookBook
                    }
                }
            };

            var remoteAudiobook = new Book
            {
                Title = "Test Book",
                GoodreadsBookId = "gr:1",
                MediaType = BookMediaType.Audiobook,
                IsOmnibus = true,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "remote-audio-1", Language = "eng" }
                }
            };

            var remoteAuthor = new Author
            {
                Id = dbAuthor.Id,
                Name = dbAuthor.Name,
                Books = new List<Book> { remoteAudiobook }
            };

            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: new StubAuthorService(dbAuthor),
                bookService: new StubBookService(new List<Book> { localEbookBook }),
                editionService: new StubEditionService(new List<Edition>()),
                mediaFileService: new StubMediaFileService(localFiles),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_keep_omnibus_when_local_file_edition_matches_remote_work_edition_cluster()
        {
            var profile = CreateProfile(id: 1, allowedLanguages: "eng", skipOmnibus: true);
            var dbAuthor = new Author { Id = 50, Name = "Jim Butcher" };

            var localBareBook = new Book
            {
                Id = 1947,
                AuthorId = dbAuthor.Id,
                Title = "Brief Cases",
                BaseBookId = "az:B07B3DKSNT",
                MediaType = BookMediaType.Audiobook
            };

            var localEdition = new Edition
            {
                Id = 5166,
                BookId = localBareBook.Id,
                ForeignEditionId = "az:B07B3OLD-audiobook",
                Asin = "B07B3DKSNT",
                AudibleASIN = "B07B3WPXMJ",
                Asins = new List<string> { "B07B3DKSNT", "B07B3WPXMJ" },
                Language = "eng",
                Book = localBareBook
            };

            var localFiles = new List<BookFile>
            {
                new BookFile
                {
                    Id = 415,
                    EditionId = localEdition.Id,
                    Edition = localEdition,
                    MediaType = "audiobook",
                    Path = "/audiobooks/Jim Butcher/Brief Cases/Brief Cases.m4b"
                }
            };

            var remoteBook = new Book
            {
                Title = "Brief Cases",
                GoodreadsWorkId = "gr:17155691",
                HardcoverBookId = "hc:461427",
                MediaType = BookMediaType.Audiobook,
                IsOmnibus = true,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        ForeignEditionId = "az:B07B3NEW-audiobook",
                        Asin = "B07B3WPXMJ",
                        Asins = new List<string> { "B07B3DKSNT", "B07B3WPXMJ" },
                        Language = "fra"
                    }
                }
            };

            var remoteAuthor = new Author
            {
                Id = dbAuthor.Id,
                Name = dbAuthor.Name,
                Books = new List<Book> { remoteBook }
            };

            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: new StubAuthorService(dbAuthor),
                bookService: new StubBookService(new List<Book> { localBareBook }),
                editionService: new StubEditionService(new List<Edition> { localEdition }),
                mediaFileService: new StubMediaFileService(localFiles),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result.Single().Title, Is.EqualTo("Brief Cases"));
            Assert.That(result.Single().Editions, Has.Count.EqualTo(1));
            Assert.That(result.Single().Editions.Single().ForeignEditionId, Is.EqualTo("az:B07B3NEW-audiobook"));
        }

        [Test]
        public void should_not_keep_ebook_omnibus_due_to_audiobook_file_edition_cluster()
        {
            var profile = CreateProfile(id: 1, allowedLanguages: "eng", skipOmnibus: true);
            var dbAuthor = new Author { Id = 50, Name = "Jim Butcher" };

            var localAudiobookBook = new Book
            {
                Id = 1947,
                AuthorId = dbAuthor.Id,
                Title = "Brief Cases",
                BaseBookId = "az:B07B3DKSNT",
                MediaType = BookMediaType.Audiobook
            };

            var localEdition = new Edition
            {
                Id = 5166,
                BookId = localAudiobookBook.Id,
                ForeignEditionId = "az:B07B3OLD-audiobook",
                Asin = "B07B3DKSNT",
                Asins = new List<string> { "B07B3DKSNT", "B07B3WPXMJ" },
                Book = localAudiobookBook
            };

            var localFiles = new List<BookFile>
            {
                new BookFile
                {
                    Id = 415,
                    EditionId = localEdition.Id,
                    Edition = localEdition,
                    MediaType = "audiobook",
                    Path = "/audiobooks/Jim Butcher/Brief Cases/Brief Cases.m4b"
                }
            };

            var remoteEbook = new Book
            {
                Title = "Brief Cases",
                GoodreadsWorkId = "gr:17155691",
                HardcoverBookId = "hc:461427",
                MediaType = BookMediaType.Ebook,
                IsOmnibus = true,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        ForeignEditionId = "az:B07B3NEW-ebook",
                        Asin = "B07B3WPXMJ",
                        Asins = new List<string> { "B07B3DKSNT", "B07B3WPXMJ" },
                        Language = "eng"
                    }
                }
            };

            var remoteAuthor = new Author
            {
                Id = dbAuthor.Id,
                Name = dbAuthor.Name,
                Books = new List<Book> { remoteEbook }
            };

            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: new StubAuthorService(dbAuthor),
                bookService: new StubBookService(new List<Book> { localAudiobookBook }),
                editionService: new StubEditionService(new List<Edition> { localEdition }),
                mediaFileService: new StubMediaFileService(localFiles),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_not_keep_omnibus_when_local_file_edition_tokens_point_to_multiple_remote_work_pockets()
        {
            var profile = CreateProfile(id: 1, allowedLanguages: "eng", skipOmnibus: true);
            var dbAuthor = new Author { Id = 50, Name = "Test Author" };

            var localBook = new Book
            {
                Id = 10,
                AuthorId = dbAuthor.Id,
                Title = "Shared ASIN",
                MediaType = BookMediaType.Audiobook
            };

            var localEdition = new Edition
            {
                Id = 100,
                BookId = localBook.Id,
                ForeignEditionId = "az:B000LOCAL-audiobook",
                Asins = new List<string> { "B000ONE", "B000TWO" },
                Book = localBook
            };

            var localFiles = new List<BookFile>
            {
                new BookFile
                {
                    Id = 1000,
                    EditionId = localEdition.Id,
                    Edition = localEdition,
                    MediaType = "audiobook",
                    Path = "/audiobooks/Test/Shared.m4b"
                }
            };

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    Title = "Work One",
                    GoodreadsWorkId = "gr:111",
                    MediaType = BookMediaType.Audiobook,
                    IsOmnibus = true,
                    Editions = new List<Edition>
                    {
                        new Edition { ForeignEditionId = "az:B000ONE-audiobook", Asins = new List<string> { "B000ONE" }, Language = "eng" }
                    }
                },
                new Book
                {
                    Title = "Work Two",
                    GoodreadsWorkId = "gr:222",
                    MediaType = BookMediaType.Audiobook,
                    IsOmnibus = true,
                    Editions = new List<Edition>
                    {
                        new Edition { ForeignEditionId = "az:B000TWO-audiobook", Asins = new List<string> { "B000TWO" }, Language = "eng" }
                    }
                }
            };

            var remoteAuthor = new Author
            {
                Id = dbAuthor.Id,
                Name = dbAuthor.Name,
                Books = remoteBooks
            };

            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: new StubAuthorService(dbAuthor),
                bookService: new StubBookService(new List<Book> { localBook }),
                editionService: new StubEditionService(new List<Edition> { localEdition }),
                mediaFileService: new StubMediaFileService(localFiles),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_not_protect_work_id_less_remote_books_through_zero_remote_key()
        {
            var profile = CreateProfile(id: 1, allowedLanguages: "eng", skipOmnibus: true);
            var dbAuthor = new Author { Id = 50, Name = "Test Author" };

            var localBareBook = new Book
            {
                Id = 10,
                AuthorId = dbAuthor.Id,
                Title = "Bare Pocket One",
                MediaType = BookMediaType.Audiobook
            };

            var localEdition = new Edition
            {
                Id = 100,
                BookId = localBareBook.Id,
                ForeignEditionId = "az:B000ONE-audiobook",
                Asins = new List<string> { "B000ONE" },
                Book = localBareBook
            };

            var localFiles = new List<BookFile>
            {
                new BookFile
                {
                    Id = 1000,
                    EditionId = localEdition.Id,
                    Edition = localEdition,
                    MediaType = "audiobook",
                    Path = "/audiobooks/Test/Bare One.m4b"
                }
            };

            var remoteBooks = new List<Book>
            {
                new Book
                {
                    Title = "Bare Pocket One",
                    MediaType = BookMediaType.Audiobook,
                    IsOmnibus = true,
                    Editions = new List<Edition>
                    {
                        new Edition { ForeignEditionId = "az:B000ONE-audiobook", Asins = new List<string> { "B000ONE" }, Language = "eng" }
                    }
                },
                new Book
                {
                    Title = "Bare Pocket Two",
                    MediaType = BookMediaType.Audiobook,
                    IsOmnibus = true,
                    Editions = new List<Edition>
                    {
                        new Edition { ForeignEditionId = "az:B000TWO-audiobook", Asins = new List<string> { "B000TWO" }, Language = "eng" }
                    }
                }
            };

            var remoteAuthor = new Author
            {
                Id = dbAuthor.Id,
                Name = dbAuthor.Name,
                Books = remoteBooks
            };

            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: new StubAuthorService(dbAuthor),
                bookService: new StubBookService(new List<Book> { localBareBook }),
                editionService: new StubEditionService(new List<Edition> { localEdition }),
                mediaFileService: new StubMediaFileService(localFiles),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_keep_audiobook_omnibus_when_audiobook_files_exist()
        {
            var profile = CreateProfile(id: 1, allowedLanguages: null, skipOmnibus: true);

            var dbAuthor = new Author { Id = 1, Name = "Test Author" };

            var localAudiobookBook = new Book
            {
                Id = 20,
                AuthorId = dbAuthor.Id,
                Title = "Test Book",
                GoodreadsBookId = "gr:1",
                MediaType = BookMediaType.Audiobook
            };

            var localFiles = new List<BookFile>
            {
                new BookFile
                {
                    Id = 2000,
                    EditionId = 200,
                    Edition = new Edition
                    {
                        Id = 200,
                        BookId = localAudiobookBook.Id,
                        ForeignEditionId = "local-audio-1",
                        Language = "eng",
                        Book = localAudiobookBook
                    }
                }
            };

            var remoteAudiobook = new Book
            {
                Title = "Test Book",
                GoodreadsBookId = "gr:1",
                MediaType = BookMediaType.Audiobook,
                IsOmnibus = true,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "remote-audio-1", Language = "eng" }
                }
            };

            var remoteAuthor = new Author
            {
                Id = dbAuthor.Id,
                Name = dbAuthor.Name,
                Books = new List<Book> { remoteAudiobook }
            };

            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: new StubAuthorService(dbAuthor),
                bookService: new StubBookService(new List<Book> { localAudiobookBook }),
                editionService: new StubEditionService(new List<Edition>()),
                mediaFileService: new StubMediaFileService(localFiles),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var result = service.FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Has.Count.EqualTo(1));
        }
    }
}
