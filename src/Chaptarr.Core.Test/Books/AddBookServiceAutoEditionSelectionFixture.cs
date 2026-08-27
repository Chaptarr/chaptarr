using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentValidation;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AddBookServiceAutoEditionSelectionFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class AuthorLibraryProxy : DispatchProxy
        {
            public Author AddedAuthor { get; set; }
            public string ProviderId { get; private set; }
            public MonitoringConfig Config { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    ProviderId = (string)args[0];
                    Config = (MonitoringConfig)args[1];
                    return Task.FromResult(AddedAuthor);
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorLibraryService.{targetMethod?.Name}");
            }
        }

        private sealed class RecordingBookAddedService : IBookAddedService
        {
            public List<int> AuthorIds { get; } = new();

            public void SearchForRecentlyAdded(int authorId)
            {
                AuthorIds.Add(authorId);
            }
        }

        private class ImportListExclusionServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IImportListExclusionService.FindByForeignId))
                {
                    return targetMethod.ReturnType == typeof(List<ImportListExclusion>) ?
                        new List<ImportListExclusion>() :
                        null;
                }

                if (targetMethod?.Name == nameof(IImportListExclusionService.Delete))
                {
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IImportListExclusionService.{targetMethod?.Name}");
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;
            private readonly Func<string, string, Author> _findByProviderId;

            public StubAuthorService(Author author, Func<string, string, Author> findByProviderId = null)
            {
                _author = author;
                _findByProviderId = findByProviderId;
            }

            public Author GetAuthor(int authorId) => _author != null && _author.Id == authorId ? _author : null;
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => _findByProviderId != null ? _findByProviderId(provider, providerId) : throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => author;
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();

            public void PromoteMediaTypeMonitoringToSelected(int authorId, string mediaType)
            {
                if (_author == null || _author.Id != authorId)
                {
                    throw new InvalidOperationException($"Author {authorId} is not available to the test service");
                }

                if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
                    (_author.AudiobookMonitorExisting ?? 0) <= 0)
                {
                    _author.AudiobookMonitorExisting = 2;
                }
                else if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase) &&
                         (_author.EbookMonitorExisting ?? 0) <= 0)
                {
                    _author.EbookMonitorExisting = 2;
                }

                _author.Monitored = _author.IsMonitoredFromMediaSettings();
            }

            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubBookService : IBookService
        {
            public List<Book> AuthorBooks { get; set; } = new();
            public List<Book> AddOptionsBooks { get; private set; }
            public Book AddedBook { get; private set; }
            public Book UpdatedBook { get; private set; }

            public Book UpdateBook(Book book)
            {
                UpdatedBook = book;
                return book;
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => AuthorBooks;
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true)
            {
                if (newBook.Id <= 0)
                {
                    newBook.Id = 101;
                }

                AddedBook = newBook;
                if (!AuthorBooks.Contains(newBook))
                {
                    AuthorBooks.Add(newBook);
                }

                return newBook;
            }
            public Book FindBySlug(string titleSlug) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => null;
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByWorkProviderId(string provider, string providerId, BookMediaType mediaType)
            {
                var normalized = ProviderIdHelper.Canonicalize(providerId, provider);
                return AuthorBooks.Where(book => book.MediaType == mediaType &&
                                                  BookEditionIdentity.GetCanonicalWorkProviderIds(book)
                                                      .Concat(book.RemoteProviderIds ?? Enumerable.Empty<string>())
                                                      .Any(id => string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => throw new NotImplementedException();
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
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => AddOptionsBooks = books.ToList();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubEditionService : IEditionService
        {
            private readonly List<Edition> _editions;
            public Edition MonitoredEdition { get; private set; }

            public StubEditionService(IEnumerable<Edition> editions)
            {
                _editions = editions?.ToList() ?? new List<Edition>();
            }

            public List<Edition> GetEditionsByBook(int bookId) => _editions.Where(e => e.BookId == bookId).ToList();

            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false)
            {
                MonitoredEdition = edition;
                foreach (var item in _editions.Where(e => e.BookId == edition.BookId))
                {
                    item.Monitored = item.Id == edition.Id;
                }

                return _editions.Where(e => e.BookId == edition.BookId).ToList();
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
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
        }

        private sealed class StubMetadataProfileService : IMetadataProfileService
        {
            private readonly Dictionary<int, MetadataProfile> _profiles;

            public StubMetadataProfileService(params MetadataProfile[] profiles)
            {
                _profiles = (profiles ?? Array.Empty<MetadataProfile>()).ToDictionary(p => p.Id);
            }

            public bool Exists(int id) => _profiles.ContainsKey(id);
            public MetadataProfile Get(int id) => _profiles.TryGetValue(id, out var profile) ? profile : null;

            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<MetadataProfile> All() => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public List<Book> FilterBooks(Author input, int profileId) => throw new NotImplementedException();
        }

        private static AddBookService BuildService(
            IAuthorService authorService,
            IBookService bookService,
            IEditionService editionService,
            IMetadataProfileService metadataProfileService,
            IAuthorLibraryService authorLibraryService = null,
            IBookAddedService bookAddedService = null,
            IImportListExclusionService importListExclusionService = null,
            IProvideBookInfo bookInfo = null)
        {
            return new AddBookService(
                authorService,
                authorLibraryService ?? DispatchProxy.Create<IAuthorLibraryService, ThrowingProxy<IAuthorLibraryService>>(),
                bookService,
                bookAddedService ?? DispatchProxy.Create<IBookAddedService, ThrowingProxy<IBookAddedService>>(),
                bookInfo ?? DispatchProxy.Create<IProvideBookInfo, ThrowingProxy<IProvideBookInfo>>(),
                importListExclusionService ?? DispatchProxy.Create<IImportListExclusionService, ThrowingProxy<IImportListExclusionService>>(),
                DispatchProxy.Create<ISeriesBookLinkService, ThrowingProxy<ISeriesBookLinkService>>(),
                DispatchProxy.Create<ISeriesService, ThrowingProxy<ISeriesService>>(),
                DispatchProxy.Create<IProvideAuthorInfo, ThrowingProxy<IProvideAuthorInfo>>(),
                DispatchProxy.Create<IBuildFileNames, ThrowingProxy<IBuildFileNames>>(),
                DispatchProxy.Create<IMonitoringService, ThrowingProxy<IMonitoringService>>(),
                editionService,
                new EditionSelector(LogManager.GetCurrentClassLogger()),
                new EditionMetadataProfileFilter(new TestTermMatcherService()),
                metadataProfileService,
                LogManager.GetCurrentClassLogger());
        }

        private static void InvokeEnsureAutoSelectedEdition(AddBookService service, Book book)
        {
            var method = typeof(AddBookService).GetMethod("EnsureAutoSelectedEdition", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.EnsureAutoSelectedEdition via reflection");
            }

            method.Invoke(service, new object[] { book });
        }

        private static Edition InvokeSelectPreferredEditionForAdd(AddBookService service, Book book, IEnumerable<Edition> editions, string requestedEditionId)
        {
            var requestedIds = requestedEditionId == null
                ? Array.Empty<string>()
                : new[] { requestedEditionId };

            return InvokeSelectPreferredEditionForAdd(service, book, editions, requestedIds);
        }

        private static Edition InvokeSelectPreferredEditionForAdd(AddBookService service, Book book, IEnumerable<Edition> editions, IReadOnlyCollection<string> requestedEditionProviderIds)
        {
            var method = typeof(AddBookService).GetMethod("SelectPreferredEditionForAdd", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.SelectPreferredEditionForAdd via reflection");
            }

            return (Edition)method.Invoke(service, new object[] { book, editions, requestedEditionProviderIds ?? Array.Empty<string>() });
        }

        private static void InvokeApplyEditionRetentionForAdd(AddBookService service, Book book, string requestedEditionId)
        {
            var method = typeof(AddBookService).GetMethod("ApplyEditionRetentionForAdd", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.ApplyEditionRetentionForAdd via reflection");
            }

            var requestedIds = requestedEditionId == null
                ? Array.Empty<string>()
                : new[] { requestedEditionId };

            method.Invoke(service, new object[] { book, requestedIds });
        }

        private static IReadOnlyCollection<string> InvokeGetRequestedEditionProviderIdsFromPayload(AddBookService service, IEnumerable<Edition> editions)
        {
            var method = typeof(AddBookService).GetMethod("GetRequestedEditionProviderIdsFromPayload", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find AddBookService.GetRequestedEditionProviderIdsFromPayload via reflection");
            }

            return (IReadOnlyCollection<string>)method.Invoke(service, new object[] { editions });
        }

        private static void AssertSingleMonitoredEdition(IEnumerable<Edition> editions, string expectedForeignEditionId)
        {
            var monitored = editions.Where(e => e.Monitored).ToList();
            Assert.That(monitored.Select(e => e.ForeignEditionId), Is.EqualTo(new[] { expectedForeignEditionId }));
        }

        [Test]
        public void should_select_allowed_audio_over_escape_hatch_and_update_book_foreign_edition_id()
        {
            var profile = new MetadataProfile { Id = 7, AllowedLanguages = "eng" };
            var author = new Author { Id = 3, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 44,
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 3, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
            };

            var bookService = new StubBookService();
            var editionService = new StubEditionService(editions);
            var service = BuildService(
                new StubAuthorService(author),
                bookService,
                editionService,
                new StubMetadataProfileService(profile));

            InvokeEnsureAutoSelectedEdition(service, book);

            Assert.That(editionService.MonitoredEdition?.ForeignEditionId, Is.EqualTo("eng-audio"));
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
            Assert.That(bookService.UpdatedBook, Is.SameAs(book));
        }

        [Test]
        public void should_use_allowed_ebook_representative_when_no_audio_candidate_survives_filters()
        {
            var profile = new MetadataProfile { Id = 8, AllowedLanguages = "eng" };
            var author = new Author { Id = 4, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 45,
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-print", Title = "Dune Print", Language = "eng", ReadingFormatId = 1 },
                new Edition { Id = 3, BookId = book.Id, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
            };

            var editionService = new StubEditionService(editions);
            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                editionService,
                new StubMetadataProfileService(profile));

            InvokeEnsureAutoSelectedEdition(service, book);

            Assert.That(editionService.MonitoredEdition?.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_fall_back_to_print_when_skip_missing_isbn_removes_ebook_candidates()
        {
            var profile = new MetadataProfile { Id = 9, AllowedLanguages = "eng", SkipMissingIsbn = true };
            var author = new Author { Id = 5, EbookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 46,
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-ebook-missing", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, BookId = book.Id, ForeignEditionId = "eng-print", Title = "Dune Print", Language = "eng", ReadingFormatId = 1, Isbn13 = "9780441013593" }
            };

            var editionService = new StubEditionService(editions);
            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                editionService,
                new StubMetadataProfileService(profile));

            InvokeEnsureAutoSelectedEdition(service, book);

            Assert.That(editionService.MonitoredEdition?.ForeignEditionId, Is.EqualTo("eng-print"));
        }

        [Test]
        public void should_throw_validation_when_no_candidate_survives_filters()
        {
            var profile = new MetadataProfile { Id = 10, AllowedLanguages = "eng", SkipMissingIsbn = true };
            var author = new Author { Id = 6, EbookMetadataProfileId = profile.Id };
            var book = new Book
            {
                Id = 47,
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, BookId = book.Id, ForeignEditionId = "eng-ebook-missing", Title = "Dune", Language = "eng", ReadingFormatId = 3 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(editions),
                new StubMetadataProfileService(profile));

            var ex = Assert.Throws<TargetInvocationException>(() => InvokeEnsureAutoSelectedEdition(service, book));
            Assert.That(ex?.InnerException, Is.TypeOf<ValidationException>());
        }

        [Test]
        public void should_use_selector_instead_of_first_remote_edition_for_ids_only_hydration()
        {
            var profile = new MetadataProfile { Id = 11, AllowedLanguages = "eng" };
            var author = new Author { Id = 7, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 },
                new Edition { ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: null);

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-audio"));
        }

        [Test]
        public void should_select_highest_rated_allowed_audiobook_when_multiple_audio_editions_exist()
        {
            var profile = new MetadataProfile { Id = 13, AllowedLanguages = "eng" };
            var author = new Author { Id = 9, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition
                {
                    Id = 1,
                    ForeignEditionId = "eng-audio-rich",
                    Title = "Dune Audio Rich",
                    Language = "eng",
                    ReadingFormatId = 2,
                    Overview = "Detailed description",
                    Narrator = "Narrator One",
                    DurationSeconds = 36000,
                    Ratings = new Ratings { Votes = 25, Value = 4.9m }
                },
                new Edition
                {
                    Id = 2,
                    ForeignEditionId = "eng-audio-popular",
                    Title = "Dune Audio Popular",
                    Language = "eng",
                    ReadingFormatId = 2,
                    Ratings = new Ratings { Votes = 5000, Value = 4.2m }
                },
                new Edition
                {
                    Id = 3,
                    ForeignEditionId = "eng-ebook",
                    Title = "Dune Ebook",
                    Language = "eng",
                    ReadingFormatId = 3,
                    Ratings = new Ratings { Votes = 50000, Value = 4.8m }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: null);

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-audio-popular"));
        }

        [Test]
        public void should_select_highest_rated_allowed_ebook_when_multiple_ebook_editions_exist()
        {
            var profile = new MetadataProfile { Id = 14, AllowedLanguages = "eng" };
            var author = new Author { Id = 10, EbookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Ebook
            };

            var editions = new List<Edition>
            {
                new Edition
                {
                    Id = 1,
                    ForeignEditionId = "eng-ebook-rich",
                    Title = "Dune Ebook Rich",
                    Language = "eng",
                    ReadingFormatId = 3,
                    Overview = "Detailed description",
                    Ratings = new Ratings { Votes = 25, Value = 4.9m }
                },
                new Edition
                {
                    Id = 2,
                    ForeignEditionId = "eng-ebook-popular",
                    Title = "Dune Ebook Popular",
                    Language = "eng",
                    ReadingFormatId = 3,
                    Ratings = new Ratings { Votes = 5000, Value = 4.2m }
                },
                new Edition
                {
                    Id = 3,
                    ForeignEditionId = "eng-print",
                    Title = "Dune Hardcover",
                    Language = "eng",
                    ReadingFormatId = 1,
                    Ratings = new Ratings { Votes = 50000, Value = 4.8m }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: null);

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-ebook-popular"));
        }

        [Test]
        public void should_fall_back_to_best_edition_when_requested_edition_is_missing()
        {
            var profile = new MetadataProfile { Id = 12, AllowedLanguages = "eng" };
            var author = new Author { Id = 8, EbookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Ebook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "fra-ebook", Title = "Dune French", Language = "fra", ReadingFormatId = 3 },
                new Edition { Id = 2, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "missing-edition");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_select_requested_native_edition_by_goodreads_alias_when_foreign_edition_shape_changes()
        {
            var profile = new MetadataProfile { Id = 27, AllowedLanguages = "eng" };
            var author = new Author { Id = 27, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition
                {
                    Id = 1,
                    ForeignEditionId = "hc:edition:22222-audiobook",
                    GoodreadsEditionId = 61304420,
                    Title = "Dune Requested Audio",
                    Language = "eng",
                    ReadingFormatId = 2,
                    Ratings = new Ratings { Votes = 1, Value = 3.0m }
                },
                new Edition
                {
                    Id = 2,
                    ForeignEditionId = "hc:edition:33333-audiobook",
                    GoodreadsEditionId = 99999999,
                    Title = "Dune Popular Audio",
                    Language = "eng",
                    ReadingFormatId = 2,
                    Ratings = new Ratings { Votes = 5000, Value = 4.8m }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "gr:61304420");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("hc:edition:22222-audiobook"));
        }

        [Test]
        public void should_select_requested_edition_from_payload_alias_when_foreign_id_is_missing()
        {
            var profile = new MetadataProfile { Id = 28, AllowedLanguages = "eng" };
            var author = new Author { Id = 28, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var payloadEditions = new List<Edition>
            {
                new Edition { HardcoverEditionId = "hc:edition:44444", Monitored = true }
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "hc:edition:44444-audiobook", HardcoverEditionId = "44444", Title = "Dune Requested Audio", Language = "eng", ReadingFormatId = 2 },
                new Edition { Id = 2, ForeignEditionId = "hc:edition:55555-audiobook", HardcoverEditionId = "55555", Title = "Dune Other Audio", Language = "eng", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var requestedIds = InvokeGetRequestedEditionProviderIdsFromPayload(service, payloadEditions);
            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedIds);

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("hc:edition:44444-audiobook"));
        }

        [Test]
        public void should_match_requested_edition_against_plural_asins()
        {
            var profile = new MetadataProfile { Id = 29, AllowedLanguages = "eng" };
            var author = new Author { Id = 29, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "eng-audio-requested", Title = "Dune Requested Audio", Language = "eng", ReadingFormatId = 2, Asins = new List<string> { "B012345678" } },
                new Edition { Id = 2, ForeignEditionId = "eng-audio-other", Title = "Dune Other Audio", Language = "eng", ReadingFormatId = 2, Asin = "B087654321" }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "az:B012345678");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-audio-requested"));
        }

        [Test]
        public void should_not_match_goodreads_provider_id_to_hardcover_numeric_collision()
        {
            var edition = new Edition
            {
                ForeignEditionId = "hc:edition:12345-audiobook",
                HardcoverEditionId = "12345",
                Title = "Dune Audio",
                Language = "eng",
                ReadingFormatId = 2
            };

            Assert.That(BookEditionIdentity.EditionMatchesProviderId(edition, "gr:12345"), Is.False);
        }

        [Test]
        public void should_monitor_only_one_saved_edition_when_requested_alias_matches_multiple_retained_editions()
        {
            var profile = new MetadataProfile { Id = 31, AllowedLanguages = "eng" };
            var author = new Author { Id = 31, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 101, ForeignEditionId = "eng-audio-one", Title = "Dune Audio One", Language = "eng", ReadingFormatId = 2, Asin = "B012345678" },
                    new Edition { Id = 102, ForeignEditionId = "eng-audio-two", Title = "Dune Audio Two", Language = "eng", ReadingFormatId = 2, AudibleASIN = "B012345678" }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: "az:B012345678");

            AssertSingleMonitoredEdition(book.Editions, "eng-audio-one");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio-one"));
        }

        [Test]
        public void should_monitor_only_one_unsaved_edition_when_requested_alias_matches_multiple_retained_editions()
        {
            var profile = new MetadataProfile { Id = 32, AllowedLanguages = "eng" };
            var author = new Author { Id = 32, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-audio-one", Title = "Dune Audio One", Language = "eng", ReadingFormatId = 2, Asin = "B012345678" },
                    new Edition { ForeignEditionId = "eng-audio-two", Title = "Dune Audio Two", Language = "eng", ReadingFormatId = 2, AudibleASIN = "B012345678" }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: "az:B012345678");

            AssertSingleMonitoredEdition(book.Editions, "eng-audio-one");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio-one"));
        }

        [Test]
        public void should_not_let_requested_non_native_alias_steal_monitoring_when_native_survives()
        {
            var profile = new MetadataProfile { Id = 30, AllowedLanguages = "eng" };
            var author = new Author { Id = 30, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "hc:edition:44444-ebook", GoodreadsEditionId = 61304420, Title = "Dune Ebook", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, ForeignEditionId = "hc:edition:55555-audiobook", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "gr:61304420");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("hc:edition:55555-audiobook"));
        }

        [Test]
        public void should_accept_requested_representative_edition_when_no_native_edition_survives_filters()
        {
            var profile = new MetadataProfile { Id = 15, AllowedLanguages = "eng" };
            var author = new Author { Id = 15, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, ForeignEditionId = "eng-print", Title = "Dune Print", Language = "eng", ReadingFormatId = 1 },
                new Edition { Id = 3, ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "eng-ebook");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_prefer_native_edition_when_requested_non_native_is_only_a_hint()
        {
            var profile = new MetadataProfile { Id = 18, AllowedLanguages = "eng" };
            var author = new Author { Id = 18, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                new Edition { Id = 2, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "eng-ebook");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-audio"));
        }

        [Test]
        public void should_honor_requested_manual_representative_even_when_native_survives()
        {
            var profile = new MetadataProfile { Id = 20, AllowedLanguages = "eng" };
            var author = new Author { Id = 20, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };

            var editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3, ManualAdd = true },
                new Edition { Id = 2, ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            var selected = InvokeSelectPreferredEditionForAdd(service, book, editions, requestedEditionId: "eng-ebook");

            Assert.That(selected?.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_prune_editions_before_direct_book_insert()
        {
            var profile = new MetadataProfile { Id = 16, AllowedLanguages = "eng" };
            var author = new Author { Id = 16, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                    new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: null);

            Assert.That(book.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "eng-audio", "eng-ebook" }));
            AssertSingleMonitoredEdition(book.Editions, "eng-audio");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
        }

        [Test]
        public void should_drop_non_allowed_languages_before_audiobook_companion_rule_fires()
        {
            var profile = new MetadataProfile { Id = 26, AllowedLanguages = "eng" };
            var author = new Author { Id = 26, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 },
                    new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "spa-audio", Title = "Dune Spanish Audio", Language = "spa", ReadingFormatId = 2 },
                    new Edition { ForeignEditionId = "spa-ebook", Title = "Dune Spanish", Language = "spa", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "spa-print", Title = "Dune Spanish Print", Language = "spa", ReadingFormatId = 1 }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: null);

            Assert.That(book.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "eng-audio", "eng-ebook" }));
            Assert.That(book.Editions.All(e => e.Language == "eng"), Is.True);
        }

        [Test]
        public void should_protect_requested_representative_when_pruning_before_direct_book_insert_if_no_native_survives()
        {
            var profile = new MetadataProfile { Id = 17, AllowedLanguages = "eng" };
            var author = new Author { Id = 17, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "eng-print", Title = "Dune Print", Language = "eng", ReadingFormatId = 1 },
                    new Edition { ForeignEditionId = "fra-audio", Title = "Dune French Audio", Language = "fra", ReadingFormatId = 2 }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: "eng-ebook");

            Assert.That(book.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "eng-ebook" }));
            AssertSingleMonitoredEdition(book.Editions, "eng-ebook");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-ebook"));
        }

        [Test]
        public void should_not_protect_requested_representative_before_direct_book_insert_when_native_survives()
        {
            var profile = new MetadataProfile { Id = 19, AllowedLanguages = "eng" };
            var author = new Author { Id = 19, AudiobookMetadataProfileId = profile.Id };
            var book = new Book
            {
                AuthorId = author.Id,
                Author = author,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { ForeignEditionId = "eng-ebook", Title = "Dune", Language = "eng", ReadingFormatId = 3 },
                    new Edition { ForeignEditionId = "eng-audio", Title = "Dune Audio", Language = "eng", ReadingFormatId = 2 }
                }
            };

            var service = BuildService(
                new StubAuthorService(author),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(profile));

            InvokeApplyEditionRetentionForAdd(service, book, requestedEditionId: "eng-ebook");

            Assert.That(book.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "eng-audio", "eng-ebook" }));
            AssertSingleMonitoredEdition(book.Editions, "eng-audio");
            Assert.That(book.ForeignEditionId, Is.EqualTo("eng-audio"));
        }

        [Test]
        public void should_use_monitored_payload_edition_for_requested_edition_id()
        {
            var service = BuildService(
                new StubAuthorService(null),
                new StubBookService(),
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService());

            var payloadEditions = new List<Edition>
            {
                new Edition { Id = 20, ForeignEditionId = "second" },
                new Edition { Id = 10, ForeignEditionId = "first", Monitored = true }
            };

            var requestedEditionIds = InvokeGetRequestedEditionProviderIdsFromPayload(service, payloadEditions);

            Assert.That(requestedEditionIds, Does.Contain("first"));
        }

        [TestCase(BookMediaType.Audiobook, 2)]
        [TestCase(BookMediaType.Ebook, 3)]
        public async Task existing_author_search_request_should_enable_the_book_and_its_media_side(
            BookMediaType mediaType,
            int readingFormatId)
        {
            var existingAuthor = new Author
            {
                Id = 42,
                Name = "Existing Author",
                HardcoverAuthorId = "hc:777",
                AudiobookMetadataProfileId = 1,
                EbookMetadataProfileId = 2,
                AudiobookMonitorExisting = 0,
                EbookMonitorExisting = 0
            };
            var requestedAuthor = new Author
            {
                Name = existingAuthor.Name,
                HardcoverAuthorId = existingAuthor.HardcoverAuthorId,
                AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.SpecificBook }
            };
            if (mediaType == BookMediaType.Audiobook)
            {
                requestedAuthor.AudiobookMonitorExisting = 2;
            }
            else
            {
                requestedAuthor.EbookMonitorExisting = 2;
            }

            var requestedEdition = new Edition
            {
                Id = 301,
                Title = "Requested Edition",
                ReadingFormatId = readingFormatId,
                Language = "eng",
                Monitored = true
            };
            var requestedBook = new Book
            {
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = mediaType,
                Author = requestedAuthor,
                AddOptions = new AddBookOptions
                {
                    SearchForNewBook = true
                },
                Editions = new List<Edition> { requestedEdition }
            };

            var authoritativeBook = new Book
            {
                Id = 101,
                AuthorId = existingAuthor.Id,
                Author = existingAuthor,
                Title = requestedBook.Title,
                HardcoverBookId = requestedBook.HardcoverBookId,
                MediaType = mediaType
            };
            var bookService = new StubBookService { AuthorBooks = new List<Book> { authoritativeBook } };
            var authorService = new StubAuthorService(existingAuthor, (_, _) => existingAuthor);
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = existingAuthor;
            var bookAddedService = new RecordingBookAddedService();
            var storedEdition = new Edition
            {
                Id = requestedEdition.Id,
                BookId = 101,
                Title = requestedEdition.Title,
                ReadingFormatId = readingFormatId,
                Language = requestedEdition.Language,
                Monitored = true
            };
            var service = BuildService(
                authorService,
                bookService,
                new StubEditionService(new[] { storedEdition }),
                new StubMetadataProfileService(
                    new MetadataProfile { Id = 1, AllowedLanguages = "eng" },
                    new MetadataProfile { Id = 2, AllowedLanguages = "eng" }),
                authorLibraryService,
                bookAddedService,
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>());

            var result = await service.AddBook(requestedBook);

            Assert.That(result, Is.SameAs(authoritativeBook));
            Assert.That(bookService.AddedBook, Is.Null, "A direct book add must never create a row outside the authoritative author catalog.");
            Assert.That(result.AddOptions.SearchForNewBook, Is.True);
            Assert.That(result.IsMonitoredWithAuthor(), Is.True);
            Assert.That(
                mediaType == BookMediaType.Audiobook
                    ? existingAuthor.AudiobookMonitorExisting
                    : existingAuthor.EbookMonitorExisting,
                Is.EqualTo(2));
            Assert.That(
                mediaType == BookMediaType.Audiobook
                    ? existingAuthor.EbookMonitorExisting
                    : existingAuthor.AudiobookMonitorExisting,
                Is.EqualTo(0));
            var config = ((AuthorLibraryProxy)authorLibraryService).Config;
            Assert.That(mediaType == BookMediaType.Audiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor,
                Is.EqualTo(new[] { "hc:1001" }));
            Assert.That(mediaType == BookMediaType.Audiobook ? config.AudiobookBooksToSearch : config.EbookBooksToSearch,
                Is.EqualTo(new[] { "hc:1001" }));
            Assert.That(bookAddedService.AuthorIds, Is.EqualTo(new[] { existingAuthor.Id }));
        }

        [Test]
        public async Task newly_imported_author_should_keep_search_for_new_book_intent()
        {
            var importedAuthor = new Author
            {
                Id = 42,
                Name = "New Author",
                HardcoverAuthorId = "hc:777"
            };
            var importedBook = new Book
            {
                Id = 101,
                AuthorId = importedAuthor.Id,
                Author = importedAuthor,
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = BookMediaType.Audiobook
            };
            var requestedBook = new Book
            {
                Title = importedBook.Title,
                HardcoverBookId = importedBook.HardcoverBookId,
                MediaType = BookMediaType.Audiobook,
                Author = new Author
                {
                    Name = importedAuthor.Name,
                    HardcoverAuthorId = importedAuthor.HardcoverAuthorId,
                    Monitored = true,
                    AddOptions = new AddAuthorOptions()
                },
                AddOptions = new AddBookOptions
                {
                    AddType = BookAddType.Manual,
                    SearchForNewBook = true
                },
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Title = "Requested Book",
                        ReadingFormatId = 2,
                        Language = "eng"
                    }
                }
            };

            var bookService = new StubBookService
            {
                AuthorBooks = new List<Book> { importedBook }
            };
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = importedAuthor;
            var bookAddedService = new RecordingBookAddedService();
            var importListExclusionService = DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>();
            var service = BuildService(
                new StubAuthorService(importedAuthor, (_, _) => null),
                bookService,
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(),
                authorLibraryService,
                bookAddedService,
                importListExclusionService);

            var result = await service.AddBook(requestedBook);

            Assert.That(result, Is.SameAs(importedBook));
            Assert.That(((AuthorLibraryProxy)authorLibraryService).Config.AudiobookBooksToSearch, Does.Contain("hc:1001"));
            Assert.That(((AuthorLibraryProxy)authorLibraryService).Config.EbookBooksToSearch, Is.Null);
            Assert.That(bookService.AddOptionsBooks, Is.EqualTo(new[] { importedBook }));
            Assert.That(importedBook.AddOptions.SearchForNewBook, Is.True);
            Assert.That(importedBook.IsMonitoredWithAuthor(), Is.True);
            Assert.That(bookAddedService.AuthorIds, Is.EqualTo(new[] { importedAuthor.Id }));
        }

        [Test]
        public void unavailable_requested_book_should_queue_exact_work_monitoring_and_search_without_direct_insert()
        {
            var pendingAuthor = new Author { Id = -77, Name = "Pending Import" };
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)authorLibraryService).AddedAuthor = pendingAuthor;
            var bookService = new StubBookService();
            var service = BuildService(
                new StubAuthorService(null, (_, _) => null),
                bookService,
                new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(),
                authorLibraryService,
                new RecordingBookAddedService(),
                DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>());
            var request = new Book
            {
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = BookMediaType.Ebook,
                Author = new Author
                {
                    Name = "Pending Author",
                    HardcoverAuthorId = " ",
                    GoodreadsAuthorId = "777",
                    EbookMonitorExisting = 2,
                    EbookQualityProfileId = 1,
                    EbookMetadataProfileId = 2,
                    EbookRootFolderPath = "/ebooks",
                    AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.SpecificBook }
                },
                AddOptions = new AddBookOptions { SearchForNewBook = true }
            };

            var exception = Assert.ThrowsAsync<PendingBookRequestException>(() => service.AddBook(request));

            Assert.That(exception.PendingId, Is.EqualTo(77));
            Assert.That(exception.Message, Is.EqualTo(PendingBookRequestException.UserMessage));
            Assert.That(bookService.AddedBook, Is.Null);
            var authorLibraryProxy = (AuthorLibraryProxy)authorLibraryService;
            Assert.That(authorLibraryProxy.ProviderId, Is.EqualTo("gr:777"));
            var config = authorLibraryProxy.Config;
            Assert.That(config.EbookBooksToMonitor, Is.EqualTo(new[] { "hc:1001" }));
            Assert.That(config.EbookBooksToSearch, Is.EqualTo(new[] { "hc:1001" }));
            Assert.That(config.AudiobookBooksToMonitor, Is.Null);
            Assert.That(config.QueueIfUnavailable, Is.True);
        }
    }
}
