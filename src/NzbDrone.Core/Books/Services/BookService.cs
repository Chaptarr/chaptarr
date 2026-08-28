using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MediaFiles;
// using NzbDrone.Core.MediaFiles.BookImport.Identification; // Disabled - old identification system
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public interface IBookService
    {
        Book GetBook(int bookId);
        List<Book> GetBooks(IEnumerable<int> bookIds);
        List<Book> GetExistingBooks(IEnumerable<int> bookIds) => GetBooks(bookIds);
        List<Book> GetBooksByAuthor(int authorId);
        List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds);
        List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds);
        List<Book> GetBooksByAuthorId(int authorId);
        List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds);
        List<Book> GetBooksByFileIds(IEnumerable<int> fileIds);
        Book AddBook(Book newBook, bool doRefresh = true);
        Book FindBySlug(string titleSlug);
        Book FindByTitle(int authorId, string title);
        Book FindByTitleInexact(int authorId, string title);
            Book FindByGoodreadsId(string goodreadsId);
            Book FindByProviderId(string provider, string providerId);
            Book FindByProviderId(string provider, string providerId, BookMediaType mediaType);
            List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType);
            List<Book> FindAllByWorkProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            Book FindByISBN(string isbn);
            Book FindByASIN(string asin);
        List<Book> GetCandidates(int authorId, string title);
        void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false);
        List<Book> GetAllBooks();
        Book UpdateBook(Book book);
        void SetBookMonitored(int bookId, bool monitored);
        void SetMonitored(IEnumerable<int> ids, bool monitored);
        void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored);
        void UpdateLastSearchTime(List<Book> books);
        List<BookSearchTarget> GetMissingBookSearchTargets(BookMediaType? mediaType, int? authorId) => throw new NotImplementedException();
        PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec);
        List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored);
        List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored);
        void InsertMany(List<Book> books);
        void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction);
        void UpdateMany(List<Book> books);
        void ReassignAuthor(Book book, Author author) { throw new NotImplementedException(); }
        void ReassignAuthor(List<Book> books, Author author) { throw new NotImplementedException(); }
        void ReassignAuthor(Book book, int authorId) { throw new NotImplementedException(); }
        void ReassignAuthor(List<Book> books, int authorId) { throw new NotImplementedException(); }
        void RefreshProviderAliases(Book book) { }
        void DeleteMany(List<Book> books);
        void SetAddOptions(IEnumerable<Book> books);
        List<Book> GetAuthorBooksWithFiles(Author author);
            List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null);
            List<Book> GetBooksByBaseId(string baseBookId);
            Book AddWantedEdition(int bookId, int editionId);
            bool ShouldSearchForMediaType(Book book, string mediaType);
            BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null);
            BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null) => GetBookBuckets(sortKey, sortDirection, includeUnmonitored, mediaType, downloaded);
            PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null);
            PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null) => GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded);
            List<int> GetBookIds(bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null, bool? monitored = null, bool? missing = null, bool? wanted = null) => throw new NotImplementedException();
        }

    public class BookService : IBookService,
                                IHandle<AuthorDeletedEvent>,
                                IExecute<BulkSyncFormatMonitoringCommand>
    {
        private readonly IBookRepository _bookRepository;
        private readonly IEditionService _editionService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IAuthorService _authorService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly ISeriesBookLinkRepository _seriesBookLinkRepository;
        private readonly IMultiCopySeriesService _multiCopySeriesService;
        private readonly IProviderAliasService _providerAliasService;
        private readonly IEditionSelector _editionSelector;
        private readonly Logger _logger;

        private sealed class MonitoredStateSnapshot
        {
            public bool AudiobookMonitored { get; set; }
            public bool EbookMonitored { get; set; }
        }

        private sealed class BookMonitoringSyncUpdate
        {
            public Book Book { get; set; }
            public Book Stored { get; set; }
        }

        public BookService(IBookRepository bookRepository,
                           IEditionService editionService,
                           IEventAggregator eventAggregator,
                           IAuthorService authorService,
                           IMediaFileService mediaFileService,
                           IRootFolderService rootFolderService,
                           ISeriesBookLinkRepository seriesBookLinkRepository,
                           IMultiCopySeriesService multiCopySeriesService,
                           Logger logger,
                           IEditionSelector editionSelector = null,
                           IProviderAliasService providerAliasService = null)
        {
            _bookRepository = bookRepository;
            _editionService = editionService;
            _eventAggregator = eventAggregator;
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _seriesBookLinkRepository = seriesBookLinkRepository;
            _multiCopySeriesService = multiCopySeriesService;
            _logger = logger;
            _editionSelector = editionSelector ?? new EditionSelector(logger);
            _providerAliasService = providerAliasService;
        }

        private static bool IsSameProviderBackedBook(Book a, Book b)
        {
            return a != null && b != null && WorkIdMatcher.WorkIdMatches(a, b);
        }

        private void EnsureBookDbFields(Book book)
        {
            if (book == null)
            {
                return;
            }

            static string CanonicalizeOrNull(string providerId, string expectedPrefix)
            {
                if (string.IsNullOrWhiteSpace(providerId) ||
                    ProviderIdHelper.ContainsProviderIdArtifact(providerId))
                {
                    return null;
                }

                try
                {
                    return ProviderIdHelper.Canonicalize(providerId.Trim(), expectedPrefix);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }

            static string CanonicalizeBaseBookId(Book book)
            {
                if (!string.IsNullOrWhiteSpace(book.BaseBookId) && book.BaseBookId.Contains(":"))
                {
                    return CanonicalizeOrNull(book.BaseBookId, expectedPrefix: null);
                }

                if (!string.IsNullOrWhiteSpace(book.HardcoverBookId))
                {
                    return book.HardcoverBookId;
                }

                if (!string.IsNullOrWhiteSpace(book.GoodreadsWorkId))
                {
                    return book.GoodreadsWorkId;
                }

                if (!string.IsNullOrWhiteSpace(book.OpenLibraryWorkId))
                {
                    return book.OpenLibraryWorkId;
                }

                return null;
            }

            book.HardcoverBookId = CanonicalizeOrNull(book.HardcoverBookId, "hc");
            book.GoodreadsWorkId = CanonicalizeOrNull(book.GoodreadsWorkId, "gr");
            book.OpenLibraryWorkId = CanonicalizeOrNull(book.OpenLibraryWorkId, "ol");
            book.BaseBookId = CanonicalizeBaseBookId(book);
        }

        private void RefreshBookProviderAliases(IEnumerable<Book> books)
        {
            if (_providerAliasService == null || books == null)
            {
                return;
            }

            foreach (var book in books)
            {
                RefreshBookProviderAliases(book);
            }
        }

        private void RefreshBookProviderAliases(Book book)
        {
            if (_providerAliasService == null || book == null || book.Id <= 0)
            {
                return;
            }

            var workIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var editionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in BookEditionIdentity.GetCanonicalWorkProviderIds(book))
            {
                AddProviderId(workIds, id);
            }

            foreach (var id in BookEditionIdentity.GetCanonicalEditionProviderIds(book, _logger, "BookService.RefreshBookProviderAliases"))
            {
                AddProviderId(editionIds, id);
            }

            AddHardcoverEditionAliases(editionIds, book.Editions);
            AddRemoteProviderAliases(workIds, editionIds, book.RemoteProviderIds);

            _providerAliasService.ReplaceAliases("Book", book.Id, "work", workIds);
            _providerAliasService.ReplaceAliases("Book", book.Id, "edition", editionIds);
        }

        public void RefreshProviderAliases(Book book)
        {
            RefreshBookProviderAliases(book);
        }

        private static void AddProviderId(ISet<string> target, string providerId)
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                target.Add(providerId.Trim());
            }
        }

        private static void AddRemoteProviderAliases(ISet<string> workIds, ISet<string> editionIds, IEnumerable<string> providerIds)
        {
            if (providerIds == null)
            {
                return;
            }

            foreach (var id in providerIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var trimmed = id.Trim();
                if (IsEditionScopedAlias(trimmed))
                {
                    AddProviderId(editionIds, trimmed);
                }
                else
                {
                    AddProviderId(workIds, trimmed);
                }
            }
        }

        private static bool IsEditionScopedAlias(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            var trimmed = providerId.Trim();
            return trimmed.StartsWith("az:", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("edition:", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddHardcoverEditionAliases(ISet<string> editionIds, IEnumerable<Edition> editions)
        {
            if (editions == null)
            {
                return;
            }

            foreach (var edition in editions)
            {
                var raw = edition?.HardcoverEditionId;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                raw = raw.Trim();
                if (raw.StartsWith("hc:", StringComparison.OrdinalIgnoreCase))
                {
                    AddProviderId(editionIds, raw);
                }
                else if (raw.StartsWith("edition:", StringComparison.OrdinalIgnoreCase))
                {
                    AddProviderId(editionIds, $"hc:{raw}");
                }
                else
                {
                    AddProviderId(editionIds, $"hc:edition:{raw}");
                }
            }
        }

        public Book AddBook(Book newBook, bool doRefresh = true)
        {
            if (newBook.AuthorId == 0)
            {
                throw new InvalidOperationException("Cannot insert book with AuthorId = 0");
            }

            // Ensure TitleSlug is never NULL (consistent with AuthorImportService.cs:725)
            if (string.IsNullOrEmpty(newBook.TitleSlug))
            {
                newBook.TitleSlug = newBook.Title?.ToLowerInvariant().Replace(" ", "-") ?? $"book-{DateTime.UtcNow.Ticks}";
                _logger.Info("Generated TitleSlug for book '{0}': {1}", newBook.Title, newBook.TitleSlug);
            }

            // LocalBookId generation removed - using database IDs directly
            EnsureBookDbFields(newBook);

            var editions = newBook.Editions;
            if (editions == null || !editions.Any())
            {
                _logger.Error("[EDITION-SAVE-DEBUG] Refusing to add book '{0}' because it has no editions.", newBook.Title);
                throw new ValidationException(new[]
                {
                    new ValidationFailure("Editions", "Cannot add book because metadata did not provide any editions for it.")
                });
            }

            _bookRepository.Upsert(newBook);

            // DEBUG: Log edition handling
            _logger.Debug("[EDITION-SAVE-DEBUG] Book '{0}' saved with ID {1}", newBook.Title, newBook.Id);

            _logger.Debug("[EDITION-SAVE-DEBUG] Book '{0}' has {1} editions to save", newBook.Title, editions.Count);
            editions.ForEach(x => x.BookId = newBook.Id);
            var editionsToInsert = editions.Where(x => x.Id == 0).ToList();
            _logger.Debug("[EDITION-SAVE-DEBUG] Inserting {0} new editions for book '{1}'", editionsToInsert.Count, newBook.Title);
            _editionService.InsertMany(editionsToInsert);
            var selectedEdition = SelectMonitoredEditionForInsert(editions, newBook.MediaType);
            if (selectedEdition != null)
            {
                _editionService.SetMonitored(selectedEdition, false); // false = not manual selection, this is automatic import
                newBook.ForeignEditionId = selectedEdition.ForeignEditionId;
            }

            // Load editions and author relationship for the event
            newBook.Editions = _editionService.GetEditionsByBook(newBook.Id);
            if (newBook.AuthorId > 0)
            {
                newBook.Author = _authorService.GetAuthor(newBook.AuthorId);
            }

            RefreshBookProviderAliases(newBook);

            _eventAggregator.PublishEvent(new BookAddedEvent(newBook, doRefresh));

            return newBook;
        }

        public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false)
        {
            // Deletion can be triggered multiple times during refresh (duplicate work items, concurrent commands, etc).
            // Treat deletes as idempotent to avoid aborting a full author refresh due to a double-delete race.
            var book = _bookRepository.Find(bookId);
            if (book == null)
            {
                _logger.Debug("DeleteBook called for missing book ID {0}; ignoring", bookId);
                return;
            }

            // Capture related data before deleting the row so downstream event handlers (notifications, etc.)
            // can access author/edition details without issuing additional queries.
            HydrateBookForDeleteEvent(book);

            var deletedBooks = new List<Book>();

            if (applyToBothFormats && book.AuthorId > 0)
            {
                var siblingBooks = GetBooksByAuthor(book.AuthorId)
                    .Where(candidate =>
                        candidate != null &&
                        candidate.Id != book.Id &&
                        WorkIdMatcher.WorkProviderIdMatches(book, candidate))
                    .OrderBy(candidate => candidate.Id)
                    .ToList();

                foreach (var sibling in siblingBooks)
                {
                    HydrateBookForDeleteEvent(sibling);
                    deletedBooks.Add(sibling);
                    DeleteBook(sibling.Id, deleteFiles, addImportListExclusion: false, applyToBothFormats: false);
                }
            }

            deletedBooks.Add(book);

            _eventAggregator.PublishEvent(new BookDeletedEvent(book, deleteFiles, addImportListExclusion, applyToBothFormats, deletedBooks));
            _providerAliasService?.DeleteAliases("Book", bookId);
            _bookRepository.Delete(bookId);
        }

        private void HydrateBookForDeleteEvent(Book book)
        {
            if (book == null)
            {
                return;
            }

            if (book.AuthorId > 0)
            {
                try
                {
                    book.Author = _authorService.GetAuthor(book.AuthorId);
                }
                catch (ModelNotFoundException)
                {
                    // Author may have been deleted/merged while commands are still running.
                }
            }

            book.Editions = _editionService.GetEditionsByBook(book.Id) ?? new List<Edition>();

            try
            {
                var bookFiles = _mediaFileService.GetFilesByBook(book.Id) ?? new List<BookFile>();
                book.BookFiles = bookFiles;

                var filesByEdition = bookFiles
                    .GroupBy(file => file.EditionId)
                    .ToDictionary(group => group.Key, group => group.ToList());

                foreach (var edition in book.Editions)
                {
                    edition.BookFiles = filesByEdition.TryGetValue(edition.Id, out var editionFiles)
                        ? editionFiles
                        : new List<BookFile>();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to hydrate book files for deleted book event: {0}", book.Id);
                book.BookFiles = new List<BookFile>();
            }
        }
        public Book FindBySlug(string titleSlug)
        {
            return _bookRepository.FindBySlug(titleSlug);
        }

        public Book FindByTitle(int authorId, string title)
        {
            return HydrateLookupBook(_bookRepository.FindByTitle(authorId, title));
        }

        public Book FindByGoodreadsId(string goodreadsId)
        {
            return FindByProviderId("gr", goodreadsId);
        }

            public Book FindByProviderId(string provider, string providerId)
            {
                if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
                {
                    return null;
                }

                var audiobook = FindByProviderId(provider, providerId, BookMediaType.Audiobook);
                var ebook = FindByProviderId(provider, providerId, BookMediaType.Ebook);

                if (audiobook == null)
                {
                    return ebook;
                }

                if (ebook == null)
                {
                    return audiobook;
                }

                // Preserve legacy behavior (first match from an unordered list) by deterministically choosing the lowest ID.
                return audiobook.Id <= ebook.Id ? audiobook : ebook;
            }

            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType)
            {
                if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
                {
                    return null;
                }

                provider = provider.Trim().ToLowerInvariant();
                providerId = providerId.Trim();

                string canonicalPrefix;
                string repositoryProvider;

                switch (provider)
                {
                    case "hc":
                        canonicalPrefix = "hc";
                        repositoryProvider = "hc";
                        break;

                    case "gr":
                        canonicalPrefix = "gr";
                        repositoryProvider = "gr";
                        break;

                    case "ol":
                        canonicalPrefix = "ol";
                        repositoryProvider = "ol";
                        break;

                    case "gb":
                        canonicalPrefix = "gb";
                        repositoryProvider = "gb";
                        break;

                    case "az":
                        canonicalPrefix = "az";
                        repositoryProvider = "az";
                        break;

                    default:
                        return null;
                }

                var normalizedProviderId = ProviderIdHelper.Canonicalize(providerId, canonicalPrefix);
                var rawProviderId = ProviderIdHelper.StripPrefix(normalizedProviderId);

                if (ShouldPreferEditionLookup(repositoryProvider, normalizedProviderId))
                {
                    var editionMatch = FindBookByEditionProviderId(repositoryProvider, rawProviderId, mediaType);
                    if (editionMatch != null)
                    {
                        return editionMatch;
                    }
                }

                var workMatch = HydrateLookupBook(_bookRepository.FindByProviderIdAndMediaType(repositoryProvider, normalizedProviderId, mediaType));
                if (workMatch != null)
                {
                    return workMatch;
                }

                var directEditionMatch = FindBookByEditionProviderId(repositoryProvider, rawProviderId, mediaType);
                if (directEditionMatch != null)
                {
                    return directEditionMatch;
                }

                return FindBooksByProviderAlias(normalizedProviderId, mediaType)
                    .OrderBy(book => book.Id)
                    .FirstOrDefault();
            }

            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType)
            {
                if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
                {
                    return new List<Book>();
                }

                provider = provider.Trim().ToLowerInvariant();
                providerId = providerId.Trim();

                string canonicalPrefix;
                string repositoryProvider;

                switch (provider)
                {
                    case "hc":
                        canonicalPrefix = "hc";
                        repositoryProvider = "hc";
                        break;

                    case "gr":
                        canonicalPrefix = "gr";
                        repositoryProvider = "gr";
                        break;

                    case "ol":
                        canonicalPrefix = "ol";
                        repositoryProvider = "ol";
                        break;

                    case "gb":
                        canonicalPrefix = "gb";
                        repositoryProvider = "gb";
                        break;

                    case "az":
                        canonicalPrefix = "az";
                        repositoryProvider = "az";
                        break;

                    case "isbn":
                        return HydrateLookupBooks(_bookRepository.FindAllByProviderIdAndMediaType("isbn", NormalizeIsbnProviderId(providerId), mediaType) ?? new List<Book>());

                    default:
                        return new List<Book>();
                }

                var normalizedProviderId = ProviderIdHelper.Canonicalize(providerId, canonicalPrefix);
                var rawProviderId = ProviderIdHelper.StripPrefix(normalizedProviderId);
                var workMatches = _bookRepository.FindAllByProviderIdAndMediaType(repositoryProvider, normalizedProviderId, mediaType) ?? new List<Book>();
                var books = HydrateLookupBooks(workMatches);

                foreach (var editionMatch in FindBooksByEditionProviderId(repositoryProvider, rawProviderId, mediaType))
                {
                    if (editionMatch != null && books.All(b => b.Id != editionMatch.Id))
                    {
                        books.Add(editionMatch);
                    }
                }

                foreach (var aliasMatch in FindBooksByProviderAlias(normalizedProviderId, mediaType))
                {
                    if (aliasMatch != null && books.All(b => b.Id != aliasMatch.Id))
                    {
                        books.Add(aliasMatch);
                    }
                }

                return books;
            }

            public List<Book> FindAllByWorkProviderId(string provider, string providerId, BookMediaType mediaType)
            {
                if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
                {
                    return new List<Book>();
                }

                provider = provider.Trim().ToLowerInvariant();
                if (provider is not ("hc" or "gr" or "ol" or "gb" or "az"))
                {
                    return new List<Book>();
                }

                var normalizedProviderId = ProviderIdHelper.Canonicalize(providerId.Trim(), provider);
                var books = provider is "hc" or "gr" or "ol"
                    ? HydrateLookupBooks(
                        _bookRepository.FindAllByProviderIdAndMediaType(provider, normalizedProviderId, mediaType) ??
                        new List<Book>())
                    : new List<Book>();

                if (_providerAliasService != null)
                {
                    var aliasBookIds = _providerAliasService.FindBookIds("work", new[] { normalizedProviderId })
                        .Distinct()
                        .ToList();
                    if (aliasBookIds.Count > 0)
                    {
                        foreach (var aliasMatch in HydrateLookupBooks(_bookRepository.Get(aliasBookIds)
                                     .Where(book => book != null && book.MediaType == mediaType)
                                     .ToList()))
                        {
                            if (books.All(book => book.Id != aliasMatch.Id))
                            {
                                books.Add(aliasMatch);
                            }
                        }
                    }
                }

                return books.OrderBy(book => book.Id).ToList();
            }

            private static string NormalizeIsbnProviderId(string providerId)
            {
                return ProviderIdHelper.StripPrefix(providerId)?.Replace("-", string.Empty).Replace(" ", string.Empty);
            }

            private List<Book> FindBooksByProviderAlias(string providerId, BookMediaType mediaType)
            {
                if (_providerAliasService == null || string.IsNullOrWhiteSpace(providerId))
                {
                    return new List<Book>();
                }

                var ids = _providerAliasService.FindBookIds("work", new[] { providerId })
                    .Concat(_providerAliasService.FindBookIds("edition", new[] { providerId }))
                    .Distinct()
                    .ToList();

                if (ids.Count == 0)
                {
                    return new List<Book>();
                }

                return HydrateLookupBooks(_bookRepository.Get(ids)
                    .Where(book => book != null && book.MediaType == mediaType)
                    .ToList());
            }

            public Book FindByISBN(string isbn)
            {
                if (string.IsNullOrWhiteSpace(isbn))
                    return null;

                // Remove any dashes or spaces from ISBN
                isbn = isbn.Replace("-", "").Replace(" ", "");

                var edition = _editionService.GetEditionByProviderAndId("isbn", isbn);
                if (edition?.BookId > 0)
                {
                    return HydrateLookupBook(_bookRepository.Get(edition.BookId));
                }

                return HydrateLookupBook(_bookRepository.FindByIsbn(isbn));
            }

            public Book FindByASIN(string asin)
            {
                if (string.IsNullOrWhiteSpace(asin))
                    return null;

                var normalizedAsin = ProviderIdHelper.StripPrefix(asin).Trim().ToUpperInvariant();
                var edition = _editionService.GetEditionByProviderAndId("az", normalizedAsin);
                if (edition?.BookId > 0)
                {
                    return HydrateLookupBook(_bookRepository.Get(edition.BookId));
                }

                return HydrateLookupBook(_bookRepository.FindByAsin(normalizedAsin));
            }

        private List<Tuple<Func<Book, string, double>, string>> BookScoringFunctions(string title, string cleanTitle)
        {
            Func<Func<Book, string, double>, string, Tuple<Func<Book, string, double>, string>> tc = Tuple.Create;
            var scoringFunctions = new List<Tuple<Func<Book, string, double>, string>>
            {
                // Deterministic match functions
                tc((a, t) => a.CleanTitle.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, cleanTitle),
                tc((a, t) => a.Title.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title),
                tc((a, t) => a.CleanTitle.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title.RemoveBracketsAndContents().CleanAuthorName()),
                tc((a, t) => a.CleanTitle.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title.RemoveAfterDash().CleanAuthorName()),
                tc((a, t) => a.CleanTitle.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title.RemoveBracketsAndContents().RemoveAfterDash().CleanAuthorName()),
                tc((a, t) => t.Contains(a.CleanTitle) || a.CleanTitle.Contains(t) ? 0.8 : 0.0, cleanTitle),
                tc((a, t) => t.Contains(a.Title) || a.Title.Contains(t) ? 0.8 : 0.0, title),
                tc((a, t) => a.Author != null && a.Title.SplitBookTitle(a.Author.Name).Item1.Equals(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title),

                // Enhanced series-aware scoring functions
                tc((a, t) => SeriesAwareTitleMatch(a.Title, t), title),
                tc((a, t) => SeriesAwareTitleMatch(a.Title, t), cleanTitle),
                tc((a, t) => SeriesAwareTitleMatch(a.CleanTitle, t), title),

                // Try matching main title without series info
                tc((a, t) => ExtractMainTitle(a.Title).Equals(ExtractMainTitle(t), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, title),
                tc((a, t) => ExtractMainTitle(a.CleanTitle).Equals(ExtractMainTitle(t), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0, cleanTitle)
            };

            return scoringFunctions;
        }

        public Book FindByTitleInexact(int authorId, string title)
        {
            var books = GetBooksByAuthorId(authorId);

            foreach (var func in BookScoringFunctions(title, title.CleanAuthorName()))
            {
                var results = FindByStringInexact(books, func.Item1, func.Item2);
                if (results.Count == 1)
                {
                    return HydrateLookupBook(results[0]);
                }
            }

            return null;
        }

        public List<Book> GetCandidates(int authorId, string title)
        {
            var books = GetBooksByAuthorId(authorId);
            var output = new List<Book>();

            foreach (var func in BookScoringFunctions(title, title.CleanAuthorName()))
            {
                output.AddRange(FindByStringInexact(books, func.Item1, func.Item2));
            }

            return output.DistinctBy(x => x.Id).ToList();
        }

        private List<Book> FindByStringInexact(List<Book> books, Func<Book, string, double> scoreFunction, string title)
        {
            const double fuzzThreshold = 0.7;
            const double fuzzGap = 0.4;

            var sortedBooks = books.Select(s => new
            {
                MatchProb = scoreFunction(s, title),
                Book = s
            })
                .ToList()
                .OrderByDescending(s => s.MatchProb)
                .ToList();

            return sortedBooks.TakeWhile((x, i) => i == 0 || sortedBooks[i - 1].MatchProb - x.MatchProb < fuzzGap)
                .TakeWhile((x, i) => x.MatchProb > fuzzThreshold || (i > 0 && sortedBooks[i - 1].MatchProb > fuzzThreshold))
                .Select(x => x.Book)
                .ToList();
        }

        public List<Book> GetAllBooks()
        {
            var books = _bookRepository.All().ToList();
            LoadSeriesLinks(books);
            return books;
        }

        public Book GetBook(int bookId)
        {
            var book = _bookRepository.Get(bookId);
            if (book != null && book.AuthorId > 0)
            {
                book.Author = _authorService.GetAuthor(book.AuthorId);

                // Load editions for the book
                book.Editions = _editionService.GetEditionsByBook(bookId);

                // Load BookFiles in one query while preserving the old edition-iteration ordering.
                if (book.Editions != null && book.Editions.Any())
                {
                    var editionOrder = book.Editions
                        .Select((edition, index) => new { edition.Id, Index = index })
                        .ToDictionary(x => x.Id, x => x.Index);

                    var filesByEdition = (_mediaFileService.GetFilesByBooks(new List<int> { book.Id }) ?? new List<BookFile>())
                        .OrderBy(file => editionOrder.TryGetValue(file.EditionId, out var index) ? index : int.MaxValue)
                        .ThenBy(file => file.Id)
                        .GroupBy(file => file.EditionId)
                        .ToDictionary(group => group.Key, group => group.ToList());

                    var allBookFiles = new List<BookFile>();
                    foreach (var edition in book.Editions)
                    {
                        if (filesByEdition.TryGetValue(edition.Id, out var files) && files.Any())
                        {
                            edition.BookFiles = files;
                            allBookFiles.AddRange(files);
                        }
                    }

                    book.BookFiles = allBookFiles;
                }
            }

            return book;
        }

        public List<Book> GetBooks(IEnumerable<int> bookIds)
        {
            var books = _bookRepository.Get(bookIds).ToList();
            HydrateBooks(books);

            return books;
        }

        public List<Book> GetExistingBooks(IEnumerable<int> bookIds)
        {
            var ids = bookIds?
                .Where(id => id > 0)
                .Distinct()
                .ToList() ?? new List<int>();

            if (!ids.Any())
            {
                return new List<Book>();
            }

            var booksById = _bookRepository.FindExisting(ids)
                .Where(book => book != null)
                .ToDictionary(book => book.Id);

            var books = ids
                .Where(booksById.ContainsKey)
                .Select(id => booksById[id])
                .ToList();

            HydrateBooks(books);

            return books;
        }

        private void HydrateBooks(List<Book> books)
        {
            LoadAuthorRelationships(books);
            LoadSeriesLinks(books);

            // Load editions so API resources can consistently select the correct edition title/cover.
            // This is explicit bulk hydration for rich API/matching paths; lean display paths opt out.
            if (!books.Any())
            {
                return;
            }

            var ids = books.Select(b => b.Id).ToList();
            var allEditions = _editionService.GetEditionsByBook(ids);
            var editionsByBook = allEditions
                .GroupBy(e => e.BookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var book in books)
            {
                if (editionsByBook.TryGetValue(book.Id, out var bookEditions))
                {
                    book.Editions = bookEditions;
                }
                else
                {
                    book.Editions = new List<Edition>();
                }
            }
        }

        public List<Book> GetBooksByAuthor(int authorId)
        {
            var books = _bookRepository.GetBooks(authorId).ToList();

            // We already know the author for these books
            Author author = null;
            try
            {
                author = _authorService.GetAuthor(authorId);
            }
            catch (ModelNotFoundException)
            {
                // Author may have been deleted/merged while a command is still running.
                // Return books without an Author relationship rather than failing the entire request.
            }

            if (author != null)
            {
                foreach (var book in books)
                {
                    book.Author = author;
                }
            }

            LoadSeriesLinks(books);

            // Load editions for cover selection (needed by EnsureBookCovers)
            if (books.Any())
            {
                var bookIds = books.Select(b => b.Id).ToList();
                var allEditions = _editionService.GetEditionsByBook(bookIds);
                var editionsByBook = allEditions.GroupBy(e => e.BookId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var book in books)
                {
                    if (editionsByBook.TryGetValue(book.Id, out var bookEditions))
                    {
                        book.Editions = bookEditions;
                    }
                }
            }

            return books;
        }

        public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds)
        {
            return _bookRepository.GetNextBooks(authorIds).ToList();
        }

        public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds)
        {
            return _bookRepository.GetLastBooks(authorIds).ToList();
        }

        public List<Book> GetBooksByAuthorId(int authorId)
        {
            var books = _bookRepository.GetBooksByAuthorId(authorId).ToList();
            LoadSeriesLinks(books);
            return books;
        }

        public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds)
        {
            return _bookRepository.GetBooksForRefresh(authorId, foreignIds);
        }

        public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds)
        {
            return _bookRepository.GetBooksByFileIds(fileIds);
        }

        public void SetAddOptions(IEnumerable<Book> books)
        {
            _bookRepository.SetFields(books.ToList(), s => s.AddOptions);
        }

        public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec)
        {
            var bookResult = _bookRepository.BooksWithoutFiles(pagingSpec);

            // Ensure Author relationships are loaded
            if (bookResult.Records.Any())
            {
                LoadAuthorRelationships(bookResult.Records.ToList());
            }

            return bookResult;
        }

        public List<BookSearchTarget> GetMissingBookSearchTargets(BookMediaType? mediaType, int? authorId)
        {
            return _bookRepository.GetMissingBookSearchTargets(mediaType, authorId);
        }

        public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored)
        {
            var books = _bookRepository.BooksBetweenDates(start.ToUniversalTime(), end.ToUniversalTime(), includeUnmonitored);

            return books;
        }

        public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored)
        {
            var books = _bookRepository.AuthorBooksBetweenDates(author, start.ToUniversalTime(), end.ToUniversalTime(), includeUnmonitored);

            return books;
        }

        public List<Book> GetAuthorBooksWithFiles(Author author)
        {
            return _bookRepository.GetAuthorBooksWithFiles(author);
        }

        private void EnsureUniqueTitleSlugs(List<Book> books)
        {
            // Group by AuthorId to handle duplicates per author
            var booksByAuthor = books.GroupBy(b => b.AuthorId);

            foreach (var authorBooks in booksByAuthor)
            {
                // Get existing books for this author
                var existingBooks = _bookRepository.GetBooksByAuthorId(authorBooks.Key);

                // Build a dictionary of existing slugs, excluding the books being updated
                var bookIdsBeingUpdated = authorBooks.Where(b => b.Id > 0).Select(b => b.Id).ToHashSet();
                var existingSlugs = existingBooks
                    .Where(b => !bookIdsBeingUpdated.Contains(b.Id) && !string.IsNullOrEmpty(b.TitleSlug))
                    .Select(b => b.TitleSlug)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Track slugs we're about to add/update
                var newSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var book in authorBooks)
                {
                    // Ensure book has a TitleSlug before processing (consistent with AddBook)
                    if (string.IsNullOrEmpty(book.TitleSlug))
                    {
                        book.TitleSlug = book.Title?.ToLowerInvariant().Replace(" ", "-") ?? $"book-{book.Id}";
                        _logger.Debug("Generated TitleSlug for book '{0}': {1}", book.Title, book.TitleSlug);
                    }
                    
                    var baseSlug = book.TitleSlug;
                    var finalSlug = baseSlug;
                    var counter = 2;

                    // Check against both existing and new slugs
                    while (existingSlugs.Contains(finalSlug) || newSlugs.Contains(finalSlug))
                    {
                        finalSlug = $"{baseSlug}_{counter}";
                        counter++;
                    }

                    if (finalSlug != baseSlug)
                    {
                        _logger.Debug("Duplicate TitleSlug detected for book '{0}'. Changed from '{1}' to '{2}'", book.Title, baseSlug, finalSlug);
                        book.TitleSlug = finalSlug;
                    }

                    newSlugs.Add(finalSlug);
                }
            }
        }

        private static MonitoredStateSnapshot SnapshotMonitoredState(Book book)
        {
            return new MonitoredStateSnapshot
            {
                AudiobookMonitored = book?.AudiobookMonitored == true,
                EbookMonitored = book?.EbookMonitored == true
            };
        }

        private static bool HasMonitoringChanged(Book book, MonitoredStateSnapshot snapshot)
        {
            if (book == null || snapshot == null)
            {
                return false;
            }

            return book.AudiobookMonitored != snapshot.AudiobookMonitored ||
                   book.EbookMonitored != snapshot.EbookMonitored;
        }

        private static Book CloneStoredBook(Book book)
        {
            if (book == null)
            {
                return null;
            }

            return new Book
            {
                Id = book.Id,
                Title = book.Title,
                CleanTitle = book.CleanTitle,
                TitleSlug = book.TitleSlug,
                MediaType = book.MediaType,
                Author = book.Author,
                Narrator = book.Narrator,
                Monitored = book.Monitored,
                AudiobookMonitored = book.AudiobookMonitored,
                EbookMonitored = book.EbookMonitored,
                AnyEditionOk = book.AnyEditionOk,
                BaseBookId = book.BaseBookId,
                GoodreadsWorkId = book.GoodreadsWorkId,
                HardcoverBookId = book.HardcoverBookId,
                OpenLibraryWorkId = book.OpenLibraryWorkId,
                ASIN = BookEditionIdentity.GetAsin(book),
                AudibleASIN = BookEditionIdentity.GetAudibleAsin(book)
            };
        }

        private static void SetRowMonitored(Book book, bool monitored)
        {
            book?.SetMonitored(monitored);
        }

        private static bool IsRowMonitored(Book book)
        {
            return book?.IsMonitored() == true;
        }

        private static bool HasFormat(List<Book> workGroup, BookMediaType mediaType)
        {
            return workGroup.Any(book => book.MediaType == mediaType);
        }

        private static bool IsSyncVariantRow(Book book)
        {
            if (book == null)
            {
                return true;
            }

            if (book.UnitKeyHash.IsNotNullOrWhiteSpace())
            {
                return true;
            }

            return book.TitleSlug.IsNotNullOrWhiteSpace() &&
                   book.TitleSlug.IndexOf("_copy_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<Book> GetSyncParticipants(List<Book> workGroup)
        {
            return (workGroup ?? new List<Book>())
                .Where(book => book != null && !IsSyncVariantRow(book))
                .ToList();
        }

        private static List<Book> GetSyncParticipants(List<Book> workGroup, BookMediaType mediaType)
        {
            return GetSyncParticipants(workGroup)
                .Where(book => book.MediaType == mediaType)
                .ToList();
        }

        private static bool HasSyncFormat(List<Book> workGroup, BookMediaType mediaType)
        {
            return GetSyncParticipants(workGroup, mediaType).Any();
        }

        private static List<List<Book>> BuildWorkGroups(List<Book> books)
        {
            var remaining = new List<Book>(books ?? new List<Book>());
            var groups = new List<List<Book>>();

            while (remaining.Count > 0)
            {
                var seed = remaining[0];
                remaining.RemoveAt(0);

                var group = new List<Book> { seed };
                var queue = new Queue<Book>();
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    for (var index = remaining.Count - 1; index >= 0; index--)
                    {
                        var candidate = remaining[index];
                        if (!WorkIdMatcher.CrossFormatSafeMatches(current, candidate))
                        {
                            continue;
                        }

                        remaining.RemoveAt(index);
                        group.Add(candidate);
                        queue.Enqueue(candidate);
                    }
                }

                groups.Add(group);
            }

            return groups;
        }

        private bool HasCompatibleRootFolderForMediaType(Author author, BookMediaType mediaType)
        {
            if (author == null)
            {
                return false;
            }

            var rootFolderPath = mediaType == BookMediaType.Audiobook
                ? author.AudiobookRootFolderPath
                : author.EbookRootFolderPath;

            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (_rootFolderService == null)
            {
                return true;
            }

            var rootFolder = _rootFolderService.All()?.FirstOrDefault(r => r.Path.PathEquals(rootFolderPath));
            if (rootFolder == null)
            {
                return false;
            }

            return rootFolder.FolderType == FolderType.Mixed ||
                   (mediaType == BookMediaType.Audiobook && rootFolder.FolderType == FolderType.Audiobook) ||
                   (mediaType == BookMediaType.Ebook && rootFolder.FolderType == FolderType.Ebook);
        }

        private bool CanEnableMonitoringForMediaType(Author author, BookMediaType mediaType)
        {
            if (author == null)
            {
                return false;
            }

            // Book-row state is independent from the author-side gate. The gate is
            // evaluated by eligibility queries, so an explicit row selection remains
            // valid while its author side is paused.
            return HasCompatibleRootFolderForMediaType(author, mediaType);
        }

        private void EnsureOneMonitoredOnFormat(List<Book> workGroup, BookMediaType mediaType, Author author)
        {
            var formatBooks = GetSyncParticipants(workGroup, mediaType);

            if (!formatBooks.Any() || formatBooks.Any(IsRowMonitored) || !CanEnableMonitoringForMediaType(author, mediaType))
            {
                return;
            }

            var targetBook = formatBooks
                .OrderBy(book => book.Id <= 0 ? int.MaxValue : book.Id)
                .First();

            SetRowMonitored(targetBook, true);
            _logger.Debug("Synced monitoring across formats for work '{0}' by enabling {1} book {2}", targetBook.Title, mediaType, targetBook.Id);
        }

        private void DisableFormat(List<Book> workGroup, BookMediaType mediaType)
        {
            foreach (var book in GetSyncParticipants(workGroup, mediaType).Where(IsRowMonitored))
            {
                SetRowMonitored(book, false);
                _logger.Debug("Synced monitoring across formats for work '{0}' by disabling {1} book {2}", book.Title, mediaType, book.Id);
            }
        }

        private void ApplyMutationSyncForWorkGroup(Author author, List<Book> workGroup, HashSet<int> changedBookIds, Dictionary<int, Book> storedById)
        {
            if (author?.SyncMonitoredAcrossFormats != true ||
                workGroup == null ||
                workGroup.Count < 2 ||
                !HasSyncFormat(workGroup, BookMediaType.Audiobook) ||
                !HasSyncFormat(workGroup, BookMediaType.Ebook))
            {
                return;
            }

            var syncParticipants = GetSyncParticipants(workGroup);
            var changedBooks = syncParticipants.Where(book => changedBookIds.Contains(book.Id)).ToList();
            if (!changedBooks.Any())
            {
                return;
            }

            var audiobookStillMonitored = syncParticipants.Any(book => book.MediaType == BookMediaType.Audiobook && IsRowMonitored(book));
            var ebookStillMonitored = syncParticipants.Any(book => book.MediaType == BookMediaType.Ebook && IsRowMonitored(book));

            var anyEnabled = false;
            var disableEbooks = false;
            var disableAudiobooks = false;

            foreach (var changedBook in changedBooks)
            {
                if (!storedById.TryGetValue(changedBook.Id, out var storedBook))
                {
                    continue;
                }

                var wasMonitored = IsRowMonitored(storedBook);
                var isMonitored = IsRowMonitored(changedBook);

                if (isMonitored && !wasMonitored)
                {
                    anyEnabled = true;
                    continue;
                }

                if (!wasMonitored || isMonitored)
                {
                    continue;
                }

                if (changedBook.MediaType == BookMediaType.Audiobook && !audiobookStillMonitored)
                {
                    disableEbooks = true;
                }
                else if (changedBook.MediaType == BookMediaType.Ebook && !ebookStillMonitored)
                {
                    disableAudiobooks = true;
                }
            }

            if (anyEnabled)
            {
                EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Audiobook, author);
                EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Ebook, author);
                return;
            }

            if (disableAudiobooks)
            {
                DisableFormat(workGroup, BookMediaType.Audiobook);
            }

            if (disableEbooks)
            {
                DisableFormat(workGroup, BookMediaType.Ebook);
            }
        }

        private List<BookMonitoringSyncUpdate> GetSyncUpdatesForMutations(List<Book> changedBooks, Dictionary<int, Book> storedById)
        {
            var syncUpdates = new List<BookMonitoringSyncUpdate>();
            if (_authorService == null || changedBooks == null || changedBooks.Count == 0)
            {
                return syncUpdates;
            }

            foreach (var authorBooks in changedBooks.Where(book => book?.AuthorId > 0).GroupBy(book => book.AuthorId))
            {
                var author = _authorService.GetAuthor(authorBooks.Key);
                if (author?.SyncMonitoredAcrossFormats != true)
                {
                    continue;
                }

                var repositoryBooks = _bookRepository.GetBooksByAuthorId(authorBooks.Key) ?? new List<Book>();
                var authorStoredById = repositoryBooks.ToDictionary(book => book.Id, CloneStoredBook);
                var authorBooksById = repositoryBooks.ToDictionary(book => book.Id);

                foreach (var changedBook in authorBooks)
                {
                    if (storedById.TryGetValue(changedBook.Id, out var storedBook))
                    {
                        authorStoredById[changedBook.Id] = CloneStoredBook(storedBook);
                    }

                    authorBooksById[changedBook.Id] = changedBook;
                }

                var changedBookIds = authorBooks.Select(book => book.Id).ToHashSet();
                var baseStates = authorBooksById.ToDictionary(pair => pair.Key, pair => SnapshotMonitoredState(pair.Value));

                foreach (var workGroup in BuildWorkGroups(authorBooksById.Values.ToList()))
                {
                    ApplyMutationSyncForWorkGroup(author, workGroup, changedBookIds, authorStoredById);
                }

                foreach (var pair in authorBooksById)
                {
                    if (changedBookIds.Contains(pair.Key) || !HasMonitoringChanged(pair.Value, baseStates[pair.Key]))
                    {
                        continue;
                    }

                    syncUpdates.Add(new BookMonitoringSyncUpdate
                    {
                        Book = pair.Value,
                        Stored = authorStoredById[pair.Key]
                    });
                }
            }

            return syncUpdates;
        }

        private void ApplyInsertSyncDefaults(List<Book> books)
        {
            if (_authorService == null || books == null || books.Count == 0)
            {
                return;
            }

            foreach (var authorBooks in books.Where(book => book?.AuthorId > 0).GroupBy(book => book.AuthorId))
            {
                var author = _authorService.GetAuthor(authorBooks.Key);
                if (author?.SyncMonitoredAcrossFormats != true)
                {
                    continue;
                }

                var insertedBooks = authorBooks.ToList();
                var combinedBooks = (_bookRepository.GetBooksByAuthorId(authorBooks.Key) ?? new List<Book>())
                    .Concat(insertedBooks)
                    .ToList();

                foreach (var workGroup in BuildWorkGroups(combinedBooks))
                {
                    var insertedGroupBooks = workGroup
                        .Where(book => insertedBooks.Any(inserted => ReferenceEquals(inserted, book)))
                        .ToList();

                    var insertedSyncBooks = insertedGroupBooks
                        .Where(book => !IsSyncVariantRow(book))
                        .ToList();

                    if (!insertedSyncBooks.Any())
                    {
                        continue;
                    }

                    var syncParticipants = GetSyncParticipants(workGroup);
                    var hasAnyMonitoredSibling = workGroup.Any(book =>
                        syncParticipants.Contains(book) &&
                        !insertedSyncBooks.Any(inserted => ReferenceEquals(inserted, book)) &&
                        IsRowMonitored(book));

                    if (!hasAnyMonitoredSibling)
                    {
                        continue;
                    }

                    if (insertedSyncBooks.Any(book => book.MediaType == BookMediaType.Audiobook) &&
                        !GetSyncParticipants(workGroup, BookMediaType.Audiobook).Any(IsRowMonitored))
                    {
                        EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Audiobook, author);
                    }

                    if (insertedSyncBooks.Any(book => book.MediaType == BookMediaType.Ebook) &&
                        !GetSyncParticipants(workGroup, BookMediaType.Ebook).Any(IsRowMonitored))
                    {
                        EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Ebook, author);
                    }
                }
            }
        }

        private List<BookMonitoringSyncUpdate> BuildReconcileSyncUpdates(IEnumerable<int> authorIds)
        {
            var syncUpdates = new List<BookMonitoringSyncUpdate>();
            if (_authorService == null)
            {
                return syncUpdates;
            }

            var authorIdList = authorIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();

            foreach (var authorId in authorIdList)
            {
                var author = _authorService.GetAuthor(authorId);
                if (author?.SyncMonitoredAcrossFormats != true)
                {
                    continue;
                }

                var authorBooks = _bookRepository.GetBooksByAuthorId(authorId) ?? new List<Book>();
                if (authorBooks.Count < 2)
                {
                    continue;
                }

                var storedById = authorBooks.ToDictionary(book => book.Id, CloneStoredBook);
                var baseStates = authorBooks.ToDictionary(book => book.Id, SnapshotMonitoredState);

                foreach (var workGroup in BuildWorkGroups(authorBooks))
                {
                    var syncParticipants = GetSyncParticipants(workGroup);
                    if (!HasSyncFormat(workGroup, BookMediaType.Audiobook) ||
                        !HasSyncFormat(workGroup, BookMediaType.Ebook) ||
                        !syncParticipants.Any(IsRowMonitored))
                    {
                        continue;
                    }

                    EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Audiobook, author);
                    EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Ebook, author);
                }

                foreach (var book in authorBooks)
                {
                    if (!HasMonitoringChanged(book, baseStates[book.Id]))
                    {
                        continue;
                    }

                    syncUpdates.Add(new BookMonitoringSyncUpdate
                    {
                        Book = book,
                        Stored = storedById[book.Id]
                    });
                }
            }

            return syncUpdates;
        }

        private void EnsureAuthorLoadedForEvent(Book book)
        {
            if (_authorService != null && book?.AuthorId > 0 && (book.Author == null || string.IsNullOrWhiteSpace(book.Author.Name)))
            {
                book.Author = _authorService.GetAuthor(book.AuthorId);
            }
        }

        private void PublishBookEditedEvents(IEnumerable<BookMonitoringSyncUpdate> updates)
        {
            foreach (var update in updates ?? Enumerable.Empty<BookMonitoringSyncUpdate>())
            {
                EnsureAuthorLoadedForEvent(update.Book);
                EnsureAuthorLoadedForEvent(update.Stored);
                _eventAggregator.PublishEvent(new BookEditedEvent(update.Book, update.Stored));
            }
        }

        public void InsertMany(List<Book> books)
        {
            if (books.Any(x => x.AuthorId == 0))
            {
                throw new InvalidOperationException("Cannot insert book with AuthorId = 0");
            }

            // LocalBookId generation removed - using database IDs directly

            // Ensure unique TitleSlugs for duplicate books
            EnsureUniqueTitleSlugs(books);
            books.ForEach(EnsureBookDbFields);
            ApplyInsertSyncDefaults(books);

            _bookRepository.InsertMany(books);
            RefreshBookProviderAliases(books);
        }

        public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction)
        {
            if (books.Any(x => x.AuthorId == 0))
            {
                throw new InvalidOperationException("Cannot insert book with AuthorId = 0");
            }

            // LocalBookId generation removed - using database IDs directly

            // Ensure unique TitleSlugs for duplicate books
            EnsureUniqueTitleSlugs(books);
            books.ForEach(EnsureBookDbFields);
            ApplyInsertSyncDefaults(books);

            _bookRepository.InsertMany(books, connection, transaction);
        }

        public void UpdateMany(List<Book> books)
        {
            // Ensure unique TitleSlugs for duplicate books when updating
            EnsureUniqueTitleSlugs(books);
            books.ForEach(EnsureBookDbFields);
            var storedById = _bookRepository.Get(books.Select(book => book.Id)).ToDictionary(book => book.Id, CloneStoredBook);
            var syncUpdates = GetSyncUpdatesForMutations(books, storedById);
            var booksToUpdate = books
                .Concat(syncUpdates.Select(update => update.Book))
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            _bookRepository.UpdateMany(booksToUpdate);
            RefreshBookProviderAliases(booksToUpdate);
        }

        public void ReassignAuthor(Book book, Author author)
        {
            ReassignAuthor(book == null ? new List<Book>() : new List<Book> { book }, author);
        }

        public void ReassignAuthor(List<Book> books, Author author)
        {
            if (author == null || author.Id <= 0)
            {
                throw new InvalidOperationException("Cannot reassign books to an author without a persisted AuthorId");
            }

            var booksToReassign = GetPersistedBooksForAuthorReassignment(books, author.Id);
            if (!booksToReassign.Any())
            {
                return;
            }

            foreach (var book in booksToReassign)
            {
                book.Author = author;
            }

            _bookRepository.SetAuthorId(booksToReassign);
            _logger.Debug("Reassigned {0} book author link(s) to AuthorId={1}: {2}",
                booksToReassign.Count,
                author.Id,
                string.Join(",", booksToReassign.Select(book => book.Id)));
        }

        public void ReassignAuthor(Book book, int authorId)
        {
            ReassignAuthor(book == null ? new List<Book>() : new List<Book> { book }, authorId);
        }

        public void ReassignAuthor(List<Book> books, int authorId)
        {
            if (authorId <= 0)
            {
                throw new InvalidOperationException("Cannot reassign books to AuthorId <= 0");
            }

            var booksToReassign = GetPersistedBooksForAuthorReassignment(books, authorId);
            if (!booksToReassign.Any())
            {
                return;
            }

            foreach (var book in booksToReassign)
            {
                book.Author = null;
                book.AuthorId = authorId;
            }

            _bookRepository.SetAuthorId(booksToReassign);
            _logger.Debug("Reassigned {0} book author link(s) to AuthorId={1}: {2}",
                booksToReassign.Count,
                authorId,
                string.Join(",", booksToReassign.Select(book => book.Id)));
        }

        private static List<Book> GetPersistedBooksForAuthorReassignment(IEnumerable<Book> books, int authorId)
        {
            return books?
                .Where(book => book != null && book.Id > 0 && book.AuthorId != authorId)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList() ?? new List<Book>();
        }

        public void DeleteMany(List<Book> books)
        {
            var booksToDelete = (books ?? new List<Book>())
                .Where(book => book != null)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            foreach (var book in booksToDelete)
            {
                HydrateBookForDeleteEvent(book);
                _eventAggregator.PublishEvent(new BookDeletedEvent(book, false, false));
                _providerAliasService?.DeleteAliases("Book", book.Id);
            }

            if (booksToDelete.Any())
            {
                _bookRepository.DeleteMany(booksToDelete);
            }
        }

        public Book UpdateBook(Book book)
        {
            var storedBook = GetBook(book.Id);

            var requestedPinnedEditionId = GetPinnedEditionSelectionId(book);

            // CRITICAL: Check if narrator is being manually changed by user
            bool narratorChanged = storedBook.Narrator != book.Narrator &&
                                  !string.IsNullOrWhiteSpace(book.Narrator);

            if (narratorChanged)
            {
                _logger.Info($"User manually selected narrator '{book.Narrator}' for book '{book.Title}' (ID: {book.Id})");

                // Mark the current monitored edition as manually selected
                var monitoredEdition = _editionService.GetEditionsByBook(book.Id)
                    .FirstOrDefault(e => e.Monitored);

                if (monitoredEdition != null && !monitoredEdition.ManualAdd)
                {
                    _logger.Debug($"Setting ManualAdd=true for edition {monitoredEdition.Id} due to narrator selection");
                    monitoredEdition.ManualAdd = true;
                    _editionService.UpdateMany(new List<Edition> { monitoredEdition });
                }
            }

            // Check if this book has only non-audiobook files
            var bookFiles = _mediaFileService.GetFilesByBook(book.Id);
            var hasAnyFiles = bookFiles.Any();
            var hasAudioFiles = bookFiles.Any(f =>
                f.MediaType == "audiobook" ||
                IsAudiobookExtension(Path.GetExtension(f.Path)));

            // Narrator only applies to audiobook media types.
            // Allow narrator selection for "wanted" audiobooks that don't have files yet.
            if (storedBook.MediaType == BookMediaType.Ebook && !string.IsNullOrWhiteSpace(book.Narrator))
            {
                book.Narrator = null;
            }
            // Clear narrator field when files exist but none of them are audiobook files.
            else if (hasAnyFiles && !hasAudioFiles && !string.IsNullOrWhiteSpace(book.Narrator))
            {
                _logger.Info($"Clearing narrator field for book '{book.Title}' (ID: {book.Id}) as it has no audiobook files");
                book.Narrator = null;
            }

            EnsureBookDbFields(book);
            var syncUpdates = GetSyncUpdatesForMutations(new List<Book> { book }, new Dictionary<int, Book> { { storedBook.Id, CloneStoredBook(storedBook) } });

            var updatedBook = _bookRepository.Update(book);
            if (syncUpdates.Any())
            {
                _bookRepository.UpdateMany(syncUpdates.Select(update => update.Book).ToList());
            }
            RefreshBookProviderAliases(updatedBook);
            RefreshBookProviderAliases(syncUpdates.Select(update => update.Book));

            // Ensure author relationship is loaded before publishing event
            // This follows the pattern used in SetBookMonitored() and other methods
            if (updatedBook.AuthorId > 0 && (updatedBook.Author == null || string.IsNullOrWhiteSpace(updatedBook.Author.Name)))
            {
                updatedBook.Author = _authorService.GetAuthor(updatedBook.AuthorId);
            }

            RelinkBookFilesToPinnedEditionIfNeeded(updatedBook.Id, requestedPinnedEditionId);

            _eventAggregator.PublishEvent(new BookEditedEvent(updatedBook, storedBook));
            PublishBookEditedEvents(syncUpdates);

            return updatedBook;
        }

        private static int? GetPinnedEditionSelectionId(Book book)
        {
            if (book == null)
            {
                return null;
            }

            if (book.AnyEditionOk)
            {
                return null;
            }

            if (book.Editions == null || book.Editions.Count == 0)
            {
                return null;
            }

            var monitoredEditions = book.Editions.Where(e => e != null && e.Monitored).ToList();
            if (monitoredEditions.Count != 1)
            {
                return null;
            }

            var monitoredEditionId = monitoredEditions[0].Id;
            if (monitoredEditionId <= 0)
            {
                return null;
            }

            return monitoredEditionId;
        }

        private void RelinkBookFilesToPinnedEditionIfNeeded(int bookId, int? pinnedEditionId)
        {
            if (pinnedEditionId == null || pinnedEditionId <= 0 || bookId <= 0)
            {
                return;
            }

            var files = _mediaFileService.GetFilesByBook(bookId);
            if (files == null || files.Count == 0)
            {
                return;
            }

            var filesToRelink = files.Where(f => f != null && f.EditionId != pinnedEditionId.Value).ToList();
            if (!filesToRelink.Any())
            {
                return;
            }

            foreach (var file in filesToRelink)
            {
                file.EditionId = pinnedEditionId.Value;
                file.Edition = null;
            }

            _mediaFileService.Update(filesToRelink);

            _logger.Info("Relinked {0} book files for BookId={1} to EditionId={2} due to pinned edition selection",
                filesToRelink.Count, bookId, pinnedEditionId.Value);
        }

        private bool IsAudiobookExtension(string extension)
        {
            return MediaFileExtensions.AudioExtensions.Contains(extension);
        }

        public void SetBookMonitored(int bookId, bool monitored)
        {
            var book = _bookRepository.Get(bookId);
            var storedBook = CloneStoredBook(book);
            book.SetMonitored(monitored);
            var syncUpdates = GetSyncUpdatesForMutations(new List<Book> { book }, new Dictionary<int, Book> { { book.Id, storedBook } });

            _bookRepository.Update(book);
            if (syncUpdates.Any())
            {
                _bookRepository.UpdateMany(syncUpdates.Select(update => update.Book).ToList());
            }

            // Ensure author relationship is loaded before publishing event
            if (book.AuthorId > 0 && (book.Author == null || string.IsNullOrWhiteSpace(book.Author.Name)))
            {
                book.Author = _authorService.GetAuthor(book.AuthorId);
            }

            // publish book edited event so author stats update
            _eventAggregator.PublishEvent(new BookEditedEvent(book, storedBook));
            PublishBookEditedEvents(syncUpdates);

            _logger.Debug("Monitored flag for Book:{0} was set to {1}", bookId, monitored);
        }

        public void SetMonitored(IEnumerable<int> ids, bool monitored)
        {
            var books = _bookRepository.Get(ids).ToList();
            var storedById = books.ToDictionary(book => book.Id, CloneStoredBook);

            foreach (var book in books)
            {
                book.SetMonitored(monitored);
            }

            var syncUpdates = GetSyncUpdatesForMutations(books, storedById);
            var booksToUpdate = books
                .Concat(syncUpdates.Select(update => update.Book))
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            if (booksToUpdate.Any())
            {
                _bookRepository.UpdateMany(booksToUpdate);
            }

            // publish book edited event so author stats update
            foreach (var book in books)
            {
                // Ensure author relationship is loaded before publishing event
                if (book.AuthorId > 0 && (book.Author == null || string.IsNullOrWhiteSpace(book.Author.Name)))
                {
                    book.Author = _authorService.GetAuthor(book.AuthorId);
                }

                _eventAggregator.PublishEvent(new BookEditedEvent(book, storedById[book.Id]));
            }

            PublishBookEditedEvents(syncUpdates);
        }

        public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored)
        {
            if (!string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn("Ignoring SetMonitoredForMediaType for unsupported media type '{0}'", mediaType);
                return;
            }

            var books = _bookRepository.Get(ids).ToList();
            var storedById = books.ToDictionary(book => book.Id, CloneStoredBook);
            var booksToMutate = new List<Book>();

            foreach (var book in books)
            {
                Author author = null;
                if (book.AuthorId > 0)
                {
                    author = _authorService.GetAuthor(book.AuthorId);
                    book.Author = author;
                }

                if (monitored && !CanEnableMonitoringForMediaType(author, mediaType == "ebook" ? BookMediaType.Ebook : BookMediaType.Audiobook))
                {
                    _logger.Warn("Cannot enable {0} monitoring for book '{1}' - author '{2}' is not monitored for {0}s", mediaType, book.Title, author?.Name ?? "unknown");
                    continue;
                }

                book.SetMonitoredForMediaType(mediaType, monitored);
                booksToMutate.Add(book);
            }

            // This method is the explicitly media-scoped mutation surface. Callers that want
            // cross-format synchronization use SetBookMonitored/SetMonitored instead.
            if (booksToMutate.Any())
            {
                _bookRepository.UpdateMany(booksToMutate);
            }

            foreach (var book in booksToMutate)
            {
                EnsureAuthorLoadedForEvent(book);
                _eventAggregator.PublishEvent(new BookEditedEvent(book, storedById[book.Id]));
            }
        }

        public void UpdateLastSearchTime(List<Book> books)
        {
            _bookRepository.SetFields(books, b => b.LastSearchTime);
        }

        public void Handle(AuthorDeletedEvent message)
        {
            var books = GetBooksByAuthorId(message.Author.Id);
            DeleteMany(books);
        }

        public void Execute(BulkSyncFormatMonitoringCommand message)
        {
            var syncUpdates = BuildReconcileSyncUpdates(message.AuthorIds);
            if (!syncUpdates.Any())
            {
                _logger.Info("No monitored format sync reconciliation was required for {0} authors", message.AuthorIds?.Count ?? 0);
                return;
            }

            _bookRepository.UpdateMany(syncUpdates.Select(update => update.Book).ToList());
            PublishBookEditedEvents(syncUpdates);

            _logger.Info("Reconciled monitored format sync across {0} book rows for {1} authors", syncUpdates.Count, message.AuthorIds?.Count ?? 0);
        }

        public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null)
        {
            _logger.Debug("[DISPLAY-DEBUG] GetBooksForDisplay called with authorId={0}, mediaType={1}", authorId, mediaType);

            List<Book> allBooks;

            if (authorId.HasValue)
            {
                allBooks = GetBooksByAuthor(authorId.Value);
                _logger.Debug("[DISPLAY-DEBUG] GetBooksByAuthor returned {0} books for author {1}", allBooks.Count, authorId.Value);
            }
            else
            {
                allBooks = GetAllBooks();
                _logger.Debug("[DISPLAY-DEBUG] GetAllBooks returned {0} books", allBooks.Count);
            }

            // NEW DUAL-INSTANCE LOGIC: Filter by MediaType enum if specified
            if (!string.IsNullOrEmpty(mediaType))
            {
                var targetMediaType = mediaType.ToLower() == "ebook" ? BookMediaType.Ebook : BookMediaType.Audiobook;
                allBooks = allBooks.Where(b => b.MediaType == targetMediaType).ToList();
                _logger.Debug("[DISPLAY-DEBUG] Filtered to {0} books with MediaType={1}", allBooks.Count, targetMediaType);

                // CRITICAL: Check if root folder exists for this media type
                // If no root folder, return empty list (GUI shows nothing)
                var rootFolders = _rootFolderService.All();
                var hasRootFolderForType = targetMediaType == BookMediaType.Audiobook
                    ? rootFolders.Any(rf => rf.FolderType == FolderType.Audiobook || rf.FolderType == FolderType.Mixed)
                    : rootFolders.Any(rf => rf.FolderType == FolderType.Ebook || rf.FolderType == FolderType.Mixed);

                if (!hasRootFolderForType)
                {
                    _logger.Debug("[DISPLAY-DEBUG] No root folder configured for {0}, returning empty list", mediaType);
                    return new List<Book>();
                }
            }

            // Bulk API/index sync path is intentionally lean: one monitored edition per book, no files.
            // Author-scoped detail paths stay rich because the UI needs all editions/files there.
            if (allBooks.Any())
            {
                var bookIds = allBooks.Select(b => b.Id).Distinct().ToList();
                var bookIdSet = bookIds.ToHashSet();
                var allEditions = authorId.HasValue
                    ? _editionService.GetEditionsByBook(bookIds)
                    : _editionService.GetAllMonitoredEditions().Where(e => e != null && bookIdSet.Contains(e.BookId)).ToList();
                var editionsByBook = allEditions.GroupBy(e => e.BookId).ToDictionary(g => g.Key, g => g.ToList());

                var filesByEdition = new Dictionary<int, List<BookFile>>();
                if (authorId.HasValue)
                {
                    // Batch load files for all books in a single query (avoids N+1 per-edition loads).
                    var allBookFiles = _mediaFileService.GetFilesByBooks(bookIds) ?? new List<BookFile>();
                    filesByEdition = allBookFiles.GroupBy(f => f.EditionId).ToDictionary(g => g.Key, g => g.ToList());
                }

                // Map files to books through their editions
                foreach (var book in allBooks)
                {
                    book.BookFiles = new List<BookFile>();

                    if (editionsByBook.TryGetValue(book.Id, out var bookEditions))
                    {
                        book.Editions = bookEditions;

                        foreach (var edition in bookEditions)
                        {
                            if (filesByEdition.TryGetValue(edition.Id, out var editionFiles))
                            {
                                edition.BookFiles = editionFiles;
                                book.BookFiles.AddRange(editionFiles);
                            }
                            else
                            {
                                edition.BookFiles = null;
                            }
                        }
                    }
                    else
                    {
                        book.Editions = new List<Edition>();
                    }
                }
            }
            // With dual-instance architecture, just return the filtered books
            _logger.Debug("[DISPLAY-DEBUG] Returning {0} books for display", allBooks.Count);
            return allBooks;
        }

        public List<Book> GetBooksByBaseId(string baseBookId)
        {
            // Find all books that share any provider ID
            return _bookRepository.All()
                .Where(b => BookEditionIdentity.HasCanonicalWorkProviderId(b, baseBookId) ||
                           BookEditionIdentity.HasCanonicalEditionProviderId(b, baseBookId) ||
                           BookIdentity.GetProviderIdentityTokens(b).Any(id => id.Equals(baseBookId, StringComparison.OrdinalIgnoreCase) ||
                                                                               ProviderIdHelper.StripPrefix(id).Equals(baseBookId, StringComparison.OrdinalIgnoreCase)) ||
                           b.Id.ToString() == baseBookId)
                .ToList();
        }

        public Book AddWantedEdition(int bookId, int editionId)
        {
            var baseBook = GetBook(bookId);
            if (baseBook == null)
            {
                throw new ArgumentException($"Book with ID {bookId} not found");
            }

            if (baseBook.MediaType != BookMediaType.Audiobook)
            {
                throw new InvalidOperationException("Wanted narrator editions can only be created for audiobook books");
            }

            var selectedEdition = _editionService.GetEdition(editionId);
            if (selectedEdition == null)
            {
                throw new ArgumentException($"Edition with ID {editionId} not found");
            }

            if (selectedEdition.BookId != baseBook.Id)
            {
                throw new InvalidOperationException($"Edition {editionId} does not belong to book {bookId}");
            }

            // If this book has no files, it is already a "missing" instance; just pin the selected edition here.
            var existingFiles = _mediaFileService.GetFilesByBook(baseBook.Id);
            if (!existingFiles.Any())
            {
                var editions = _editionService.GetEditionsByBook(baseBook.Id);

                foreach (var e in editions)
                {
                    var isSelected = e.Id == editionId;
                    e.Monitored = isSelected;
                    if (isSelected)
                    {
                        e.ManualAdd = true;
                    }
                    else
                    {
                        e.ManualAdd = false;
                    }
                }

                baseBook.AnyEditionOk = false;
                baseBook.AudiobookMonitored = true;
                baseBook.EbookMonitored = false;
                baseBook.Monitored = true;

                UpdateBook(baseBook);
                _editionService.UpdateMany(editions);

                return GetBook(baseBook.Id);
            }

            static string GetEditionKey(Edition e)
            {
                if (e == null)
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(e.HardcoverEditionId))
                {
                    return $"hc:{e.HardcoverEditionId}".Trim();
                }

                if (e.GoodreadsEditionId.HasValue && e.GoodreadsEditionId.Value > 0)
                {
                    return $"gr:{e.GoodreadsEditionId.Value}";
                }

                if (!string.IsNullOrWhiteSpace(e.OpenLibraryEditionId))
                {
                    return $"ol:{e.OpenLibraryEditionId}".Trim();
                }

                if (!string.IsNullOrWhiteSpace(e.AudibleASIN))
                {
                    return $"az:{e.AudibleASIN}".Trim().ToUpperInvariant();
                }

                    if (!string.IsNullOrWhiteSpace(e.Asin))
                    {
                        return $"az:{e.Asin}".Trim().ToUpperInvariant();
                    }

                if (!string.IsNullOrWhiteSpace(e.Isbn13))
                {
                    return $"isbn13:{e.Isbn13}".Trim();
                }

                if (!string.IsNullOrWhiteSpace(e.Isbn10))
                {
                    return $"isbn10:{e.Isbn10}".Trim();
                }

                if (!string.IsNullOrWhiteSpace(e.ForeignEditionId))
                {
                    return $"foreign:{e.ForeignEditionId}".Trim();
                }

                return null;
            }

            // If a matching wanted instance already exists, return it (don’t create duplicates).
            var selectedEditionKey = GetEditionKey(selectedEdition);
            if (baseBook.AuthorId > 0 && !string.IsNullOrWhiteSpace(selectedEditionKey))
            {
                var authorBooks = _bookRepository.GetBooksByAuthorId(baseBook.AuthorId);
                var wantedCandidates = authorBooks
                    .Where(b => b.MediaType == BookMediaType.Audiobook)
                    .Where(b => b.AddOptions?.AddType == BookAddType.Manual)
                    .Where(b => IsSameProviderBackedBook(baseBook, b))
                    .ToList();

                if (wantedCandidates.Any())
                {
                    var candidateIds = wantedCandidates.Select(b => b.Id).ToList();
                    var candidateEditions = _editionService.GetEditionsByBook(candidateIds);

                    foreach (var candidate in wantedCandidates)
                    {
                        var candidateManual = candidateEditions.FirstOrDefault(e => e.BookId == candidate.Id && e.ManualAdd)
                                           ?? candidateEditions.FirstOrDefault(e => e.BookId == candidate.Id && e.Monitored);

                        if (candidateManual == null)
                        {
                            continue;
                        }

                        var candidateKey = GetEditionKey(candidateManual);
                        if (candidateKey == null)
                        {
                            continue;
                        }

                        if (string.Equals(candidateKey, selectedEditionKey, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!candidate.AudiobookMonitored)
                            {
                                candidate.AudiobookMonitored = true;
                                candidate.EbookMonitored = false;
                                candidate.Monitored = true;
                                candidate.AnyEditionOk = false;
                                candidate.AddOptions ??= new AddBookOptions();
                                candidate.AddOptions.AddType = BookAddType.Manual;
                                UpdateBook(candidate);
                            }

                            return GetBook(candidate.Id);
                        }
                    }
                }
            }

            var author = baseBook.Author ?? (baseBook.AuthorId > 0 ? _authorService.GetAuthor(baseBook.AuthorId) : null);
            if (author != null)
            {
                baseBook.Author = author;
            }

            var baseSlug = baseBook.TitleSlug;
            if (string.IsNullOrWhiteSpace(baseSlug))
            {
                baseSlug = baseBook.Title?.ToLowerInvariant().Replace(" ", "-") ?? $"book-{DateTime.UtcNow.Ticks}";
            }

            var wantedBook = new Book
            {
                // Copy metadata/provider IDs from base book so searches and matching stay grounded.
                TitleSlug = $"{baseSlug}_wanted_{editionId}",
                Title = baseBook.Title,
                CleanTitle = baseBook.CleanTitle,
                Overview = baseBook.Overview,
                AuthorId = baseBook.AuthorId,
                Author = author ?? baseBook.Author,
                BaseBookId = baseBook.BaseBookId,
                HardcoverBookId = baseBook.HardcoverBookId,
                GoodreadsWorkId = baseBook.GoodreadsWorkId,
                OpenLibraryWorkId = baseBook.OpenLibraryWorkId,

                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                EbookMonitored = false,
                Monitored = true,
                AnyEditionOk = false,
                Added = DateTime.UtcNow,
                ReleaseDate = baseBook.ReleaseDate,
                Links = new List<Links>(baseBook.Links ?? new List<Links>()),
                Genres = new List<string>(baseBook.Genres ?? new List<string>()),
                Ratings = baseBook.Ratings,
                Images = baseBook.Images != null ? new List<MediaCover.MediaCover>(baseBook.Images) : new List<MediaCover.MediaCover>(),
                ProviderUrls = baseBook.ProviderUrls != null ? new ProviderUrlMap(baseBook.ProviderUrls) : new ProviderUrlMap(),

                // Mark as manual so refresh logic doesn't delete it; searching is handled by the caller.
                AddOptions = new AddBookOptions { AddType = BookAddType.Manual, SearchForNewBook = false },
                Editions = new List<Edition>()
            };

            BookEditionIdentity.ClearBookLevelEditionIdentity(wantedBook);

                // Clone audiobook editions so matching can still discriminate and manual edition protection can work.
                // Audiobook is defined strictly as ReadingFormatId == 2 (no string heuristics).
                bool IsAudiobookEdition(Edition e) => e?.ReadingFormatId == 2;

            var sourceEditions = baseBook.Editions ?? _editionService.GetEditionsByBook(baseBook.Id);
            var clonedEditions = sourceEditions
                .Where(e => e.Id == editionId || IsAudiobookEdition(e))
                .Select(e =>
                {
                    var isSelected = e.Id == editionId;
                    return new Edition
                    {
                        Id = 0,
                        BookId = 0,
                        ForeignEditionId = BookEditionIdentity.GetTrustedForeignEditionId(e),
                        TitleSlug = e.TitleSlug,
                        Isbn13 = e.Isbn13,
                        Isbn10 = e.Isbn10,
                        Asin = e.Asin,
                        Title = e.Title,
                        Subtitle = e.Subtitle,
                        MatchingTitle = e.MatchingTitle,
                        Language = e.Language,
                        Overview = e.Overview,
                        Format = e.Format,
                        IsEbook = e.IsEbook,
                        Disambiguation = e.Disambiguation,
                        Publisher = e.Publisher,
                        PageCount = e.PageCount,
                        ReleaseDate = e.ReleaseDate,
                        Images = e.Images != null ? new List<MediaCover.MediaCover>(e.Images) : new List<MediaCover.MediaCover>(),
                        Links = e.Links != null ? new List<Links>(e.Links) : new List<Links>(),
                        Ratings = e.Ratings,
                        GoodreadsEditionId = e.GoodreadsEditionId,
                        HardcoverEditionId = e.HardcoverEditionId,
                        OpenLibraryEditionId = e.OpenLibraryEditionId,
                        ReadingFormatId = e.ReadingFormatId,
                        EditionFormat = e.EditionFormat,
                        EditionInfo = e.EditionInfo,
                        DurationSeconds = e.DurationSeconds,
                        ChapterCount = e.ChapterCount,
                        HasChapters = e.HasChapters,
                        Chapters = e.Chapters != null ? e.Chapters.Select(c => new EditionChapter
                        {
                            Title = c?.Title,
                            StartOffsetMs = c?.StartOffsetMs ?? 0,
                            StartOffsetSec = c?.StartOffsetSec ?? 0,
                            LengthMs = c?.LengthMs ?? 0
                        }).ToList() : new List<EditionChapter>(),
                        IsGraphicAudio = e.IsGraphicAudio,
                        AudioProductionType = e.AudioProductionType,
                        Narrator = e.Narrator,
                        AudibleASIN = e.AudibleASIN,
                        GoogleBooksEditionId = e.GoogleBooksEditionId,
                        ReviewCount = e.ReviewCount,
                        NarratorNames = e.NarratorNames != null ? new List<string>(e.NarratorNames) : new List<string>(),
                        ProviderUrls = e.ProviderUrls != null ? new ProviderUrlMap(e.ProviderUrls) : new ProviderUrlMap(),
                        LastUpdated = e.LastUpdated,
                        Monitored = isSelected,
                        ManualAdd = isSelected
                    };
                })
                .ToList();

            if (!clonedEditions.Any())
            {
                throw new InvalidOperationException("No audiobook editions were available to create a wanted narrator variant");
            }

            wantedBook.Editions = clonedEditions;

            // Create the wanted book without triggering a full author refresh/rescan; callers can queue a targeted search.
            AddBook(wantedBook, doRefresh: false);

            // Ensure the selected edition is treated as a manual pin, so refresh/matching won't override it.
            // AddBook sets a monitored edition automatically, but does not mark it as a manual selection.
            try
            {
                var persistedEditions = wantedBook.Editions ?? _editionService.GetEditionsByBook(wantedBook.Id);
                var persistedSelected = persistedEditions?.FirstOrDefault(e =>
                    !string.IsNullOrWhiteSpace(e.ForeignEditionId) &&
                    !string.IsNullOrWhiteSpace(selectedEdition.ForeignEditionId) &&
                    string.Equals(e.ForeignEditionId, selectedEdition.ForeignEditionId, StringComparison.OrdinalIgnoreCase))
                    ?? SelectMonitoredEditionOrNull(persistedEditions);

                if (persistedSelected != null)
                {
                    _editionService.SetMonitored(persistedSelected, isManualSelection: true);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to pin wanted edition for wanted book {0}", wantedBook.Id);
            }

            return GetBook(wantedBook.Id);
        }

        private Edition SelectMonitoredEditionForInsert(IEnumerable<Edition> editions, BookMediaType mediaType)
        {
            return SelectMonitoredEditionOrNull(editions)
                   ?? _editionSelector.SelectBestEdition(editions, mediaType);
        }

        private Edition SelectMonitoredEditionOrNull(IEnumerable<Edition> editions)
        {
            return editions?
                .Where(e => e != null && e.Monitored)
                .OrderBy(e => e.Id)
                .FirstOrDefault();
        }

        private void LoadAuthorRelationships(List<Book> books)
        {
            // Group books by AuthorId to minimize database queries
            var booksByAuthor = books.Where(b => b.AuthorId > 0)
                                             .GroupBy(b => b.AuthorId);

            foreach (var group in booksByAuthor)
            {
                var author = _authorService.GetAuthor(group.Key);
                if (author != null)
                {
                    foreach (var book in group)
                    {
                        book.Author = author;
                    }
                }
            }
        }

        private Book HydrateLookupBook(Book book)
        {
            if (book == null)
            {
                return null;
            }

            HydrateLookupBooks(new List<Book> { book });
            return book;
        }

        private List<Book> HydrateLookupBooks(List<Book> books)
        {
            if (books == null || books.Count == 0)
            {
                return books ?? new List<Book>();
            }

            var editionsByBookId = _editionService.GetEditionsByBook(books.Select(b => b.Id))
                                                  .GroupBy(e => e.BookId)
                                                  .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var book in books)
            {
                book.Editions = editionsByBookId.TryGetValue(book.Id, out var editions)
                    ? editions
                    : new List<Edition>();
            }

            var booksByAuthor = books.Where(b => b.AuthorId > 0)
                                     .GroupBy(b => b.AuthorId);

            foreach (var group in booksByAuthor)
            {
                Author author = null;

                try
                {
                    author = _authorService.GetAuthor(group.Key);
                }
                catch (ModelNotFoundException)
                {
                    // Lookups should remain tolerant if a book row survives an author delete/merge race.
                }

                if (author == null)
                {
                    continue;
                }

                foreach (var book in group)
                {
                    book.Author = author;
                }
            }

            return books;
        }

        private static bool ShouldPreferEditionLookup(string provider, string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            return provider switch
            {
                "gb" => true,
                "az" => true,
                "hc" => providerId.IndexOf("edition:", StringComparison.OrdinalIgnoreCase) >= 0,
                "ol" => providerId.EndsWith("M", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private Book FindBookByEditionProviderId(string provider, string rawProviderId, BookMediaType mediaType)
        {
            return FindBooksByEditionProviderId(provider, rawProviderId, mediaType)
                .OrderBy(book => book.Id)
                .FirstOrDefault();
        }

        private List<Book> FindBooksByEditionProviderId(string provider, string rawProviderId, BookMediaType mediaType)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(rawProviderId))
            {
                return new List<Book>();
            }

            var bookIds = (_editionService.GetEditionsByProviderAndId(provider, rawProviderId) ?? new List<Edition>())
                .Where(edition => edition?.BookId > 0)
                .Select(edition => edition.BookId)
                .Distinct()
                .ToList();

            if (!bookIds.Any())
            {
                return new List<Book>();
            }

            return HydrateLookupBooks(_bookRepository.Get(bookIds)
                .Where(book => book != null && book.MediaType == mediaType)
                .OrderBy(book => book.Id)
                .ToList());
        }

        private void LoadSeriesLinks(List<Book> books)
        {
            if (!books.Any())
            {
                return;
            }

            var bookIds = books.Select(b => b.Id).ToList();
            var seriesLinks = _seriesBookLinkRepository.GetLinksByBook(bookIds);
            var linksByBookId = seriesLinks.GroupBy(l => l.BookId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var book in books)
            {
                if (linksByBookId.TryGetValue(book.Id, out var links))
                {
                    book.SeriesLinks = links;
                }
                else
                {
                    book.SeriesLinks = new List<SeriesBookLink>();
                }
            }
        }

        private double SeriesAwareTitleMatch(string bookTitle, string searchTitle)
        {
            if (string.IsNullOrWhiteSpace(bookTitle) || string.IsNullOrWhiteSpace(searchTitle))
            {
                return 0.0;
            }

            var bookMain = ExtractMainTitle(bookTitle);
            var searchMain = ExtractMainTitle(searchTitle);

            if (bookMain.Equals(searchMain, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }

            if (bookMain.Contains(searchMain, StringComparison.OrdinalIgnoreCase) ||
                searchMain.Contains(bookMain, StringComparison.OrdinalIgnoreCase))
            {
                var lengthRatio = (double)Math.Min(bookMain.Length, searchMain.Length) / Math.Max(bookMain.Length, searchMain.Length);
                return Math.Max(0.8, lengthRatio);
            }

            return 0.0;
        }

        private string ExtractMainTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            // Remove series patterns
            var patterns = new[]
            {
                @"\s*[:\-]\s*(?:Book|Series|Part|Vol|Volume|#)\s*\d+.*$",
                @"\s*\((?:Book|Series|Part|Vol|Volume|#)\s*\d+.*?\)",
                @"\s*(?:Book|Series|Part|Vol|Volume|#)\s*\d+.*$",
                @"\s*[:\-]\s*[^:\-]+(?:Series|Trilogy|Saga|Chronicles|Cycle)\s*.*$"
            };

            var result = title;
            foreach (var pattern in patterns)
            {
                result = System.Text.RegularExpressions.Regex.Replace(result, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            // Also remove common audiobook annotations
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\((?:unabridged|abridged|audiobook|audio\s*book)\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return result.Trim();
        }

        public bool ShouldSearchForMediaType(Book book, string mediaType)
        {
            return book.IsMonitoredForMediaType(mediaType);
        }

            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null)
            {
                return _bookRepository.GetBookBuckets(sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, monitored, missing, wanted);
            }

            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null)
            {
                return GetBookBuckets(sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, null, null, null);
            }

            public List<int> GetBookIds(bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null, bool? monitored = null, bool? missing = null, bool? wanted = null)
            {
                return _bookRepository.GetBookIds(includeUnmonitored, mediaType, downloaded, monitored, missing, wanted);
            }

                public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null)
                {
                    var books = _bookRepository.GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, monitored, missing, wanted);

                    // Load author relationships for the returned books
                    if (books.Records != null && books.Records.Any())
                    {
                        LoadAuthorRelationships(books.Records);
                        LoadSeriesLinks(books.Records);

                        // Paged index responses are lean: the monitored edition is the display truth.
                        var bookIds = books.Records.Select(b => b.Id).ToList();
                        var bookIdSet = bookIds.ToHashSet();
                        var allEditions = _editionService.GetEditionsByBook(bookIds)
                            .Where(e => e != null && bookIdSet.Contains(e.BookId))
                            .Where(e => e.Monitored)
                            .ToList();
                        var editionsByBook = allEditions.GroupBy(e => e.BookId).ToDictionary(g => g.Key, g => g.ToList());

                        foreach (var book in books.Records)
                        {
                            book.Editions = editionsByBook.TryGetValue(book.Id, out var eds) && eds != null
                                ? eds
                                : new List<Edition>();

                        book.BookFiles = new List<BookFile>();

                            foreach (var edition in book.Editions)
                            {
                                edition.BookFiles = null;
                            }
                        }
                    }

	                    return books;
	                }

                public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null)
                {
                    return GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, null, null, null);
                }
	    }
	}
