using System;
using System.Collections.Generic;
using System.Data;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Releases;

namespace Chaptarr.Core.Test.Profiles.Metadata
{
    [TestFixture]
    public partial class MetadataProfileServiceMediaTypeFilteringFixture
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
            public int CatalogReads { get; private set; }

            private readonly Author _author;

            public StubAuthorService(Author author)
            {
                _author = author;
            }

            public Author GetAuthor(int authorId)
            {
                CatalogReads++;
                return authorId == _author.Id ? _author : null;
            }
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
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
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

            public int CatalogReads { get; private set; }

            private readonly List<Book> _books;

            public StubBookService(List<Book> books)
            {
                _books = books ?? new List<Book>();
            }

            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false)
            {
                CatalogReads++;
                return _books.Where(b => b.AuthorId == authorId).ToList();
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public List<Book> FindBySlug(string titleSlug) => throw new NotImplementedException();
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
            public Book UpdateBook(Book book, List<Edition> editionUpdates, string monitoredMediaType = null, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> writeRelated = null) => throw new System.NotSupportedException();
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
            public int CatalogReads { get; private set; }

            private readonly List<Edition> _editions;

            public StubEditionService(List<Edition> editions)
            {
                _editions = editions ?? new List<Edition>();
            }

            public List<Edition> GetEditionsByAuthor(int authorId, bool onlyMonitored = false)
            {
                CatalogReads++;
                return _editions;
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
            public void InsertMany(List<Edition> editions, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Edition selectedEdition = null) => throw new System.NotSupportedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();

            public void RefreshProviderAliases(List<Edition> editions) { }
            public void UpdateSelection(List<Edition> editions) => UpdateMany(editions);
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public void ValidateReplacementFiles(System.Collections.Generic.List<BookFile> replacedFiles,
                System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles)
                => throw new System.NotSupportedException("This test adapter does not support replacement validation.");

            public void ReplaceMany(System.Collections.Generic.List<BookFile> bookFiles, System.Collections.Generic.List<BookFile> replacedFiles,
                DeleteMediaFileReason reason, System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles,
                System.Action<System.Action<System.Data.IDbConnection, System.Data.IDbTransaction>> persist)
                => throw new System.NotSupportedException("This test adapter does not support transactional replacement.");

            public int CatalogReads { get; private set; }

            private readonly List<BookFile> _files;

            public StubMediaFileService(List<BookFile> files)
            {
                _files = files ?? new List<BookFile>();
            }

            public List<BookFile> GetFilesByAuthor(int authorId)
            {
                CatalogReads++;
                return _files;
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, NzbDrone.Core.MediaFiles.AudioProductionCorrection correction, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();

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

        private static Book CreateSeriesBook(string title, string workId, BookMediaType mediaType, params SeriesBookLink[] seriesLinks)
        {
            return new Book
            {
                Title = title,
                GoodreadsWorkId = workId,
                MediaType = mediaType,
                SeriesLinks = seriesLinks?.ToList(),
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = $"{workId}-{mediaType}", Title = title }
                }
            };
        }

        private static MetadataProfileService CreateService(MetadataProfile profile, Author dbAuthor = null, List<Book> localBooks = null, List<Edition> localEditions = null, List<BookFile> localFiles = null)
        {
            return new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(new[] { profile }),
                authorService: dbAuthor == null ? null : new StubAuthorService(dbAuthor),
                bookService: localBooks == null ? null : new StubBookService(localBooks),
                editionService: localEditions == null ? null : new StubEditionService(localEditions),
                mediaFileService: new StubMediaFileService(localFiles ?? new List<BookFile>()),
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: new StubTermMatcherService(),
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_filter_only_books_with_exclusively_secondary_series_links()
        {
            var profile = CreateProfile(id: 1);
            profile.SkipSeriesSecondary = true;

            var primaryBook = CreateSeriesBook(
                "A Game of Thrones",
                "gr:1216467",
                BookMediaType.Ebook,
                new SeriesBookLink { IsPrimary = true, Position = "1" },
                new SeriesBookLink { IsPrimary = false, Position = "2" });
            var secondaryBook = CreateSeriesBook(
                "The Hedge Knight",
                "gr:1466917",
                BookMediaType.Ebook,
                new SeriesBookLink { IsPrimary = false, Position = "0.5" });
            var unflaggedBook = CreateSeriesBook(
                "The World of Ice & Fire",
                "gr:22083575",
                BookMediaType.Ebook,
                new SeriesBookLink { IsPrimary = null, Position = "0.4" });
            var noSeriesBook = CreateSeriesBook(
                "Fevre Dream",
                "gr:382450",
                BookMediaType.Ebook,
                null);

            var remoteAuthor = new Author
            {
                Books = new List<Book> { primaryBook, secondaryBook, unflaggedBook, noSeriesBook }
            };

            var result = CreateService(profile).FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result.Select(x => x.Title), Is.EquivalentTo(new[]
            {
                primaryBook.Title,
                unflaggedBook.Title,
                noSeriesBook.Title
            }));
        }

        [Test]
        public void should_filter_series_secondary_per_media_type()
        {
            var profile = CreateProfile(id: 1);
            profile.SkipSeriesSecondary = true;

            var audiobook = CreateSeriesBook(
                "A Game of Thrones",
                "gr:1216467",
                BookMediaType.Audiobook,
                new SeriesBookLink { IsPrimary = true, Position = "1" });
            var ebook = CreateSeriesBook(
                "A Game of Thrones",
                "gr:1216467",
                BookMediaType.Ebook,
                new SeriesBookLink { IsPrimary = false, Position = "1" });
            var remoteAuthor = new Author { Books = new List<Book> { audiobook, ebook } };

            var result = CreateService(profile).FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result.Select(x => x.MediaType), Is.EqualTo(new[] { BookMediaType.Audiobook }));
        }

        [Test]
        public void should_filter_value_equal_remote_pockets_independently()
        {
            var profile = CreateProfile(id: 1);
            profile.SkipSeriesSecondary = true;

            var secondaryPocket = CreateSeriesBook(
                "A Game of Thrones",
                "gr:1216467",
                BookMediaType.Ebook,
                new SeriesBookLink { IsPrimary = false, Position = "1" });
            secondaryPocket.Editions.Single().ForeignEditionId = "gr:edition-secondary";

            var primaryPocket = CreateSeriesBook(
                "A Game of Thrones",
                "gr:1216467",
                BookMediaType.Ebook,
                new SeriesBookLink { IsPrimary = true, Position = "1" });
            primaryPocket.Editions.Single().ForeignEditionId = "gr:edition-primary";

            Assert.Multiple(() =>
            {
                Assert.That(secondaryPocket.Equals(primaryPocket), Is.True, "edition and series-link differences are excluded from Book value equality");
                Assert.That(secondaryPocket, Is.Not.SameAs(primaryPocket));
            });

            var result = CreateService(profile).FilterBooks(
                new Author { Books = new List<Book> { secondaryPocket, primaryPocket } },
                profile.Id);

            Assert.That(result, Is.EqualTo(new[] { primaryPocket }));
        }

        [TestCase("5.5,6.5,7.5", 1)]
        [TestCase("1,2,3", 0)]
        [TestCase("1-3", 0)]
        public void should_classify_series_slots_when_skipping_parts_and_sets(string position, int expectedCount)
        {
            var profile = CreateProfile(id: 1);
            profile.SkipPartsAndSets = true;

            var book = CreateSeriesBook(
                "The Complete Tales of Dunk and Egg",
                "gr:18635622",
                BookMediaType.Ebook,
                new SeriesBookLink { IsPrimary = true, Position = position });

            var result = CreateService(profile).FilterBooks(new Author { Books = new List<Book> { book } }, profile.Id);

            Assert.That(result, Has.Count.EqualTo(expectedCount));
        }

        [Test]
        public void should_keep_secondary_series_book_with_an_existing_file()
        {
            var profile = CreateProfile(id: 1);
            profile.SkipSeriesSecondary = true;
            var dbAuthor = new Author { Id = 1, Name = "George R. R. Martin" };
            var localBook = CreateSeriesBook("The Hedge Knight", "gr:1466917", BookMediaType.Audiobook);
            localBook.Id = 10;
            localBook.AuthorId = dbAuthor.Id;
            var localEdition = localBook.Editions.Single();
            localEdition.Id = 20;
            localEdition.BookId = localBook.Id;
            localEdition.Book = localBook;
            var localFile = new BookFile
            {
                Id = 30,
                EditionId = localEdition.Id,
                Edition = localEdition,
                MediaType = "audiobook",
                Path = "/audiobooks/George R. R. Martin/The Hedge Knight.m4b"
            };
            var remoteBook = CreateSeriesBook(
                localBook.Title,
                localBook.GoodreadsWorkId,
                BookMediaType.Audiobook,
                new SeriesBookLink { IsPrimary = false, Position = "0.5" });
            var remoteAuthor = new Author
            {
                Id = dbAuthor.Id,
                Name = dbAuthor.Name,
                Books = new List<Book> { remoteBook }
            };

            var result = CreateService(
                profile,
                dbAuthor,
                new List<Book> { localBook },
                new List<Edition> { localEdition },
                new List<BookFile> { localFile })
                .FilterBooks(remoteAuthor, profile.Id);

            Assert.That(result, Has.Count.EqualTo(1));
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
        [TestCase(false)]
        [TestCase(true)]
        public void requested_narration_should_protect_only_its_edition_and_audio_book(bool explicitRequest)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            profile.SkipMissingDate = true;
            Book Remote(string work, BookMediaType mediaType) => new Book
            {
                Title = "Requested book", GoodreadsWorkId = work, MediaType = mediaType,
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "gr:202-audiobook", ReadingFormatId = 2, Language = "fra", Narrator = "Requested Narrator" },
                    new() { ForeignEditionId = "gr:203-audiobook", ReadingFormatId = 2, Language = "fra", Narrator = "Other Narrator" }
                }
            };
            var requested = Remote("gr:1001", BookMediaType.Audiobook);
            var unrelated = Remote("gr:1002", BookMediaType.Audiobook);
            unrelated.Editions.ForEach(edition => edition.ForeignEditionId += "-other");
            var author = new Author { Books = new List<Book> { requested, unrelated, Remote("gr:1001", BookMediaType.Ebook) } };
            var ids = explicitRequest ? new Dictionary<BookMediaType, IReadOnlySet<string>> { [BookMediaType.Audiobook] = new HashSet<string> { "gr:202-audiobook" } } : null;
            var result = CreateService(profile).FilterBooks(author, profile.Id, ids);
            Assert.That(result, Has.Count.EqualTo(explicitRequest ? 1 : 0));
            if (explicitRequest)
            {
                Assert.That(result.Single(), Is.SameAs(requested));
                Assert.That(result.Single().Editions.Select(edition => edition.ForeignEditionId), Is.EqualTo(new[] { "gr:202-audiobook" }));
            }
        }

        [TestCase(2)]
        [TestCase(1001)]
        public void requested_narration_should_not_protect_other_amazon_only_provider_pockets(int bookCount)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var mapper = new BookInfoProxy(null, null, null, null, null, null, null, null, null, null,
                logger, new CacheManager(), new MetadataServerHealthGate(null, new MetadataServerHealthService(logger), logger));
            var convert = typeof(BookInfoProxy).GetMethod("ConvertV5BookToDomain", BindingFlags.Instance | BindingFlags.NonPublic);
            var author = new Author { Name = "Test Author" };
            author.Books = Enumerable.Range(0, bookCount).Select(index => (Book)convert.Invoke(mapper, new object[]
            {
                new V5Book
                {
                    Id = $"az:B{index:000000000}", Title = $"Book {index}",
                    Editions = new List<V5Edition>
                    {
                        new() { Id = $"az:B{index:000000000}", Title = $"Book {index}", ReadingFormatId = 2, LanguageCode = "en" }
                    }
                }, author, BookMediaType.Audiobook
            })).ToList();
            var requested = author.Books[0];
            Assert.That(author.Books.All(book => book.Id == 0 && !BookEditionIdentity.GetCanonicalWorkProviderIds(book).Any()), Is.True);
            var profile = CreateProfile(1);
            profile.SkipMissingDate = true;

            var result = CreateService(profile).FilterBooks(author, profile.Id,
                new Dictionary<BookMediaType, IReadOnlySet<string>> { [BookMediaType.Audiobook] = new HashSet<string> { requested.Editions.Single().ForeignEditionId } });

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result.Single(), Is.SameAs(requested));
        }

        [Test]
        public void requested_cast_should_preserve_a_pruned_language_alternate_and_its_authoritative_parent()
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            profile.SkipMissingDate = true;
            var matching = new Edition { ForeignEditionId = "hc:edition:203", ReadingFormatId = 2, Language = "fra",
                NarratorNames = new List<string> { "Jane Smith", "Jim Dale" } };
            var parent = new Book { Title = "Requested book", HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { matching, new() { ForeignEditionId = "hc:edition:204", ReadingFormatId = 2, Language = "fra", Narrator = "Other Narrator" } } };
            var author = new Author { Books = new List<Book> { parent } };
            var config = new MonitoringConfig { CreateAudiobook = true, CreateEbook = false,
                AudiobookNarrations = new List<RequestedNarration> { new() { WorkProviderId = "hc:1001", EditionProviderId = "hc-ed:999",
                    RequiresEdition = true, NarratorNames = new List<string> { "Jim Dale", "Jane Smith" } } } };

            AuthorLibraryService.PrepareRequestedNarrations(author, config);
            var result = CreateService(profile).FilterBooks(author, profile.Id, config.ProtectedNarrationIds, config.ProtectedNarrationWorkIds);

            Assert.Multiple(() =>
            {
                Assert.That(config.KnownParentEditionRequests, Does.Contain((BookMediaType.Audiobook, "hc:999")));
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result.Single(), Is.SameAs(parent));
                Assert.That(result.Single().Editions, Is.EqualTo(new[] { matching }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unavailable_explicit_narration_preserves_only_its_audiobook_parent(bool explicitNarration)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            profile.SkipMissingDate = true;
            Book Remote(string work, BookMediaType mediaType) => new()
            {
                Title = "Requested work", HardcoverBookId = work, MediaType = mediaType,
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = work + "-other-edition", Title = "Requested work", Language = "eng", ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3 }
                }
            };
            var requested = Remote("hc:1001", BookMediaType.Audiobook);
            var remote = new[] { requested, Remote("hc:1002", BookMediaType.Audiobook), Remote("hc:1001", BookMediaType.Ebook) };
            var result = RefreshAuthorService.NormalizeRemoteBooks(
                new Author { AudiobookMetadataProfileId = 1, EbookMetadataProfileId = 1 }, remote, null,
                CreateService(profile), null, LogManager.GetCurrentClassLogger(), null, null, null, true, "explicit parent regression",
                requestedNarrationIds: new Dictionary<BookMediaType, IReadOnlySet<string>> { [BookMediaType.Audiobook] = new HashSet<string> { "gr:unavailable-edition" } },
                requestedNarrationWorkIds: explicitNarration ? new Dictionary<BookMediaType, IReadOnlySet<string>> { [BookMediaType.Audiobook] = new HashSet<string> { "hc:1001" } } : null);

            Assert.That(result, Has.Count.EqualTo(explicitNarration ? 1 : 0));
            if (explicitNarration)
            {
                Assert.That(result.Single(), Is.SameAs(requested));
                Assert.That(result.Single().Editions.Single().ForeignEditionId, Is.EqualTo("hc:1001-other-edition"));
            }
        }

        [Test]
        public void narration_pin_should_keep_its_book_and_edition_on_later_profile_filtering()
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            profile.SkipMissingDate = true;
            var localEdition = new Edition { Id = 10, BookId = 1, ForeignEditionId = "gr:202", Language = "fra", ReadingFormatId = 2, Monitored = true, ManualAdd = true };
            var localBook = new Book { Id = 1, AuthorId = 1, GoodreadsWorkId = "gr:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = false };
            var dbAuthor = new Author { Id = 1 };
            var remoteBook = new Book
            {
                GoodreadsWorkId = "gr:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { ForeignEditionId = "gr:202", Language = "fra", ReadingFormatId = 2 }, new() { ForeignEditionId = "gr:203", Language = "fra", ReadingFormatId = 2 } }
            };
            var result = CreateService(profile, dbAuthor, new List<Book> { localBook }, new List<Edition> { localEdition })
                .FilterBooks(new Author { Id = 1, Books = new List<Book> { remoteBook } }, profile.Id);
            Assert.That(result.Single().Editions.Select(edition => edition.ForeignEditionId), Is.EqualTo(new[] { "gr:202" }));
        }

        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        [TestCase(BookMediaType.Audiobook, true)]
        public void identifier_requirements_should_filter_each_edition_and_remove_empty_books(BookMediaType mediaType, bool requireAsin)
        {
            var profile = CreateProfile(1);
            profile.SkipMissingIsbn = true;
            profile.SkipMissingAsin = requireAsin;

            Book CreateBook(string title, string workId, params Edition[] editions)
            {
                return new Book
                {
                    Title = title,
                    GoodreadsWorkId = workId,
                    MediaType = mediaType,
                    Editions = editions.ToList()
                };
            }

            var asinOnlyPocket = CreateBook(
                "ASIN only",
                "gr:identifier-asin-only",
                new Edition { ForeignEditionId = "gr:edition-asin-only", Asin = "B000ASIN" },
                new Edition { ForeignEditionId = "gr:edition-unidentified" });
            var splitIdentifierPocket = CreateBook(
                "Identifiers split across editions",
                "gr:identifier-split",
                new Edition { ForeignEditionId = "gr:edition-isbn-only", Isbn10 = "0441172717" },
                new Edition { ForeignEditionId = "gr:edition-sibling-asin-only", Asin = "B000SIBLING" });
            var isbnOnlyPocket = CreateBook(
                "ISBN only",
                "gr:identifier-isbn-only",
                new Edition { ForeignEditionId = "gr:edition-isbn-only-single", Isbn13 = "9780441172719" });
            var unidentifiedPocket = CreateBook(
                "No identifier",
                "gr:identifier-empty",
                new Edition { ForeignEditionId = "gr:edition-empty" });
            var completeEditionPocket = CreateBook(
                "Both identifiers on one edition",
                "gr:identifier-complete",
                new Edition { ForeignEditionId = "gr:edition-complete", Isbn13 = "9780441172719", Asin = "B000COMPLETE" });

            var result = CreateService(profile).FilterBooks(
                new Author { Books = new List<Book> { asinOnlyPocket, splitIdentifierPocket, isbnOnlyPocket, unidentifiedPocket, completeEditionPocket } },
                profile.Id);

            Assert.That(
                result.Select(book => book.GoodreadsWorkId),
                Is.EquivalentTo(requireAsin
                    ? new[] { asinOnlyPocket.GoodreadsWorkId, splitIdentifierPocket.GoodreadsWorkId, completeEditionPocket.GoodreadsWorkId }
                    : new[] { asinOnlyPocket.GoodreadsWorkId, splitIdentifierPocket.GoodreadsWorkId, isbnOnlyPocket.GoodreadsWorkId, completeEditionPocket.GoodreadsWorkId }));
            Assert.That(
                asinOnlyPocket.Editions.Select(edition => edition.ForeignEditionId),
                Is.EqualTo(new[] { "gr:edition-asin-only" }),
                "an ASIN is sufficient for both controls");
            Assert.That(
                splitIdentifierPocket.Editions.Select(edition => edition.ForeignEditionId),
                Is.EqualTo(requireAsin
                    ? new[] { "gr:edition-sibling-asin-only" }
                    : new[] { "gr:edition-isbn-only", "gr:edition-sibling-asin-only" }),
                "each sibling edition must meet the enabled requirements independently");
            Assert.That(
                isbnOnlyPocket.Editions.Select(edition => edition.ForeignEditionId),
                Is.EqualTo(requireAsin ? Array.Empty<string>() : new[] { "gr:edition-isbn-only-single" }));
            Assert.That(unidentifiedPocket.Editions, Is.Empty);
            Assert.That(
                completeEditionPocket.Editions.Select(edition => edition.ForeignEditionId),
                Is.EqualTo(new[] { "gr:edition-complete" }));
        }

        [TestCase(BookMediaType.Audiobook, true)]
        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        [TestCase(BookMediaType.Ebook, false)]
        public void manual_work_should_keep_one_ordinary_representative_without_pinning_an_edition(BookMediaType mediaType, bool requestTime)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            profile.SkipMissingDate = true;
            var oppositeMedia = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            Book Remote(string workId, BookMediaType media) => new()
            {
                GoodreadsWorkId = workId, MediaType = media, Title = "Requested Work", AnyEditionOk = true,
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = workId + "-best-" + media, Language = "fra", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Ratings = new Ratings { Votes = 50 } },
                    new() { ForeignEditionId = workId + "-other-" + media, Language = "fra", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Ratings = new Ratings { Votes = 1 } }
                }
            };
            var requested = Remote("gr:1001", mediaType);
            var local = Remote("gr:1001", mediaType);
            local.Id = 10;
            local.AuthorId = 1;
            local.AddOptions.AddType = BookAddType.Manual;
            if (requestTime) requested.AddOptions.AddType = BookAddType.Manual;
            var author = new Author { Id = requestTime ? 0 : 1, AudiobookMetadataProfileId = 1, EbookMetadataProfileId = 1 };
            var filtered = RefreshAuthorService.NormalizeRemoteBooks(author,
                new[] { requested, Remote("gr:1002", mediaType), Remote("gr:1001", oppositeMedia) }, null,
                CreateService(profile, requestTime ? null : author, requestTime ? null : new List<Book> { local }, requestTime ? null : new List<Edition>()),
                null, LogManager.GetCurrentClassLogger(), null, null, null, true, "manual work regression");
            Assert.Multiple(() =>
            {
                Assert.That(filtered, Is.EqualTo(new[] { requested }));
                Assert.That(requested.Editions.Select(edition => edition.ForeignEditionId), Is.EqualTo(new[] { "gr:1001-best-" + mediaType }));
                Assert.That(requested.AnyEditionOk, Is.True);
                Assert.That(requested.Editions.All(edition => !edition.ManualAdd), Is.True);
            });
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void manual_work_should_use_profile_eligible_editions_when_they_become_available(BookMediaType mediaType)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var remote = new Book
            {
                GoodreadsWorkId = "gr:1001", MediaType = mediaType, AnyEditionOk = true,
                AddOptions = new AddBookOptions { AddType = BookAddType.Manual },
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "old-representative", Language = "fra", ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3, Monitored = true, Ratings = new Ratings { Votes = 100 } },
                    new() { ForeignEditionId = "eligible-edition", Language = "eng", ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3, Ratings = new Ratings { Votes = 1 } }
                }
            };
            var filtered = CreateService(profile).FilterBooks(new Author { Books = new List<Book> { remote } }, 1);
            Assert.That(filtered.Single().Editions.Select(edition => edition.ForeignEditionId), Is.EqualTo(new[] { "eligible-edition" }));
            Assert.That(new EditionSelector(LogManager.GetCurrentClassLogger()).SelectBestEdition(filtered.Single().Editions, mediaType).ForeignEditionId,
                Is.EqualTo("eligible-edition"));
            Assert.That(remote.AnyEditionOk, Is.True);
            Assert.That(remote.Editions.All(edition => !edition.ManualAdd), Is.True);
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void manual_work_without_any_supported_representative_should_still_be_removed(BookMediaType mediaType)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var author = new Author { AudiobookMetadataProfileId = 1, EbookMetadataProfileId = 1 };
            var remote = new Book
            {
                GoodreadsWorkId = "gr:1001", MediaType = mediaType, AnyEditionOk = true,
                AddOptions = new AddBookOptions { AddType = BookAddType.Manual },
                Editions = new List<Edition> { new() { ForeignEditionId = "unsupported-format", Language = "fra", ReadingFormatId = 4 } }
            };
            Assert.That(RefreshAuthorService.NormalizeRemoteBooks(author, new[] { remote }, null, CreateService(profile),
                null, LogManager.GetCurrentClassLogger(), null, null, null, true, "manual unsupported work regression"), Is.Empty);
        }

    }
}
