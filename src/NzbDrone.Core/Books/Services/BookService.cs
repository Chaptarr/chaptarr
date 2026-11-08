using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
// using NzbDrone.Core.MediaFiles.BookImport.Identification; // Disabled - old identification system
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public sealed class BookTitleSlugIndex
    {
        private readonly Dictionary<int, (BookMediaType MediaType, string Slug)> _slugsByBookId = new();
        private readonly Dictionary<(BookMediaType MediaType, string Slug), int> _counts = new();
        private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

        public int AuthorId { get; }

        public BookTitleSlugIndex(int authorId, IEnumerable<Book> books, IEnumerable<Book> incoming = null)
        {
            AuthorId = authorId;
            foreach (var book in books ?? Enumerable.Empty<Book>())
            {
                Add(book);
                _pending.Add(BaseSlug(book));
            }

            foreach (var book in incoming ?? Enumerable.Empty<Book>()) _pending.Add(BaseSlug(book));
        }

        internal static string BaseSlug(Book book) =>
            (string.IsNullOrEmpty(book.TitleSlug) ? book.Title?.Replace(" ", "-") ?? $"book-{book.Id}" : book.TitleSlug).ToLowerInvariant();

        internal int LoadConflicts(IEnumerable<Book> books, Func<IEnumerable<string>, List<Book>> load)
        {
            foreach (var book in books) _pending.Add(BaseSlug(book));
            _pending.ExceptWith(_loaded);
            if (_pending.Count == 0) return 0;
            var conflicts = load(_pending);
            foreach (var book in conflicts) Add(book);
            _loaded.UnionWith(_pending);
            _pending.Clear();
            return conflicts.Count;
        }

        public bool Contains(BookMediaType mediaType, string slug) => _counts.ContainsKey((mediaType, slug.ToLowerInvariant()));

        public void Remove(int bookId)
        {
            if (bookId <= 0 || !_slugsByBookId.Remove(bookId, out var key)) return;
            if (_counts[key] == 1) _counts.Remove(key);
            else _counts[key]--;
        }

        public void Add(Book book)
        {
            if (book == null || string.IsNullOrEmpty(book.TitleSlug)) return;
            Remove(book.Id);
            var key = (book.MediaType, book.TitleSlug.ToLowerInvariant());
            if (book.Id > 0) _slugsByBookId[book.Id] = key;
            _counts.TryGetValue(key, out var count);
            _counts[key] = count + 1;
        }
    }

    public interface IBookService
    {
        void SyncMonitoredAcrossFormats(List<int> authorIds) => throw new NotSupportedException();
        Book GetBook(int bookId);
        List<Book> GetBooks(IEnumerable<int> bookIds);
        List<Book> GetExistingBooks(IEnumerable<int> bookIds) => GetBooks(bookIds);
        List<Book> GetBooksForLookup(IEnumerable<int> bookIds) => throw new NotImplementedException();
        List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false);
        List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds);
        List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds);
        List<Book> GetBooksByAuthorId(int authorId);
        IReadOnlySet<BookMediaType> GetMediaTypesByAuthor(int authorId) => throw new NotImplementedException();
        List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds);
        List<Book> GetBooksByFileIds(IEnumerable<int> fileIds);
        Book AddBook(Book newBook, bool doRefresh = true);
        List<Book> FindBySlug(string titleSlug);
            Book FindByGoodreadsId(string goodreadsId);
            Book FindByProviderId(string provider, string providerId);
            Book FindByProviderId(string provider, string providerId, BookMediaType mediaType);
            List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType);
        Dictionary<(string Scope, string Provider, string Id), List<int>> FindBookIdsByProviderIds(IEnumerable<(string Scope, string Provider, string Id)> lookups, Action<(string Scope, string Provider, string Id)> onIncomplete = null) => throw new NotImplementedException();
            List<Book> FindAllByWorkProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            Book FindByISBN(string isbn);
            Book FindByASIN(string asin);
        void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false);
        List<Book> GetAllBooks();
        List<Book> GetImportListBookIdentities();
        Book UpdateBook(Book book);
        Book UpdateBook(Book book, List<Edition> editionUpdates, string monitoredMediaType = null, Action<IDbConnection, IDbTransaction> writeRelated = null);
        void UpdateManyWithLifecycle(List<Book> books)
        {
            foreach (var book in books ?? new List<Book>())
            {
                UpdateBook(book);
            }
        }
        void SetBookMonitored(int bookId, bool monitored);
        void SetMonitored(IEnumerable<int> ids, bool monitored);
        void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored);
        void MonitorRequestedBooks(IEnumerable<int> ids, string mediaType) => throw new NotImplementedException();
        void UpdateLastSearchTime(List<Book> books);
        List<BookSearchTarget> GetMissingBookSearchTargets(BookMediaType? mediaType, int? authorId) => throw new NotImplementedException();
        PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec);
        List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored);
        List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored);
        void InsertMany(List<Book> books);
        void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction);
        void UpdateMany(List<Book> books);
        void UpdateMany(List<Book> books, BookTitleSlugIndex titleSlugs) => UpdateMany(books);
        void ReassignAuthor(Book book, Author author) { throw new NotImplementedException(); }
        void ReassignAuthor(List<Book> books, Author author) { throw new NotImplementedException(); }
        void ReassignAuthor(Book book, int authorId) { throw new NotImplementedException(); }
        void ReassignAuthor(List<Book> books, int authorId) { throw new NotImplementedException(); }
        void DeleteMany(List<Book> books, bool filesAlreadyDeleted = false, List<BookFile> deletedBookFiles = null);
        void SetAddOptions(IEnumerable<Book> books);
        List<Book> GetAuthorBooksWithFiles(Author author);
            List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null);
            List<Book> GetBooksByBaseId(string baseBookId);
        // Add a separately pinned audiobook copy; return an exact existing copy without changing it.
        (Book Book, bool ExistingCopy) AddWantedEditionCopy(int bookId, int editionId);
        // Fulfill a batch of edition requests, reusing eligible rows and updating their selection.
        Dictionary<int, Book> AddWantedEditions(List<Edition> requestedEditions, Action<Edition, Exception> onFailure = null) => throw new NotImplementedException();
            bool ShouldSearchForMediaType(Book book, string mediaType);
            BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null);
            BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null, bool? library = null) => GetBookBuckets(sortKey, sortDirection, includeUnmonitored, mediaType, downloaded);
            PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null);
            PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null, bool? library = null) => GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded);
            List<int> GetBookIds(bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null, bool? monitored = null, bool? missing = null, bool? wanted = null, bool? library = null) => throw new NotImplementedException();
            List<int> GetLibraryBookIds(IEnumerable<int> bookIds = null) => throw new NotImplementedException();
        }

    public interface IWantedEditionCopyFinder
    {
        (List<Book> Books, Dictionary<int, Book> ExistingCopies, int CandidateCount, int EditionCount, int AuthorRowCount, int FileCount) FindWantedEditionCopies(IEnumerable<int> bookIds);
    }

    public class BookService : IBookService, IWantedEditionCopyFinder,
                                IHandle<AuthorDeletedEvent>
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
        private readonly Lazy<IDeleteMediaFiles> _deleteMediaFiles;
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
                           IProviderAliasService providerAliasService = null,
                           Lazy<IDeleteMediaFiles> deleteMediaFiles = null)
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
            _deleteMediaFiles = deleteMediaFiles;
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

            WriteWithUniqueTitleSlugs(new List<Book> { newBook }, () => _bookRepository.Upsert(newBook));

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


            if (newBook.IsMonitored())
            {
                MonitorRequestedBooks(new[] { newBook.Id }, newBook.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook");
            }

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

            var deletedBooks = applyToBothFormats && book.AuthorId > 0
                ? GetBooksByAuthor(book.AuthorId)
                    .Where(candidate => candidate != null && candidate.Id != book.Id && WorkIdMatcher.WorkProviderIdMatches(book, candidate))
                    .OrderBy(candidate => candidate.Id).ToList()
                : new List<Book>();
            deletedBooks.Add(book);
            HydrateBooksForDeleteEvent(deletedBooks);
            var deletedFiles = deleteFiles
                ? (_deleteMediaFiles?.Value ?? throw new InvalidOperationException("Book file deletion service is unavailable"))
                    .DeleteBookFiles(deletedBooks)
                : new List<BookFile>();

            _bookRepository.DeleteManyAtomic(deletedBooks.Select(item => item.Id), deletedFiles.Select(file => file.Id));

            foreach (var deletedBook in deletedBooks)
            {
                _eventAggregator.PublishEvent(new BookDeletedEvent(deletedBook, deleteFiles,
                    deletedBook.Id == bookId && addImportListExclusion,
                    deletedBook.Id == bookId && applyToBothFormats, deletedBooks, filesAlreadyDeleted: deleteFiles));
            }

            if (deleteFiles)
            {
                _eventAggregator.PublishEvent(new DeleteCompletedEvent());
            }
        }

        private void HydrateBooksForDeleteEvent(List<Book> books, List<BookFile> deletedBookFiles = null)
        {
            var booksToHydrate = (books ?? new List<Book>())
                .Where(book => book != null && book.Id > 0)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            if (!booksToHydrate.Any())
            {
                return;
            }

            foreach (var authorBooks in booksToHydrate.Where(book => book.AuthorId > 0).GroupBy(book => book.AuthorId))
            {
                var author = authorBooks
                    .Select(book => book.Author)
                    .FirstOrDefault(candidate => candidate?.Id == authorBooks.Key);

                if (author == null && _authorService != null)
                {
                    try
                    {
                        author = _authorService.GetAuthor(authorBooks.Key);
                    }
                    catch (ModelNotFoundException)
                    {
                        // Author may have been deleted/merged while commands are still running.
                    }
                }

                if (author != null)
                {
                    author = author.ShallowClone();
                    foreach (var book in authorBooks)
                    {
                        book.Author = author;
                    }
                }
            }

            var bookIds = booksToHydrate.Select(book => book.Id).ToList();
            var editions = _editionService?.GetEditionsByBook(bookIds) ?? new List<Edition>();
            var bookFiles = deletedBookFiles ?? _mediaFileService?.GetFilesByBooks(bookIds) ?? new List<BookFile>();
            var editionsByBook = editions
                .Where(edition => edition != null)
                .GroupBy(edition => edition.BookId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var filesByEdition = bookFiles
                .Where(file => file != null)
                .GroupBy(file => file.EditionId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var book in booksToHydrate)
            {
                book.Editions = editionsByBook.TryGetValue(book.Id, out var bookEditions)
                    ? bookEditions
                    : new List<Edition>();
                book.BookFiles = book.Editions
                    .SelectMany(edition => filesByEdition.TryGetValue(edition.Id, out var editionFiles)
                        ? editionFiles
                        : Enumerable.Empty<BookFile>())
                    .DistinctBy(file => file.Id)
                    .ToList();

                foreach (var edition in book.Editions)
                {
                    edition.BookFiles = filesByEdition.TryGetValue(edition.Id, out var editionFiles)
                        ? editionFiles
                        : new List<BookFile>();
                }
            }
        }

        public List<Book> FindBySlug(string titleSlug)
        {
            return _bookRepository.FindBySlug(titleSlug);
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

        public Dictionary<(string Scope, string Provider, string Id), List<int>> FindBookIdsByProviderIds(IEnumerable<(string Scope, string Provider, string Id)> lookups, Action<(string Scope, string Provider, string Id)> onIncomplete = null)
        {
            var result = lookups.Distinct().ToDictionary(key => key, _ => new List<int>());
            foreach (var group in result.Keys.GroupBy(key => (key.Scope, key.Provider)))
            {
                var keys = group.ToList();
                var ids = keys.Select(key => key.Id).ToArray();
                try
                {
                    if (group.Key.Scope == "work")
                    {
                        var workIds = ids.Select(id => group.Key.Provider == "az" ? ProviderIdHelper.StripPrefix(id) : id).ToArray();
                        var matches = _bookRepository.FindIdsByProviderIds(group.Key.Provider, workIds);
                        foreach (var key in keys)
                        {
                            var workKey = key.Provider == "az" ? ProviderIdHelper.StripPrefix(key.Id) : key.Id;
                            result[key].AddRange(matches.GetValueOrDefault(workKey) ?? new List<int>());
                        }
                    }
                    else if (group.Key.Scope == "edition")
                    {
                        var matches = _editionService.GetEditionsByProviderIds(group.Key.Provider, ids.Select(ProviderIdHelper.StripPrefix));
                        foreach (var key in keys)
                        {
                            result[key].AddRange((matches.GetValueOrDefault(ProviderIdHelper.StripPrefix(key.Id)) ?? new List<Edition>()).Select(edition => edition.BookId));
                        }
                    }
                }
                catch (Exception exception)
                {
                    foreach (var key in keys) onIncomplete?.Invoke(key);
                    _logger.Warn(exception, "Local {0} lookup failed for provider {1}; other provider identities will still be checked", group.Key.Scope, group.Key.Provider);
                }

            }

            if (_providerAliasService != null)
            {
                foreach (var group in result.Keys.Where(key => key.Scope is "work" or "edition").GroupBy(key => key.Scope))
                {
                    try
                    {
                        var aliases = _providerAliasService.FindBookAliases(group.Key, group.Select(key => key.Id))
                            .ToLookup(alias => (alias.Provider.ToLowerInvariant(), alias.NormalizedProviderId.ToLowerInvariant()));
                        foreach (var key in group)
                        {
                            foreach (var alias in _providerAliasService.NormalizeProviderIds(new[] { key.Id }))
                            {
                                result[key].AddRange(aliases[(alias.Provider.ToLowerInvariant(), alias.NormalizedProviderId.ToLowerInvariant())].Select(match => match.EntityId));
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        foreach (var key in group) onIncomplete?.Invoke(key);
                        _logger.Warn(exception, "Local {0} aliases failed; column matches remain available", group.Key);
                    }
                }
            }

            foreach (var key in result.Keys.ToList())
            {
                result[key] = result[key].Where(id => id > 0).Distinct().ToList();
            }

            return result;
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
                var ids = provider is "hc" or "gr" or "ol"
                    ? _bookRepository.FindIdsByProviderIds(provider, new[] { normalizedProviderId })[normalizedProviderId]
                    : new List<int>();
                if (_providerAliasService != null)
                {
                    ids.AddRange(_providerAliasService.FindBookIds("work", new[] { normalizedProviderId }));
                }

                return HydrateLookupBooks(_bookRepository.FindExisting(ids.Distinct().ToList())
                    .Where(book => book.MediaType == mediaType).OrderBy(book => book.Id).ToList());
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

        public List<Book> GetImportListBookIdentities() => _bookRepository.GetImportListBookIdentities();

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

        public List<Book> GetBooksForLookup(IEnumerable<int> bookIds)
        {
            var books = _bookRepository.FindExisting(bookIds).ToList();
            if (books.Count == 0)
            {
                return books;
            }

            var ids = books.Select(book => book.Id).ToList();
            var editions = _editionService.GetEditionRowsByBook(ids).ToLookup(edition => edition.BookId);
            var files = _mediaFileService.GetFilesByBooks(ids).ToLookup(file => file.Edition.BookId);
            foreach (var book in books)
            {
                book.Editions = editions[book.Id].ToList();
                book.BookFiles = files[book.Id].ToList();
            }

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

        public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false)
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
                var allEditions = onlyMonitoredEditions
                    ? _editionService.GetEditionsByAuthor(authorId, onlyMonitored: true)
                    : _editionService.GetEditionsByBook(bookIds);
                var editionsByBook = allEditions.GroupBy(e => e.BookId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var book in books)
                {
                    book.Editions = editionsByBook.TryGetValue(book.Id, out var bookEditions)
                        ? bookEditions : new List<Edition>();
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

        public IReadOnlySet<BookMediaType> GetMediaTypesByAuthor(int authorId)
        {
            return _bookRepository.GetMediaTypesByAuthor(authorId);
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

        private void EnsureUniqueTitleSlugs(List<Book> books, BookTitleSlugIndex titleSlugs = null, IDbConnection connection = null, IDbTransaction transaction = null)
        {
            if (books.Count == 0) return;
            if (titleSlugs != null && books.Any(book => book.AuthorId != titleSlugs.AuthorId))
                throw new ArgumentException("Title slug index belongs to a different author", nameof(titleSlugs));

            var timer = Stopwatch.StartNew();
            titleSlugs ??= new BookTitleSlugIndex(0, Array.Empty<Book>());
            var loaded = titleSlugs.LoadConflicts(books, slugs => _bookRepository.FindTitleSlugConflicts(slugs, connection, transaction));
            foreach (var book in books) titleSlugs.Remove(book.Id);
            foreach (var book in books)
            {
                var baseSlug = BookTitleSlugIndex.BaseSlug(book);
                var finalSlug = TitleSlugAllocator.FindAvailable(baseSlug, slug => titleSlugs.Contains(book.MediaType, slug));
                if (finalSlug != book.TitleSlug)
                    _logger.Debug("[BOOK-SLUG] Book {0} ({1}): '{2}' -> '{3}'", book.Id, book.MediaType, book.TitleSlug, finalSlug);
                book.TitleSlug = finalSlug;
                titleSlugs.Add(book);
            }
            _logger.Debug("[BOOK-SLUG] Checked {0} rows, loaded {1} conflicting rows in {2} ms", books.Count, loaded, timer.ElapsedMilliseconds);
        }

        private void WriteWithUniqueTitleSlugs(List<Book> books, Action write)
        {
            var originals = books.Select(book => (Book: book, book.Id, book.TitleSlug)).ToArray();
            EnsureUniqueTitleSlugs(books);
            try
            {
                write();
            }
            catch (Exception ex) when (IsBookTitleSlugConflict(ex))
            {
                // The repository has rolled back and disposed its transaction. Restore
                // IDs assigned by earlier batch chunks before retrying the whole write.
                foreach (var original in originals)
                {
                    original.Book.Id = original.Id;
                    original.Book.TitleSlug = original.TitleSlug;
                }

                _logger.Warn(ex, "[BOOK-SLUG] Concurrent link allocation for {0} books; reloading conflicts and retrying once", books.Count);
                EnsureUniqueTitleSlugs(books);
                write();
            }
        }

        private static bool IsBookTitleSlugConflict(Exception exception)
        {
            const string indexName = "IX_Books_MediaType_TitleSlug";
            return exception is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                postgres.ConstraintName == indexName ||
                exception is SqliteException sqlite && sqlite.SqliteExtendedErrorCode == 2067 &&
                sqlite.Message.Contains(indexName, StringComparison.Ordinal);
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
                AuthorId = book.AuthorId,
                LazyAuthor = book.LazyAuthor,
                Narrator = book.Narrator,
                Monitored = book.Monitored,
                AudiobookMonitored = book.AudiobookMonitored,
                EbookMonitored = book.EbookMonitored,
                AnyEditionOk = book.AnyEditionOk,
                BaseBookId = book.BaseBookId,
                GoodreadsWorkId = book.GoodreadsWorkId,
                HardcoverBookId = book.HardcoverBookId,
                OpenLibraryWorkId = book.OpenLibraryWorkId
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
                   (book.TitleSlug.IndexOf("_copy_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    book.TitleSlug.IndexOf("_wanted_", StringComparison.OrdinalIgnoreCase) >= 0);
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

        private void LoadWorklessEditionsForSync(List<Book> books)
        {
            var pending = (books ?? new List<Book>())
                .Where(book => book != null && BookIdentity.GetStableWorkProviderIdentityTokens(book).Count == 0)
                .GroupBy(book => book.MediaType)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group)
                .Where(book => book.Id > 0 && book.LazyEditions?.IsLoaded == false)
                .ToList();

            if (pending.Count == 0)
            {
                return;
            }

            var editionsByBook = _editionService.GetEditionRowsByBook(pending.Select(book => book.Id))
                .GroupBy(edition => edition.BookId)
                .ToDictionary(group => group.Key, group => group.ToList());
            foreach (var book in pending)
            {
                book.Editions = editionsByBook.GetValueOrDefault(book.Id) ?? new List<Edition>();
            }
        }

        // Work IDs are the only candidates for work-backed books. Edition fallback
        // applies only to same-format pairs without work IDs.
        internal static List<List<Book>> BuildWorkGroups(List<Book> books)
        {
            var list = books ?? new List<Book>();
            var n = list.Count;
            var parent = new int[n];
            for (var i = 0; i < n; i++)
            {
                parent[i] = i;
            }

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b)
                {
                    parent[a] = b;
                }
            }

            var workTokensByIndex = new HashSet<string>[n];
            var worklessCountsByFormat = new Dictionary<BookMediaType, int>();
            for (var i = 0; i < n; i++)
            {
                var workTokens = BookIdentity.GetStableWorkProviderIdentityTokens(list[i]);
                workTokensByIndex[i] = workTokens;
                if (list[i] == null || workTokens.Count > 0)
                {
                    continue;
                }

                var mediaType = list[i].MediaType;
                worklessCountsByFormat.TryGetValue(mediaType, out var count);
                worklessCountsByFormat[mediaType] = count + 1;
            }

            var firstByWorkToken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstByEditionToken = new Dictionary<BookMediaType, Dictionary<string, int>>();
            for (var i = 0; i < n; i++)
            {
                var workTokens = workTokensByIndex[i];
                if (workTokens.Count > 0)
                {
                    foreach (var token in workTokens)
                    {
                        if (firstByWorkToken.TryGetValue(token, out var first))
                        {
                            Union(first, i);
                        }
                        else
                        {
                            firstByWorkToken[token] = i;
                        }
                    }

                    continue;
                }

                if (list[i] == null || worklessCountsByFormat[list[i].MediaType] < 2)
                {
                    continue;
                }

                if (!firstByEditionToken.TryGetValue(list[i].MediaType, out var firstByToken))
                {
                    firstByToken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    firstByEditionToken[list[i].MediaType] = firstByToken;
                }

                foreach (var token in BookIdentity.GetEditionProviderIdentityTokens(list[i]))
                {
                    if (firstByToken.TryGetValue(token, out var first))
                    {
                        Union(first, i);
                    }
                    else
                    {
                        firstByToken[token] = i;
                    }
                }
            }

            var groupsByRoot = new Dictionary<int, List<Book>>();
            for (var i = 0; i < n; i++)
            {
                var root = Find(i);
                if (!groupsByRoot.TryGetValue(root, out var group))
                {
                    group = new List<Book>();
                    groupsByRoot[root] = group;
                }

                group.Add(list[i]);
            }

            return groupsByRoot.Values.ToList();
        }

        private bool HasCompatibleRootFolderForMediaType(Author author, BookMediaType mediaType, IReadOnlyList<RootFolder> rootFolders)
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

            var rootFolder = rootFolders?.FirstOrDefault(r => r.Path.PathEquals(rootFolderPath));
            if (rootFolder == null)
            {
                return false;
            }

            return rootFolder.FolderType == FolderType.Mixed ||
                   (mediaType == BookMediaType.Audiobook && rootFolder.FolderType == FolderType.Audiobook) ||
                   (mediaType == BookMediaType.Ebook && rootFolder.FolderType == FolderType.Ebook);
        }

        private bool CanEnableMonitoringForMediaType(Author author, BookMediaType mediaType, IReadOnlyList<RootFolder> rootFolders)
        {
            if (author == null)
            {
                return false;
            }

            // Book-row state is independent from the author-side gate. The gate is
            // evaluated by eligibility queries, so an explicit row selection remains
            // valid while its author side is paused.
            return HasCompatibleRootFolderForMediaType(author, mediaType, rootFolders);
        }

        private void EnsureOneMonitoredOnFormat(List<Book> workGroup, BookMediaType mediaType, Author author, IReadOnlyList<RootFolder> rootFolders)
        {
            var formatBooks = GetSyncParticipants(workGroup, mediaType);

            if (!formatBooks.Any() || formatBooks.Any(IsRowMonitored) || !CanEnableMonitoringForMediaType(author, mediaType, rootFolders))
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

        private void ApplyMutationSyncForWorkGroup(Author author, List<Book> workGroup, HashSet<int> changedBookIds, Dictionary<int, Book> storedById, IReadOnlyList<RootFolder> rootFolders)
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
                EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Audiobook, author, rootFolders);
                EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Ebook, author, rootFolders);
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

        private List<BookMonitoringSyncUpdate> GetSyncUpdatesForMutations(List<Book> changedBooks, Dictionary<int, Book> storedById, Dictionary<int, Author> authorsById = null)
        {
            var syncUpdates = new List<BookMonitoringSyncUpdate>();
            if (_authorService == null || changedBooks == null || changedBooks.Count == 0 ||
                !changedBooks.Any(book => book != null && storedById.TryGetValue(book.Id, out var stored) &&
                    (book.MediaType != stored.MediaType ||
                     book.AudiobookMonitored != stored.AudiobookMonitored ||
                     book.EbookMonitored != stored.EbookMonitored)))
            {
                return syncUpdates;
            }

            foreach (var authorBooks in changedBooks.Where(book => book?.AuthorId > 0).GroupBy(book => book.AuthorId))
            {
                var author = authorsById == null ? _authorService.GetAuthor(authorBooks.Key) : authorsById.GetValueOrDefault(authorBooks.Key);
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

                var groupedBooks = authorBooksById.Values.ToList();
                LoadWorklessEditionsForSync(groupedBooks);
                var rootFolders = _rootFolderService?.All();
                foreach (var workGroup in BuildWorkGroups(groupedBooks))
                {
                    ApplyMutationSyncForWorkGroup(author, workGroup, changedBookIds, authorStoredById, rootFolders);
                }

                foreach (var pair in authorBooksById)
                {
                    if (changedBookIds.Contains(pair.Key) || !HasMonitoringChanged(pair.Value, baseStates[pair.Key]))
                    {
                        continue;
                    }

                    pair.Value.Author = author;
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
                var insertedBookSet = insertedBooks.ToHashSet(BookReferenceComparer.Instance);
                var combinedBooks = (_bookRepository.GetBooksByAuthorId(authorBooks.Key) ?? new List<Book>())
                    .Concat(insertedBooks)
                    .ToList();

                LoadWorklessEditionsForSync(combinedBooks);
                var rootFolders = _rootFolderService?.All();
                foreach (var workGroup in BuildWorkGroups(combinedBooks))
                {
                    var insertedSyncBooks = workGroup
                        .Where(book => insertedBookSet.Contains(book) && !IsSyncVariantRow(book))
                        .ToList();

                    if (!insertedSyncBooks.Any())
                    {
                        continue;
                    }

                    var insertedSyncBookSet = insertedSyncBooks.ToHashSet(BookReferenceComparer.Instance);
                    var syncParticipants = GetSyncParticipants(workGroup);
                    var hasAnyMonitoredSibling = syncParticipants.Any(book =>
                        !insertedSyncBookSet.Contains(book) && IsRowMonitored(book));

                    if (!hasAnyMonitoredSibling)
                    {
                        continue;
                    }

                    if (insertedSyncBooks.Any(book => book.MediaType == BookMediaType.Audiobook) &&
                        !GetSyncParticipants(workGroup, BookMediaType.Audiobook).Any(IsRowMonitored))
                    {
                        EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Audiobook, author, rootFolders);
                    }

                    if (insertedSyncBooks.Any(book => book.MediaType == BookMediaType.Ebook) &&
                        !GetSyncParticipants(workGroup, BookMediaType.Ebook).Any(IsRowMonitored))
                    {
                        EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Ebook, author, rootFolders);
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
                if (authorBooks.Count < 2 ||
                    !authorBooks.Any(book => !IsSyncVariantRow(book) && IsRowMonitored(book)))
                {
                    continue;
                }

                var storedById = authorBooks.ToDictionary(book => book.Id, CloneStoredBook);
                var baseStates = authorBooks.ToDictionary(book => book.Id, SnapshotMonitoredState);

                LoadWorklessEditionsForSync(authorBooks);
                var rootFolders = _rootFolderService?.All();
                foreach (var workGroup in BuildWorkGroups(authorBooks))
                {
                    var syncParticipants = GetSyncParticipants(workGroup);
                    if (!HasSyncFormat(workGroup, BookMediaType.Audiobook) ||
                        !HasSyncFormat(workGroup, BookMediaType.Ebook) ||
                        !syncParticipants.Any(IsRowMonitored))
                    {
                        continue;
                    }

                    EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Audiobook, author, rootFolders);
                    EnsureOneMonitoredOnFormat(workGroup, BookMediaType.Ebook, author, rootFolders);
                }

                foreach (var book in authorBooks)
                {
                    if (!HasMonitoringChanged(book, baseStates[book.Id]))
                    {
                        continue;
                    }

                    book.Author = author;
                    syncUpdates.Add(new BookMonitoringSyncUpdate
                    {
                        Book = book,
                        Stored = storedById[book.Id]
                    });
                }
            }

            return syncUpdates;
        }

        private void PublishBookEditedEvents(IEnumerable<BookMonitoringSyncUpdate> updates)
        {
            var updateList = updates?.ToList() ?? new List<BookMonitoringSyncUpdate>();
            var authorsById = updateList
                .SelectMany(update => new[] { update.Book, update.Stored })
                .Where(book => book?.AuthorId > 0 && book.LazyAuthor?.IsLoaded == true && book.Author?.Id == book.AuthorId)
                .GroupBy(book => book.AuthorId)
                .ToDictionary(group => group.Key, group => group.First().Author);

            var missingAuthorIds = updateList
                .SelectMany(update => new[] { update.Book, update.Stored })
                .Where(book => book?.AuthorId > 0)
                .Select(book => book.AuthorId)
                .Distinct()
                .Where(authorId => !authorsById.ContainsKey(authorId))
                .ToList();
            if (missingAuthorIds.Count > 0)
            {
                foreach (var author in _authorService.GetAuthors(missingAuthorIds))
                {
                    authorsById[author.Id] = author;
                }
            }

            var changes = new List<BookEditedEvent>();

            foreach (var update in updateList)
            {
                if (update.Book?.AuthorId > 0 && authorsById.TryGetValue(update.Book.AuthorId, out var author))
                {
                    update.Book.Author = author;
                }

                if (update.Stored?.AuthorId > 0 && authorsById.TryGetValue(update.Stored.AuthorId, out author))
                {
                    update.Stored.Author = author;
                }

                changes.Add(new BookEditedEvent(update.Book, update.Stored));
            }

            foreach (var change in changes)
            {
                _eventAggregator.PublishEvent(change);
            }

            if (changes.Count > 0)
            {
                _eventAggregator.PublishEvent(new BooksEditedEvent(changes));
            }
        }

        public void InsertMany(List<Book> books)
        {
            if (books.Any(x => x.AuthorId == 0))
            {
                throw new InvalidOperationException("Cannot insert book with AuthorId = 0");
            }

            // LocalBookId generation removed - using database IDs directly

            books.ForEach(EnsureBookDbFields);
            ApplyInsertSyncDefaults(books);

            WriteWithUniqueTitleSlugs(books, () => _bookRepository.InsertMany(books));
        }

        public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction)
        {
            if (books.Any(x => x.AuthorId == 0))
            {
                throw new InvalidOperationException("Cannot insert book with AuthorId = 0");
            }

            // LocalBookId generation removed - using database IDs directly

            // Ensure unique TitleSlugs for duplicate books
            EnsureUniqueTitleSlugs(books, connection: connection, transaction: transaction);
            books.ForEach(EnsureBookDbFields);
            ApplyInsertSyncDefaults(books);

            _bookRepository.InsertMany(books, connection, transaction);
        }

        public void UpdateMany(List<Book> books)
        {
            UpdateMany(books, null);
        }

        public void UpdateMany(List<Book> books, BookTitleSlugIndex titleSlugs)
        {
            // Author refresh can reuse its one complete book snapshot across per-book saves.
            EnsureUniqueTitleSlugs(books, titleSlugs);
            books.ForEach(EnsureBookDbFields);
            var storedById = _bookRepository.Get(books.Select(book => book.Id)).ToDictionary(book => book.Id, CloneStoredBook);
            var syncUpdates = GetSyncUpdatesForMutations(books, storedById);
            var booksToUpdate = books
                .Concat(syncUpdates.Select(update => update.Book))
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            _bookRepository.UpdateMany(booksToUpdate);

            // Quiet saves notify aggregate consumers when a row becomes monitored without invoking per-book lifecycle listeners.
            var newlyMonitoredBooks = books
                .Select(book => new BookMonitoringSyncUpdate
                {
                    Book = book,
                    Stored = storedById.GetValueOrDefault(book.Id)
                })
                .Concat(syncUpdates)
                .GroupBy(update => update.Book.Id)
                .Select(group => group.First())
                .Where(update =>
                    update.Stored != null &&
                    ((!update.Stored.AudiobookMonitored && update.Book.AudiobookMonitored) ||
                     (!update.Stored.EbookMonitored && update.Book.EbookMonitored)))
                .Select(update => new BookEditedEvent(update.Book, update.Stored))
                .ToList();

            if (newlyMonitoredBooks.Count > 0)
            {
                _eventAggregator.PublishEvent(new BooksEditedEvent(newlyMonitoredBooks));
            }
        }

        public void UpdateManyWithLifecycle(List<Book> books)
        {
            PersistWithLifecycle(books);
        }

        private List<Book> PersistWithLifecycle(List<Book> books, List<Edition> editionUpdates = null, string monitoredMediaType = null,
            Action<IDbConnection, IDbTransaction> writeRelated = null)
        {
            var changedBooks = (books ?? new List<Book>())
                .Where(book => book?.Id > 0)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            if (!changedBooks.Any())
            {
                return changedBooks;
            }

            var bookIds = changedBooks.Select(book => book.Id).ToList();
            var storedById = _bookRepository.FindExisting(bookIds)
                .Where(book => book != null)
                .ToDictionary(book => book.Id, CloneStoredBook);

            changedBooks = changedBooks.Where(book => storedById.ContainsKey(book.Id)).ToList();
            if (!changedBooks.Any())
            {
                return changedBooks;
            }

            bookIds = changedBooks.Select(book => book.Id).ToList();
            var missingEditionBookIds = changedBooks
                .Where(book => book.Editions == null)
                .Select(book => book.Id)
                .ToList();
            var missingEditions = missingEditionBookIds.Any()
                ? _editionService.GetEditionsByBook(missingEditionBookIds)
                : new List<Edition>();
            var fallbackEditionsByBook = missingEditions
                .GroupBy(edition => edition.BookId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var book in changedBooks.Where(book => book.Editions == null))
            {
                book.Editions = fallbackEditionsByBook.TryGetValue(book.Id, out var editions)
                    ? editions
                    : new List<Edition>();
            }

            var bookFiles = _mediaFileService.GetFilesByBooks(bookIds) ?? new List<BookFile>();

            var editionBookIds = changedBooks
                .SelectMany(book => book.Editions ?? new List<Edition>())
                .Where(edition => edition?.Id > 0)
                .ToDictionary(edition => edition.Id, edition => edition.BookId);
            var filesByBook = bookFiles
                .Where(file => file != null)
                .Select(file => new
                {
                    File = file,
                    BookId = file.Edition?.BookId > 0
                        ? file.Edition.BookId
                        : editionBookIds.GetValueOrDefault(file.EditionId)
                })
                .Where(item => item.BookId > 0)
                .GroupBy(item => item.BookId)
                .ToDictionary(group => group.Key, group => group.Select(item => item.File).ToList());
            var manualEditionUpdates = new List<Edition>();
            var narratorChangedBookIds = changedBooks
                .Where(book => editionUpdates == null && storedById[book.Id].Narrator != book.Narrator &&
                               !string.IsNullOrWhiteSpace(book.Narrator))
                .Select(book => book.Id)
                .ToList();
            var persistedEditionsForNarratorByBook = fallbackEditionsByBook
                .Where(pair => narratorChangedBookIds.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var narratorEditionBookIdsToLoad = narratorChangedBookIds
                .Where(bookId => !persistedEditionsForNarratorByBook.ContainsKey(bookId))
                .ToList();

            if (narratorEditionBookIdsToLoad.Any())
            {
                foreach (var group in _editionService.GetEditionsByBook(narratorEditionBookIdsToLoad)
                             .GroupBy(edition => edition.BookId))
                {
                    persistedEditionsForNarratorByBook[group.Key] = group.ToList();
                }
            }

            foreach (var book in changedBooks)
            {
                var storedBook = storedById[book.Id];
                var narratorChanged = storedBook.Narrator != book.Narrator &&
                                      !string.IsNullOrWhiteSpace(book.Narrator);

                if (narratorChanged && (editionUpdates == null || !book.AnyEditionOk))
                {
                    _logger.Info("User manually selected narrator '{0}' for book '{1}' (ID: {2})", book.Narrator, book.Title, book.Id);
                    var monitoredEdition = (editionUpdates != null ? book.Editions :
                        persistedEditionsForNarratorByBook.GetValueOrDefault(book.Id) ?? new List<Edition>())
                        .FirstOrDefault(edition => edition.Monitored);
                    if (monitoredEdition != null && !monitoredEdition.ManualAdd)
                    {
                        _logger.Debug("Setting ManualAdd=true for edition {0} due to narrator selection", monitoredEdition.Id);
                        monitoredEdition.ManualAdd = true;
                        manualEditionUpdates.Add(monitoredEdition);
                    }
                }

                var files = filesByBook.GetValueOrDefault(book.Id) ?? new List<BookFile>();
                var hasAudioFiles = files.Any(file =>
                    file.MediaType == "audiobook" ||
                    IsAudiobookExtension(Path.GetExtension(file.Path)));

                if (storedBook.MediaType == BookMediaType.Ebook && !string.IsNullOrWhiteSpace(book.Narrator))
                {
                    book.Narrator = null;
                }
                else if (files.Any() && !hasAudioFiles && !string.IsNullOrWhiteSpace(book.Narrator))
                {
                    _logger.Info("Clearing narrator field for book '{0}' (ID: {1}) as it has no audiobook files", book.Title, book.Id);
                    book.Narrator = null;
                }

                EnsureBookDbFields(book);
            }

            if (editionUpdates == null && manualEditionUpdates.Any())
            {
                _editionService.UpdateMany(manualEditionUpdates.DistinctBy(edition => edition.Id).ToList());
            }

            var syncUpdates = monitoredMediaType == null
                ? GetSyncUpdatesForMutations(changedBooks, storedById)
                : PrepareScopedMonitoring(changedBooks, storedById, monitoredMediaType, true, synchronizeRequest: true)
                    .Where(update => !storedById.ContainsKey(update.Book.Id)).ToList();

            var persistedBooks = changedBooks
                .Concat(syncUpdates.Select(update => update.Book))
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            EnsureUniqueTitleSlugs(persistedBooks);
            if (editionUpdates == null)
            {
                _bookRepository.UpdateMany(persistedBooks);
            }
            else
            {
                // Share the normal lifecycle and metadata writers; publish only after both models commit.
                var editionsToSave = editionUpdates.Concat(manualEditionUpdates)
                    .GroupBy(edition => edition.Id).Select(group =>
                    {
                        var edition = group.First();
                        if (group.Skip(1).Any(item => item.ManualAdd)) edition.ManualAdd = true;
                        return edition;
                    }).ToList();
                _bookRepository.UpdateManyAtomic(persistedBooks, (connection, transaction) =>
                {
                    _editionService.UpdateMany(editionsToSave, connection, transaction,
                        changedBooks.Count == 1 ? changedBooks[0].Editions.SingleOrDefault(edition => edition.Monitored) : null);

                    writeRelated?.Invoke(connection, transaction);
                });
            }

            var editedBooks = changedBooks
                .Select(book => new BookMonitoringSyncUpdate
                {
                    Book = book,
                    Stored = storedById[book.Id]
                })
                .Concat(syncUpdates)
                .ToList();

            if (editionUpdates == null)
            {
                PublishBookEditedEvents(editedBooks);
            }
            else
            {
                try
                {
                    PublishBookEditedEvents(editedBooks);
                }
                catch (Exception error)
                {
                    // Persistence has committed; an observer failure must not undo transferred files.
                    _logger.Error(error, "Book update committed for BookIds={0}, but publishing edit notifications failed",
                        string.Join(",", persistedBooks.Select(book => book.Id)));
                }
            }

            return changedBooks;
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
            try { _eventAggregator.PublishEvent(new LibraryIdentityChangedEvent(Array.Empty<int>(), booksToReassign.Select(book => book.Id))); }
            catch (Exception ex) { _logger.Warn(ex, "Book ownership was saved, but a cache/UI observer failed"); }
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
            try { _eventAggregator.PublishEvent(new LibraryIdentityChangedEvent(Array.Empty<int>(), booksToReassign.Select(book => book.Id))); }
            catch (Exception ex) { _logger.Warn(ex, "Book ownership was saved, but a cache/UI observer failed"); }
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

        public void DeleteMany(List<Book> books, bool filesAlreadyDeleted = false, List<BookFile> deletedBookFiles = null)
        {
            var booksToDelete = (books ?? new List<Book>())
                .Where(book => book != null)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            HydrateBooksForDeleteEvent(booksToDelete, deletedBookFiles);

            _bookRepository.DeleteManyAtomic(booksToDelete.Select(book => book.Id), deletedBookFiles?.Select(file => file.Id));
            foreach (var book in booksToDelete)
            {
                _eventAggregator.PublishEvent(new BookDeletedEvent(book, false, false, filesAlreadyDeleted: filesAlreadyDeleted));
            }
        }

        public Book UpdateBook(Book book) => UpdateBook(book, null);

        public Book UpdateBook(Book book, List<Edition> editionUpdates, string monitoredMediaType = null, Action<IDbConnection, IDbTransaction> writeRelated = null)
        {
            if (book == null)
            {
                throw new ArgumentNullException(nameof(book));
            }

            var persisted = PersistWithLifecycle(new List<Book> { book }, editionUpdates ?? (writeRelated == null ? null : new List<Edition>()), monitoredMediaType, writeRelated);
            if (!persisted.Any())
            {
                throw new ModelNotFoundException(typeof(Book), book.Id);
            }

            return persisted[0];
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

            _bookRepository.SetMonitoring(new[] { book }.Concat(syncUpdates.Select(update => update.Book)).ToList());

            PublishBookEditedEvents(new[]
            {
                new BookMonitoringSyncUpdate { Book = book, Stored = storedBook }
            }.Concat(syncUpdates));

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
                _bookRepository.SetMonitoring(booksToUpdate);
            }

            PublishBookEditedEvents(books.Select(book => new BookMonitoringSyncUpdate
            {
                Book = book,
                Stored = storedById[book.Id]
            }).Concat(syncUpdates));
        }

        public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored)
        {
            SetMonitoredForMediaTypeInternal(ids, mediaType, monitored, synchronizeRequest: false);
        }

        public void MonitorRequestedBooks(IEnumerable<int> ids, string mediaType)
        {
            SetMonitoredForMediaTypeInternal(ids, mediaType, true, synchronizeRequest: true);
        }

        private void SetMonitoredForMediaTypeInternal(IEnumerable<int> ids, string mediaType, bool monitored, bool synchronizeRequest)
        {
            if (!string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn("Ignoring SetMonitoredForMediaType for unsupported media type '{0}'", mediaType);
                return;
            }

            var books = _bookRepository.Get(ids).ToList();
            var storedById = books.ToDictionary(book => book.Id, CloneStoredBook);
            var updates = PrepareScopedMonitoring(books, storedById, mediaType, monitored, synchronizeRequest);
            if (updates.Any())
            {
                _bookRepository.SetMonitoring(updates.Select(update => update.Book).ToList());
            }

            PublishBookEditedEvents(updates);
        }

        private List<BookMonitoringSyncUpdate> PrepareScopedMonitoring(List<Book> books,
            Dictionary<int, Book> storedById, string mediaType, bool monitored, bool synchronizeRequest)
        {
            var requestedMediaType = string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase)
                ? BookMediaType.Ebook
                : BookMediaType.Audiobook;
            var rootFolders = monitored ? _rootFolderService?.All() : null;
            var authorIds = books.Where(book => book.AuthorId > 0 && (!synchronizeRequest || book.MediaType == requestedMediaType))
                .Select(book => book.AuthorId).Distinct().ToList();
            var authorsById = authorIds.Any() ? _authorService.GetAuthors(authorIds).ToDictionary(author => author.Id) : new Dictionary<int, Author>();
            var booksToMutate = new List<Book>();

            foreach (var book in books)
            {
                if (synchronizeRequest && book.MediaType != requestedMediaType)
                {
                    continue;
                }

                var author = authorsById.GetValueOrDefault(book.AuthorId);
                if (author != null)
                {
                    book.Author = author;
                }

                if (monitored && !CanEnableMonitoringForMediaType(author, requestedMediaType, rootFolders))
                {
                    _logger.Warn("Cannot enable {0} monitoring for book '{1}' - author '{2}' is missing or has no compatible root folder", mediaType, book.Title, author?.Name ?? "unknown");
                    continue;
                }

                book.SetMonitoredForMediaType(mediaType, monitored);
                if (!synchronizeRequest && !HasMonitoringChanged(book, SnapshotMonitoredState(storedById[book.Id])))
                {
                    continue;
                }

                booksToMutate.Add(book);
            }

            var syncUpdates = new List<BookMonitoringSyncUpdate>();
            if (synchronizeRequest && booksToMutate.Any())
            {
                // An explicit request asserts monitoring even when the row was already selected.
                // Reuse the mutation sync rule with an unmonitored baseline to repair its sibling.
                var requestBaselineById = booksToMutate.ToDictionary(book => book.Id, book =>
                {
                    var baseline = CloneStoredBook(book);
                    baseline.AudiobookMonitored = false;
                    baseline.EbookMonitored = false;
                    return baseline;
                });
                syncUpdates = GetSyncUpdatesForMutations(booksToMutate, requestBaselineById, authorsById);
            }

            return booksToMutate.Where(book => HasMonitoringChanged(book, SnapshotMonitoredState(storedById[book.Id])))
                .Select(book => new BookMonitoringSyncUpdate
            {
                Book = book,
                Stored = storedById[book.Id]
            }).Concat(syncUpdates).ToList();
        }

        public void UpdateLastSearchTime(List<Book> books)
        {
            _bookRepository.SetFields(books, b => b.LastSearchTime);
        }

        public void Handle(AuthorDeletedEvent message)
        {
            var books = GetBooksByAuthorId(message.Author.Id);

            DeleteMany(books, filesAlreadyDeleted: message.DeleteFiles, deletedBookFiles: message.DeletedBookFiles);
        }

        public void SyncMonitoredAcrossFormats(List<int> authorIds)
        {
            var watch = Stopwatch.StartNew();
            var syncUpdates = BuildReconcileSyncUpdates(authorIds);
            if (!syncUpdates.Any())
            {
                _logger.Info("Monitored format sync: 0 book rows changed for {0} authors in {1}ms", authorIds?.Count ?? 0, watch.ElapsedMilliseconds);
                return;
            }

            _bookRepository.SetMonitoring(syncUpdates.Select(update => update.Book).ToList());
            PublishBookEditedEvents(syncUpdates);

            _logger.Info("Reconciled monitored format sync across {0} book rows for {1} authors in {2}ms", syncUpdates.Count, authorIds?.Count ?? 0, watch.ElapsedMilliseconds);
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

        public (List<Book> Books, Dictionary<int, Book> ExistingCopies, int CandidateCount, int EditionCount, int AuthorRowCount, int FileCount) FindWantedEditionCopies(IEnumerable<int> bookIds)
        {
            var stopwatch = Stopwatch.StartNew();
            var ids = bookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0) return (new List<Book>(), new Dictionary<int, Book>(), 0, 0, 0, 0);
            var booksById = _bookRepository.FindExisting(ids).ToDictionary(book => book.Id);
            var books = ids.Where(booksById.ContainsKey).Select(id => booksById[id]).ToList();
            var copies = new Dictionary<int, Book>();
            if (books.Count == 0) return (books, copies, 0, 0, 0, 0);

            var audioBooks = books.Where(book => book.MediaType == BookMediaType.Audiobook).ToList();
            var authorBooks = audioBooks.Select(book => book.AuthorId).Distinct()
                .SelectMany(_bookRepository.GetBooksByAuthorId).ToList();
            var candidatesByBook = audioBooks.ToDictionary(book => book.Id,
                book => BookEditionIdentity.GetWantedEditionCopyCandidates(book, authorBooks));
            var candidates = candidatesByBook.Values.SelectMany(rows => rows).DistinctBy(book => book.Id).ToList();
            var editions = _editionService.GetEditionRowsByBook(candidates.Select(book => book.Id).Concat(ids).Distinct()).ToList();
            var editionsByBook = editions.ToLookup(edition => edition.BookId);
            foreach (var book in books.Concat(candidates)) book.Editions = editionsByBook[book.Id].ToList();

            var files = candidates.Count == 0 ? new List<BookFile>() :
                _mediaFileService.GetFilesByBooks(candidates.Select(book => book.Id).ToList());
            var filedEditionIds = files.Select(file => file.EditionId).ToHashSet();
            foreach (var book in audioBooks)
            {
                foreach (var copy in BookEditionIdentity.FindWantedEditionCopies(book, book.Editions, candidatesByBook[book.Id], filedEditionIds))
                {
                    copies.Add(copy.Key, copy.Value);
                }
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("Wanted edition lookup: {0} books, {1} candidate rows, {2} editions, {3} file-linked editions in {4}ms; {5} author rows, {6} files",
                    books.Count, candidates.Count, editions.Count, filedEditionIds.Count, stopwatch.ElapsedMilliseconds, authorBooks.Count, files.Count);
            }
            return (books, copies, candidates.Count, editions.Count, authorBooks.Count, files.Count);
        }

        public (Book Book, bool ExistingCopy) AddWantedEditionCopy(int bookId, int editionId)
        {
            var initial = _bookRepository.Get(bookId);
            var stopwatch = Stopwatch.StartNew();
            using var writerLock = EnterWantedEditionLock(new Author { Id = initial.AuthorId }, _logger);
            var candidateCount = 0;
            var editionCount = 0;
            var authorRowCount = 0;
            var fileCount = 0;
            var waited = stopwatch.ElapsedMilliseconds;
            try
            {
                // Reload under the same lock as refresh and batch requests before checking for a copy.
                var author = _authorService.GetAuthor(initial.AuthorId);
                var lookup = FindWantedEditionCopies(new[] { bookId });
                candidateCount = lookup.CandidateCount;
                editionCount = lookup.EditionCount;
                authorRowCount = lookup.AuthorRowCount;
                fileCount = lookup.FileCount;
                var source = lookup.Books.SingleOrDefault(book => book.Id == bookId &&
                    book.AuthorId == author.Id && book.MediaType == BookMediaType.Audiobook)
                    ?? throw RequestedEditionRefusal("Wanted edition copies require an audiobook book in the current catalog.");
                source.Author = author;
                var requested = source.Editions.SingleOrDefault(edition => edition.Id == editionId)
                    ?? throw RequestedEditionRefusal("The requested edition does not belong to this book in the current catalog.");
                if (!EditionReadingFormat.IsAudiobook(requested.ReadingFormatId))
                {
                    throw RequestedEditionRefusal("Wanted edition copies require an audiobook edition.");
                }

                lookup.ExistingCopies.TryGetValue(requested.Id, out var existing);
                if (existing != null)
                {
                    return (existing, true);
                }

                if (!CanEnableMonitoringForMediaType(author, source.MediaType, _rootFolderService?.All()))
                {
                    throw RequestedEditionRefusal("The requested edition cannot be monitored because its author has no compatible root folder for its media type.");
                }

                return (CreateWantedEdition(source, requested), false);
            }
            finally
            {
                var held = stopwatch.ElapsedMilliseconds - waited;
                _logger.Info("Wanted edition writer for author {0}: waited {1}ms, held {2}ms, 1 copy request, {3} candidate rows, {4} editions, {5} author rows, {6} files; total {7}ms",
                    initial.AuthorId, waited, held, candidateCount, editionCount, authorRowCount, fileCount, stopwatch.ElapsedMilliseconds);
            }
        }

        private static readonly ConcurrentDictionary<int, object> WantedEditionLocks = new();

        internal static object GetWantedEditionLock(int authorId) => WantedEditionLocks.GetOrAdd(authorId, _ => new object());

        internal static IDisposable EnterWantedEditionLock(Author author, Logger logger, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var writerLock = GetWantedEditionLock(author.Id);
            var stopwatch = Stopwatch.StartNew();
            if (!Monitor.TryEnter(writerLock))
            {
                var authorLabel = string.IsNullOrWhiteSpace(author.Name) ? author.Id.ToString() : $"{author.Id} ('{author.Name}')";
                logger.Info("[AUTHOR-REFRESH-GATE] Waiting for author {0} to settle", authorLabel);
                while (!Monitor.TryEnter(writerLock, 100))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                logger.Info("[AUTHOR-REFRESH-GATE] Author {0} settled after {1}ms", authorLabel, stopwatch.ElapsedMilliseconds);
            }

            return new WantedEditionLockLease(() => Monitor.Exit(writerLock));
        }

        private sealed class WantedEditionLockLease : IDisposable
        {
            private Action _release;

            public WantedEditionLockLease(Action release)
            {
                _release = release;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _release, null)?.Invoke();
            }
        }

        public Dictionary<int, Book> AddWantedEditions(List<Edition> requestedEditions, Action<Edition, Exception> onFailure = null)
        {
            var result = new Dictionary<int, Book>();
            if (requestedEditions.Count == 0)
            {
                return result;
            }

            var failures = new List<Exception>();
            void Failed(Edition edition, Exception exception)
            {
                failures.Add(exception);
                _logger.Warn(exception, "Wanted edition request failed for local book {0}, edition {1}", edition.BookId, edition.Id);
                onFailure?.Invoke(edition, exception);
            }

            var owners = _bookRepository.FindExisting(requestedEditions.Select(edition => edition.BookId).Distinct().ToList()).ToDictionary(book => book.Id);
            foreach (var missing in requestedEditions.Where(edition => !owners.ContainsKey(edition.BookId)))
            {
                Failed(missing, RequestedEditionRefusal("The requested edition owner is no longer in the authoritative catalog."));
            }

            foreach (var group in requestedEditions.Where(edition => owners.ContainsKey(edition.BookId)).GroupBy(edition => owners[edition.BookId].AuthorId))
            {
                var requests = group.ToList();
                var mediaTypes = requests.Select(request => owners[request.BookId].MediaType).ToHashSet();
                var stopwatch = Stopwatch.StartNew();
                var writerLock = GetWantedEditionLock(group.Key);
                var contended = !Monitor.TryEnter(writerLock);
                if (contended)
                {
                    Monitor.Enter(writerLock);
                }

                var waited = stopwatch.ElapsedMilliseconds;
                try
                {
                    // Reload at the existing writer boundary so concurrent requests see persisted copies.
                    var author = _authorService.GetAuthor(group.Key);
                    var candidates = _bookRepository.GetBooksByAuthorId(group.Key)
                        .Where(book => mediaTypes.Contains(book.MediaType)).ToList();
                    var editions = _editionService.GetEditionRowsByBook(candidates.Select(book => book.Id)).ToLookup(edition => edition.BookId);
                    candidates.ForEach(book => { book.Editions = editions[book.Id].ToList(); book.Author = author; });
                    foreach (var selection in SelectWantedEditions(requests, candidates, Failed))
                    {
                        result[selection.Key] = selection.Value;
                    }
                }
                catch (Exception exception)
                {
                    foreach (var request in requests)
                    {
                        Failed(request, exception);
                    }
                }
                finally
                {
                    var held = stopwatch.ElapsedMilliseconds - waited;
                    Monitor.Exit(writerLock);
                    _logger.Log(contended ? LogLevel.Info : LogLevel.Debug,
                        "Wanted edition writer for author {0}: waited {1}ms, held {2}ms, {3} requests",
                        group.Key, waited, held, requests.Count);
                }
            }

            if (failures.Count > 0)
            {
                _logger.Warn("Wanted edition batch completed with {0} failed requests out of {1}", failures.Count, requestedEditions.Count);
                if (onFailure == null)
                {
                    throw failures.Count == 1 ? failures[0] : new AggregateException("Wanted edition requests failed", failures);
                }
            }

            return result;
        }

        private Dictionary<int, Book> SelectWantedEditions(List<Edition> requestedEditions, List<Book> candidates, Action<Edition, Exception> onFailure)
        {
            var result = new Dictionary<int, Book>();
            if (requestedEditions.Count == 0)
            {
                return result;
            }

            var byId = candidates.ToDictionary(book => book.Id);
            var workIndex = new Dictionary<(int, BookMediaType, string), List<Book>>();
            var editionIndex = new Dictionary<(int, BookMediaType, string), List<Book>>();
            void Index(Book book)
            {
                void Add(Dictionary<(int, BookMediaType, string), List<Book>> index, IEnumerable<string> identities)
                {
                    foreach (var id in identities.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var key = (book.AuthorId, book.MediaType, id.ToLowerInvariant());
                        if (!index.TryGetValue(key, out var books))
                        {
                            books = new List<Book>();
                            index[key] = books;
                        }

                        books.Add(book);
                    }
                }

                Add(workIndex, BookIdentity.GetStableWorkProviderIdentityTokens(book));
                Add(editionIndex, book.Editions.SelectMany(BookEditionIdentity.GetEditionRehomeTokens));
            }

            foreach (var book in byId.Values)
            {
                book.Editions ??= new List<Edition>();
                Index(book);
            }

            var booksWithFiles = _mediaFileService.GetFilesByBooks(byId.Keys.ToList())
                .Select(file => file.Edition.BookId).ToHashSet();
            var rootFolders = _rootFolderService?.All();
            var changed = new Dictionary<int, Book>();
            var oldBooks = new Dictionary<int, Book>();
            foreach (var requested in requestedEditions)
            {
                try
                {
                    if (!byId.TryGetValue(requested.BookId, out var source))
                    {
                        throw RequestedEditionRefusal("The requested edition is not in the authoritative catalog.");
                    }

                    var authoritative = source.Editions.Where(edition => BookEditionIdentity.EditionsMatch(edition, requested)).ToList();
                    var currentRequest = authoritative.SingleOrDefault(edition => edition.Id == requested.Id)
                        ?? (authoritative.Count == 1 ? authoritative[0] : null);
                    if (currentRequest == null)
                    {
                        throw RequestedEditionRefusal("The requested edition is missing or ambiguous in the authoritative catalog.");
                    }

                    if (source.MediaType == BookMediaType.Audiobook && !EditionReadingFormat.IsAudiobook(currentRequest.ReadingFormatId))
                    {
                        throw RequestedEditionRefusal("Wanted narrator editions require an audiobook edition.");
                    }

                    var requestedIds = BookEditionIdentity.GetEditionRehomeTokens(currentRequest).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var sourceWorkIds = BookIdentity.GetStableWorkProviderIdentityTokens(source);
                    bool MatchesRequested(Edition edition) => BookEditionIdentity.GetEditionRehomeTokens(edition).Any(requestedIds.Contains);
                    var matching = BookIdentity.GetStableWorkProviderIdentityTokens(source)
                        .SelectMany(id => workIndex.GetValueOrDefault((source.AuthorId, source.MediaType, id.ToLowerInvariant())) ?? new List<Book>())
                        .Concat(requestedIds
                            .SelectMany(id => editionIndex.GetValueOrDefault((source.AuthorId, source.MediaType, id.ToLowerInvariant())) ?? new List<Book>()))
                        .Append(source).DistinctBy(book => book.Id)
                        .Where(book => source.MediaType == BookMediaType.Audiobook || book.Id == source.Id ||
                            BookIdentity.MatchesByStableWorkProviderId(book, source))
                        .Where(book => !requestedIds.All(id => id.StartsWith("az:", StringComparison.OrdinalIgnoreCase)) ||
                            sourceWorkIds.Count == 0 || BookIdentity.GetStableWorkProviderIdentityTokens(book).Count == 0 ||
                            BookIdentity.MatchesByStableWorkProviderId(book, source))
                        .OrderBy(book => book.AnyEditionOk).ThenByDescending(book => book.IsMonitored()).ThenBy(book => book.Id).ToList();
                    var selected = matching.Select(book => (Book: book, Edition: BookEditionIdentity.GetMonitoredEdition(book)))
                        .FirstOrDefault(candidate => candidate.Edition != null &&
                            (MatchesRequested(candidate.Edition) || source.MediaType == BookMediaType.Audiobook &&
                                BookEditionIdentity.HasSameNarration(candidate.Edition, currentRequest)) &&
                            (source.MediaType == BookMediaType.Audiobook ||
                             EditionPinPolicy.FindConflictingProtectedEdition(candidate.Book, candidate.Book.Editions, candidate.Edition.Id) == null));
                    if (selected.Book == null)
                    {
                        selected = matching.Where(book => !booksWithFiles.Contains(book.Id) && book.AnyEditionOk &&
                                EditionPinPolicy.CanAutomationSelectEdition(book, book.Editions))
                            .Select(book => (Book: book, Edition: book.Editions.FirstOrDefault(MatchesRequested)))
                            .FirstOrDefault(candidate => candidate.Edition != null);
                    }

                    if (selected.Book == null)
                    {
                        if (source.MediaType == BookMediaType.Ebook)
                        {
                            throw RequestedEditionRefusal("The requested Ebook edition cannot be selected without changing a protected selection or existing files; the edition request was not fulfilled.");
                        }

                        if (!CanEnableMonitoringForMediaType(source.Author, source.MediaType, rootFolders))
                        {
                            throw RequestedEditionRefusal("The requested edition cannot be monitored because its author has no compatible root folder for its media type.");
                        }

                        var copy = CreateWantedEdition(source, currentRequest);
                        byId[copy.Id] = copy;
                        candidates.Add(copy);
                        Index(copy);
                        result[requested.Id] = copy;
                        continue;
                    }

                    var target = selected.Book;
                    if (!target.IsMonitored() && !CanEnableMonitoringForMediaType(target.Author, target.MediaType, rootFolders))
                    {
                        throw RequestedEditionRefusal("The requested edition cannot be monitored because its author has no compatible root folder for its media type.");
                    }

                    if (target.AnyEditionOk || !selected.Edition.ManualAdd || target.Editions.Count(edition => edition.Monitored) != 1)
                    {
                        oldBooks.TryAdd(target.Id, CloneStoredBook(target));
                        foreach (var edition in target.Editions)
                        {
                            edition.Monitored = edition.Id == selected.Edition.Id;
                            edition.ManualAdd = edition.Monitored;
                        }

                        target.AnyEditionOk = false;
                        target.ForeignEditionId = selected.Edition.ForeignEditionId;
                        changed[target.Id] = target;
                    }

                    result[requested.Id] = target;
                }
                catch (Exception ex) when (onFailure != null)
                {
                    onFailure(requested, ex);
                }
            }

            var unmonitored = result.Values.Where(book => !book.IsMonitored()).DistinctBy(book => book.Id).ToList();
            var unmonitoredIds = unmonitored.Select(book => book.Id).ToList();
            if (unmonitoredIds.Count > 0)
            {
                foreach (var side in unmonitored.GroupBy(book => book.MediaType))
                {
                    MonitorRequestedBooks(side.Select(book => book.Id).ToList(), side.Key == BookMediaType.Audiobook ? "audiobook" : "ebook");
                }
                var monitored = _bookRepository.FindExisting(unmonitoredIds).Where(book => book.IsMonitored()).Select(book => book.Id).ToHashSet();
                foreach (var request in requestedEditions.Where(request => result.TryGetValue(request.Id, out var book) &&
                    unmonitoredIds.Contains(book.Id) && !monitored.Contains(book.Id)))
                {
                    var target = result[request.Id];
                    changed.Remove(target.Id);
                    result.Remove(request.Id);
                    onFailure(request, new InvalidOperationException("The requested edition remained unmonitored after applying monitoring."));
                }
            }

            if (changed.Count > 0)
            {
                _editionService.UpdateSelection(changed.Values.SelectMany(book => book.Editions).ToList());
                _bookRepository.SetFields(changed.Values.ToList(), book => book.AnyEditionOk);
                PublishBookEditedEvents(changed.Values.Select(book => new BookMonitoringSyncUpdate { Book = book, Stored = oldBooks[book.Id] }));
            }


            foreach (var book in result.Values)
            {
                book.SetMonitored(true);
            }

            return result;
        }

        private static ValidationException RequestedEditionRefusal(string message)
        {
            return new ValidationException(new[] { new ValidationFailure("Edition", message) });
        }

        private Book CreateWantedEdition(Book baseBook, Edition selectedEdition)
        {
            var editionId = selectedEdition.Id;
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

            var wantedBook = RefreshEntityCopy.CloneBook(baseBook, includeEditions: false, includeSeriesLinks: false);
            wantedBook.TitleSlug = $"{baseSlug}_wanted_{editionId}";
            wantedBook.AuthorId = baseBook.AuthorId;
            wantedBook.Author = author;
            wantedBook.MediaType = BookMediaType.Audiobook;
            wantedBook.AudiobookMonitored = true;
            wantedBook.EbookMonitored = false;
            wantedBook.Monitored = true;
            wantedBook.AnyEditionOk = false;
            wantedBook.Added = DateTime.UtcNow;
            wantedBook.LastSearchTime = null;
            wantedBook.UnitKeyHash = null;
            wantedBook.AddOptions = new AddBookOptions { AddType = BookAddType.Manual, SearchForNewBook = false };

            BookEditionIdentity.ClearBookLevelEditionIdentity(wantedBook);

            // Clone audiobook editions so matching can still discriminate and manual edition protection can work.

            var sourceEditions = baseBook.Editions ?? _editionService.GetEditionsByBook(baseBook.Id);
            var clonedEditions = sourceEditions
                .Where(e => e.Id == editionId || EditionReadingFormat.IsAudiobook(e?.ReadingFormatId))
                .Select(e =>
                {
                    // Copy the known edition verbatim; inferring identity can discard valid stored IDs.
                    var copy = RefreshEntityCopy.CloneEdition(e);
                    copy.BookId = 0;
                    copy.Monitored = e.Id == editionId;
                    copy.ManualAdd = copy.Monitored;
                    return copy;
                })
                .ToList();

            if (!clonedEditions.Any())
            {
                throw new InvalidOperationException("No audiobook editions were available to create a wanted narrator variant");
            }

            wantedBook.Editions = clonedEditions;

            // Create the wanted book without triggering a full author refresh/rescan; callers can queue a targeted search.
            try
            {
                AddBook(wantedBook, doRefresh: false);

                var persistedSelected = wantedBook.Editions.Single(edition => edition.Monitored);
                if (!BookEditionIdentity.EditionsMatch(persistedSelected, selectedEdition) || !persistedSelected.ManualAdd)
                {
                    throw new InvalidOperationException("The wanted narration was not persisted as the sole pinned edition.");
                }

                return wantedBook;
            }
            catch
            {
                if (wantedBook.Id > 0)
                {
                    DeleteBook(wantedBook.Id, deleteFiles: false);
                }

                throw;
            }
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
            var authorIds = books.Select(book => book.AuthorId).Where(id => id > 0).Distinct().ToList();
            if (authorIds.Count == 0)
            {
                return;
            }

            var authors = _authorService.GetExistingAuthors(authorIds).ToDictionary(author => author.Id);
            foreach (var authorId in authorIds)
            {
                if (!authors.ContainsKey(authorId))
                {
                    throw new ModelNotFoundException(typeof(Author), authorId);
                }
            }

            foreach (var book in books)
            {
                if (authors.TryGetValue(book.AuthorId, out var author))
                {
                    book.Author = author;
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

            LoadSeriesLinks(books);

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
            BookSeries.LoadLinks(books, ids => _seriesBookLinkRepository.GetLinksByBook(ids), reload: true);
        }

        public bool ShouldSearchForMediaType(Book book, string mediaType)
        {
            return book.IsMonitoredForMediaType(mediaType);
        }

            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null, bool? library = null)
            {
                return _bookRepository.GetBookBuckets(sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, monitored, missing, wanted, library);
            }

            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null)
            {
                return GetBookBuckets(sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, null, null, null);
            }

            public List<int> GetBookIds(bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null, bool? monitored = null, bool? missing = null, bool? wanted = null, bool? library = null)
            {
                return _bookRepository.GetBookIds(includeUnmonitored, mediaType, downloaded, monitored, missing, wanted, library);
            }

            public List<int> GetLibraryBookIds(IEnumerable<int> bookIds = null)
            {
                return _bookRepository.GetLibraryBookIds(bookIds);
            }

                public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null, bool? library = null)
                {
                    var books = _bookRepository.GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, monitored, missing, wanted, library);

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
