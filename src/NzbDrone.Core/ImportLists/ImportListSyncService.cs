using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
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
        private readonly IBookRepository _bookRepository;
        private readonly IEditionService _editionService;
        private readonly IEditionMetadataProfileFilter _editionMetadataProfileFilter;
        private readonly IEditionSelector _editionSelector;
        private readonly IAuthorLibraryService _authorLibraryService;
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
                                     IBookRepository bookRepository,
                                     IEditionService editionService,
                                     IEditionMetadataProfileFilter editionMetadataProfileFilter,
                                     IEditionSelector editionSelector,
                                     IAuthorLibraryService authorLibraryService,
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
            _bookRepository = bookRepository;
            _editionService = editionService;
            _editionMetadataProfileFilter = editionMetadataProfileFilter;
            _editionSelector = editionSelector;
            _authorLibraryService = authorLibraryService;
            _pendingAuthorImportService = pendingAuthorImportService;
            _bookIdentityCacheRepository = bookIdentityCacheRepository;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        private ResolvedRootFolderSettings ResolveRootFolderSettings(string rootFolderPath, BookMediaType mediaType)
        {
            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return new ResolvedRootFolderSettings { IsConfigured = false, Source = "Unconfigured" };
            }

            var rootFolder = _rootFolderService.GetBestRootFolder(rootFolderPath);
            return _rootFolderSettingsResolver.ResolveSettings(rootFolder, mediaType);
        }

        private MonitoringConfig BuildMonitoringConfigForHardcoverLibraryImportList(ImportListDefinition importList,
            HardcoverLibraryImportListSettings settings,
            string authorProviderId,
            string authorName,
            Dictionary<(int importListId, string authorProviderId), (HashSet<string> audiobookBooks, HashSet<string> ebookBooks)> booksToMonitor)
        {
            if (importList == null || settings == null)
            {
                return BuildConfigFromImportList(importList);
            }

            var monitorExisting = importList.ShouldMonitor switch
            {
                ImportListMonitorType.SpecificBook => 2,
                ImportListMonitorType.EntireAuthor => 1,
                _ => 0
            };

            var monitorAudiobooks = settings.MonitorAudiobooks;
            var monitorEbooks = settings.MonitorEbooks;

            var audiobookRootFolderPath = settings.AudiobookRootFolderPath.IsNotNullOrWhiteSpace()
                ? settings.AudiobookRootFolderPath
                : importList.RootFolderPath;
            var ebookRootFolderPath = settings.EbookRootFolderPath.IsNotNullOrWhiteSpace()
                ? settings.EbookRootFolderPath
                : importList.RootFolderPath;

            var audiobookRootFolderSettings = ResolveRootFolderSettings(audiobookRootFolderPath, BookMediaType.Audiobook);
            var ebookRootFolderSettings = ResolveRootFolderSettings(ebookRootFolderPath, BookMediaType.Ebook);

            var audiobookQualityProfileId = settings.AudiobookQualityProfileId > 0
                ? settings.AudiobookQualityProfileId
                : (audiobookRootFolderSettings?.QualityProfileId ?? 0);

            var ebookQualityProfileId = settings.EbookQualityProfileId > 0
                ? settings.EbookQualityProfileId
                : (ebookRootFolderSettings?.QualityProfileId ?? 0);

            var audiobookMetadataProfileId = settings.AudiobookMetadataProfileId > 0
                ? settings.AudiobookMetadataProfileId
                : (audiobookRootFolderSettings?.MetadataProfileId ?? 0);

            var ebookMetadataProfileId = settings.EbookMetadataProfileId > 0
                ? settings.EbookMetadataProfileId
                : (ebookRootFolderSettings?.MetadataProfileId ?? 0);

            if (monitorAudiobooks && (audiobookQualityProfileId <= 0 || audiobookMetadataProfileId <= 0))
            {
                throw new InvalidOperationException("Hardcover Library: audiobook defaults are not configured (set profile overrides or configure root folder defaults)");
            }

            if (monitorEbooks && (ebookQualityProfileId <= 0 || ebookMetadataProfileId <= 0))
            {
                throw new InvalidOperationException("Hardcover Library: ebook defaults are not configured (set profile overrides or configure root folder defaults)");
            }

            var (audiobookTags, ebookTags) = BuildMediaTagsForHardcoverLibrary(importList, settings);

            var config = new MonitoringConfig
            {
                MonitorNewItems = importList.ShouldMonitor != ImportListMonitorType.None,
                MonitorExisting = importList.ShouldMonitorExisting,
                MonitorFuture = false,

                CreateAudiobook = monitorAudiobooks,
                CreateEbook = monitorEbooks,

                AudiobookQualityProfileId = monitorAudiobooks ? audiobookQualityProfileId : null,
                EbookQualityProfileId = monitorEbooks ? ebookQualityProfileId : null,
                AudiobookMetadataProfileId = monitorAudiobooks ? audiobookMetadataProfileId : null,
                EbookMetadataProfileId = monitorEbooks ? ebookMetadataProfileId : null,
                AudiobookRootFolderPath = monitorAudiobooks ? audiobookRootFolderPath : null,
                EbookRootFolderPath = monitorEbooks ? ebookRootFolderPath : null,

                AudiobookMonitorExisting = monitorAudiobooks ? monitorExisting : 0,
                EbookMonitorExisting = monitorEbooks ? monitorExisting : 0,
                AudiobookMonitorFuture = false,
                EbookMonitorFuture = false,

                AudiobookTags = audiobookTags,
                EbookTags = ebookTags,
                Tags = audiobookTags.Concat(ebookTags).ToHashSet()
            };

            config.QueueIfUnavailable = true;
            config.RequestedBy = $"ImportList:{importList.Name}";
            config.AuthorName = authorName;
            config.SearchForMissingBooks = importList.ShouldSearch;

            if (importList.ShouldMonitor == ImportListMonitorType.SpecificBook &&
                booksToMonitor != null &&
                booksToMonitor.TryGetValue((importList.Id, authorProviderId), out var monitorLists))
            {
                config.AudiobookBooksToMonitor = monitorLists.audiobookBooks?.ToList();
                config.EbookBooksToMonitor = monitorLists.ebookBooks?.ToList();
            }

            return config;
        }

        private MonitoringConfig BuildMonitoringConfigForGoodreadsImportList(ImportListDefinition importList,
            IGoodreadsDualMediaImportListSettings settings,
            string authorProviderId,
            string authorName,
            Dictionary<(int importListId, string authorProviderId), (HashSet<string> audiobookBooks, HashSet<string> ebookBooks)> booksToMonitor)
        {
            if (importList == null || settings == null)
            {
                return BuildConfigFromImportList(importList);
            }

            var monitorExisting = importList.ShouldMonitor switch
            {
                ImportListMonitorType.SpecificBook => 2,
                ImportListMonitorType.EntireAuthor => 1,
                _ => 0
            };

            var monitorAudiobooks = settings.MonitorAudiobooks;
            var monitorEbooks = settings.MonitorEbooks;

            var audiobookRootFolderPath = settings.AudiobookRootFolderPath.IsNotNullOrWhiteSpace()
                ? settings.AudiobookRootFolderPath
                : importList.RootFolderPath;
            var ebookRootFolderPath = settings.EbookRootFolderPath.IsNotNullOrWhiteSpace()
                ? settings.EbookRootFolderPath
                : importList.RootFolderPath;

            var audiobookRootFolderSettings = ResolveRootFolderSettings(audiobookRootFolderPath, BookMediaType.Audiobook);
            var ebookRootFolderSettings = ResolveRootFolderSettings(ebookRootFolderPath, BookMediaType.Ebook);

            var audiobookQualityProfileId = settings.AudiobookQualityProfileId > 0
                ? settings.AudiobookQualityProfileId
                : (audiobookRootFolderSettings?.QualityProfileId ?? 0);

            var ebookQualityProfileId = settings.EbookQualityProfileId > 0
                ? settings.EbookQualityProfileId
                : (ebookRootFolderSettings?.QualityProfileId ?? 0);

            var audiobookMetadataProfileId = settings.AudiobookMetadataProfileId > 0
                ? settings.AudiobookMetadataProfileId
                : (audiobookRootFolderSettings?.MetadataProfileId ?? 0);

            var ebookMetadataProfileId = settings.EbookMetadataProfileId > 0
                ? settings.EbookMetadataProfileId
                : (ebookRootFolderSettings?.MetadataProfileId ?? 0);

            if (monitorAudiobooks && (audiobookQualityProfileId <= 0 || audiobookMetadataProfileId <= 0))
            {
                throw new InvalidOperationException("Goodreads import list: audiobook defaults are not configured (set profile overrides or configure root folder defaults)");
            }

            if (monitorEbooks && (ebookQualityProfileId <= 0 || ebookMetadataProfileId <= 0))
            {
                throw new InvalidOperationException("Goodreads import list: ebook defaults are not configured (set profile overrides or configure root folder defaults)");
            }

            var (audiobookTags, ebookTags) = BuildMediaTagsForGoodreads(importList, settings);

            var config = new MonitoringConfig
            {
                MonitorNewItems = importList.ShouldMonitor != ImportListMonitorType.None,
                MonitorExisting = importList.ShouldMonitorExisting,
                MonitorFuture = false,

                CreateAudiobook = monitorAudiobooks,
                CreateEbook = monitorEbooks,

                AudiobookQualityProfileId = monitorAudiobooks ? audiobookQualityProfileId : null,
                EbookQualityProfileId = monitorEbooks ? ebookQualityProfileId : null,
                AudiobookMetadataProfileId = monitorAudiobooks ? audiobookMetadataProfileId : null,
                EbookMetadataProfileId = monitorEbooks ? ebookMetadataProfileId : null,
                AudiobookRootFolderPath = monitorAudiobooks ? audiobookRootFolderPath : null,
                EbookRootFolderPath = monitorEbooks ? ebookRootFolderPath : null,

                AudiobookMonitorExisting = monitorAudiobooks ? monitorExisting : 0,
                EbookMonitorExisting = monitorEbooks ? monitorExisting : 0,
                AudiobookMonitorFuture = false,
                EbookMonitorFuture = false,

                AudiobookTags = audiobookTags,
                EbookTags = ebookTags,
                Tags = audiobookTags.Concat(ebookTags).ToHashSet()
            };

            config.QueueIfUnavailable = true;
            config.RequestedBy = $"ImportList:{importList.Name}";
            config.AuthorName = authorName;
            config.SearchForMissingBooks = importList.ShouldSearch;

            if (importList.ShouldMonitor == ImportListMonitorType.SpecificBook &&
                booksToMonitor != null &&
                booksToMonitor.TryGetValue((importList.Id, authorProviderId), out var monitorLists))
            {
                config.AudiobookBooksToMonitor = monitorLists.audiobookBooks?.ToList();
                config.EbookBooksToMonitor = monitorLists.ebookBooks?.ToList();
            }

            return config;
        }

        private bool QueueAuthorForImportList(ImportListDefinition importList,
            string authorProviderId,
            string authorName,
            MonitoringConfig config,
            ISet<string> pendingAuthors,
            string context)
        {
            if (authorProviderId.IsNullOrWhiteSpace())
            {
                return false;
            }

            config ??= BuildConfigFromImportList(importList);
            config.QueueIfUnavailable = true;
            config.RequestedBy ??= $"ImportList:{importList?.Name ?? "Unknown"}";
            config.AuthorName ??= authorName;
            config.SearchForMissingBooks ??= importList?.ShouldSearch ?? false;

            var pendingId = _pendingAuthorImportService.EnqueueAsync(authorProviderId, config, config.RequestedBy)
                .GetAwaiter()
                .GetResult();

            if (pendingId <= 0)
            {
                return false;
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

        private static void AddGenericSpecificBooksToMonitor(MonitoringConfig config,
            ImportListDefinition importList,
            string authorProviderId,
            IEnumerable<(string authorProviderId, string bookProviderId, ImportListItemInfo report)> booksToMonitor)
        {
            if (config == null ||
                importList?.ShouldMonitor != ImportListMonitorType.SpecificBook ||
                authorProviderId.IsNullOrWhiteSpace() ||
                booksToMonitor == null)
            {
                return;
            }

            foreach (var item in booksToMonitor)
            {
                if (!authorProviderId.Equals(item.authorProviderId, StringComparison.OrdinalIgnoreCase) ||
                    item.report?.ImportListId != importList.Id ||
                    item.bookProviderId.IsNullOrWhiteSpace())
                {
                    continue;
                }

                config.AudiobookBooksToMonitor ??= new List<string>();
                config.EbookBooksToMonitor ??= new List<string>();

                if (!config.AudiobookBooksToMonitor.Contains(item.bookProviderId, StringComparer.OrdinalIgnoreCase))
                {
                    config.AudiobookBooksToMonitor.Add(item.bookProviderId);
                }

                if (!config.EbookBooksToMonitor.Contains(item.bookProviderId, StringComparer.OrdinalIgnoreCase))
                {
                    config.EbookBooksToMonitor.Add(item.bookProviderId);
                }
            }
        }

        private static MonitoringConfig BuildConfigFromImportList(ImportListDefinition importList)
        {
            if (importList == null)
            {
                return new MonitoringConfig();
            }

            var tags = importList.Tags != null ? new HashSet<int>(importList.Tags) : new HashSet<int>();

            var config = new MonitoringConfig
            {
                MonitorNewItems = importList.ShouldMonitor != ImportListMonitorType.None, // For backward compatibility
                MonitorExisting = importList.ShouldMonitorExisting,
                MonitorFuture = importList.ShouldMonitor != ImportListMonitorType.None,
                AudiobookQualityProfileId = importList.QualityProfileId,
                EbookQualityProfileId = importList.QualityProfileId,
                AudiobookMetadataProfileId = importList.MetadataProfileId,
                EbookMetadataProfileId = importList.MetadataProfileId,
                AudiobookRootFolderPath = importList.RootFolderPath,
                EbookRootFolderPath = importList.RootFolderPath,
                AudiobookTags = tags,
                EbookTags = tags,
                Tags = tags
            };

            return config;
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

        private static bool MatchesExclusionId(string providerId, string exclusionId)
        {
            if (providerId.IsNullOrWhiteSpace() || exclusionId.IsNullOrWhiteSpace())
            {
                return false;
            }

            providerId = providerId.Trim();
            exclusionId = exclusionId.Trim();

            if (exclusionId.Contains(":"))
            {
                return providerId.Equals(exclusionId, StringComparison.OrdinalIgnoreCase);
            }

            // Backward compatibility: some exclusions were stored as raw IDs (no provider prefix),
            // including ISBN10/ISBN13 (and potentially other non-prefixed identifiers).
            if (!providerId.Contains(":"))
            {
                return providerId.Equals(exclusionId, StringComparison.OrdinalIgnoreCase);
            }

            // Backward compatibility: old exclusions stored raw Goodreads IDs without the gr: prefix.
            var (prefix, rawId) = SplitProviderId(providerId);
            return prefix == "gr" && rawId.IsNotNullOrWhiteSpace() && rawId.Equals(exclusionId, StringComparison.OrdinalIgnoreCase);
        }

        private string GetAuthorProviderId(ImportListItemInfo report)
        {
            return NormalizeProviderId(report?.AuthorProviderId, "gr");
        }

        private string GetBookProviderId(ImportListItemInfo report)
        {
            if (report == null)
            {
                return null;
            }

            if (report.BookProviderId.IsNotNullOrWhiteSpace() && report.BookProviderId.Contains(":"))
            {
                return report.BookProviderId.Trim();
            }

            if (report.EditionProviderId.IsNotNullOrWhiteSpace() && report.EditionProviderId.Contains(":"))
            {
                return report.EditionProviderId.Trim();
            }

            if (report.BookProviderId.IsNotNullOrWhiteSpace())
            {
                return NormalizeProviderId(report.BookProviderId, "gr");
            }

            return NormalizeProviderId(report.EditionProviderId, "gr");
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
            private readonly Dictionary<string, Book> _books = new(StringComparer.OrdinalIgnoreCase);

            public int AuthorCount { get; private set; }
            public int BookCount { get; private set; }

            public void AddAuthor(Author author)
            {
                if (author == null)
                {
                    return;
                }

                AuthorCount++;

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

                foreach (var providerId in GetBookProviderIds(book))
                {
                    AddBookKey(providerId, book);
                }
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

            public Book FindBook(string providerId)
            {
                foreach (var key in ExpandProviderLookupKeys(providerId))
                {
                    if (_books.TryGetValue(key, out var book))
                    {
                        return book;
                    }
                }

                return null;
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
                    if (!_books.ContainsKey(key))
                    {
                        _books[key] = book;
                    }
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

        private ImportListLocalLookup BuildImportListLocalLookup(IEnumerable<ImportListItemInfo> items = null)
        {
            var lookup = new ImportListLocalLookup();

            if (items == null)
            {
                return lookup;
            }

            var authorProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bookProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                var authorId = GetAuthorProviderId(item);
                if (authorId.IsNotNullOrWhiteSpace())
                {
                    authorProviderIds.Add(authorId.Trim());
                }

                var bookId = GetBookProviderId(item);
                if (bookId.IsNotNullOrWhiteSpace())
                {
                    bookProviderIds.Add(bookId.Trim());
                }
            }

            foreach (var authorId in authorProviderIds)
            {
                var (prefix, rawId) = SplitProviderId(authorId);
                prefix ??= "gr";

                var author = _authorService.FindByProviderId(prefix, authorId)
                    ?? (rawId.IsNotNullOrWhiteSpace() ? _authorService.FindByProviderId(prefix, rawId) : null);

                if (author != null)
                {
                    lookup.AddAuthor(author);
                }
            }

            foreach (var bookId in bookProviderIds)
            {
                var (prefix, rawId) = SplitProviderId(bookId);
                prefix ??= "gr";

                var book = _bookService.FindByProviderId(prefix, bookId)
                    ?? (rawId.IsNotNullOrWhiteSpace() ? _bookService.FindByProviderId(prefix, rawId) : null);

                if (book != null)
                {
                    lookup.AddBook(book);
                }
            }

            return lookup;
        }

        private static bool MatchesProviderId(string candidateId, string providerId, string rawId)
        {
            if (candidateId.IsNullOrWhiteSpace())
            {
                return false;
            }

            candidateId = candidateId.Trim();
            if (candidateId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return rawId.IsNotNullOrWhiteSpace() && candidateId.Equals(rawId, StringComparison.OrdinalIgnoreCase);
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

            return FindExistingBookLive(bookProviderId, liveBookLookupHitCache);
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

            var (prefix, rawId) = SplitProviderId(bookProviderId);
            if (prefix.IsNullOrWhiteSpace())
            {
                return null;
            }

            var book = _bookService.FindByProviderId(prefix, bookProviderId)
                ?? (rawId.IsNotNullOrWhiteSpace() ? _bookService.FindByProviderId(prefix, rawId) : null);

            if (book != null && liveBookLookupHitCache != null)
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

            var (prefix, rawId) = SplitProviderId(authorProviderId);
            if (prefix.IsNullOrWhiteSpace())
            {
                return null;
            }

            var author = _authorService.FindByProviderId(prefix, authorProviderId)
                ?? (rawId.IsNotNullOrWhiteSpace() ? _authorService.FindByProviderId(prefix, rawId) : null);

            if (author != null && liveAuthorLookupHitCache != null)
            {
                liveAuthorLookupHitCache[authorProviderId] = author;
            }

            return author;
        }

        private static HardcoverLibraryImportListSettings GetHardcoverLibrarySettings(ImportListDefinition importList)
        {
            return importList?.Settings as HardcoverLibraryImportListSettings;
        }

        private static IGoodreadsDualMediaImportListSettings GetGoodreadsSettings(ImportListDefinition importList)
        {
            return importList?.Settings as IGoodreadsDualMediaImportListSettings;
        }

        private static (HashSet<int> audiobookTags, HashSet<int> ebookTags) BuildMediaTagsForHardcoverLibrary(ImportListDefinition importList, HardcoverLibraryImportListSettings settings)
        {
            var baseTags = importList?.Tags ?? new HashSet<int>();
            var audiobookTags = new HashSet<int>(baseTags);
            var ebookTags = new HashSet<int>(baseTags);

            if (settings == null)
            {
                return (audiobookTags, ebookTags);
            }

            if (settings.MonitorAudiobooks && settings.AudiobookTags != null)
            {
                foreach (var tag in settings.AudiobookTags)
                {
                    audiobookTags.Add(tag);
                }
            }

            if (settings.MonitorEbooks && settings.EbookTags != null)
            {
                foreach (var tag in settings.EbookTags)
                {
                    ebookTags.Add(tag);
                }
            }

            return (audiobookTags, ebookTags);
        }

        private static (HashSet<int> audiobookTags, HashSet<int> ebookTags) BuildMediaTagsForGoodreads(ImportListDefinition importList, IGoodreadsDualMediaImportListSettings settings)
        {
            var baseTags = importList?.Tags ?? new HashSet<int>();
            var audiobookTags = new HashSet<int>(baseTags);
            var ebookTags = new HashSet<int>(baseTags);

            if (settings == null)
            {
                return (audiobookTags, ebookTags);
            }

            if (settings.MonitorAudiobooks && settings.AudiobookTags != null)
            {
                foreach (var tag in settings.AudiobookTags)
                {
                    audiobookTags.Add(tag);
                }
            }

            if (settings.MonitorEbooks && settings.EbookTags != null)
            {
                foreach (var tag in settings.EbookTags)
                {
                    ebookTags.Add(tag);
                }
            }

            return (audiobookTags, ebookTags);
        }

        private static bool BookMatchesProviderId(Book book, string providerPrefix, string providerId, string rawId)
        {
            if (book == null || providerId.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (BookIdentity.GetProviderIdentityTokens(book).Contains(providerId))
            {
                return true;
            }

            switch (providerPrefix)
            {
                case "hc":
                    return BookEditionIdentity.HasCanonicalWorkProviderId(book, providerId) ||
                           BookEditionIdentity.HasCanonicalEditionProviderId(book, providerId);

                case "gr":
                    return BookEditionIdentity.HasCanonicalWorkProviderId(book, providerId) ||
                           BookEditionIdentity.HasCanonicalEditionProviderId(book, providerId) ||
                           MatchesProviderId(book.ForeignEditionId, providerId, rawId);

                case "ol":
                    return BookEditionIdentity.HasCanonicalWorkProviderId(book, providerId) ||
                           BookEditionIdentity.HasCanonicalEditionProviderId(book, providerId);

                case "gb":
                    return BookEditionIdentity.HasCanonicalEditionProviderId(book, providerId);

                case "az":
                case "ax":
                    return BookEditionIdentity.HasCanonicalEditionProviderId(book, providerId);
            }

            return BookEditionIdentity.HasCanonicalWorkProviderId(book, providerId) ||
                   BookEditionIdentity.HasCanonicalEditionProviderId(book, providerId) ||
                   MatchesProviderId(book.ForeignEditionId, providerId, rawId);
        }

        private static BookMediaType? GetMediaTypeForEdition(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            // Hardcover: reading_format_id=2 indicates audiobook.
            if (edition.ReadingFormatId == 2)
            {
                return BookMediaType.Audiobook;
            }

            if (edition.DurationSeconds.HasValue && edition.DurationSeconds.Value > 0)
            {
                return BookMediaType.Audiobook;
            }

            if (edition.ReadingFormatId == 3 || edition.ReadingFormatId == 4)
            {
                return BookMediaType.Ebook;
            }

            if (edition.IsEbook)
            {
                return BookMediaType.Ebook;
            }

            var format = (edition.EditionFormat ?? edition.Format ?? string.Empty).Trim();
            if (format.IsNotNullOrWhiteSpace())
            {
                if (format.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                    format.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
                {
                    return BookMediaType.Audiobook;
                }

                if (format.Contains("kindle", StringComparison.OrdinalIgnoreCase) ||
                    format.Contains("ebook", StringComparison.OrdinalIgnoreCase) ||
                    format.Contains("e-book", StringComparison.OrdinalIgnoreCase))
                {
                    return BookMediaType.Ebook;
                }

                if (format.Contains("hardcover", StringComparison.OrdinalIgnoreCase) ||
                    format.Contains("paperback", StringComparison.OrdinalIgnoreCase) ||
                    format.Contains("mass market", StringComparison.OrdinalIgnoreCase) ||
                    format.Contains("library binding", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            // Physical/unknown: don't force into ebook/audiobook.
            if (edition.ReadingFormatId == 1)
            {
                return null;
            }

            return null;
        }

        private Book CloneBookWithEditions(Book canonicalBook, string requiredEditionProviderId = null, string requiredEditionRawId = null)
        {
            if (canonicalBook == null)
            {
                return null;
            }

            var sourceEditions = canonicalBook.Editions ?? _editionService.GetEditionsByBook(canonicalBook.Id);
            var retainedSourceEditions = SelectCloneEditions(canonicalBook, sourceEditions, requiredEditionProviderId, requiredEditionRawId);
            if (!retainedSourceEditions.Any())
            {
                _logger.Warn("[HARDCOVER-CLONE] Skipping clone for BookId={0} (MediaType={1}) because no editions survived metadata-profile filtering and media-type retention",
                    canonicalBook.Id,
                    canonicalBook.MediaType);
                return null;
            }

            // Build a unique slug suffix (short but high-entropy)
            var suffix = DateTime.UtcNow.Ticks.ToString().Substring(10);

            var newBook = new Book
            {
                Title = canonicalBook.Title,
                Subtitle = canonicalBook.Subtitle,
                TitleSlug = (canonicalBook.TitleSlug ?? canonicalBook.Title).Trim() + "_copy_" + suffix,
                Author = canonicalBook.Author,
                AuthorId = canonicalBook.AuthorId,
                MediaType = canonicalBook.MediaType,
                ForeignEditionId = canonicalBook.ForeignEditionId,

                GoodreadsWorkId = canonicalBook.GoodreadsWorkId,
                HardcoverBookId = canonicalBook.HardcoverBookId,
                OpenLibraryWorkId = canonicalBook.OpenLibraryWorkId,
                BaseBookId = canonicalBook.BaseBookId,
                RemoteProviderIds = CloneProviderIds(canonicalBook.RemoteProviderIds),

                // Monitoring state will be updated after edition selection
                AudiobookMonitored = false,
                EbookMonitored = false,
                AnyEditionOk = true,
                Added = DateTime.UtcNow,
                Narrator = canonicalBook.Narrator,
                DurationMinutes = canonicalBook.DurationMinutes,
                CleanTitle = canonicalBook.CleanTitle,

                SeriesId = canonicalBook.SeriesId,
                SeriesName = canonicalBook.SeriesName,
                SeriesPosition = canonicalBook.SeriesPosition,

                OriginalTitle = canonicalBook.OriginalTitle,
                Overview = canonicalBook.Overview,
                ReleaseDate = canonicalBook.ReleaseDate,
                Links = canonicalBook.Links,
                Genres = canonicalBook.Genres,
                Ratings = canonicalBook.Ratings,
                Images = canonicalBook.Images,
                LanguageCode = canonicalBook.LanguageCode,
                LanguageName = canonicalBook.LanguageName,
                PublicationYear = canonicalBook.PublicationYear,
                Publisher = canonicalBook.Publisher,
                PageCount = canonicalBook.PageCount,
                IsGraphicAudio = canonicalBook.IsGraphicAudio,
                AudioProductionType = canonicalBook.AudioProductionType,
                ProviderUrls = canonicalBook.ProviderUrls
            };

            // Insert the book (sets Id on the instance)
            _bookService.InsertMany(new List<Book> { newBook });

            var newEditions = new List<Edition>();

            foreach (var srcEd in retainedSourceEditions)
            {
                var edSuffix = $"{suffix}_{srcEd.Id}";
                var newEd = new Edition
                {
                    BookId = newBook.Id,
                    ForeignEditionId = srcEd.ForeignEditionId,
                    Title = srcEd.Title,
                    Subtitle = srcEd.Subtitle,
                    TitleSlug = (srcEd.TitleSlug ?? srcEd.Title).Trim() + "_copy_" + edSuffix,
                    MatchingTitle = srcEd.MatchingTitle,
                    Asin = srcEd.Asin,
                    Isbn13 = srcEd.Isbn13,
                    Isbn10 = srcEd.Isbn10,
                    GoodreadsEditionId = srcEd.GoodreadsEditionId,
                    HardcoverEditionId = srcEd.HardcoverEditionId,
                    OpenLibraryEditionId = srcEd.OpenLibraryEditionId,
                    Language = srcEd.Language,
                    Overview = srcEd.Overview,
                    Format = srcEd.Format,
                    IsEbook = srcEd.IsEbook,
                    Disambiguation = srcEd.Disambiguation,
                    Publisher = srcEd.Publisher,
                    PageCount = srcEd.PageCount,
                    ReleaseDate = srcEd.ReleaseDate,
                    Images = srcEd.Images,
                    Links = srcEd.Links,
                    Ratings = srcEd.Ratings,
                    AudibleASIN = srcEd.AudibleASIN,
                    GoogleBooksEditionId = srcEd.GoogleBooksEditionId,
                    ReviewCount = srcEd.ReviewCount,
                    Narrator = srcEd.Narrator,
                    NarratorNames = srcEd.NarratorNames,
                    Chapters = srcEd.Chapters?.Select(c => new EditionChapter
                    {
                        Title = c?.Title,
                        StartOffsetMs = c?.StartOffsetMs ?? 0,
                        StartOffsetSec = c?.StartOffsetSec ?? 0,
                        LengthMs = c?.LengthMs ?? 0
                    }).ToList() ?? new List<EditionChapter>(),
                    ProviderUrls = srcEd.ProviderUrls,
                    LastUpdated = DateTime.UtcNow,
                    Monitored = false,
                    ManualAdd = false,
                    IsFallbackEdition = srcEd.IsFallbackEdition,
                    ReadingFormatId = srcEd.ReadingFormatId,
                    EditionFormat = srcEd.EditionFormat,
                    EditionInfo = srcEd.EditionInfo,
                    DurationSeconds = srcEd.DurationSeconds,
                    ChapterCount = srcEd.ChapterCount,
                    HasChapters = srcEd.HasChapters,
                    IsGraphicAudio = srcEd.IsGraphicAudio,
                    AudioProductionType = srcEd.AudioProductionType
                };
                newEditions.Add(newEd);
            }

            if (newEditions.Any())
            {
                _editionService.InsertMany(newEditions);
            }

            newBook.Editions = newEditions;
            EditionPinPolicy.MarkSelectionAsAutomatic(newBook, newEditions);
            _bookService.RefreshProviderAliases(newBook);

            _logger.Debug("[HARDCOVER-CLONE] Cloned BookId={0} from BookId={1} (MediaType={2}) with {3} editions",
                newBook.Id, canonicalBook.Id, canonicalBook.MediaType, newEditions.Count);

            return newBook;
        }

        private static HashSet<string> CloneProviderIds(IEnumerable<string> source)
        {
            var values = source?
                .Where(id => !id.IsNullOrWhiteSpace())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return values?.Count > 0 ? values : null;
        }

        private IReadOnlyList<Edition> SelectCloneEditions(Book canonicalBook, IEnumerable<Edition> sourceEditions, string requiredEditionProviderId, string requiredEditionRawId)
        {
            var editions = (sourceEditions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null)
                .ToList();

            if (!editions.Any() || _editionSelector == null || canonicalBook == null)
            {
                return editions;
            }

            var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requiredEdition = editions.FirstOrDefault(edition =>
                !requiredEditionProviderId.IsNullOrWhiteSpace() &&
                MatchesProviderId(edition.HardcoverEditionId, requiredEditionProviderId, requiredEditionRawId));

            foreach (var edition in editions)
            {
                if (edition.ForeignEditionId.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (!requiredEditionProviderId.IsNullOrWhiteSpace() &&
                    MatchesProviderId(edition.HardcoverEditionId, requiredEditionProviderId, requiredEditionRawId))
                {
                    protectedIds.Add(edition.ForeignEditionId);
                }
            }

            var author = canonicalBook.Author;
            if ((author == null || (!author.AudiobookMetadataProfileId.HasValue && !author.EbookMetadataProfileId.HasValue)) &&
                canonicalBook.AuthorId > 0)
            {
                try
                {
                    author = _authorService.GetAuthor(canonicalBook.AuthorId);
                }
                catch
                {
                    author = canonicalBook.Author;
                }
            }

            MetadataProfile metadataProfile = canonicalBook.MediaType == BookMediaType.Ebook
                ? author?.EbookMetadataProfile?.Value
                : author?.AudiobookMetadataProfile?.Value;

            var filteredEditions = _editionMetadataProfileFilter?.Apply(editions, metadataProfile, protectedIds) ?? editions;

            var selection = _editionSelector.SelectRetainedEditions(
                canonicalBook.MediaType,
                filteredEditions);

            var retained = selection?.RetainedEditions?.Where(e => e != null).ToList() ?? new List<Edition>();

            if (requiredEdition == null)
            {
                return retained;
            }

            var requiredEditionKey = EditionSelector.GetRetentionDedupeKey(requiredEdition);
            var retainedKeys = new HashSet<string>(retained
                .Select(EditionSelector.GetRetentionDedupeKey)
                .Where(key => key.IsNotNullOrWhiteSpace()),
                StringComparer.OrdinalIgnoreCase);

            if (requiredEditionKey.IsNotNullOrWhiteSpace() && retainedKeys.Contains(requiredEditionKey))
            {
                return retained;
            }

            return editions
                .Where(edition =>
                    retainedKeys.Contains(EditionSelector.GetRetentionDedupeKey(edition)) ||
                    string.Equals(EditionSelector.GetRetentionDedupeKey(edition), requiredEditionKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private Book FindBookAlreadyTargetingHardcoverEdition(IEnumerable<Book> candidateBooks, string editionProviderId, string editionRawId)
        {
            return (candidateBooks ?? Enumerable.Empty<Book>())
                .Where(book =>
                {
                    var monitoredEdition = GetMonitoredEdition(book?.Editions);
                    return monitoredEdition != null &&
                           MatchesProviderId(monitoredEdition.HardcoverEditionId, editionProviderId, editionRawId);
                })
                .OrderBy(book => book.Id)
                .FirstOrDefault();
        }

        private Edition GetMonitoredEdition(IEnumerable<Edition> editions)
        {
            return editions?
                .Where(e => e != null && e.Monitored)
                .OrderBy(e => e.Id)
                .FirstOrDefault();
        }

        private static Book FindReusableHardcoverTargetBook(IEnumerable<Book> candidateBooks, ISet<int> reservedIds, ISet<int> bookIdsWithFiles)
        {
            return (candidateBooks ?? Enumerable.Empty<Book>())
                .Where(book =>
                    book != null &&
                    (reservedIds?.Contains(book.Id) != true) &&
                    (bookIdsWithFiles?.Contains(book.Id) != true) &&
                    EditionPinPolicy.CanAutomationSelectEdition(book, book.Editions))
                .OrderBy(book => book.Id)
                .FirstOrDefault();
        }

        private bool ShouldProcessBookReport(ImportListItemInfo report, List<ImportListExclusion> listExclusions, ImportListLocalLookup localLookup, ImportListSyncStats stats = null, Dictionary<string, Book> liveBookLookupHitCache = null)
        {
            var bookProviderId = GetBookProviderId(report);
            var authorProviderId = GetAuthorProviderId(report);
            var reportMediaType = GetReportMediaTypeForExclusion(report);

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
            if (existingBook == null && bookProviderId.IsNotNullOrWhiteSpace() && listExclusions.Any(s => ImportListExclusionBookMatcher.AppliesToProviderId(s, bookProviderId, reportMediaType)))
            {
                _logger.Debug("{0} [{1}] Rejected due to list exclusion", report.EditionProviderId, report.Book);
                stats?.MarkExcluded();
                return false;
            }

            // Check if author is excluded
            if (existingBook != null && existingBook.Author != null)
            {
                if (IsAuthorExcludedByAnyProviderId(listExclusions, existingBook.Author))
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
            if (existingAuthor != null && IsAuthorExcludedByAnyProviderId(listExclusions, existingAuthor))
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

        private static BookMediaType? GetReportMediaTypeForExclusion(ImportListItemInfo report)
        {
            if (!report?.HardcoverReadingFormatId.HasValue ?? true)
            {
                return null;
            }

            return report.HardcoverReadingFormatId == 2
                ? BookMediaType.Audiobook
                : (report.HardcoverReadingFormatId == 3 || report.HardcoverReadingFormatId == 4)
                    ? BookMediaType.Ebook
                    : null;
        }

        private bool IsAuthorExcludedByAnyProviderId(List<ImportListExclusion> listExclusions, Author author)
        {
            if (author == null) return false;

            var providerIds = AuthorIdentity.GetProviderIdentityTokenList(author);
            return listExclusions.Any(exclusion => providerIds.Any(pid => MatchesExclusionId(pid, exclusion.ForeignId)));
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

            var processed = ProcessListItems(listItems);

            foreach (var importList in enabledImportLists)
            {
                try
                {
                    importList.CommitState();
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Error committing state for Import List {0} ({1})", importList.Name, importList.Definition.Name);
                }
            }

            return processed;
        }

        private List<Book> SyncList(ImportListDefinition definition)
        {
            _logger.ProgressInfo($"Starting Import List Refresh for List {definition.Name}");

            var listItems = _listFetcherAndParser.FetchSingleList(definition).ToList();

            var processed = ProcessListItems(listItems);

            try
            {
                var importList = _importListFactory.GetInstance(definition);
                importList?.CommitState();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error committing state for Import List {0} ({1})", definition.Name, definition.Id);
            }

            return processed;
        }

        private List<Book> ProcessListItems(List<ImportListItemInfo> items)
        {
            var processed = new List<Book>();
            var addedAuthorIds = new List<int>();
            var bookIdsToSearch = new HashSet<int>();
            var authorIdsToMissingSearch = new HashSet<int>();
            var authorsToMonitor = new Dictionary<string, ImportListItemInfo>(StringComparer.OrdinalIgnoreCase); // providerId -> report
            var booksToMonitor = new List<(string authorProviderId, string bookProviderId, ImportListItemInfo report)>();
            var hardcoverBooksToMonitor = new Dictionary<(int importListId, string authorProviderId), (HashSet<string> audiobookBooks, HashSet<string> ebookBooks)>();
            var goodreadsBooksToMonitor = new Dictionary<(int importListId, string authorProviderId), (HashSet<string> audiobookBooks, HashSet<string> ebookBooks)>();
            var pendingAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingAuthorsQueuedEarly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var booksMonitored = 0;

            if (items.Count == 0)
            {
                _logger.ProgressInfo("No list items to process");
                return new List<Book>();
            }

            var stats = new ImportListSyncStats();
            var liveAuthorLookupHitCache = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);
            var liveBookLookupHitCache = new Dictionary<string, Book>(StringComparer.OrdinalIgnoreCase);

            _logger.ProgressInfo("Import list sync: fetched {0} list items; indexing local library", items.Count);
            var localLookup = BuildImportListLocalLookup(items);
            _logger.ProgressInfo("Import list sync: local match index ready ({0} authors, {1} books)",
                localLookup.AuthorCount, localLookup.BookCount);

            var reportNumber = 1;
            var listExclusions = _importListExclusionService.All();
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

            // Map provider IDs in bounded parallelism. Goodreads bookshelf RSS only includes edition IDs,
            // so we need to map those to work + author IDs before we can import authors/books.
            // We process mapping in batches so we can start importing authors before mapping completes,
            // allowing later mapping to hit the local library instead of the metadata server.
            const int maxDegreeOfParallelism = 12;
            const int mappingBatchSize = 250;
            const int mappingReportInterval = 100;

            var reportsNeedingMapping = items
                .Where(r =>
                    (r.Book.IsNotNullOrWhiteSpace() || r.EditionGoodreadsId.IsNotNullOrWhiteSpace()) &&
                    (r.EditionGoodreadsId.IsNullOrWhiteSpace() ||
                     r.AuthorGoodreadsId.IsNullOrWhiteSpace() ||
                     r.BookGoodreadsId.IsNullOrWhiteSpace()))
                .ToList();

            var totalToMap = reportsNeedingMapping.Count;
            var directIdentityCount = items.Count(r => GetAuthorProviderId(r).IsNotNullOrWhiteSpace() && GetBookProviderId(r).IsNotNullOrWhiteSpace());
            var mappedCompleted = 0;
            var mappedReady = 0;
            var mappedUnavailable = 0;
            var lastReported = 0;
            var attemptedEarlyAuthorImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowEarlyAuthorQueueing = totalToMap > 0;

            _logger.ProgressInfo("Import list sync: checking local matches for {0} items ({1} already include author/book IDs, {2} need metadata ID resolution)",
                items.Count, directIdentityCount, totalToMap);

            if (!allowEarlyAuthorQueueing)
            {
                _logger.ProgressInfo("Import list sync: provider IDs are inline; collecting full selections before queueing authors");
            }

            if (totalToMap > 0)
            {
                _logger.ProgressInfo("Import list sync: mapping 0/{0} items (concurrency: {1})",
                    totalToMap, maxDegreeOfParallelism);
            }

            for (var offset = 0; offset < items.Count; offset += mappingBatchSize)
            {
                var batch = items.Skip(offset).Take(mappingBatchSize).ToList();

                var batchToMap = batch
                    .Where(r =>
                        (r.Book.IsNotNullOrWhiteSpace() || r.EditionGoodreadsId.IsNotNullOrWhiteSpace()) &&
                        (r.EditionGoodreadsId.IsNullOrWhiteSpace() ||
                         r.AuthorGoodreadsId.IsNullOrWhiteSpace() ||
                         r.BookGoodreadsId.IsNullOrWhiteSpace()))
                    .ToList();

                if (batchToMap.Any())
                {
                    Parallel.ForEach(batchToMap,
                        new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
                        report =>
                        {
                            try
                            {
                                MapBookReport(report);
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug(ex, "Import list sync: failed to map list item ({0} - {1})", report.Author, report.Book);
                            }
                            finally
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
                                if (completed == totalToMap ||
                                    (completed - reported >= mappingReportInterval &&
                                     Interlocked.CompareExchange(ref lastReported, completed, reported) == reported))
                                {
                                    _logger.ProgressInfo("Import list sync: resolving metadata identities {0}/{1} (ready: {2}, unavailable: {3}, queued: {4})",
                                        completed, totalToMap, Volatile.Read(ref mappedReady), Volatile.Read(ref mappedUnavailable), pendingAuthors.Count);
                                }
                            }
                        });
                }

                if (allowEarlyAuthorQueueing)
                {
                    // Opportunistically import authors as soon as fallback mapping gives us their provider IDs.
                    foreach (var report in batch)
                    {
                        string authorProviderId;

                        if (report.Book.IsNotNullOrWhiteSpace() || report.EditionProviderId.IsNotNullOrWhiteSpace())
                        {
                            if (!ShouldProcessBookReport(report, listExclusions, localLookup, liveBookLookupHitCache: liveBookLookupHitCache))
                            {
                                continue;
                            }

                            authorProviderId = GetAuthorProviderId(report);
                        }
                        else if (report.Author.IsNotNullOrWhiteSpace() || report.AuthorProviderId.IsNotNullOrWhiteSpace())
                        {
                            if (!ShouldProcessAuthorReport(report, listExclusions, localLookup, liveAuthorLookupHitCache))
                            {
                                continue;
                            }

                            authorProviderId = GetAuthorProviderId(report);
                        }
                        else
                        {
                            continue;
                        }

                        if (authorProviderId.IsNullOrWhiteSpace() ||
                            pendingAuthors.Contains(authorProviderId) ||
                            attemptedEarlyAuthorImports.Contains(authorProviderId))
                        {
                            continue;
                        }

                        attemptedEarlyAuthorImports.Add(authorProviderId);

                        var importList = GetImportListDefinition(report.ImportListId);

                        try
                        {
                            var existingAuthor = FindExistingAuthor(authorProviderId, localLookup, liveAuthorLookupHitCache);
                            if (existingAuthor != null)
                            {
                                stats.MarkExistingAuthor(authorProviderId);

                                if (importList.ShouldMonitorExisting && !existingAuthor.Monitored)
                                {
                                    existingAuthor.Monitored = true;
                                    _authorService.UpdateAuthor(existingAuthor);
                                    addedAuthorIds.Add(existingAuthor.Id);

                                    if (importList.ShouldSearch && importList.ShouldMonitor == ImportListMonitorType.EntireAuthor)
                                    {
                                        authorIdsToMissingSearch.Add(existingAuthor.Id);
                                    }
                                }

                                continue;
                            }

                            var hardcoverLibrarySettings = GetHardcoverLibrarySettings(importList);
                            var goodreadsSettings = GetGoodreadsSettings(importList);

                            MonitoringConfig config;
                            if (hardcoverLibrarySettings != null)
                            {
                                config = BuildMonitoringConfigForHardcoverLibraryImportList(importList, hardcoverLibrarySettings, authorProviderId, report.Author, null);
                            }
                            else if (goodreadsSettings != null)
                            {
                                config = BuildMonitoringConfigForGoodreadsImportList(importList, goodreadsSettings, authorProviderId, report.Author, null);
                            }
                            else
                            {
                                config = BuildConfigFromImportList(importList);
                            }

                            if (QueueAuthorForImportList(importList, authorProviderId, report.Author, config, pendingAuthors, "early mapping"))
                            {
                                pendingAuthorsQueuedEarly.Add(authorProviderId);

                                if (pendingAuthorsQueuedEarly.Count % 25 == 0)
                                {
                                    _logger.ProgressInfo("Import list sync: queued {0} authors while resolving metadata identities", pendingAuthorsQueuedEarly.Count);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Failed to add author {0} from import list (early import)", report.Author);
                        }
                    }
                }
            }

            if (pendingAuthorsQueuedEarly.Any())
            {
                _logger.ProgressInfo("Import list sync: refreshing local match index after queueing {0} authors", pendingAuthorsQueuedEarly.Count);
                localLookup = BuildImportListLocalLookup(items);
                _logger.ProgressInfo("Import list sync: refreshed local match index ({0} authors, {1} books)",
                    localLookup.AuthorCount, localLookup.BookCount);
            }

            _logger.ProgressInfo("Import list sync: collecting author and book selections");

            // First pass: collect all authors and books to process
            foreach (var report in items)
            {
                var currentReportNumber = reportNumber++;

                if (currentReportNumber == 1 || currentReportNumber % 25 == 0 || currentReportNumber == items.Count)
                {
                    _logger.ProgressInfo("Import list sync: checking list item {0}/{1} (authors: {2}, books: {3}, unavailable: {4}, excluded: {5})",
                        currentReportNumber, items.Count, authorsToMonitor.Count, booksToMonitor.Count, mappedUnavailable + stats.MissingProviderIds, stats.Excluded);
                }

                var importList = GetImportListDefinition(report.ImportListId);

                if (report.Book.IsNotNullOrWhiteSpace() || report.EditionProviderId.IsNotNullOrWhiteSpace())
                {
                    // Check exclusions
                    if (!ShouldProcessBookReport(report, listExclusions, localLookup, stats, liveBookLookupHitCache))
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
                    if (!authorsToMonitor.ContainsKey(authorProviderId))
                    {
                        authorsToMonitor[authorProviderId] = report;
                    }

                    // Track book to monitor
                    booksToMonitor.Add((authorProviderId, bookProviderId, report));

                    var hardcoverLibrarySettings = GetHardcoverLibrarySettings(importList);
                    if (hardcoverLibrarySettings != null && importList.ShouldMonitor == ImportListMonitorType.SpecificBook)
                    {
                        var key = (importList.Id, authorProviderId);
                        if (!hardcoverBooksToMonitor.TryGetValue(key, out var monitorLists))
                        {
                            monitorLists = (
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            );
                        }

                        var editionProviderId = report.EditionProviderId?.Trim();
                        var (editionPrefix, editionRawId) = SplitProviderId(editionProviderId);
                        var hasEditionSelection = editionPrefix.IsNotNullOrWhiteSpace() && editionRawId.IsNotNullOrWhiteSpace();

                        if (!hasEditionSelection)
                        {
                            if (hardcoverLibrarySettings.MonitorAudiobooks)
                            {
                                monitorLists.audiobookBooks.Add(bookProviderId);
                            }

                            if (hardcoverLibrarySettings.MonitorEbooks)
                            {
                                monitorLists.ebookBooks.Add(bookProviderId);
                            }
                        }
                        else if (report.HardcoverReadingFormatId == 2)
                        {
                            if (hardcoverLibrarySettings.MonitorAudiobooks)
                            {
                                monitorLists.audiobookBooks.Add(bookProviderId);
                            }
                        }
                        else if (report.HardcoverReadingFormatId == 3 || report.HardcoverReadingFormatId == 4)
                        {
                            if (hardcoverLibrarySettings.MonitorEbooks)
                            {
                                monitorLists.ebookBooks.Add(bookProviderId);
                            }
                        }
                        else
                        {
                            if (hardcoverLibrarySettings.MonitorAudiobooks)
                            {
                                monitorLists.audiobookBooks.Add(bookProviderId);
                            }

                            if (hardcoverLibrarySettings.MonitorEbooks)
                            {
                                monitorLists.ebookBooks.Add(bookProviderId);
                            }
                        }

                        hardcoverBooksToMonitor[key] = monitorLists;
                    }

                    if (importList.Settings is IGoodreadsDualMediaImportListSettings goodreadsSettings &&
                        importList.ShouldMonitor == ImportListMonitorType.SpecificBook)
                    {
                        var key = (importList.Id, authorProviderId);
                        if (!goodreadsBooksToMonitor.TryGetValue(key, out var monitorLists))
                        {
                            monitorLists = (
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            );
                        }

                        if (goodreadsSettings.MonitorAudiobooks)
                        {
                            monitorLists.audiobookBooks.Add(bookProviderId);
                        }

                        if (goodreadsSettings.MonitorEbooks)
                        {
                            monitorLists.ebookBooks.Add(bookProviderId);
                        }

                        goodreadsBooksToMonitor[key] = monitorLists;
                    }
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
                        if (!ShouldProcessAuthorReport(report, listExclusions, localLookup, liveAuthorLookupHitCache, stats))
                        {
                            continue;
                        }

                        if (!authorsToMonitor.ContainsKey(authorProviderId))
                        {
                            authorsToMonitor[authorProviderId] = report;
                        }
                    }
                }

            // Process authors and books
            var hardcoverAuthorBookIdsWithFilesCache = new Dictionary<int, HashSet<int>>();
            var hardcoverReservedBookIds = new Dictionary<(int authorId, string bookProviderId, BookMediaType mediaType), HashSet<int>>();
            var authorsProcessed = 0;
            var totalAuthorsToProcess = authorsToMonitor.Count;

            // Add authors that need to be added
            foreach (var kvp in authorsToMonitor)
            {
                authorsProcessed++;
                var authorProviderId = kvp.Key;
                var report = kvp.Value;
                var importList = GetImportListDefinition(report.ImportListId);

                if (pendingAuthors.Contains(authorProviderId))
                {
                    continue;
                }

                try
                {
                    // Check if author already exists
                    var existingAuthor = FindExistingAuthor(authorProviderId, localLookup, liveAuthorLookupHitCache);

                    if (existingAuthor == null)
                    {
                        // Add new author
                        var hardcoverLibrarySettings = GetHardcoverLibrarySettings(importList);
                        var goodreadsSettings = GetGoodreadsSettings(importList);

                        MonitoringConfig config;

                        if (hardcoverLibrarySettings != null)
                        {
                            config = BuildMonitoringConfigForHardcoverLibraryImportList(importList, hardcoverLibrarySettings, authorProviderId, report.Author, hardcoverBooksToMonitor);
                        }
                        else if (goodreadsSettings != null)
                        {
                            config = BuildMonitoringConfigForGoodreadsImportList(importList, goodreadsSettings, authorProviderId, report.Author, goodreadsBooksToMonitor);
                        }
                        else
                        {
                            config = BuildConfigFromImportList(importList);
                            AddGenericSpecificBooksToMonitor(config, importList, authorProviderId, booksToMonitor);
                        }

                        QueueAuthorForImportList(importList, authorProviderId, report.Author, config, pendingAuthors, "author pass");
                    }
                    else
                    {
                        stats.MarkExistingAuthor(authorProviderId);

                        if (importList.ShouldMonitorExisting && !existingAuthor.Monitored)
                        {
                            // Update existing author monitoring
                            existingAuthor.Monitored = true;
                            _authorService.UpdateAuthor(existingAuthor);
                            addedAuthorIds.Add(existingAuthor.Id);

                            if (importList.ShouldSearch && importList.ShouldMonitor == ImportListMonitorType.EntireAuthor)
                            {
                                authorIdsToMissingSearch.Add(existingAuthor.Id);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to add author {0} from import list", report.Author);
                    _logger.ProgressError("Import list '{0}': failed to add author '{1}' ({2})", importList.Name, report.Author, ex.Message);
                }

                if (authorsProcessed == totalAuthorsToProcess || authorsProcessed % 25 == 0)
                {
                    _logger.ProgressInfo("Import list sync: authors {0}/{1} (queued: {2})",
                        authorsProcessed, totalAuthorsToProcess, pendingAuthors.Count);
                }
            }

            // For authors that were queued during early import (before we had a complete picture of all selected books),
            // update the pending import record with the full book selection so it monitors the right items when it becomes available.
            if (pendingAuthorsQueuedEarly.Any())
            {
                _logger.ProgressInfo("Import list sync: updating queued authors with full selection ({0})", pendingAuthorsQueuedEarly.Count);

                foreach (var authorProviderId in pendingAuthorsQueuedEarly)
                {
                    if (!authorsToMonitor.TryGetValue(authorProviderId, out var report))
                    {
                        continue;
                    }

                    var importList = GetImportListDefinition(report.ImportListId);
                    var hardcoverLibrarySettings = GetHardcoverLibrarySettings(importList);
                    var goodreadsSettings = GetGoodreadsSettings(importList);

                    MonitoringConfig config;
                    if (hardcoverLibrarySettings != null)
                    {
                        config = BuildMonitoringConfigForHardcoverLibraryImportList(importList, hardcoverLibrarySettings, authorProviderId, report.Author, hardcoverBooksToMonitor);
                    }
                    else if (goodreadsSettings != null)
                    {
                        config = BuildMonitoringConfigForGoodreadsImportList(importList, goodreadsSettings, authorProviderId, report.Author, goodreadsBooksToMonitor);
                    }
                    else
                    {
                        config = BuildConfigFromImportList(importList);
                        AddGenericSpecificBooksToMonitor(config, importList, authorProviderId, booksToMonitor);
                    }

                    try
                    {
                        _pendingAuthorImportService.EnqueueAsync(authorProviderId, config, config.RequestedBy ?? "ImportListSync")
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Import list sync: failed to update queued author config for {0}", authorProviderId);
                    }
                }
            }

            var booksProcessed = 0;
            var totalBooksToProcess = booksToMonitor.Count;
            var authorBooksCache = new Dictionary<int, List<Book>>();

            List<Book> GetCachedBooksByAuthor(int authorId)
            {
                if (!authorBooksCache.TryGetValue(authorId, out var authorBooks))
                {
                    authorBooks = _bookService.GetBooksByAuthor(authorId);
                    authorBooksCache[authorId] = authorBooks;
                }

                return authorBooks;
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

                if (importList.ShouldMonitor == ImportListMonitorType.SpecificBook)
                {
                    try
                    {
                        // Respect "Monitor Existing" for authors already in Chaptarr. Newly added authors should always be processed.
                        if (!importList.ShouldMonitorExisting)
                        {
                            continue;
                        }

                        if (pendingAuthors.Contains(authorProviderId))
                        {
                            continue;
                        }

                        var hardcoverLibrarySettings = GetHardcoverLibrarySettings(importList);

                        if (hardcoverLibrarySettings != null)
                        {
                            var author = FindExistingAuthor(authorProviderId, localLookup, liveAuthorLookupHitCache);
                            if (author == null)
                            {
                                // Author add was queued or failed during the author pass; skip book monitoring until it exists locally.
                                continue;
                            }

                            var authorBooks = GetCachedBooksByAuthor(author.Id);

                            // Hardcover library import list stores the Hardcover edition ID (when selected) in EditionGoodreadsId as hc-ed:<id>.
                            var editionProviderId = report.EditionProviderId?.Trim();
                            var (editionPrefix, editionRawId) = SplitProviderId(editionProviderId);
                            var hasEditionSelection = editionPrefix.IsNotNullOrWhiteSpace() && editionRawId.IsNotNullOrWhiteSpace();

                            var (bookPrefix, bookRawId) = SplitProviderId(bookProviderId);

                            // If no edition is selected on Hardcover (Want to Read without edition), monitor both media types.
                            if (!hasEditionSelection)
                            {
                                var books = authorBooks
                                    .Where(b => BookMatchesProviderId(b, bookPrefix, bookProviderId, bookRawId))
                                    .ToList();

                                foreach (var book in books)
                                {
                                    var wasMonitored = book.AudiobookMonitored || book.EbookMonitored;

                                    if (book.MediaType == BookMediaType.Audiobook)
                                    {
                                        if (hardcoverLibrarySettings.MonitorAudiobooks)
                                        {
                                            book.AudiobookMonitored = true;
                                        }
                                    }
                                    else if (book.MediaType == BookMediaType.Ebook)
                                    {
                                        if (hardcoverLibrarySettings.MonitorEbooks)
                                        {
                                            book.EbookMonitored = true;
                                        }
                                    }

                                    if (importList.ShouldSearch &&
                                        importList.ShouldMonitor == ImportListMonitorType.SpecificBook &&
                                        !wasMonitored &&
                                        (book.AudiobookMonitored || book.EbookMonitored) &&
                                        book.Id > 0)
                                    {
                                        bookIdsToSearch.Add(book.Id);
                                    }
                                }

                                if (books.Any())
                                {
                                    _bookService.UpdateMany(books);
                                    booksMonitored++;
                                }

                                // Ensure the author is monitored if we are monitoring any books
                                if (books.Any(b => b.AudiobookMonitored || b.EbookMonitored) && !author.Monitored)
                                {
                                    author.Monitored = true;
                                    _authorService.UpdateAuthor(author);
                                }

                                if (!addedAuthorIds.Contains(author.Id))
                                {
                                    addedAuthorIds.Add(author.Id);
                                }

                                continue;
                            }

                            // Edition selected: monitor only the media type implied by that edition, and ensure this edition is the monitored one.
                            var editionToClassify = authorBooks
                                .SelectMany(b => b.Editions ?? new List<Edition>())
                                .FirstOrDefault(e => MatchesProviderId(e.HardcoverEditionId, editionProviderId, editionRawId));

                            if (editionToClassify == null)
                            {
                                var hasHardcoverReadingFormat = report.HardcoverReadingFormatId.HasValue && report.HardcoverReadingFormatId.Value > 0;

                                if (hasHardcoverReadingFormat)
                                {
                                    BookMediaType? desiredMediaTypeFromHardcover = report.HardcoverReadingFormatId == 2
                                        ? BookMediaType.Audiobook
                                        : (report.HardcoverReadingFormatId == 3 || report.HardcoverReadingFormatId == 4)
                                            ? BookMediaType.Ebook
                                            : null;

                                    _logger.Warn("Hardcover Library: selected edition '{0}' not found locally for '{1}' by '{2}'. Using Hardcover reading_format_id={3} to monitor by book ID.",
                                        editionProviderId, report.Book, report.Author, report.HardcoverReadingFormatId);

                                    if (!desiredMediaTypeFromHardcover.HasValue)
                                    {
                                        _logger.Warn("Hardcover Library: reading_format_id={0} does not map to a tracked media type; falling back to monitoring by book ID using list defaults",
                                            report.HardcoverReadingFormatId);
                                    }
                                    else if (desiredMediaTypeFromHardcover == BookMediaType.Audiobook && !hardcoverLibrarySettings.MonitorAudiobooks)
                                    {
                                        continue;
                                    }
                                    else if (desiredMediaTypeFromHardcover == BookMediaType.Ebook && !hardcoverLibrarySettings.MonitorEbooks)
                                    {
                                        continue;
                                    }

                                    var books = authorBooks
                                        .Where(b => !desiredMediaTypeFromHardcover.HasValue || b.MediaType == desiredMediaTypeFromHardcover.Value)
                                        .Where(b => BookMatchesProviderId(b, bookPrefix, bookProviderId, bookRawId))
                                        .ToList();

                                    foreach (var book in books)
                                    {
                                        var wasMonitored = book.AudiobookMonitored || book.EbookMonitored;

                                        if (desiredMediaTypeFromHardcover == BookMediaType.Audiobook)
                                        {
                                            book.AudiobookMonitored = true;
                                            book.EbookMonitored = false;
                                        }
                                        else if (desiredMediaTypeFromHardcover == BookMediaType.Ebook)
                                        {
                                            book.EbookMonitored = true;
                                            book.AudiobookMonitored = false;
                                        }
                                        else
                                        {
                                            if (book.MediaType == BookMediaType.Audiobook)
                                            {
                                                book.AudiobookMonitored = hardcoverLibrarySettings.MonitorAudiobooks;
                                            }
                                            else
                                            {
                                                book.EbookMonitored = hardcoverLibrarySettings.MonitorEbooks;
                                            }
                                        }

                                        if (importList.ShouldSearch &&
                                            importList.ShouldMonitor == ImportListMonitorType.SpecificBook &&
                                            !wasMonitored &&
                                            (book.AudiobookMonitored || book.EbookMonitored) &&
                                            book.Id > 0)
                                        {
                                            bookIdsToSearch.Add(book.Id);
                                        }
                                    }

                                    if (books.Any())
                                    {
                                        _bookService.UpdateMany(books);
                                        booksMonitored++;
                                    }

                                    if (books.Any(b => b.AudiobookMonitored || b.EbookMonitored) && !author.Monitored)
                                    {
                                        author.Monitored = true;
                                        _authorService.UpdateAuthor(author);
                                    }

                                    if (!addedAuthorIds.Contains(author.Id))
                                    {
                                        addedAuthorIds.Add(author.Id);
                                    }

                                    continue;
                                }

                                _logger.Warn("Hardcover Library: selected edition '{0}' not found locally for '{1}' by '{2}'. Falling back to monitoring by book ID.",
                                    editionProviderId, report.Book, report.Author);

                                var fallbackBooks = authorBooks
                                    .Where(b => BookMatchesProviderId(b, bookPrefix, bookProviderId, bookRawId))
                                    .ToList();

                                foreach (var book in fallbackBooks)
                                {
                                    var wasMonitored = book.AudiobookMonitored || book.EbookMonitored;

                                    if (book.MediaType == BookMediaType.Audiobook)
                                    {
                                        if (hardcoverLibrarySettings.MonitorAudiobooks)
                                        {
                                            book.AudiobookMonitored = true;
                                        }
                                    }
                                    else if (book.MediaType == BookMediaType.Ebook)
                                    {
                                        if (hardcoverLibrarySettings.MonitorEbooks)
                                        {
                                            book.EbookMonitored = true;
                                        }
                                    }

                                    if (importList.ShouldSearch &&
                                        importList.ShouldMonitor == ImportListMonitorType.SpecificBook &&
                                        !wasMonitored &&
                                        (book.AudiobookMonitored || book.EbookMonitored) &&
                                        book.Id > 0)
                                    {
                                        bookIdsToSearch.Add(book.Id);
                                    }
                                }

                                if (fallbackBooks.Any())
                                {
                                    _bookService.UpdateMany(fallbackBooks);
                                    booksMonitored++;
                                }

                                if (fallbackBooks.Any(b => b.AudiobookMonitored || b.EbookMonitored) && !author.Monitored)
                                {
                                    author.Monitored = true;
                                    _authorService.UpdateAuthor(author);
                                }

                                if (!addedAuthorIds.Contains(author.Id))
                                {
                                    addedAuthorIds.Add(author.Id);
                                }

                                continue;
                            }

                            var desiredMediaType = GetMediaTypeForEdition(editionToClassify);

                            if (!desiredMediaType.HasValue)
                            {
                                _logger.Warn("Hardcover Library: selected edition '{0}' maps to a physical/unknown format for '{1}' by '{2}'. Falling back to monitoring by book ID using list defaults.",
                                    editionProviderId, report.Book, report.Author);

                                var fallbackBooks = authorBooks
                                    .Where(b => BookMatchesProviderId(b, bookPrefix, bookProviderId, bookRawId))
                                    .ToList();

                                foreach (var book in fallbackBooks)
                                {
                                    var wasMonitored = book.AudiobookMonitored || book.EbookMonitored;

                                    if (book.MediaType == BookMediaType.Audiobook)
                                    {
                                        if (hardcoverLibrarySettings.MonitorAudiobooks)
                                        {
                                            book.AudiobookMonitored = true;
                                        }
                                    }
                                    else if (book.MediaType == BookMediaType.Ebook)
                                    {
                                        if (hardcoverLibrarySettings.MonitorEbooks)
                                        {
                                            book.EbookMonitored = true;
                                        }
                                    }

                                    if (importList.ShouldSearch &&
                                        importList.ShouldMonitor == ImportListMonitorType.SpecificBook &&
                                        !wasMonitored &&
                                        (book.AudiobookMonitored || book.EbookMonitored) &&
                                        book.Id > 0)
                                    {
                                        bookIdsToSearch.Add(book.Id);
                                    }
                                }

                                if (fallbackBooks.Any())
                                {
                                    _bookService.UpdateMany(fallbackBooks);
                                    booksMonitored++;
                                }

                                if (fallbackBooks.Any(b => b.AudiobookMonitored || b.EbookMonitored) && !author.Monitored)
                                {
                                    author.Monitored = true;
                                    _authorService.UpdateAuthor(author);
                                }

                                if (!addedAuthorIds.Contains(author.Id))
                                {
                                    addedAuthorIds.Add(author.Id);
                                }

                                continue;
                            }

                            if (desiredMediaType == BookMediaType.Audiobook && !hardcoverLibrarySettings.MonitorAudiobooks)
                            {
                                continue;
                            }

                            if (desiredMediaType == BookMediaType.Ebook && !hardcoverLibrarySettings.MonitorEbooks)
                            {
                                continue;
                            }

                            var candidateBooks = authorBooks
                                .Where(b => b.MediaType == desiredMediaType.Value)
                                .Where(b => BookMatchesProviderId(b, bookPrefix, bookProviderId, bookRawId))
                                .ToList();

                            if (!candidateBooks.Any())
                            {
                                _logger.Warn("Hardcover Library: no '{0}' instance found for '{1}' by '{2}' (book provider '{3}')",
                                    desiredMediaType, report.Book, report.Author, bookProviderId);
                                continue;
                            }

                            var reservationKey = (author.Id, bookProviderId, desiredMediaType.Value);
                            if (!hardcoverReservedBookIds.TryGetValue(reservationKey, out var reservedIds))
                            {
                                reservedIds = new HashSet<int>();
                                hardcoverReservedBookIds[reservationKey] = reservedIds;
                            }

                            // Prefer a book instance whose monitored edition already targets this Hardcover edition.
                            var targetBook = FindBookAlreadyTargetingHardcoverEdition(candidateBooks, editionProviderId, editionRawId);

                            if (targetBook == null)
                            {
                                if (!hardcoverAuthorBookIdsWithFilesCache.TryGetValue(author.Id, out var bookIdsWithFiles))
                                {
                                    bookIdsWithFiles = new HashSet<int>(_bookService.GetAuthorBooksWithFiles(author).Select(b => b.Id));
                                    hardcoverAuthorBookIdsWithFilesCache[author.Id] = bookIdsWithFiles;
                                }

                                // Reuse an unreserved, non-pinned, no-files instance if possible; otherwise clone a new instance.
                                targetBook = FindReusableHardcoverTargetBook(candidateBooks, reservedIds, bookIdsWithFiles);

                                if (targetBook == null)
                                {
                                    var sourceBook = candidateBooks.OrderBy(b => b.Id).First();
                                    var cloned = CloneBookWithEditions(sourceBook, editionProviderId, editionRawId);
                                    if (cloned != null)
                                    {
                                        authorBooks.Add(cloned);
                                        candidateBooks.Add(cloned);
                                        targetBook = cloned;
                                    }
                                }

                                if (targetBook != null)
                                {
                                    var targetEdition = targetBook.Editions?.FirstOrDefault(e =>
                                        MatchesProviderId(e.HardcoverEditionId, editionProviderId, editionRawId));

                                    if (targetEdition != null)
                                    {
                                        _editionService.SetMonitored(targetEdition);

                                        if (targetBook.Editions != null)
                                        {
                                            foreach (var ed in targetBook.Editions)
                                            {
                                                if (ed.Id == targetEdition.Id)
                                                {
                                                    ed.Monitored = true;
                                                }
                                                else
                                                {
                                                    ed.Monitored = false;
                                                }
                                            }
                                        }

                                        EditionPinPolicy.MarkSelectionAsAutomatic(targetBook, targetBook.Editions);

                                        if (targetEdition.ForeignEditionId.IsNotNullOrWhiteSpace())
                                        {
                                            targetBook.ForeignEditionId = targetEdition.ForeignEditionId;
                                        }
                                    }
                                    else
                                    {
                                        _logger.Warn("Hardcover Library: edition '{0}' not present in local editions for '{1}' by '{2}' (BookId={3})",
                                            editionProviderId, report.Book, report.Author, targetBook.Id);
                                    }
                                }
                            }
                            else
                            {
                                // The desired edition is already selected. Import-list synchronization must not
                                // create, clear, or strengthen an explicit user preservation pin.
                            }

                            if (targetBook == null)
                            {
                                continue;
                            }

                            var wasTargetBookMonitored = targetBook.AudiobookMonitored || targetBook.EbookMonitored;

                            if (targetBook.MediaType == BookMediaType.Audiobook)
                            {
                                targetBook.AudiobookMonitored = true;
                                targetBook.EbookMonitored = false;
                            }
                            else
                            {
                                targetBook.EbookMonitored = true;
                                targetBook.AudiobookMonitored = false;
                            }

                            _bookService.UpdateMany(new List<Book> { targetBook });
                            booksMonitored++;

                            if (importList.ShouldSearch &&
                                importList.ShouldMonitor == ImportListMonitorType.SpecificBook &&
                                !wasTargetBookMonitored &&
                                (targetBook.AudiobookMonitored || targetBook.EbookMonitored) &&
                                targetBook.Id > 0)
                            {
                                bookIdsToSearch.Add(targetBook.Id);
                            }

                            reservedIds.Add(targetBook.Id);

                            if (!author.Monitored)
                            {
                                author.Monitored = true;
                                _authorService.UpdateAuthor(author);
                            }

                            if (!addedAuthorIds.Contains(author.Id))
                            {
                                addedAuthorIds.Add(author.Id);
                            }
                        }
                        else
                        {
                            // Non-Hardcover import lists have full context (root folder + profiles) on ImportListDefinition.
                            // Do not call AddAuthorMonitoringBookAsync here because it cannot inherit import list settings and
                            // would attempt to add the author with config=null, which under "missing profile = disabled" can
                            // hydrate 0 books/editions for a brand new author.
                            var author = FindExistingAuthor(authorProviderId, localLookup, liveAuthorLookupHitCache);

                            if (author == null)
                            {
                                MonitoringConfig config;

                                var goodreadsSettings = GetGoodreadsSettings(importList);
                                if (goodreadsSettings != null)
                                {
                                    config = BuildMonitoringConfigForGoodreadsImportList(importList, goodreadsSettings, authorProviderId, report.Author, goodreadsBooksToMonitor);
                                }
                                else
                                {
                                    config = BuildConfigFromImportList(importList);
                                }

                                if (goodreadsSettings == null && importList.ShouldMonitor == ImportListMonitorType.SpecificBook)
                                {
                                    config.AudiobookBooksToMonitor ??= new List<string>();
                                    config.EbookBooksToMonitor ??= new List<string>();

                                    if (!config.AudiobookBooksToMonitor.Contains(bookProviderId, StringComparer.OrdinalIgnoreCase))
                                    {
                                        config.AudiobookBooksToMonitor.Add(bookProviderId);
                                    }

                                    if (!config.EbookBooksToMonitor.Contains(bookProviderId, StringComparer.OrdinalIgnoreCase))
                                    {
                                        config.EbookBooksToMonitor.Add(bookProviderId);
                                    }
                                }

                                QueueAuthorForImportList(importList, authorProviderId, report.Author, config, pendingAuthors, "specific book");
                                continue;
                            }

                            if (author == null || author.Id <= 0)
                            {
                                continue;
                            }

                            var (bookPrefix, bookRawId) = SplitProviderId(bookProviderId);
                            var targetBooks = GetCachedBooksByAuthor(author.Id)
                                .Where(b => BookMatchesProviderId(b, bookPrefix, bookProviderId, bookRawId))
                                .ToList();

                            if (!targetBooks.Any())
                            {
                                _logger.Warn("Import list '{0}': book '{1}' by '{2}' was not found locally after ensuring author exists; it may have been filtered out or the media type is disabled",
                                    importList.Name, report.Book, report.Author);
                                continue;
                            }

                            var allowAudiobooks = true;
                            var allowEbooks = true;

                            if (importList.Settings is IGoodreadsDualMediaImportListSettings goodreadsMediaSettings)
                            {
                                allowAudiobooks = goodreadsMediaSettings.MonitorAudiobooks;
                                allowEbooks = goodreadsMediaSettings.MonitorEbooks;

                                if (!allowAudiobooks && !allowEbooks)
                                {
                                    allowAudiobooks = true;
                                    allowEbooks = true;
                                }
                            }

                            foreach (var book in targetBooks)
                            {
                                var wasMonitored = book.AudiobookMonitored || book.EbookMonitored;

                                if (book.MediaType == BookMediaType.Audiobook)
                                {
                                    book.AudiobookMonitored = allowAudiobooks;
                                    book.EbookMonitored = false;
                                }
                                else if (book.MediaType == BookMediaType.Ebook)
                                {
                                    book.EbookMonitored = allowEbooks;
                                    book.AudiobookMonitored = false;
                                }

                                if (importList.ShouldSearch &&
                                    importList.ShouldMonitor == ImportListMonitorType.SpecificBook &&
                                    !wasMonitored &&
                                    (book.AudiobookMonitored || book.EbookMonitored) &&
                                    book.Id > 0)
                                {
                                    bookIdsToSearch.Add(book.Id);
                                }
                            }

                            _bookService.UpdateMany(targetBooks);
                            booksMonitored++;

                            if (targetBooks.Any(b => b.AudiobookMonitored || b.EbookMonitored) && !author.Monitored)
                            {
                                author.Monitored = true;
                                _authorService.UpdateAuthor(author);
                            }

                            if (!addedAuthorIds.Contains(author.Id))
                            {
                                addedAuthorIds.Add(author.Id);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to monitor book {0} from import list", report.Book);
                    }
                }
            }

            var unavailableItems = mappedUnavailable + stats.MissingProviderIds;
            var message = $"Import List Sync Completed. Items found: {items.Count}, Existing authors: {stats.ExistingAuthors}, Authors queued: {pendingAuthors.Count}, Books monitored: {booksMonitored}, Unavailable: {unavailableItems}, Excluded: {stats.Excluded}";
            _logger.ProgressInfo(message);

            if (bookIdsToSearch.Any())
            {
                var bookIds = bookIdsToSearch.OrderBy(x => x).ToList();
                _logger.ProgressInfo("Import list sync: Queuing searches for {0} newly monitored books", bookIds.Count);
                _commandQueueManager.Push(new BookSearchCommand(bookIds));
            }

            if (authorIdsToMissingSearch.Any())
            {
                var authorIds = authorIdsToMissingSearch.OrderBy(x => x).ToList();
                _logger.ProgressInfo("Import list sync: Queuing missing searches for {0} authors", authorIds.Count);
                foreach (var authorId in authorIds)
                {
                    _commandQueueManager.Push(new MissingBookSearchCommand(authorId));
                }
            }

            // Refresh added authors
            if (addedAuthorIds.Any())
            {
                _commandQueueManager.Push(new BulkRefreshAuthorCommand(addedAuthorIds.Distinct().ToList(), areNewAuthors: true, forceRefresh: true));
            }

            if (pendingAuthors.Any())
            {
                _logger.ProgressInfo("Import list sync: queued {0} authors for background import", pendingAuthors.Count);
                PushImportListPendingImportDrain();
            }

            return processed;
        }

        private bool TryMapBookReportFromLocalLibrary(ImportListItemInfo report)
        {
            if (report == null)
            {
                return false;
            }

            if (report.AuthorProviderId.IsNotNullOrWhiteSpace())
            {
                report.AuthorProviderId = NormalizeProviderId(report.AuthorProviderId, "gr");
            }

            if (report.BookProviderId.IsNotNullOrWhiteSpace())
            {
                report.BookProviderId = NormalizeProviderId(report.BookProviderId, "gr");
            }

            if (report.EditionProviderId.IsNotNullOrWhiteSpace())
            {
                report.EditionProviderId = NormalizeProviderId(report.EditionProviderId, "gr");
            }

            if (report.AuthorProviderId.IsNotNullOrWhiteSpace() && report.BookProviderId.IsNotNullOrWhiteSpace())
            {
                return true;
            }

            if (report.EditionProviderId.IsNullOrWhiteSpace())
            {
                return false;
            }

            var editionProviderId = report.EditionProviderId.Trim();
            var (editionPrefix, editionRawId) = SplitProviderId(editionProviderId);

            Edition edition = null;

            // Preferred: use provider-specific columns (GoodreadsEditionId / HardcoverEditionId / OpenLibraryEditionId)
            // rather than ForeignEditionId, since ForeignEditionId includes media-type suffixes and can be Hardcover-based.
            if (editionPrefix == "gr" && long.TryParse(editionRawId, out var goodreadsEditionId))
            {
                edition = _editionService.GetEditionByGoodreadsEditionId(goodreadsEditionId);
            }
            else if (editionPrefix == "hc-ed" || editionPrefix == "hc")
            {
                edition = _editionService.GetEditionByHardcoverEditionId(editionRawId);
            }
            else if (editionPrefix == "ol")
            {
                edition = _editionService.GetEditionByOpenLibraryEditionId(editionRawId);
            }

            // Fallback: ForeignEditionId lookup (already-suffixed IDs)
            if (edition == null)
            {
                edition = _editionService.GetEditionByForeignEditionId(editionProviderId);
            }

            // Backward-compat: older ForeignEditionIds used "gr:{id}-{suffix}".
            if (edition == null && editionPrefix == "gr" && long.TryParse(editionRawId, out _))
            {
                edition = _editionService.GetEditionByForeignEditionId($"{editionProviderId}-audiobook") ??
                         _editionService.GetEditionByForeignEditionId($"{editionProviderId}-ebook");
            }

            if (edition == null)
            {
                return false;
            }

            var book = _bookRepository.Find(edition.BookId);
            if (book == null)
            {
                return false;
            }

            Author author = null;
            if (book.AuthorId > 0)
            {
                try
                {
                    author = _authorService.GetAuthor(book.AuthorId);
                }
                catch
                {
                    author = null;
                }
            }

            report.BookProviderId ??= GetPreferredBookProviderId(book);
            report.Book ??= edition.Title ?? book.Title;
            report.Author ??= author?.Name;
            report.AuthorProviderId ??= GetPreferredAuthorProviderId(author);
            return report.AuthorProviderId.IsNotNullOrWhiteSpace() && report.BookProviderId.IsNotNullOrWhiteSpace();
        }

        private bool TryMapBookReportFromIdentityCache(ImportListItemInfo report)
        {
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
                var cached = _bookIdentityCacheRepository.FindBySourceProviderId(sourceProviderId);
                if (cached == null ||
                    cached.BookProviderId.IsNullOrWhiteSpace() ||
                    cached.AuthorProviderId.IsNullOrWhiteSpace())
                {
                    return false;
                }

                report.BookProviderId ??= cached.BookProviderId;
                report.AuthorProviderId ??= cached.AuthorProviderId;
                report.Book ??= cached.Book;
                report.Author ??= cached.Author;
                return report.BookProviderId.IsNotNullOrWhiteSpace() && report.AuthorProviderId.IsNotNullOrWhiteSpace();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Import list sync: failed to read cached identity mapping for {0}", sourceProviderId);
                return false;
            }
        }

        private void CacheBookReportMapping(ImportListItemInfo report)
        {
            if (_bookIdentityCacheRepository == null)
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
                _bookIdentityCacheRepository.UpsertBySourceProviderId(new ImportListBookIdentityCache
                {
                    SourceProviderId = sourceProviderId,
                    BookProviderId = bookProviderId,
                    AuthorProviderId = authorProviderId,
                    Book = report.Book,
                    Author = report.Author,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Import list sync: failed to cache identity mapping for {0}", sourceProviderId);
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
                return NormalizeProviderId(report.EditionProviderId, "gr");
            }

            return NormalizeProviderId(report.BookProviderId, "gr");
        }

        private void MapBookReport(ImportListItemInfo report)
        {
            if (TryMapBookReportFromLocalLibrary(report) || TryMapBookReportFromIdentityCache(report))
            {
                return;
            }

            if (report.EditionProviderId.IsNotNullOrWhiteSpace())
            {
                try
                {
                    // Prefer the metadata server for mapping edition IDs to canonical work + author IDs.
                    // This avoids app-identifying Goodreads API keys and keeps provider ID mapping centralized.
                    var mapped = _bookInfoProxy.GetEditionInfo(report.EditionProviderId);
                    var book = mapped.Item2;
                    var author = mapped.Item3?.FirstOrDefault() ?? book?.Author;

                    report.BookProviderId ??= GetPreferredBookProviderId(book);
                    report.Book = book?.Title;
                    report.Author ??= author?.Name;
                    report.AuthorProviderId ??= GetPreferredAuthorProviderId(author);
                    CacheBookReportMapping(report);
                }
                catch (BookNotFoundException)
                {
                    // No fallback mapping. If the metadata server can't map the provider ID, the import list item is skipped.
                    _logger.Debug("No metadata mapping found for edition [{0}] ({1} - {2})",
                        report.EditionProviderId, report.Author, report.Book);
                }
            }
            else if (report.BookProviderId.IsNotNullOrWhiteSpace())
            {
                try
                {
                    var bookProviderId = GetBookProviderId(report);
                    var mappedWork = _bookInfoProxy.GetWorkInfo(bookProviderId, BookMediaType.Audiobook, AuthorIdentity.NormalizeWorkLookupAuthorHint(bookProviderId, GetAuthorProviderId(report)));
                    var book = mappedWork.Item2;
                    var author = mappedWork.Item3?.FirstOrDefault() ?? book?.Author;

                    report.BookProviderId ??= GetPreferredBookProviderId(book);
                    report.Book ??= book?.Title;
                    report.Author ??= author?.Name;
                    report.AuthorProviderId ??= GetPreferredAuthorProviderId(author);
                    CacheBookReportMapping(report);
                }
                catch (BookNotFoundException)
                {
                    _logger.Debug("No metadata mapping found for work [{0}] ({1} - {2})",
                        report.BookProviderId, report.Author, report.Book);
                }
            }
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
                try
                {
                    listItems.AddRange(importList.Fetch());
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to fetch list items for {0}", importList.Definition.Name);
                }
            }

            var processed = ProcessListItems(listItems);

            foreach (var importList in hardcoverImportLists)
            {
                try
                {
                    importList.CommitState();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to commit state for {0}", importList.Definition.Name);
                }
            }

            return processed;
        }
    }
}
