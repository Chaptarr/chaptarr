using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.ImportLists
{
    public class ImportListSyncService : IExecute<ImportListSyncCommand>,
                                         IExecute<HardcoverLibrarySyncCommand>
    {
        private const int ImportListPendingImportBatchSize = 10;

        private readonly IImportListFactory _importListFactory;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly IFetchAndParseImportList _listFetcherAndParser;
        private readonly IProvideBookInfo _bookInfoProxy;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IPendingAuthorImportService _pendingAuthorImportService;
        private readonly IImportListBookIdentityCacheRepository _bookIdentityCacheRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public ImportListSyncService(IImportListFactory importListFactory,
                                     IImportListExclusionService importListExclusionService,
                                     IFetchAndParseImportList listFetcherAndParser,
                                     IProvideBookInfo bookInfoProxy,
                                     IAuthorService authorService,
                                     IBookService bookService,
                                     IEditionService editionService,
                                     IPendingAuthorImportService pendingAuthorImportService,
                                     IRootFolderService rootFolderService,
                                     IRootFolderSettingsResolver rootFolderSettingsResolver,
                                     IEventAggregator eventAggregator,
                                     IManageCommandQueue commandQueueManager,
                                     Logger logger,
                                     IImportListBookIdentityCacheRepository bookIdentityCacheRepository)
        {
            _importListFactory = importListFactory;
            _importListExclusionService = importListExclusionService;
            _listFetcherAndParser = listFetcherAndParser;
            _bookInfoProxy = bookInfoProxy;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _pendingAuthorImportService = pendingAuthorImportService;
            _bookIdentityCacheRepository = bookIdentityCacheRepository;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        private ResolvedRootFolderSettings ResolveRootFolderSettings(string rootFolderPath, BookMediaType mediaType,
            Dictionary<string, RootFolder> roots = null)
        {
            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return new ResolvedRootFolderSettings { IsConfigured = false, Source = "Unconfigured" };
            }

            RootFolder rootFolder;
            if (roots == null || !roots.TryGetValue(rootFolderPath, out rootFolder))
            {
                rootFolder = _rootFolderService.GetBestRootFolder(rootFolderPath);
                if (roots != null)
                {
                    roots[rootFolderPath] = rootFolder;
                }
            }

            return _rootFolderSettingsResolver.ResolveSettings(rootFolder, mediaType);
        }

        private MonitoringConfig BuildConfigFromImportList(ImportListDefinition importList,
            string authorName,
            Dictionary<string, RootFolder> roots = null)
        {
            var settings = (DualMediaImportListSettings)importList.Settings;
            var options = importList.GetMonitoringOptions();
            var monitorAudiobooks = settings.MonitorAudiobooks;
            var monitorEbooks = settings.MonitorEbooks;
            var audiobookQualityOverride = settings.AudiobookQualityProfileId;
            var ebookQualityOverride = settings.EbookQualityProfileId;
            var audiobookMetadataOverride = settings.AudiobookMetadataProfileId;
            var ebookMetadataOverride = settings.EbookMetadataProfileId;
            var audiobookRootFolderPath = settings.AudiobookRootFolderPath;
            var ebookRootFolderPath = settings.EbookRootFolderPath;

            var audiobookRootFolderSettings = monitorAudiobooks
                ? ResolveRootFolderSettings(audiobookRootFolderPath, BookMediaType.Audiobook, roots) : null;
            var ebookRootFolderSettings = monitorEbooks
                ? ResolveRootFolderSettings(ebookRootFolderPath, BookMediaType.Ebook, roots) : null;

            var audiobookQualityProfileId = audiobookQualityOverride > 0
                ? audiobookQualityOverride
                : (audiobookRootFolderSettings?.QualityProfileId ?? 0);

            var ebookQualityProfileId = ebookQualityOverride > 0
                ? ebookQualityOverride
                : (ebookRootFolderSettings?.QualityProfileId ?? 0);

            var audiobookMetadataProfileId = audiobookMetadataOverride > 0
                ? audiobookMetadataOverride
                : (audiobookRootFolderSettings?.MetadataProfileId ?? 0);

            var ebookMetadataProfileId = ebookMetadataOverride > 0
                ? ebookMetadataOverride
                : (ebookRootFolderSettings?.MetadataProfileId ?? 0);

            if (monitorAudiobooks && (audiobookQualityProfileId <= 0 || audiobookMetadataProfileId <= 0))
            {
                throw new InvalidOperationException($"{importList.Name}: audiobook defaults are not configured (set profile overrides or configure root folder defaults)");
            }

            if (monitorEbooks && (ebookQualityProfileId <= 0 || ebookMetadataProfileId <= 0))
            {
                throw new InvalidOperationException($"{importList.Name}: ebook defaults are not configured (set profile overrides or configure root folder defaults)");
            }

            var audiobookTags = new HashSet<int>();
            var ebookTags = new HashSet<int>();
            if (monitorAudiobooks)
            {
                audiobookTags.UnionWith((IEnumerable<int>)settings.AudiobookTags ?? Enumerable.Empty<int>());
            }
            if (monitorEbooks)
            {
                ebookTags.UnionWith((IEnumerable<int>)settings.EbookTags ?? Enumerable.Empty<int>());
            }

            var config = new MonitoringConfig
            {
                CreateAudiobook = monitorAudiobooks,
                CreateEbook = monitorEbooks,

                AudiobookQualityProfileId = monitorAudiobooks ? audiobookQualityProfileId : null,
                EbookQualityProfileId = monitorEbooks ? ebookQualityProfileId : null,
                AudiobookMetadataProfileId = monitorAudiobooks ? audiobookMetadataProfileId : null,
                EbookMetadataProfileId = monitorEbooks ? ebookMetadataProfileId : null,
                AudiobookRootFolderPath = monitorAudiobooks ? audiobookRootFolderPath : null,
                EbookRootFolderPath = monitorEbooks ? ebookRootFolderPath : null,

                AudiobookMonitored = monitorAudiobooks ? options.AudiobookMonitored ?? audiobookRootFolderSettings?.Monitored : null,
                AudiobookMonitorNewItems = monitorAudiobooks ? options.AudiobookMonitorNewItems ?? audiobookRootFolderSettings?.MonitorNewItems : null,
                AudiobookMonitorExistingMode = monitorAudiobooks
                    ? options.AudiobookMonitor ?? audiobookRootFolderSettings?.MonitorExistingMode : MonitorTypes.None,
                EbookMonitored = monitorEbooks ? options.EbookMonitored ?? ebookRootFolderSettings?.Monitored : null,
                EbookMonitorNewItems = monitorEbooks ? options.EbookMonitorNewItems ?? ebookRootFolderSettings?.MonitorNewItems : null,
                EbookMonitorExistingMode = monitorEbooks
                    ? options.EbookMonitor ?? ebookRootFolderSettings?.MonitorExistingMode : MonitorTypes.None,

                AudiobookTags = audiobookTags,
                EbookTags = ebookTags,
                Tags = audiobookTags.Concat(ebookTags).ToHashSet()
            };

            config.QueueIfUnavailable = true;
            config.RequestedBy = $"ImportList:{importList.Name}";
            config.AuthorName = authorName;
            config.SearchForMissingBooks = importList.ShouldSearch;

            return config;
        }

        private bool QueueAuthorForImportList(ImportListDefinition importList,
            string authorProviderId,
            string authorName,
            MonitoringConfig config,
            ISet<string> pendingAuthors,
            string context,
            IEnumerable<ImportListItemInfo> reports,
            List<ImportListExclusion> listExclusions,
            bool authorExists = false)
        {
            if ((!authorExists && !importList.GetMonitoringOptions().AddMissingAuthors) || authorProviderId.IsNullOrWhiteSpace())
            {
                return false;
            }

            // Filter requested sides before persisting intent so excluded books cannot become endless retries.
            var requestedAudiobooks = config.AudiobookBooksToMonitor?.Count ?? 0;
            var requestedEbooks = config.EbookBooksToMonitor?.Count ?? 0;
            config.AudiobookBooksToMonitor?.RemoveAll(id => listExclusions.Any(exclusion =>
                ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, id, BookMediaType.Audiobook) ||
                ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, authorProviderId, BookMediaType.Audiobook)));
            config.EbookBooksToMonitor?.RemoveAll(id => listExclusions.Any(exclusion =>
                ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, id, BookMediaType.Ebook) ||
                ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, authorProviderId, BookMediaType.Ebook)));
            if ((requestedAudiobooks > 0 && config.AudiobookBooksToMonitor?.Any() != true) ||
                listExclusions.Any(exclusion => ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, authorProviderId, BookMediaType.Audiobook)))
            {
                config.CreateAudiobook = false;
            }

            if ((requestedEbooks > 0 && config.EbookBooksToMonitor?.Any() != true) ||
                listExclusions.Any(exclusion => ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, authorProviderId, BookMediaType.Ebook)))
            {
                config.CreateEbook = false;
            }

            if (!config.CreateAudiobook && !config.CreateEbook)
            {
                return false;
            }

            config.QueueIfUnavailable = true;
            config.RequestedBy ??= $"ImportList:{importList?.Name ?? "Unknown"}";
            config.AuthorName ??= authorName;
            config.SearchForMissingBooks = !authorExists && importList.ShouldSearch &&
                ((config.CreateAudiobook && config.AudiobookMonitored == true) ||
                 (config.CreateEbook && config.EbookMonitored == true));
            if (importList.ShouldSearch)
            {
                // Existing authors keep row-scoped list intent; catalog-wide search belongs to first author setup.
                config.AudiobookBooksToSearch = !authorExists && config.AudiobookMonitored == true ? null : config.AudiobookBooksToMonitor?.ToList();
                config.EbookBooksToSearch = !authorExists && config.EbookMonitored == true ? null : config.EbookBooksToMonitor?.ToList();
            }

            var audiobookWorkIds = (config.AudiobookBooksToMonitor ?? new List<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ebookWorkIds = (config.EbookBooksToMonitor ?? new List<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            config.AudiobookNarrations = reports.Select(report => BuildRequestedNarration(report, importList.ShouldSearch &&
                    (authorExists || (report.EditionMediaType == BookMediaType.Ebook ? config.EbookMonitored : config.AudiobookMonitored) != true)))
                .Where(request => request != null && (request.MediaType == BookMediaType.Audiobook
                    ? config.CreateAudiobook && audiobookWorkIds.Contains(request.WorkProviderId)
                    : config.CreateEbook && ebookWorkIds.Contains(request.WorkProviderId))).ToList();

            var pendingId = _pendingAuthorImportService.EnqueueAsync(authorProviderId, config, config.RequestedBy)
                .GetAwaiter()
                .GetResult();

            if (pendingId <= 0)
            {
                throw new InvalidOperationException("Pending import request was not durably accepted");
            }

            pendingAuthors?.Add(authorProviderId);
            _logger.Info("Queued author from import list for background import ({0}): {1}", context, authorName ?? authorProviderId);
            return true;
        }

        private void PushImportListPendingImportDrain()
        {
            _commandQueueManager.Push(new ProcessPendingImportsCommand
            {
                BatchSize = ImportListPendingImportBatchSize,
                ContinueUntilEmpty = true
            }, CommandPriority.Normal);
        }

        private void AddListedBooksToMonitor(ImportListDefinition importList, MonitoringConfig config, IEnumerable<ImportListItemInfo> reports)
        {
            if (config == null || reports == null)
            {
                return;
            }

            var bookIds = reports.Select(GetBookProviderId).Where(id => id.IsNotNullOrWhiteSpace()).ToList();
            var options = importList.GetMonitoringOptions();
            config.AudiobookBooksToMonitor = config.CreateAudiobook && options.GetMonitorListedBooks(BookMediaType.Audiobook, importList.ShouldMonitor)
                ? (config.AudiobookBooksToMonitor ?? Enumerable.Empty<string>()).Concat(bookIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : null;
            config.EbookBooksToMonitor = config.CreateEbook && options.GetMonitorListedBooks(BookMediaType.Ebook, importList.ShouldMonitor)
                ? (config.EbookBooksToMonitor ?? Enumerable.Empty<string>()).Concat(bookIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : null;
        }


        private static string NormalizeProviderId(string value, string defaultPrefix)
        {
            return ImportListProviderIdHelper.Normalize(value, defaultPrefix);
        }

        private static (string prefix, string rawId) SplitProviderId(string providerId)
        {
            if (providerId.IsNullOrWhiteSpace())
            {
                return (null, null);
            }

            providerId = providerId.Trim();
            var idx = providerId.IndexOf(':');
            if (idx <= 0 || idx == providerId.Length - 1)
            {
                return (null, providerId);
            }

            return (providerId.Substring(0, idx).ToLowerInvariant(), providerId.Substring(idx + 1));
        }

        internal static bool MatchesExclusionId(string providerId, string exclusionId)
        {
            return ImportListProviderIdHelper.MatchesExclusionId(providerId, exclusionId);
        }

        private string GetAuthorProviderId(ImportListItemInfo report)
        {
            return NormalizeProviderId(report?.AuthorProviderId, "gr");
        }

        private string GetBookProviderId(ImportListItemInfo report)
        {
            return NormalizeProviderId(report?.BookProviderId, "gr");
        }

        private static IEnumerable<string> GetBookProviderIds(Book book)
        {
            if (book == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var providerId in new[]
            {
                NormalizeProviderId(book.HardcoverBookId, "hc"),
                NormalizeProviderId(book.GoodreadsWorkId, "gr"),
                NormalizeProviderId(book.OpenLibraryWorkId, "ol")
            }.Concat(BookEditionIdentity.GetCanonicalEditionProviderIds(book))
             .Concat(book.RemoteProviderIds ?? Enumerable.Empty<string>()))
            {
                if (providerId.IsNotNullOrWhiteSpace() && seen.Add(providerId))
                {
                    yield return providerId;
                }
            }
        }

        private static IEnumerable<string> GetAuthorProviderIds(Author author)
        {
            return AuthorIdentity.GetProviderIdentityTokenList(author);
        }

        private static string GetPreferredBookProviderId(Book book)
        {
            return GetBookProviderIds(book).FirstOrDefault();
        }

        private static string GetPreferredAuthorProviderId(Author author)
        {
            return GetAuthorProviderIds(author).FirstOrDefault();
        }

        private sealed class ImportListLocalLookup
        {
            private readonly Dictionary<string, Author> _authors = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<Book>> _books = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<Book>> _editionBooks = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<int, Author> _authorsById = new();
            private readonly Dictionary<int, Book> _booksById = new();
            private readonly Dictionary<int, HashSet<BookMediaType>> _mediaTypesByAuthor = new();

            public bool HasBooks(int authorId, BookMediaType mediaType) => _mediaTypesByAuthor.TryGetValue(authorId, out var types) && types.Contains(mediaType);

            public int AuthorCount { get; private set; }
            public int BookCount { get; private set; }

            public void AddAuthor(Author author)
            {
                if (author == null)
                {
                    return;
                }

                AuthorCount++;
                _authorsById[author.Id] = author;

                foreach (var providerId in GetAuthorProviderIds(author))
                {
                    AddAuthorKey(providerId, author);
                }
            }

            public void AddBook(Book book)
            {
                if (book == null)
                {
                    return;
                }

                BookCount++;
                _booksById[book.Id] = book;
                if (!_mediaTypesByAuthor.TryGetValue(book.AuthorId, out var mediaTypes))
                {
                    mediaTypes = new HashSet<BookMediaType>();
                    _mediaTypesByAuthor[book.AuthorId] = mediaTypes;
                }

                mediaTypes.Add(book.MediaType);
                foreach (var edition in book.Editions ?? new List<Edition>())
                {
                    foreach (var id in BookEditionIdentity.GetEditionProviderIds(edition))
                    {
                        if (!_editionBooks.TryGetValue(id, out var matches))
                        {
                            matches = new List<Book>();
                            _editionBooks[id] = matches;
                        }

                        matches.Add(book);
                    }
                }

                foreach (var providerId in BookIdentity.GetStableWorkProviderIdentityTokens(book))
                {
                    AddBookKey(providerId, book);
                }
            }

            public Book FindOwnedBook(string workId, string editionId, IEnumerable<BookMediaType> mediaTypes, int? cachedAuthorId = null)
            {
                var editionKey = BookEditionIdentity.NormalizeRequestedEditionProviderId(editionId);
                var books = FindBooks(workId).Where(book => mediaTypes.Contains(book.MediaType)).ToList();
                var editionBooks = editionKey != null && _editionBooks.TryGetValue(editionKey, out var editions)
                    ? editions.Where(book => mediaTypes.Contains(book.MediaType)).ToList() : new List<Book>();
                if (editionBooks.Count > 0 && books.Select(book => book.AuthorId).Distinct().Count() != 1)
                {
                    var compatibleEditionBooks = editionBooks.Where(book => MatchesWork(workId, book, mediaTypes)).ToList();
                    if (compatibleEditionBooks.Count == 0)
                    {
                        return null;
                    }

                    // Edition membership cannot select a primary from multiple stored work owners.
                    if (books.Count == 0)
                    {
                        books = compatibleEditionBooks.Concat(compatibleEditionBooks
                                .SelectMany(BookIdentity.GetStableWorkProviderIdentityTokens)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .SelectMany(FindBooks)
                                .Where(book => mediaTypes.Contains(book.MediaType)))
                            .DistinctBy(book => book.Id).ToList();
                    }
                }

                if (cachedAuthorId.HasValue)
                {
                    books = books.Where(book => book.AuthorId == cachedAuthorId.Value).ToList();
                }

                if (books.Count == 0 || books.Select(book => book.AuthorId).Distinct().Count() != 1 ||
                    !_authorsById.ContainsKey(books[0].AuthorId))
                {
                    return null;
                }

                return books.OrderBy(book => book.Id).First();
            }

            public bool MatchesWork(string workId, Book book, IEnumerable<BookMediaType> mediaTypes)
            {
                if (workId.IsNullOrWhiteSpace())
                {
                    return true;
                }

                var workTokens = BookIdentity.GetStableWorkProviderIdentityTokens(book);
                if (workTokens.Contains(workId) || FindBooks(workId).Any(local => mediaTypes.Contains(local.MediaType) &&
                    BookIdentity.MatchesByStableWorkProviderId(local, book)))
                {
                    return true;
                }

                var requested = new Book { RemoteProviderIds = new HashSet<string> { workId } };
                return workTokens.Count == 0 && BookIdentity.GetStableWorkProviderIdentityTokens(requested).Count == 0 &&
                    mediaTypes.Contains(book.MediaType) && GetBookProviderIds(book).Contains(workId, StringComparer.OrdinalIgnoreCase);
            }

            public bool TryMapBookReport(ImportListItemInfo report, IEnumerable<BookMediaType> mediaTypes = null)
            {
                mediaTypes ??= new[] { BookMediaType.Audiobook, BookMediaType.Ebook };
                report.AuthorProviderId = NormalizeProviderId(report.AuthorProviderId, "gr");
                report.BookProviderId = NormalizeProviderId(report.BookProviderId, "gr");
                report.EditionProviderId = NormalizeProviderId(report.EditionProviderId, "gr");
                var book = FindOwnedBook(report.BookProviderId, report.EditionProviderId, mediaTypes);
                if (book == null)
                {
                    return false;
                }

                var author = _authorsById[book.AuthorId];
                report.BookProviderId ??= GetPreferredBookProviderId(book);
                report.AuthorProviderId = GetPreferredAuthorProviderId(author);
                report.Book ??= book.Title;
                report.Author = author.Name;
                var source = FindBooks(report.BookProviderId, report.EditionProviderId)
                    .Where(candidate => mediaTypes.Contains(candidate.MediaType) && candidate.AuthorId == author.Id &&
                        MatchesWork(report.BookProviderId, candidate, mediaTypes))
                    .SelectMany(candidate => candidate.Editions ?? new List<Edition>())
                    .FirstOrDefault(edition => BookEditionIdentity.EditionMatchesProviderId(edition, report.EditionProviderId));
                if (source != null && report.EditionHasNarration != true)
                {
                    report.EditionHasNarration = BookEditionIdentity.IsNarratedAudiobookEdition(source);
                }
                if (BookEditionIdentity.IsNarratedAudiobookEdition(source))
                {
                    report.EditionNarratorNames = BookEditionIdentity.GetNarratorNames(source);
                }

                return report.BookProviderId.IsNotNullOrWhiteSpace() && report.AuthorProviderId.IsNotNullOrWhiteSpace();
            }

            public Author FindAuthor(string providerId)
            {
                foreach (var key in ExpandProviderLookupKeys(providerId))
                {
                    if (_authors.TryGetValue(key, out var author))
                    {
                        return author;
                    }
                }

                return null;
            }

            public IEnumerable<Book> FindBooks(string providerId)
            {
                if (providerId != null && _books.TryGetValue(providerId.Trim(), out var books))
                {
                    return books;
                }

                var key = providerId?.Trim();
                // These providers number works and editions independently; a missing work is not an edition lookup.
                var isWorkId = key?.StartsWith("hc:", StringComparison.OrdinalIgnoreCase) == true ||
                    key?.StartsWith("gr:", StringComparison.OrdinalIgnoreCase) == true;
                return key != null && !isWorkId && _editionBooks.TryGetValue(key, out var book)
                    ? book : Enumerable.Empty<Book>();
            }

            public IEnumerable<Book> FindBooks(string workId, string editionId)
            {
                var key = BookEditionIdentity.NormalizeRequestedEditionProviderId(editionId);
                var editions = key != null && _editionBooks.TryGetValue(key, out var books) ? books : Enumerable.Empty<Book>();
                return FindBooks(workId).Concat(editions).DistinctBy(book => book.Id);
            }

            public Book FindBook(string providerId) => FindBooks(providerId).FirstOrDefault();

            public void AddBookAlias(string providerId, Book book) => AddBookKey(providerId, book);

            public void AddProviderMatches((string Scope, string Provider, string Id) key, IEnumerable<int> ids)
            {
                foreach (var id in ids)
                {
                    if (!_booksById.TryGetValue(id, out var book))
                    {
                        continue;
                    }

                    if (key.Scope == "work")
                    {
                        AddBookKey(key.Id, book);
                    }
                    else if (key.Scope == "edition")
                    {
                        var providerId = BookEditionIdentity.NormalizeRequestedEditionProviderId(key.Id);
                        if (!_editionBooks.TryGetValue(providerId, out var matches))
                        {
                            matches = new List<Book>();
                            _editionBooks[providerId] = matches;
                        }

                        if (!matches.Any(candidate => candidate.Id == book.Id))
                        {
                            matches.Add(book);
                        }
                    }
                }
            }

            private void AddAuthorKey(string providerId, Author author)
            {
                foreach (var key in ExpandProviderLookupKeys(providerId))
                {
                    if (!_authors.ContainsKey(key))
                    {
                        _authors[key] = author;
                    }
                }
            }

            private void AddBookKey(string providerId, Book book)
            {
                foreach (var key in ExpandProviderLookupKeys(providerId))
                {
                    if (!_books.TryGetValue(key, out var books))
                    {
                        books = new List<Book>();
                        _books[key] = books;
                    }

                    books.Add(book);
                }
            }

            private static IEnumerable<string> ExpandProviderLookupKeys(string providerId)
            {
                if (providerId.IsNullOrWhiteSpace())
                {
                    yield break;
                }

                yield return providerId.Trim();
            }
        }

        private sealed class ImportListSyncStats
        {
            private readonly HashSet<string> _existingAuthors = new(StringComparer.OrdinalIgnoreCase);

            public int EditionLookups;
            public int WorkLookups;
            public int CacheFailures;
            public int EvidenceRejections;
            public Exception CacheError;
            public bool CacheReadFailed;

            public void MarkCacheFailure(Exception exception)
            {
                Interlocked.Increment(ref CacheFailures);
                Interlocked.CompareExchange(ref CacheError, exception, null);
            }

            public int MissingProviderIds { get; private set; }
            public int Excluded { get; private set; }

            public int ExistingAuthors => _existingAuthors.Count;

            public void MarkMissingProviderIds()
            {
                MissingProviderIds++;
            }

            public void MarkExcluded()
            {
                Excluded++;
            }

            public void MarkExistingAuthor(string providerId)
            {
                if (providerId.IsNotNullOrWhiteSpace())
                {
                    _existingAuthors.Add(providerId.Trim());
                }
            }
        }

        private ImportListLocalLookup BuildImportListLocalLookup(IReadOnlySet<int> editionBookIds, int incompleteLookups, Exception lookupError)
        {
            var stopwatch = Stopwatch.StartNew();
            var lookup = new ImportListLocalLookup();

            foreach (var author in _authorService.GetAllAuthors() ?? new List<Author>())
            {
                lookup.AddAuthor(author);
            }

            var books = _bookService.GetImportListBookIdentities() ?? new List<Book>();
            if (editionBookIds == null)
            {
                _logger.Warn(lookupError, "Import list sync: {0} provider lookups incomplete; loading editions for all {1} books this run", incompleteLookups, books.Count);
            }
            var unloadedBooks = books.Where(book => book?.Id > 0 && book.LazyEditions?.IsLoaded == false).ToList();
            var booksNeedingEditions = unloadedBooks.Where(book => editionBookIds == null || editionBookIds.Contains(book.Id)).ToList();
            // All work identities remain available for owner/cached-mapping checks. Only requested
            // works/editions need edition metadata; never let indexing trigger a lazy query per book.
            foreach (var book in unloadedBooks) book.Editions = new List<Edition>();
            if (booksNeedingEditions.Count > 0)
            {
                var editionsByBook = _editionService.GetEditionRowsByBook(booksNeedingEditions.Select(book => book.Id))
                    .GroupBy(edition => edition.BookId)
                    .ToDictionary(group => group.Key, group => group.ToList());
                foreach (var book in booksNeedingEditions)
                {
                    book.Editions = editionsByBook.GetValueOrDefault(book.Id) ?? new List<Edition>();
                }
            }

            foreach (var book in books)
            {
                lookup.AddBook(book);
            }

            _logger.Debug("Indexed {0} authors and {1} books for import lists; loaded editions for {2} books in {3} ms", lookup.AuthorCount, lookup.BookCount, booksNeedingEditions.Count, stopwatch.ElapsedMilliseconds);
            return lookup;
        }

        private Book FindExistingBook(string bookProviderId, ImportListLocalLookup localLookup, Dictionary<string, Book> liveBookLookupHitCache = null)
        {
            if (bookProviderId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var indexedBook = localLookup?.FindBook(bookProviderId);
            if (indexedBook != null)
            {
                return indexedBook;
            }

            var book = FindExistingBookLive(bookProviderId, liveBookLookupHitCache);
            if (book != null)
            {
                localLookup?.AddBookAlias(bookProviderId, book);
            }

            return book;
        }

        private Book FindExistingBookLive(string bookProviderId, Dictionary<string, Book> liveBookLookupHitCache = null)
        {
            if (bookProviderId.IsNullOrWhiteSpace())
            {
                return null;
            }

            bookProviderId = bookProviderId.Trim();

            if (liveBookLookupHitCache != null && liveBookLookupHitCache.TryGetValue(bookProviderId, out var cachedBook))
            {
                return cachedBook;
            }

            var (prefix, _) = SplitProviderId(bookProviderId);
            if (prefix.IsNullOrWhiteSpace())
            {
                return null;
            }

            var book = _bookService.FindByProviderId(prefix, bookProviderId);

            if (liveBookLookupHitCache != null)
            {
                liveBookLookupHitCache[bookProviderId] = book;
            }

            return book;
        }

        private Author FindExistingAuthor(string authorProviderId, ImportListLocalLookup localLookup, Dictionary<string, Author> liveAuthorLookupHitCache = null)
        {
            if (authorProviderId.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (localLookup != null)
            {
                var indexedAuthor = localLookup.FindAuthor(authorProviderId);
                if (indexedAuthor != null)
                {
                    return indexedAuthor;
                }
            }

            return FindExistingAuthorLive(authorProviderId, liveAuthorLookupHitCache);
        }

        private Author FindExistingAuthorLive(string authorProviderId, Dictionary<string, Author> liveAuthorLookupHitCache = null)
        {
            if (authorProviderId.IsNullOrWhiteSpace())
            {
                return null;
            }

            authorProviderId = authorProviderId.Trim();

            if (liveAuthorLookupHitCache != null && liveAuthorLookupHitCache.TryGetValue(authorProviderId, out var cachedAuthor))
            {
                return cachedAuthor;
            }

            var (prefix, _) = SplitProviderId(authorProviderId);
            if (prefix.IsNullOrWhiteSpace())
            {
                return null;
            }

            var author = _authorService.FindByProviderId(prefix, authorProviderId);

            if (liveAuthorLookupHitCache != null)
            {
                liveAuthorLookupHitCache[authorProviderId] = author;
            }

            return author;
        }

        private bool ShouldProcessBookReport(ImportListItemInfo report, List<ImportListExclusion> listExclusions, ImportListLocalLookup localLookup, ImportListSyncStats stats = null, Dictionary<string, Book> liveBookLookupHitCache = null)
        {
            var bookProviderId = GetBookProviderId(report);
            var authorProviderId = GetAuthorProviderId(report);

            // Check if book already exists
            var existingBook = FindExistingBook(bookProviderId, localLookup, liveBookLookupHitCache);

            // Check if book is excluded by any provider ID
            if (existingBook != null && IsBookExcludedByAnyProviderId(listExclusions, existingBook))
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion (matched by provider ID)", report.EditionProviderId, report.Book);
                stats?.MarkExcluded();
                return false;
            }

            // For new books, check if the provider ID from the report is excluded
            if (existingBook == null && bookProviderId.IsNotNullOrWhiteSpace() && listExclusions.Any(s => ImportListExclusionBookMatcher.AppliesToProviderId(s, bookProviderId, null)))
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion", report.EditionProviderId, report.Book);
                stats?.MarkExcluded();
                return false;
            }

            // Exclusions follow the proven owner without reloading each matched book's author.
            var existingAuthor = localLookup.FindAuthor(authorProviderId);
            if (existingAuthor != null)
            {
                if (IsAuthorExcludedByAnyProviderId(listExclusions, existingAuthor, null))
                {
                    _logger.Debug("{0} [{1}] Rejected due to list exclusion for parent author", report.EditionProviderId, report.Book);
                    stats?.MarkExcluded();
                    return false;
                }
            }
            else if (authorProviderId.IsNotNullOrWhiteSpace() && listExclusions.Any(s => MatchesExclusionId(authorProviderId, s.ForeignId)))
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion for parent author", report.EditionProviderId, report.Book);
                stats?.MarkExcluded();
                return false;
            }

            return true;
        }

        private bool ShouldProcessAuthorReport(ImportListItemInfo report, List<ImportListExclusion> listExclusions, ImportListLocalLookup localLookup, Dictionary<string, Author> liveAuthorLookupHitCache = null, ImportListSyncStats stats = null)
        {
            var authorProviderId = GetAuthorProviderId(report);
            if (authorProviderId.IsNullOrWhiteSpace())
            {
                return false;
            }

            // Check if author already exists
            var existingAuthor = FindExistingAuthor(authorProviderId, localLookup, liveAuthorLookupHitCache);

            // Check if author excluded by any provider ID
            if (existingAuthor != null && IsAuthorExcludedByAnyProviderId(listExclusions, existingAuthor, null))
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion (matched by provider ID)", report.AuthorProviderId, report.Author);
                stats?.MarkExcluded();
                return false;
            }

            // For new authors, check if the provider ID from the report is excluded
            if (existingAuthor == null && listExclusions.Any(s => MatchesExclusionId(authorProviderId, s.ForeignId)))
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion", report.AuthorProviderId, report.Author);
                stats?.MarkExcluded();
                return false;
            }

            return true;
        }

        private bool IsBookExcludedByAnyProviderId(List<ImportListExclusion> listExclusions, Book book)
        {
            if (book == null) return false;

            return listExclusions.Any(exclusion => ImportListExclusionBookMatcher.AppliesToBook(exclusion, book));
        }

        private static bool? IsReportedAudioEdition(ImportListItemInfo report)
        {
            if (report?.IsGenericBookRequest == true) return false;
            return report?.EditionHasNarration ?? (report?.HardcoverReadingFormatId switch
            {
                1 or 4 => false,
                _ => null
            });
        }

        private RequestedNarration BuildRequestedNarration(ImportListItemInfo report, bool search)
        {
            if (report.IsGenericBookRequest || report.EditionProviderId.IsNullOrWhiteSpace() ||
                (report.EditionMediaType != BookMediaType.Ebook && IsReportedAudioEdition(report) == false))
            {
                return null;
            }

            var mediaType = report.EditionMediaType == BookMediaType.Ebook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            return new RequestedNarration
            {
                MediaType = mediaType,
                WorkProviderId = GetBookProviderId(report),
                EditionProviderId = BookEditionIdentity.NormalizeRequestedEditionProviderId(NormalizeProviderId(report.EditionProviderId, "gr")),
                NarratorNames = mediaType == BookMediaType.Audiobook ? report.EditionNarratorNames?.ToList() : null,
                RequiresEdition = mediaType == BookMediaType.Ebook || IsReportedAudioEdition(report) == true,
                Search = search
            };
        }

        private static bool NeedsMapping(ImportListItemInfo report, ImportListLocalLookup localLookup, IEnumerable<BookMediaType> mediaTypes)
        {
            return ((report.Book.IsNotNullOrWhiteSpace() || report.BookProviderId.IsNotNullOrWhiteSpace() || report.EditionProviderId.IsNotNullOrWhiteSpace()) &&
                    !localLookup.TryMapBookReport(report, mediaTypes)) ||
                   (report.EditionProviderId.IsNotNullOrWhiteSpace() && IsReportedAudioEdition(report) == null);
        }

        private bool IsAuthorExcludedByAnyProviderId(List<ImportListExclusion> listExclusions, Author author, BookMediaType? mediaType)
        {
            if (author == null) return false;

            var providerIds = AuthorIdentity.GetProviderIdentityTokenList(author);
            return listExclusions.Any(exclusion => ImportListExclusionBookMatcher.AppliesToMediaType(exclusion, mediaType) && providerIds.Any(pid => MatchesExclusionId(pid, exclusion.ForeignId)));
        }

        private List<Book> SyncAll()
        {
            var enabledImportLists = _importListFactory.AutomaticAddEnabled()
                .Where(l => !l.Definition.Implementation.EqualsIgnoreCase(nameof(HardcoverLibraryImportList)))
                .ToList();

            if (enabledImportLists.Empty())
            {
                _logger.Debug("No import lists with automatic add enabled");

                return new List<Book>();
            }

            _logger.ProgressInfo("Starting Import List Sync");

            var listItems = _listFetcherAndParser.Fetch().ToList();

            return ProcessListItems(listItems);
        }

        private List<Book> SyncList(ImportListDefinition definition)
        {
            _logger.ProgressInfo($"Starting Import List Refresh for List {definition.Name}");

            var importList = _importListFactory.GetInstance(definition);
            var listItems = _listFetcherAndParser.FetchSingleList(importList).ToList();
            var failuresByList = new ConcurrentDictionary<int, int>();
            var processed = ProcessListItems(listItems, failuresByList);
            CommitHardcoverState(importList, failuresByList);
            return processed;
        }

        private List<Book> ProcessListItems(List<ImportListItemInfo> items, ConcurrentDictionary<int, int> failuresByList = null)
        {
            var stopwatch = Stopwatch.StartNew();
            var processed = new List<Book>();
            var bookIdsToSearch = new HashSet<int>();
            var selectedBooks = new Dictionary<int, Book>();
            var changedAddOptions = new Dictionary<int, Book>();
            var newlyMonitoredIds = new HashSet<int>();
            var changedEditionBookIds = new HashSet<int>();
            var authorsToMonitor = new Dictionary<string, ImportListItemInfo>(StringComparer.OrdinalIgnoreCase); // list and author -> report
            var booksToMonitor = new List<(string authorProviderId, string bookProviderId, ImportListItemInfo report)>();
            var pendingAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingExistingAuthors = new Dictionary<string, (HashSet<string> Audiobooks, HashSet<string> Ebooks)>(StringComparer.OrdinalIgnoreCase);
            var roots = new Dictionary<string, RootFolder>(StringComparer.Ordinal);
            var booksMonitored = 0;
            var missingBookSides = 0;
            var failedItems = 0;
            var appliedBooksByList = new Dictionary<int, HashSet<int>>();
            var missingSidesByList = new Dictionary<int, int>();
            var pendingByList = new HashSet<(int ListId, string AuthorId)>();
            failuresByList ??= new ConcurrentDictionary<int, int>();
            void MarkFailure(int listId) => failuresByList.AddOrUpdate(listId, 1, (_, count) => count + 1);

            if (items.Count == 0)
            {
                _logger.ProgressInfo("No list items to process");
                return new List<Book>();
            }

            var stats = new ImportListSyncStats();
            var liveAuthorLookupHitCache = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);
            var liveBookLookupHitCache = new Dictionary<string, Book>(StringComparer.OrdinalIgnoreCase);

            _logger.ProgressInfo("Import list sync: fetched {0} list items; indexing local library", items.Count);
            var lookupKeys = items.SelectMany(report => new[]
                {
                    (Scope: "work", Id: NormalizeProviderId(report.BookProviderId, "gr")),
                    (Scope: "edition", Id: BookEditionIdentity.NormalizeRequestedEditionProviderId(NormalizeProviderId(report.EditionProviderId, "gr")))
                }).Where(key => key.Id.IsNotNullOrWhiteSpace()).Select(key =>
                {
                    var (provider, _) = SplitProviderId(key.Id);
                    return (key.Scope, Provider: provider, key.Id);
                }).Where(key => key.Provider.IsNotNullOrWhiteSpace()).Distinct().ToList();
            Dictionary<(string Scope, string Provider, string Id), List<int>> providerMatches = null;
            var incomplete = new HashSet<(string Scope, string Provider, string Id)>();
            var lookupTimer = Stopwatch.StartNew();
            Exception lookupError = null;
            try
            {
                providerMatches = lookupKeys.Count == 0 ? new() : _bookService.FindBookIdsByProviderIds(lookupKeys, key => incomplete.Add(key));
            }
            catch (Exception ex)
            {
                lookupError = ex;
                incomplete.UnionWith(lookupKeys);
            }
            _logger.Debug("Import list sync: batch lookup of {0} provider keys, {1} incomplete, took {2} ms", lookupKeys.Count, incomplete.Count, lookupTimer.ElapsedMilliseconds);
            var localLookup = BuildImportListLocalLookup(incomplete.Count == 0 ? providerMatches?.Values.SelectMany(ids => ids).ToHashSet() : null, incomplete.Count, lookupError);
            if (providerMatches != null)
            {
                foreach (var (key, ids) in providerMatches)
                {
                    localLookup.AddProviderMatches(key, ids);
                    if (key.Scope == "work" && !incomplete.Contains(key))
                        liveBookLookupHitCache[key.Id] = localLookup.FindBook(key.Id);
                }
            }
            _logger.ProgressInfo("Import list sync: local match index ready ({0} authors, {1} books)",
                localLookup.AuthorCount, localLookup.BookCount);


            var reportNumber = 1;
            var listExclusions = _importListExclusionService.All();
            var globalListExclusions = listExclusions.Where(exclusion => !exclusion.MediaType.HasValue).ToList();
            var importListCache = new Dictionary<int, ImportListDefinition>();

            ImportListDefinition GetImportListDefinition(int importListId)
            {
                if (!importListCache.TryGetValue(importListId, out var importList))
                {
                    importList = _importListFactory.Get(importListId);
                    importListCache[importListId] = importList;
                }

                return importList;
            }

            var listMediaTypes = new Dictionary<int, (bool audiobook, bool ebook)>();
            (bool audiobook, bool ebook) GetListMediaTypes(ImportListDefinition list)
            {
                if (!listMediaTypes.TryGetValue(list.Id, out var mediaTypes))
                {
                    var settings = (DualMediaImportListSettings)list.Settings;
                    mediaTypes = (settings.MonitorAudiobooks, settings.MonitorEbooks);

                    listMediaTypes[list.Id] = mediaTypes;
                }

                return mediaTypes;
            }

            var requestedMediaTypes = new Dictionary<int, BookMediaType[]>();
            var unavailableLists = new HashSet<int>();
            foreach (var listItems in items.GroupBy(report => report.ImportListId))
            {
                try
                {
                    var list = GetImportListDefinition(listItems.Key);
                    if (list == null)
                    {
                        throw new InvalidOperationException("Import list definition is no longer available");
                    }
                    var sides = GetListMediaTypes(list);
                    requestedMediaTypes[listItems.Key] = new[] { BookMediaType.Audiobook, BookMediaType.Ebook }
                        .Where(side => side == BookMediaType.Audiobook ? sides.audiobook : sides.ebook).ToArray();
                }
                catch (Exception ex)
                {
                    unavailableLists.Add(listItems.Key);
                    failedItems += listItems.Count();
                    foreach (var report in listItems)
                    {
                        MarkFailure(report.ImportListId);
                    }
                    _logger.Warn(ex, "Import list sync: could not load settings for list {0}; skipping {1} entries and continuing with other lists",
                        listItems.Key, listItems.Count());
                }
            }
            items = items.Where(report => !unavailableLists.Contains(report.ImportListId)).ToList();

            // Resolve each source once, then collect complete selections before persisting list demand.
            const int maxDegreeOfParallelism = 12;
            const int mappingReportInterval = 100;

            var reportsNeedingMapping = items
                .Where(report => requestedMediaTypes[report.ImportListId].Length > 0 &&
                    NeedsMapping(report, localLookup, requestedMediaTypes[report.ImportListId]))
                .ToList();

            ConcurrentDictionary<string, ImportListBookIdentityCache> sourceMappings = null;
            if (_bookIdentityCacheRepository != null && reportsNeedingMapping.Count > 0)
            {
                try
                {
                    sourceMappings = new ConcurrentDictionary<string, ImportListBookIdentityCache>(
                        _bookIdentityCacheRepository.FindBySourceProviderIds(reportsNeedingMapping.Select(GetMappingSourceProviderId))
                            .ToDictionary(mapping => mapping.SourceProviderId, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    stats.MarkCacheFailure(ex);
                    stats.CacheReadFailed = true;
                    sourceMappings = new ConcurrentDictionary<string, ImportListBookIdentityCache>(StringComparer.OrdinalIgnoreCase);
                }
            }

            var totalToMap = reportsNeedingMapping.Count;
            var directIdentityCount = items.Count(r => GetAuthorProviderId(r).IsNotNullOrWhiteSpace() && GetBookProviderId(r).IsNotNullOrWhiteSpace());
            var mappedCompleted = 0;
            var mappedReady = 0;
            var mappedUnavailable = 0;
            var lastReported = 0;

            _logger.ProgressInfo("Import list sync: checking local matches for {0} items ({1} already include author/book IDs, {2} need metadata ID resolution)",
                items.Count, directIdentityCount, totalToMap);

            if (totalToMap > 0)
            {
                _logger.ProgressInfo("Import list sync: mapping 0/{0} items (concurrency: {1})",
                    totalToMap, maxDegreeOfParallelism);
            }

            var mappingStopwatch = Stopwatch.StartNew();
            Parallel.ForEach(reportsNeedingMapping.GroupBy(GetMappingSourceProviderId),
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
                sourceReports =>
                {
                    var reports = sourceReports.OrderByDescending(report => report.EditionMediaType.HasValue)
                        .ThenByDescending(report => GetAuthorProviderId(report).IsNotNullOrWhiteSpace() &&
                            GetBookProviderId(report).IsNotNullOrWhiteSpace()).ToList();
                    var mappedReport = reports[0];
                    try
                    {
                        var mapped = MapBookReportWithMappings(mappedReport, localLookup, sourceMappings, stats,
                            requestedMediaTypes[mappedReport.ImportListId]);
                        var mappedBook = mapped?.Item2;
                        var mappedAuthor = mapped?.Item3?.FirstOrDefault();
                        var mappingCached = mappedReport.AuthorProviderId.IsNotNullOrWhiteSpace() &&
                            mappedReport.AuthorProviderId.EqualsIgnoreCase(GetPreferredAuthorProviderId(mappedAuthor)) &&
                            localLookup.MatchesWork(mappedReport.BookProviderId, mappedBook, requestedMediaTypes[mappedReport.ImportListId]);
                        foreach (var report in reports.Skip(1))
                        {
                            var mediaTypes = requestedMediaTypes[report.ImportListId];
                            var localMapped = localLookup.TryMapBookReport(report, mediaTypes);
                            if (sourceReports.Key == null || mappedBook == null || GetPreferredAuthorProviderId(mappedAuthor).IsNullOrWhiteSpace() ||
                                !localLookup.MatchesWork(report.BookProviderId, mappedBook, mediaTypes))
                            {
                                if (!localMapped)
                                {
                                    report.AuthorProviderId = null;
                                    if (mappedBook != null)
                                    {
                                        Interlocked.Increment(ref stats.EvidenceRejections);
                                        _logger.Warn("Metadata mapping for {0} rejected: requested work or selected primary does not match; retaining only proven local intent", GetMappingSourceProviderId(report));
                                    }
                                }
                                continue;
                            }

                            report.BookProviderId ??= GetPreferredBookProviderId(mappedBook);
                            report.AuthorProviderId = GetPreferredAuthorProviderId(mappedAuthor);
                            report.Book ??= mappedBook.Title;
                            report.Author = mappedAuthor.Name;
                            var edition = mappedBook.Editions?.FirstOrDefault(candidate => BookEditionIdentity.EditionMatchesProviderId(candidate, report.EditionProviderId));
                            if (edition != null)
                            {
                                report.EditionHasNarration = BookEditionIdentity.IsNarratedAudiobookEdition(edition);
                                report.EditionNarratorNames = report.EditionHasNarration == true ? BookEditionIdentity.GetNarratorNames(edition) : null;
                            }
                            else
                            {
                                report.EditionHasNarration ??= mappedReport.EditionHasNarration;
                                report.EditionNarratorNames ??= mappedReport.EditionNarratorNames?.ToList();
                            }
                            if (!mappingCached)
                            {
                                CacheBookReportMapping(report, sourceMappings, stats);
                                mappingCached = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        foreach (var report in reports)
                        {
                            if (!localLookup.TryMapBookReport(report, requestedMediaTypes[report.ImportListId]))
                            {
                                report.AuthorProviderId = null;
                            }
                            MarkFailure(report.ImportListId);
                        }
                        _logger.Warn(ex, "Import list sync: failed to map source {0} ({1} - {2}); continuing with other entries",
                            sourceReports.Key, mappedReport.Author, mappedReport.Book);
                    }
                    finally
                    {
                        foreach (var report in reports)
                        {
                            if (GetAuthorProviderId(report).IsNotNullOrWhiteSpace() && GetBookProviderId(report).IsNotNullOrWhiteSpace())
                            {
                                Interlocked.Increment(ref mappedReady);
                            }
                            else
                            {
                                Interlocked.Increment(ref mappedUnavailable);
                            }

                            var completed = Interlocked.Increment(ref mappedCompleted);
                            var reported = Volatile.Read(ref lastReported);
                            if (completed == totalToMap || (completed - reported >= mappingReportInterval &&
                                Interlocked.CompareExchange(ref lastReported, completed, reported) == reported))
                            {
                                _logger.ProgressInfo("Import list sync: resolving metadata identities {0}/{1} (ready: {2}, unavailable: {3})",
                                    completed, totalToMap, Volatile.Read(ref mappedReady), Volatile.Read(ref mappedUnavailable));
                            }
                        }
                    }
                });

            _logger.Info("Import list sync: {0} edition lookups, {1} work lookups, {2} unresolved narration sources, {3} source evidence rejections; mapping elapsed: {4} ms",
                stats.EditionLookups, stats.WorkLookups,
                reportsNeedingMapping.Where(report => report.EditionProviderId.IsNotNullOrWhiteSpace() && IsReportedAudioEdition(report) == null)
                    .Select(GetMappingSourceProviderId).Distinct().Count(), stats.EvidenceRejections, mappingStopwatch.ElapsedMilliseconds);
            if (stats.CacheFailures > 0)
            {
                _logger.Warn(stats.CacheError, "Import list sync: source cache had {0} failures; continuing with available local and metadata evidence. Uncached sources may require another lookup next sync.", stats.CacheFailures);
            }

            _logger.ProgressInfo("Import list sync: collecting author and book selections");

            // First pass: collect all authors and books to process
            foreach (var report in items)
            {
                var currentReportNumber = reportNumber++;

                if (currentReportNumber == 1 || currentReportNumber % 25 == 0 || currentReportNumber == items.Count)
                {
                    _logger.ProgressInfo("Import list sync: checking list item {0}/{1} (author selections: {2}, books: {3}, unavailable: {4}, excluded: {5})",
                        currentReportNumber, items.Count, authorsToMonitor.Count, booksToMonitor.Count, mappedUnavailable + stats.MissingProviderIds, stats.Excluded);
                }

                if (report.Book.IsNotNullOrWhiteSpace() || report.EditionProviderId.IsNotNullOrWhiteSpace())
                {
                    // Check exclusions
                    if (!ShouldProcessBookReport(report, globalListExclusions, localLookup, stats, liveBookLookupHitCache))
                    {
                        continue;
                    }

                    var authorProviderId = GetAuthorProviderId(report);
                    var bookProviderId = GetBookProviderId(report);
                    if (authorProviderId.IsNullOrWhiteSpace() || bookProviderId.IsNullOrWhiteSpace())
                    {
                        stats.MarkMissingProviderIds();
                        _logger.Debug("Skipping list item due to missing provider IDs: {0} - {1}", report.Author, report.Book);
                        continue;
                    }

                    // Track author if not already tracked
                    authorsToMonitor.TryAdd($"{report.ImportListId}:{authorProviderId}", report);

                    // Track book to monitor
                    booksToMonitor.Add((authorProviderId, bookProviderId, report));

                    }
                    else if (report.Author.IsNotNullOrWhiteSpace() || report.AuthorProviderId.IsNotNullOrWhiteSpace())
                    {
                        var authorProviderId = GetAuthorProviderId(report);
                        if (authorProviderId.IsNullOrWhiteSpace())
                        {
                            stats.MarkMissingProviderIds();
                            _logger.Debug("Skipping author-only list item due to missing provider ID: {0}", report.Author);
                            continue;
                        }

                        // Check exclusions
                        if (!ShouldProcessAuthorReport(report, globalListExclusions, localLookup, liveAuthorLookupHitCache, stats))
                        {
                            continue;
                        }

                        authorsToMonitor.TryAdd($"{report.ImportListId}:{authorProviderId}", report);
                    }
                }

            var reportsByListAndAuthor = booksToMonitor.ToLookup(
                item => (item.report.ImportListId, item.authorProviderId), item => item.report);

            // Process authors and books
            var authorsProcessed = 0;
            var totalAuthorsToProcess = authorsToMonitor.Count;

            bool HasConfiguredSide(Author author, BookMediaType side) => localLookup.HasBooks(author.Id, side) || author.HasMediaTypeSettings(side);

            // Add authors that need to be added
            foreach (var report in authorsToMonitor.Values)
            {
                authorsProcessed++;
                var authorProviderId = GetAuthorProviderId(report);
                var importList = GetImportListDefinition(report.ImportListId);

                try
                {
                    // Check if author already exists
                    var existingAuthor = FindExistingAuthor(authorProviderId, localLookup, liveAuthorLookupHitCache);

                    if (existingAuthor == null)
                    {
                        if (!importList.GetMonitoringOptions().AddMissingAuthors)
                        {
                            continue;
                        }

                        var config = BuildConfigFromImportList(importList, report.Author, roots);
                        AddListedBooksToMonitor(importList, config, reportsByListAndAuthor[(importList.Id, authorProviderId)]);

                        if (QueueAuthorForImportList(importList, authorProviderId, report.Author, config, pendingAuthors, "author pass", reportsByListAndAuthor[(importList.Id, authorProviderId)], listExclusions))
                        {
                            pendingByList.Add((importList.Id, authorProviderId));
                        }
                    }
                    else
                    {
                        stats.MarkExistingAuthor(authorProviderId);
                        var missingAudiobookSide = !HasConfiguredSide(existingAuthor, BookMediaType.Audiobook);
                        var missingEbookSide = !HasConfiguredSide(existingAuthor, BookMediaType.Ebook);
                        var sides = GetListMediaTypes(importList);
                        missingAudiobookSide &= sides.audiobook;
                        missingEbookSide &= sides.ebook;
                        if (importList.GetMonitoringOptions().AddMissingAuthors && (missingAudiobookSide || missingEbookSide) &&
                            reportsByListAndAuthor[(importList.Id, authorProviderId)].Any())
                        {
                            var config = BuildConfigFromImportList(importList, report.Author, roots);
                            config.CreateAudiobook &= missingAudiobookSide;
                            config.CreateEbook &= missingEbookSide;
                            AddListedBooksToMonitor(importList, config, reportsByListAndAuthor[(importList.Id, authorProviderId)]);
                            if (config.CreateAudiobook || config.CreateEbook)
                            {
                                if (QueueAuthorForImportList(importList, authorProviderId, report.Author, config, null,
                                    "missing media side", reportsByListAndAuthor[(importList.Id, authorProviderId)], listExclusions, authorExists: true))
                                {
                                    pendingExistingAuthors[authorProviderId] = (
                                        (config.CreateAudiobook ? config.AudiobookBooksToMonitor : null)?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                        (config.CreateEbook ? config.EbookBooksToMonitor : null)?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                                    pendingByList.Add((importList.Id, authorProviderId));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MarkFailure(importList.Id);
                    _logger.Warn(ex, "Failed to add author {0} from import list; continuing with other entries", report.Author);
                    _logger.ProgressError("Import list '{0}': failed to add author '{1}' ({2})", importList.Name, report.Author, ex.Message);
                }

                if (authorsProcessed == totalAuthorsToProcess || authorsProcessed % 25 == 0)
                {
                    _logger.ProgressInfo("Import list sync: author selections {0}/{1} (queued: {2})",
                        authorsProcessed, totalAuthorsToProcess, pendingAuthors.Count);
                }
            }

            var candidateIds = booksToMonitor.Where(item => !pendingAuthors.Contains(item.authorProviderId))
                .SelectMany(item => localLookup.FindBooks(item.bookProviderId, item.report.EditionProviderId)).Select(book => book.Id).Distinct().ToList();
            var currentBooks = candidateIds.Count == 0 ? new Dictionary<int, Book>() :
                _bookService.GetExistingBooks(candidateIds).ToDictionary(book => book.Id);
            var wasMonitoredById = currentBooks.Values.ToDictionary(book => book.Id, book => book.IsMonitored());
            var selectedEditionById = currentBooks.Values.ToDictionary(book => book.Id, book => BookEditionIdentity.GetMonitoredEdition(book)?.Id);
            var narratorRequests = new Dictionary<ImportListItemInfo, (Edition Edition, Author Author)>();
            var missingRequests = new Dictionary<(int ListId, string AuthorId),
                (Author Author, List<ImportListItemInfo> Reports, HashSet<string> Audiobooks, HashSet<string> Ebooks)>();
            var pendingNarrationReports = new HashSet<ImportListItemInfo>();
            void HoldEditionRequest(ImportListItemInfo report, Author author, BookMediaType mediaType)
            {
                var key = (report.ImportListId, GetAuthorProviderId(report));
                if (!missingRequests.TryGetValue(key, out var missing))
                {
                    missing = (author, new List<ImportListItemInfo>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    missingRequests[key] = missing;
                }
                missing.Reports.Add(report);
                (mediaType == BookMediaType.Audiobook ? missing.Audiobooks : missing.Ebooks).Add(GetBookProviderId(report));
                pendingNarrationReports.Add(report);
            }

            foreach (var item in booksToMonitor.Where(item => !pendingAuthors.Contains(item.authorProviderId)))
            {
                var list = GetImportListDefinition(item.report.ImportListId);
                var request = BuildRequestedNarration(item.report, false);
                var sides = GetListMediaTypes(list);
                if (!list.ShouldMonitorExisting || request == null ||
                    !(request.MediaType == BookMediaType.Audiobook ? sides.audiobook : sides.ebook))
                {
                    continue;
                }

                var author = FindExistingAuthor(item.authorProviderId, localLookup, liveAuthorLookupHitCache);
                if (author == null || !HasConfiguredSide(author, request.MediaType) ||
                    IsAuthorExcludedByAnyProviderId(listExclusions, author, request.MediaType))
                {
                    continue;
                }

                var edition = BookEditionIdentity.OrderRequestedNarrationEditions(localLookup.FindBooks(item.bookProviderId,
                        request.MediaType == BookMediaType.Ebook ? null : item.report.EditionProviderId)
                    .Select(book => currentBooks.GetValueOrDefault(book.Id))
                    .Where(book => book?.MediaType == request.MediaType && book.AuthorId == author.Id &&
                        !IsBookExcludedByAnyProviderId(listExclusions, book))
                    .SelectMany(book => book.Editions ?? new List<Edition>())
                    .Where(edition => request.MediaType == BookMediaType.Ebook
                        ? BookEditionIdentity.EditionMatchesProviderId(edition, request.EditionProviderId)
                        : BookEditionIdentity.IsNarratedAudiobookEdition(edition) &&
                            BookEditionIdentity.MatchesRequestedNarration(edition, request.EditionProviderId, request.NarratorNames))).FirstOrDefault();
                if (edition == null && request.RequiresEdition && !listExclusions.Any(exclusion =>
                    ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, item.bookProviderId, request.MediaType)))
                {
                    HoldEditionRequest(item.report, author, request.MediaType);
                }

                if (edition != null)
                {
                    narratorRequests[item.report] = (edition, author);
                }
            }

            var narratorBooks = narratorRequests.Count == 0 ? new Dictionary<int, Book>() :
                _bookService.AddWantedEditions(narratorRequests.Values.Select(request => request.Edition).DistinctBy(edition => edition.Id).ToList(),
                    (edition, error) => _logger.Warn(error, "Import list: could not select requested edition {0}; continuing with other entries", edition.ForeignEditionId));
            foreach (var (report, request) in narratorRequests.Where(pair => !narratorBooks.ContainsKey(pair.Value.Edition.Id)))
            {
                HoldEditionRequest(report, request.Author, report.EditionMediaType ?? BookMediaType.Audiobook);
            }
            foreach (var book in narratorBooks.Values.DistinctBy(book => book.Id))
            {
                if (!currentBooks.ContainsKey(book.Id))
                {
                    localLookup.AddBook(book);
                }

                currentBooks[book.Id] = book;
                if (selectedEditionById.GetValueOrDefault(book.Id) != BookEditionIdentity.GetMonitoredEdition(book)?.Id)
                {
                    changedEditionBookIds.Add(book.Id);
                }
            }

            var booksProcessed = 0;
            var totalBooksToProcess = booksToMonitor.Count;
            void ApplyListedBooks(ImportListDefinition list, Author author, IEnumerable<Book> candidates, ImportListItemInfo report)
            {
                var mediaTypes = GetListMediaTypes(list);

                var books = candidates.Select(book => currentBooks.GetValueOrDefault(book.Id)).Where(book => book != null)
                    .Where(book => book.MediaType == BookMediaType.Audiobook
                    ? mediaTypes.audiobook : mediaTypes.ebook)
                    .Where(book => !IsBookExcludedByAnyProviderId(listExclusions, book) &&
                        !IsAuthorExcludedByAnyProviderId(listExclusions, author, book.MediaType))
                    .GroupBy(book => book.MediaType)
                    .Select(group => group.Key == (report.EditionMediaType ?? BookMediaType.Audiobook) && pendingNarrationReports.Contains(report) ? null :
                        narratorRequests.TryGetValue(report, out var requested) && group.Key ==
                            (report.EditionMediaType ?? BookMediaType.Audiobook)
                        ? narratorBooks.GetValueOrDefault(requested.Edition.Id) : BookIdentity.SelectRequestedBook(group)).Where(book => book != null).ToList();
                foreach (var book in books)
                {
                    if (!list.ShouldMonitorExisting)
                    {
                        continue;
                    }

                    book.Author = author;
                    var wasMonitored = wasMonitoredById.GetValueOrDefault(book.Id);
                    wasMonitoredById[book.Id] = true;

                    if (list.ShouldSearch && (!wasMonitored || book.AddOptions?.SearchForNewBook == true ||
                        changedEditionBookIds.Contains(book.Id)))
                    {
                        bookIdsToSearch.Add(book.Id);
                    }

                    selectedBooks[book.Id] = book;
                    if (book.AddOptions?.AddType != BookAddType.Manual)
                    {
                        // The list requests this work; edition selection keeps its existing policy.
                        book.AddOptions ??= new AddBookOptions();
                        book.AddOptions.AddType = BookAddType.Manual;
                        changedAddOptions[book.Id] = book;
                    }
                    if (!appliedBooksByList.TryGetValue(list.Id, out var applied))
                    {
                        applied = new HashSet<int>();
                        appliedBooksByList[list.Id] = applied;
                    }
                    applied.Add(book.Id);
                    if (!wasMonitored)
                    {
                        if (!book.IsMonitored())
                        {
                            newlyMonitoredIds.Add(book.Id);
                            book.SetMonitored(true);
                        }
                        booksMonitored++;
                    }

                }

                EnsureAuthorMonitoring(list, author, books.Any(book => book.MediaType == BookMediaType.Audiobook),
                    books.Any(book => book.MediaType == BookMediaType.Ebook));
            }

            // Monitor specific books if needed
            foreach (var (authorProviderId, bookProviderId, report) in booksToMonitor)
            {
                booksProcessed++;
                var importList = GetImportListDefinition(report.ImportListId);

                if (booksProcessed == totalBooksToProcess || booksProcessed % 50 == 0)
                {
                    _logger.ProgressInfo("Import list sync: items {0}/{1} (queued authors: {2}, books monitored: {3})",
                        booksProcessed, totalBooksToProcess, pendingAuthors.Count, booksMonitored);
                }

                try
                {
                    if (!importList.ShouldMonitorExisting && !importList.GetMonitoringOptions().ResumePausedAuthors)
                    {
                        continue;
                    }

                    if (pendingAuthors.Contains(authorProviderId))
                    {
                        continue;
                    }

                    var author = FindExistingAuthor(authorProviderId, localLookup, liveAuthorLookupHitCache);
                    if (author == null)
                    {
                        continue;
                    }

                    var targetBooks = localLookup.FindBooks(bookProviderId, report.EditionProviderId)
                        .Where(book => book.AuthorId == author.Id &&
                            !IsAuthorExcludedByAnyProviderId(listExclusions, author, book.MediaType)).ToList();
                    var sides = GetListMediaTypes(importList);
                    foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                    {
                        if (!(side == BookMediaType.Audiobook ? sides.audiobook : sides.ebook) || !HasConfiguredSide(author, side) ||
                            (pendingExistingAuthors.TryGetValue(authorProviderId, out var queuedTargets) &&
                                (side == BookMediaType.Audiobook ? queuedTargets.Audiobooks : queuedTargets.Ebooks).Contains(bookProviderId)) ||
                            targetBooks.Any(book => book.MediaType == side) ||
                            IsAuthorExcludedByAnyProviderId(listExclusions, author, side) || listExclusions.Any(exclusion =>
                                ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, bookProviderId, side)))
                        {
                            continue;
                        }

                        missingBookSides++;
                        missingSidesByList[importList.Id] = missingSidesByList.GetValueOrDefault(importList.Id) + 1;
                        EnsureAuthorMonitoring(importList, author, side == BookMediaType.Audiobook, side == BookMediaType.Ebook);
                        if (importList.ShouldMonitorExisting)
                        {
                            var key = (importList.Id, authorProviderId);
                            if (!missingRequests.TryGetValue(key, out var missing))
                            {
                                missing = (author, new List<ImportListItemInfo>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                                missingRequests[key] = missing;
                            }

                            missing.Reports.Add(report);
                            (side == BookMediaType.Audiobook ? missing.Audiobooks : missing.Ebooks).Add(bookProviderId);
                        }
                        else
                        {
                            _logger.Warn("Import list '{0}': '{1}' ({2}) is missing from existing author '{3}' on the {4} side; monitoring listed books for existing authors is off",
                                importList.Name, report.Book, bookProviderId, author.Name, side);
                        }
                    }

                    ApplyListedBooks(importList, author, targetBooks, report);
                }
                catch (Exception ex)
                {
                    failedItems++;
                    MarkFailure(importList.Id);
                    _logger.Warn(ex, "Failed to monitor book {0} from import list; continuing with other entries", report.Book);
                }
            }

            foreach (var group in selectedBooks.Values.Where(book => newlyMonitoredIds.Contains(book.Id)).GroupBy(book => book.MediaType))
            {
                _bookService.MonitorRequestedBooks(group.Select(book => book.Id).ToList(), group.Key == BookMediaType.Audiobook ? "audiobook" : "ebook");
            }

            var searchBooks = bookIdsToSearch.Select(id => selectedBooks[id]).ToList();
            foreach (var book in searchBooks)
            {
                book.AddOptions ??= new AddBookOptions();
                book.AddOptions.SearchForNewBook = true;
                changedAddOptions[book.Id] = book;
            }

            if (changedAddOptions.Any())
            {
                _bookService.SetAddOptions(changedAddOptions.Values);
            }

            var acceptedSearchIds = new HashSet<int>();
            var rejectedSearchIds = new HashSet<int>();
            if (searchBooks.Count > 0)
            {
                try
                {
                    if (_commandQueueManager.Push(new BookSearchCommand(bookIdsToSearch.OrderBy(id => id).ToList())) == null)
                        throw new InvalidOperationException("The search command was not accepted");
                    acceptedSearchIds.UnionWith(bookIdsToSearch);
                }
                catch (Exception ex)
                {
                    rejectedSearchIds.UnionWith(bookIdsToSearch);
                    _logger.Warn(ex, "Import list sync: could not queue searches for {0} books; requests retained for retry", searchBooks.Count);
                }
                if (acceptedSearchIds.Count > 0)
                {
                    searchBooks.ForEach(book => book.AddOptions.SearchForNewBook = false);
                    _bookService.SetAddOptions(searchBooks);
                    _logger.Info("Import list sync: queued {0} books in one automatic search batch", searchBooks.Count);
                }
            }

            if (changedEditionBookIds.Any())
            {
                _commandQueueManager.Push(new DownloadBookMediaCommand { BookIds = changedEditionBookIds.ToList() });
            }

            foreach (var listItems in items.GroupBy(item => item.ImportListId))
            {
                if (!GetImportListDefinition(listItems.Key).GetMonitoringOptions().AddMissingAuthors)
                {
                    var skipped = listItems.Select(GetAuthorProviderId).Where(id => id.IsNotNullOrWhiteSpace())
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count(id => localLookup.FindAuthor(id) == null &&
                            (!liveAuthorLookupHitCache.TryGetValue(id, out var liveAuthor) || liveAuthor == null));
                    _logger.Info("Import list {0}: skipped {1} missing authors because adding is off", GetImportListDefinition(listItems.Key).Name, skipped);
                }
            }

            foreach (var (key, missing) in missingRequests)
            {
                try
                {
                    var list = GetImportListDefinition(key.ListId);
                    var author = missing.Author;
                    var requests = missing.Reports.DistinctBy(report => (GetBookProviderId(report), report.EditionProviderId, report.EditionMediaType)).ToList();
                    var requestedBooks = requests.SelectMany(report => localLookup.FindBooks(GetBookProviderId(report), report.EditionProviderId))
                        .Where(book => book.AuthorId == author.Id &&
                            (book.MediaType == BookMediaType.Audiobook ? missing.Audiobooks.Any() : missing.Ebooks.Any()) &&
                            !IsBookExcludedByAnyProviderId(listExclusions, book) &&
                            !IsAuthorExcludedByAnyProviderId(listExclusions, author, book.MediaType)).ToList();
                    EnsureAuthorMonitoring(list, author, requestedBooks.Any(book => book.MediaType == BookMediaType.Audiobook),
                        requestedBooks.Any(book => book.MediaType == BookMediaType.Ebook));
                    // Existing author settings remain authoritative for a bounded catalog request.
                    var config = new MonitoringConfig
                    {
                        CreateAudiobook = missing.Audiobooks.Any(),
                        CreateEbook = missing.Ebooks.Any(),
                        AudiobookMonitored = author.AudiobookMonitored,
                        EbookMonitored = author.EbookMonitored,
                        AudiobookMonitorNewItems = author.AudiobookMonitorNewItems,
                        EbookMonitorNewItems = author.EbookMonitorNewItems,
                        AudiobookQualityProfileId = author.AudiobookQualityProfileId,
                        EbookQualityProfileId = author.EbookQualityProfileId,
                        AudiobookMetadataProfileId = author.AudiobookMetadataProfileId,
                        EbookMetadataProfileId = author.EbookMetadataProfileId,
                        AudiobookRootFolderPath = author.AudiobookRootFolderPath,
                        EbookRootFolderPath = author.EbookRootFolderPath,
                        AudiobookTags = author.AudiobookTags,
                        EbookTags = author.EbookTags,
                        AudiobookBooksToMonitor = missing.Audiobooks.ToList(),
                        EbookBooksToMonitor = missing.Ebooks.ToList()
                    };
                    if (QueueAuthorForImportList(list, key.AuthorId, author.Name, config, pendingAuthors,
                        "requested catalog works", requests, listExclusions, authorExists: true))
                    {
                        pendingByList.Add((key.ListId, key.AuthorId));
                    }
                    else
                    {
                        _logger.Warn("Import list '{0}': catalog request for existing author '{1}' was not accepted",
                            list.Name, author.Name);
                    }
                }
                catch (Exception ex)
                {
                    MarkFailure(key.ListId);
                    _logger.Warn(ex, "Import list: could not queue requested catalog works for author {0}; continuing with other entries", key.AuthorId);
                }
            }

            if (pendingAuthors.Any() || pendingExistingAuthors.Any())
            {
                _logger.ProgressInfo("Import list sync: queued {0} authors for background import", pendingAuthors.Union(pendingExistingAuthors.Keys).Count());
                PushImportListPendingImportDrain();
            }

            foreach (var listItems in items.GroupBy(item => item.ImportListId))
            {
                var applied = appliedBooksByList.GetValueOrDefault(listItems.Key) ?? new HashSet<int>();
                var rejected = applied.Count(rejectedSearchIds.Contains);
                _logger.Info("Import list '{0}': {1} items, {2} selected book rows, {3} accepted searches, {4} queued authors, {5} missing book sides, {6} failures, {7} changed edition rows; apply run {8} ms",
                    GetImportListDefinition(listItems.Key).Name, listItems.Count(), applied.Count, applied.Count(acceptedSearchIds.Contains),
                    pendingByList.Count(request => request.ListId == listItems.Key), missingSidesByList.GetValueOrDefault(listItems.Key),
                    failuresByList.GetValueOrDefault(listItems.Key) + rejected, applied.Count(changedEditionBookIds.Contains), stopwatch.ElapsedMilliseconds);
            }

            var unavailableItems = mappedUnavailable + stats.MissingProviderIds + missingBookSides;
            if (missingBookSides > 0 || failedItems > 0)
            {
                _logger.Warn("Import list sync: {0} requested book sides were absent and {1} items failed", missingBookSides, failedItems);
            }
            _logger.ProgressInfo("Import List Sync Completed. Items found: {0}, Existing authors: {1}, Authors queued: {2}, Books monitored: {3}, Unavailable: {4}, Excluded: {5}, Elapsed: {6} ms",
                items.Count, stats.ExistingAuthors, pendingAuthors.Count, booksMonitored, unavailableItems, stats.Excluded, stopwatch.ElapsedMilliseconds);

            return processed;
        }

        private bool TryMapBookReportFromIdentityCache(ImportListItemInfo report, ImportListLocalLookup localLookup,
            ConcurrentDictionary<string, ImportListBookIdentityCache> sourceMappings, ImportListSyncStats stats, IEnumerable<BookMediaType> mediaTypes,
            out Book ownerBook)
        {
            ownerBook = null;
            if (_bookIdentityCacheRepository == null)
            {
                return false;
            }

            var sourceProviderId = GetMappingSourceProviderId(report);
            if (sourceProviderId.IsNullOrWhiteSpace())
            {
                return false;
            }

            try
            {
                var cached = sourceMappings != null ? sourceMappings.GetValueOrDefault(sourceProviderId) :
                    _bookIdentityCacheRepository.FindBySourceProviderId(sourceProviderId);
                if (cached == null ||
                    cached.BookProviderId.IsNullOrWhiteSpace() ||
                    cached.AuthorProviderId.IsNullOrWhiteSpace())
                {
                    return false;
                }

                var cachedAuthor = localLookup.FindAuthor(cached.AuthorProviderId);
                var cacheIsCurrent = cachedAuthor != null && (cachedAuthor.LastInfoSync == null || cachedAuthor.LastInfoSync <= cached.UpdatedAt);
                ownerBook = localLookup.FindOwnedBook(cached.BookProviderId, report.EditionProviderId, mediaTypes);
                if (ownerBook == null && cacheIsCurrent)
                {
                    // Only source metadata's cached owner can disambiguate duplicate local work owners.
                    ownerBook = localLookup.FindOwnedBook(cached.BookProviderId, report.EditionProviderId, mediaTypes, cachedAuthor.Id);
                }
                if (ownerBook == null || cachedAuthor?.Id != ownerBook.AuthorId)
                {
                    return false;
                }

                var workMatches = localLookup.MatchesWork(report.BookProviderId, ownerBook, mediaTypes);
                var reportedAuthor = localLookup.FindAuthor(report.AuthorProviderId);
                if (!workMatches && report.EditionProviderId.IsNotNullOrWhiteSpace() && cached.EditionHasNarration.HasValue &&
                    !localLookup.FindBooks(report.BookProviderId).Any(book => mediaTypes.Contains(book.MediaType)) &&
                    reportedAuthor?.Id == ownerBook.AuthorId)
                {
                    report.BookProviderId = cached.BookProviderId;
                    workMatches = true;
                }
                if (!workMatches)
                {
                    return false;
                }

                report.BookProviderId ??= cached.BookProviderId;
                report.AuthorProviderId = GetPreferredAuthorProviderId(cachedAuthor);
                report.Book ??= cached.Book;
                report.Author = cachedAuthor.Name;
                if (cacheIsCurrent)
                {
                    report.EditionHasNarration ??= cached.EditionHasNarration;
                    report.EditionNarratorNames ??= cached.EditionNarratorNames?.ToList();
                }
                return report.BookProviderId.IsNotNullOrWhiteSpace() && report.AuthorProviderId.IsNotNullOrWhiteSpace();
            }
            catch (Exception ex)
            {
                stats.MarkCacheFailure(ex);
                return false;
            }
        }

        private void CacheBookReportMapping(ImportListItemInfo report,
            ConcurrentDictionary<string, ImportListBookIdentityCache> sourceMappings, ImportListSyncStats stats)
        {
            if (_bookIdentityCacheRepository == null || stats.CacheReadFailed)
            {
                return;
            }

            var sourceProviderId = GetMappingSourceProviderId(report);
            var bookProviderId = GetBookProviderId(report);
            var authorProviderId = GetAuthorProviderId(report);

            if (sourceProviderId.IsNullOrWhiteSpace() ||
                bookProviderId.IsNullOrWhiteSpace() ||
                authorProviderId.IsNullOrWhiteSpace())
            {
                return;
            }

            try
            {
                var now = DateTime.UtcNow;
                var saved = _bookIdentityCacheRepository.UpsertBySourceProviderId(new ImportListBookIdentityCache
                {
                    SourceProviderId = sourceProviderId,
                    BookProviderId = bookProviderId,
                    AuthorProviderId = authorProviderId,
                    Book = report.Book,
                    Author = report.Author,
                    EditionHasNarration = report.EditionHasNarration,
                    EditionNarratorNames = report.EditionNarratorNames?.ToList(),
                    CreatedAt = now,
                    UpdatedAt = now
                });
                if (sourceMappings != null)
                {
                    sourceMappings[sourceProviderId] = saved;
                }
            }
            catch (Exception ex)
            {
                stats.MarkCacheFailure(ex);
            }
        }

        private static string GetMappingSourceProviderId(ImportListItemInfo report)
        {
            if (report == null)
            {
                return null;
            }

            if (report.EditionProviderId.IsNotNullOrWhiteSpace())
            {
                return "edition:" + BookEditionIdentity.NormalizeRequestedEditionProviderId(NormalizeProviderId(report.EditionProviderId, "gr"));
            }

            var workId = NormalizeProviderId(report.BookProviderId, "gr");
            return workId == null ? null : "work:" + workId;
        }

        private Tuple<string, Book, List<Author>> MapBookReportWithMappings(ImportListItemInfo report, ImportListLocalLookup localLookup,
            ConcurrentDictionary<string, ImportListBookIdentityCache> sourceMappings, ImportListSyncStats stats,
            IEnumerable<BookMediaType> mediaTypes = null)
        {
            mediaTypes ??= new[] { BookMediaType.Audiobook, BookMediaType.Ebook };
            if (!mediaTypes.Any())
            {
                return null;
            }
            var localMapped = localLookup.TryMapBookReport(report, mediaTypes);
            var localBook = localMapped ? localLookup.FindOwnedBook(report.BookProviderId, report.EditionProviderId, mediaTypes) : null;
            var localMapping = localBook == null ? null : Tuple.Create(report.AuthorProviderId, localBook,
                new List<Author> { localLookup.FindAuthor(report.AuthorProviderId) });
            if (localMapped && (report.EditionProviderId.IsNullOrWhiteSpace() || IsReportedAudioEdition(report) != null))
            {
                var cached = sourceMappings?.GetValueOrDefault(GetMappingSourceProviderId(report));
                if (sourceMappings != null && report.EditionHasNarration.HasValue &&
                    (cached?.EditionHasNarration != report.EditionHasNarration ||
                     !(cached?.EditionNarratorNames ?? new List<string>()).SequenceEqual(report.EditionNarratorNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase)))
                {
                    CacheBookReportMapping(report, sourceMappings, stats);
                }

                return localMapping;
            }

            if (TryMapBookReportFromIdentityCache(report, localLookup, sourceMappings, stats, mediaTypes, out var cachedBook))
            {
                localBook = cachedBook;
                localMapped = localBook != null;
                localMapping = localBook == null ? null : Tuple.Create(report.AuthorProviderId, localBook,
                    new List<Author> { localLookup.FindAuthor(report.AuthorProviderId) });
                if (report.EditionProviderId.IsNullOrWhiteSpace() || IsReportedAudioEdition(report) == false ||
                    (IsReportedAudioEdition(report) == true && report.EditionNarratorNames?.Count > 0))
                {
                    return localMapping;
                }
            }

            var requestedAuthorId = GetAuthorProviderId(report);
            if (!localMapped)
            {
                report.AuthorProviderId = null;
            }

            try
            {
                var requestedWorkId = GetBookProviderId(report);
                var isEdition = report.EditionProviderId.IsNotNullOrWhiteSpace();
                Tuple<string, Book, List<Author>> mapped;
                var mediaType = mediaTypes.First();
                if (isEdition)
                {
                    Interlocked.Increment(ref stats.EditionLookups);
                    mapped = _bookInfoProxy.GetEditionInfo(report.EditionProviderId, report.EditionMediaType ?? mediaType);
                }
                else if (requestedWorkId.IsNotNullOrWhiteSpace())
                {
                    Interlocked.Increment(ref stats.WorkLookups);
                    mapped = _bookInfoProxy.GetWorkInfo(requestedWorkId, mediaType,
                        AuthorIdentity.NormalizeWorkLookupAuthorHint(requestedWorkId, requestedAuthorId));
                }
                else
                {
                    return localMapping;
                }

                var book = mapped.Item2;
                var author = BookIdentity.GetLookupPrimaryAuthor(mapped);
                mapped = Tuple.Create(mapped.Item1, book, new List<Author> { author });
                var edition = book?.Editions?.FirstOrDefault(candidate => BookEditionIdentity.EditionMatchesProviderId(candidate, report.EditionProviderId));
                var workMatches = book != null && localLookup.MatchesWork(requestedWorkId, book, mediaTypes);
                if (!workMatches && edition != null && requestedWorkId.IsNotNullOrWhiteSpace() &&
                    !localLookup.FindBooks(requestedWorkId).Any(local => mediaTypes.Contains(local.MediaType)))
                {
                    var sourceAuthor = localLookup.FindAuthor(GetPreferredAuthorProviderId(author));
                    var reportedAuthor = localLookup.FindAuthor(requestedAuthorId);
                    workMatches = sourceAuthor != null && (requestedAuthorId.IsNullOrWhiteSpace() || reportedAuthor?.Id == sourceAuthor.Id) &&
                        localLookup.FindBooks(GetPreferredBookProviderId(book)).Any(local => mediaTypes.Contains(local.MediaType) &&
                            BookIdentity.MatchesByStableWorkProviderId(local, book) && local.AuthorId == sourceAuthor.Id);
                    if (workMatches)
                    {
                        report.BookProviderId = GetPreferredBookProviderId(book);
                    }
                }

                // A supplied contributor or cached pair cannot establish ownership of a different work.
                if (!workMatches || (isEdition && edition == null) || GetPreferredAuthorProviderId(author).IsNullOrWhiteSpace())
                {
                    Interlocked.Increment(ref stats.EvidenceRejections);
                    var reason = !workMatches ? "requested work does not match" :
                        isEdition && edition == null ? "requested edition is absent" : "selected primary author is absent or conflicting";
                    _logger.Warn("Metadata mapping for {0} rejected: {1}; retaining only proven local intent", GetMappingSourceProviderId(report), reason);
                    if (!localMapped)
                    {
                        report.AuthorProviderId = null;
                    }
                    var rejectedMapping = isEdition && edition != null && GetPreferredAuthorProviderId(author).IsNotNullOrWhiteSpace() ? mapped : localMapping;
                    return rejectedMapping;
                }

                report.BookProviderId ??= GetPreferredBookProviderId(book);
                report.Book = book.Title;
                report.Author = author.Name;
                report.AuthorProviderId = GetPreferredAuthorProviderId(author);
                if (edition != null)
                {
                    report.EditionHasNarration = BookEditionIdentity.IsNarratedAudiobookEdition(edition);
                    report.EditionNarratorNames = report.EditionHasNarration == true ? BookEditionIdentity.GetNarratorNames(edition) : null;
                }

                CacheBookReportMapping(report, sourceMappings, stats);
                return mapped;
            }
            catch (Exception ex) when (ex is BookNotFoundException || ex is WorkRescueTerminalException || ex is InvalidProviderIdException)
            {
                if (ex is BookNotFoundException)
                {
                    _logger.Debug("No metadata mapping found for source [{0}] ({1} - {2})",
                        GetMappingSourceProviderId(report), report.Author, report.Book);
                }
                else
                {
                    Interlocked.Increment(ref stats.EvidenceRejections);
                    _logger.Warn("Metadata mapping for {0} rejected: {1}; retaining only proven local intent", GetMappingSourceProviderId(report), ex.Message);
                }
                if (!localMapped)
                {
                    report.AuthorProviderId = null;
                }
                return localMapping;
            }
        }

        private void EnsureAuthorMonitoring(ImportListDefinition importList, Author author, bool enableAudiobooks, bool enableEbooks)
        {
            if (!importList.GetMonitoringOptions().ResumePausedAuthors || author == null)
            {
                return;
            }

            if ((!enableAudiobooks || author.AudiobookMonitored == true) &&
                (!enableEbooks || author.EbookMonitored == true))
            {
                return;
            }

            if (enableAudiobooks && author.AudiobookMonitored != true)
            {
                _authorService.SetMediaTypeMonitoring(author.Id, "audiobook", true);
                author.AudiobookMonitored = true;
            }

            if (enableEbooks && author.EbookMonitored != true)
            {
                _authorService.SetMediaTypeMonitoring(author.Id, "ebook", true);
                author.EbookMonitored = true;
            }

            author.Monitored = author.IsMonitoredFromMediaSettings();
        }

        public void Execute(ImportListSyncCommand message)
        {
            var processed = message.DefinitionId.HasValue ? SyncList(_importListFactory.Get(message.DefinitionId.Value)) : SyncAll();

            _eventAggregator.PublishEvent(new ImportListSyncCompleteEvent(processed));
        }

        public void Execute(HardcoverLibrarySyncCommand message)
        {
            var filterBlockedImportLists = message.Trigger != CommandTrigger.Manual;
            var processed = SyncHardcoverLibrary(filterBlockedImportLists);

            _eventAggregator.PublishEvent(new ImportListSyncCompleteEvent(processed));
        }

        private List<Book> SyncHardcoverLibrary(bool filterBlockedImportLists)
        {
            var hardcoverImportLists = _importListFactory.AutomaticAddEnabled(filterBlockedImportLists)
                .Where(l => l.Definition.Implementation.EqualsIgnoreCase(nameof(HardcoverLibraryImportList)))
                .ToList();

            if (hardcoverImportLists.Empty())
            {
                _logger.Debug("No Hardcover Library import lists with automatic add enabled");
                return new List<Book>();
            }

            _logger.ProgressInfo("Starting Hardcover Library Sync");

            var listItems = new List<ImportListItemInfo>();
            foreach (var importList in hardcoverImportLists)
            {
                listItems.AddRange(_listFetcherAndParser.FetchSingleList(importList));
            }

            var failuresByList = new ConcurrentDictionary<int, int>();
            var processed = ProcessListItems(listItems, failuresByList);
            foreach (var importList in hardcoverImportLists)
            {
                CommitHardcoverState(importList, failuresByList);
            }
            return processed;
        }

        private void CommitHardcoverState(IImportList importList, ConcurrentDictionary<int, int> failuresByList)
        {
            if (importList is not HardcoverLibraryImportList hardcover)
            {
                return;
            }

            var failures = failuresByList.GetValueOrDefault(importList.Definition.Id);
            if (failures > 0)
            {
                _logger.Warn("Keeping Hardcover import list '{0}' cursor unchanged after {1} unaccepted items", importList.Definition.Name, failures);
                return;
            }

            try
            {
                hardcover.CommitState();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Could not commit Hardcover import list '{0}' cursor", importList.Definition.Name);
            }
        }
    }
}
