using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Dapper;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.History;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Services;

namespace NzbDrone.Core.Books
{
    public interface IRefreshAuthorService
    {
        Author ReconcilePublishedAuthorIdentity(Author local, Author remote);
        bool ReconcileAuthorBlob(Author localAuthor, Author authoritativeRemoteAuthor, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds = null, IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds = null, CancellationToken cancellationToken = default);
    }

    public partial class RefreshAuthorService : RefreshEntityServiceBase<Author, Book>,
        IRefreshAuthorService,
        IExecute<RefreshAuthorCommand>,
        IExecute<BulkRefreshAuthorCommand>,
        IExecute<RetryAuthorRefreshCommand>
    {
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IAuthorService _authorService;
        private readonly IAuthorRepository _authorRepository;
        private readonly IBookRepository _bookRepository;
        private readonly IHistoryRepository _historyRepository;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly IRefreshBookService _refreshBookService;
        private readonly IRefreshSeriesService _refreshSeriesService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly ICheckIfAuthorShouldBeRefreshed _checkIfAuthorShouldBeRefreshed;
        private readonly IMonitorNewBookService _monitorNewBookService;
        private readonly IConfigService _configService;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly IAuthorSyncMetadataService _syncMetadataService;
        private readonly IAuthorSyncQueueService _syncQueueService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
        private readonly IEditionSelector _editionSelector;
        private readonly IMainDatabase _mainDatabase;
        private readonly IMetadataServerHealthGate _metadataServerHealthGate;
        private readonly Logger _logger;
        private int _setupRetryQueueFailures;
        private sealed class AuthorRefreshContext : RefreshContext
        {
            public BookRefreshMatchingIndex MatchingIndex { get; set; }
            public List<Exception> IdentityFailures { get; } = new();
            public List<Book> RehomeBlueprint { get; set; }
            public IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> RequestedNarrationIds { get; init; }
            public IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> RequestedNarrationWorkIds { get; init; }
        }

        protected override RefreshContext CreateContext() => new AuthorRefreshContext();
        // SMS /authors/diff hard-rejects requests above 10,000 items and caps the body at 1MB;
        // 9,000 leaves headroom for one oversize author id-set appended to a nearly-full chunk.
        internal const int BulkAuthorDiffMaxItemsPerRequest = 9000;

        public RefreshAuthorService(IMetadataServerHealthGate metadataServerHealthGate,
                                    IProvideAuthorInfo authorInfo,
                                    IAuthorService authorService,
                                    IBookService bookService,
                                    IEditionService editionService,
                                    IMetadataProfileService metadataProfileService,
                                    IRefreshBookService refreshBookService,
                                    IRefreshSeriesService refreshSeriesService,
                                    IEventAggregator eventAggregator,
                                    IManageCommandQueue commandQueueManager,
                                    IMediaFileService mediaFileService,
                                    IHistoryRepository historyRepository,
                                    IRootFolderService rootFolderService,
                                    ICheckIfAuthorShouldBeRefreshed checkIfAuthorShouldBeRefreshed,
                                    IMonitorNewBookService monitorNewBookService,
                                    IConfigService configService,
                                    IImportListExclusionService importListExclusionService,
                                    IAuthorSyncMetadataService syncMetadataService,
                                    IAuthorSyncQueueService syncQueueService,
                                    IRootFolderSettingsResolver rootFolderSettingsResolver,
                                    Logger logger,
                                    IAuthorRepository authorRepository,
                                    IBookRepository bookRepository,
                                    IEditionSelector editionSelector = null,
                                    IMainDatabase mainDatabase = null)
        : base(logger)
        {
            _authorInfo = authorInfo;
            _authorService = authorService;
            _authorRepository = authorRepository ?? throw new ArgumentNullException(nameof(authorRepository));
            _bookRepository = bookRepository ?? throw new ArgumentNullException(nameof(bookRepository));
            _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
            _bookService = bookService;
            _editionService = editionService;
            _metadataProfileService = metadataProfileService;
            _refreshBookService = refreshBookService;
            _refreshSeriesService = refreshSeriesService;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _checkIfAuthorShouldBeRefreshed = checkIfAuthorShouldBeRefreshed;
            _monitorNewBookService = monitorNewBookService;
            _configService = configService;
            _importListExclusionService = importListExclusionService;
            _syncMetadataService = syncMetadataService;
            _syncQueueService = syncQueueService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _logger = logger;
            _editionSelector = editionSelector ?? new EditionSelector(logger);
            _mainDatabase = mainDatabase;
            _metadataServerHealthGate = metadataServerHealthGate ?? throw new ArgumentNullException(nameof(metadataServerHealthGate));
        }

        private RefreshResult GetSkyhookData(ref Author author, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false, string authorIdentifierOverride = null)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var authorIdentifier = authorIdentifierOverride.IsNullOrWhiteSpace()
                    ? GetPreferredAuthorIdentifier(author)
                    : authorIdentifierOverride;

                if (string.IsNullOrEmpty(authorIdentifier))
                {
                    _logger.Error($"No provider ID available for author {author.Name}");
                    return null;
                }

                RefreshResult refreshResult = null;
                try
                {
                    // Get the current ETag from AuthorSyncMetadata if available
                    var syncMetadata = _syncMetadataService.GetSyncMetadata(author.Id);
                    var currentETag = syncMetadata?.ETag;

                    var competingMapping = _syncMetadataService.GetSyncMetadataByExternalId(authorIdentifier);
                    var identityNeedsRefresh = competingMapping != null && competingMapping.AuthorId != author.Id;
                    // Persist the mapping only after the fresh payload has reconciled ownership.
                    // A preceding author in this batch may already have absorbed this local row.

                    refreshResult = _authorInfo.RefreshAuthorInfo(authorIdentifier, currentETag, forceRefresh, expectedPublishedETag,
                        bypassEtag || identityNeedsRefresh || author.AddOptions?.AwaitingMetadata == true);

                    if (refreshResult.Terminal != null)
                    {
                        if (author.AddOptions?.AwaitingMetadata == true)
                        {
                            _eventAggregator.PublishEvent(new AuthorSetupMetadataRefusedEvent(author, authorIdentifier, refreshResult.Terminal));
                        }

                        _syncMetadataService.UpdateSyncResult(author.Id, success: false,
                            error: refreshResult.Terminal.Message, httpStatus: (int?)refreshResult.HttpStatus ?? 0, retryAllowed: false);
                        return null;
                    }

                    if (refreshResult.HasChanges && refreshResult.Author != null)
                    {
                        author = ReconcilePublishedAuthorIdentity(author, refreshResult.Author);
                        refreshResult.ETag ??= expectedPublishedETag;
                        return refreshResult;
                    }
                    else if (refreshResult.Reason == RefreshReason.NotModified)
                    {
                        _logger.Debug($"Author {author.Name} not modified since last refresh (ETag: {refreshResult.ETag ?? "none"})");

                        // Update sync metadata to record successful check (even though no data changed)
                        _syncMetadataService.UpdateSyncResult(
                            author.Id,
                            success: true,
                            etag: refreshResult.ETag,
                            httpStatus: 304
                        );

                        StampAuthorsChecked(new[] { author });

                        // Return null to skip refresh entirely — nothing changed on the server,
                        // and the local author object has Books == null which would cause
                        // SortChildren to delete all local books.
                        return null;
                    }
                    else if (refreshResult.Reason == RefreshReason.Error)
                    {
                        _logger.Error($"Failed to refresh author info for {author.Name}: {refreshResult.Message}");

                        // Record the failure in sync metadata
                        _syncMetadataService.UpdateSyncResult(
                            author.Id,
                            success: false,
                            error: refreshResult.Message,
                            httpStatus: refreshResult.HttpStatus.HasValue ? (int)refreshResult.HttpStatus.Value : 0
                        );

                        // Return null to skip refresh entirely — returning the local author
                        // (which has Books == null) would cause SortChildren to interpret
                        // the empty book list as "API says 0 books" and delete all local books.
                        return null;
                    }
                    else if (refreshResult.Reason == RefreshReason.NotFound)
                    {
                        _logger.Error($"Author {author.Name} not found during refresh with ID {authorIdentifier}");

                        // Record that author was deleted from source
                        _syncMetadataService.UpdateSyncResult(
                            author.Id,
                            success: false,
                            error: "Author not found on server (may have been deleted)",
                            httpStatus: 404
                        );

                        return null; // Author was deleted from metadata source
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to refresh author info for {0}", author.Name);
                    _syncMetadataService.UpdateSyncResult(author.Id, success: false, error: ex.Message,
                        httpStatus: (int?)refreshResult?.HttpStatus ?? 0,
                        localFailure: ex is AuthorIdentityUnverifiedException { IsTransient: false });
                    // Repeating the same endpoint through GetAuthorInfo cannot turn unavailable
                    // ownership evidence into a valid snapshot. Retry on the next refresh.

                }

                // Return null to skip refresh — returning the local author (Books == null)
                // would cause mass book deletion via SortChildren.
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exceptions.AuthorNotFoundException)
            {
                _logger.Error($"Could not find author {author.Name} with any provider ID");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Unexpected error refreshing author {author.Name}");
            }
            finally
            {
                stopwatch.Stop();
                _logger.Debug("[AUTHOR-REFRESH-TIMING] Metadata fetch for author '{0}' finished in {1}ms", author?.Name ?? "Unknown", stopwatch.ElapsedMilliseconds);
            }

            return null;
        }

        private static string GetPreferredAuthorIdentifier(Author author)
        {
            return AuthorIdentity.GetPreferredProviderId(author);
        }

        private bool RefreshEntityInfoIfRemoteDataAvailable(Author author, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, CancellationToken cancellationToken = default, RefreshContext context = null)
        {
            if (remoteData == null)
            {
                _logger.Debug("Skipping local refresh for {0}; metadata source returned no changed author data.", author.Name);
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var writer = BookService.EnterWantedEditionLock(author, _logger, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                author = _authorService.GetAuthor(author.Id);
                return author != null && RefreshEntityInfo(author, null, remoteData, forceChildRefresh, forceUpdateFileTags, lastUpdate, context: context);
            }
            finally
            {
                stopwatch.Stop();
                _logger.Debug("[AUTHOR-REFRESH-TIMING] Local reconciliation for author '{0}' finished in {1}ms", author?.Name ?? "Unknown", stopwatch.ElapsedMilliseconds);
            }
        }

        private bool ApplyFetchedAuthor(Author author, RefreshResult fetched, CancellationToken cancellationToken)
        {
            if (fetched == null) return false;
            try
            {
                var updated = RefreshEntityInfoIfRemoteDataAvailable(author, fetched.Author, true, false, null, cancellationToken);
                _syncMetadataService.CreateOrUpdateSyncMetadata(author.Id, GetPreferredAuthorIdentifier(fetched.Author), fetched.ETag);
                _syncMetadataService.UpdateSyncResult(author.Id, success: true, etag: fetched.ETag, httpStatus: (int?)fetched.HttpStatus ?? 200);
                return updated;
            }
            catch (OperationCanceledException) { throw; }
            catch (AuthorIdentityUnverifiedException) { throw; }
            catch (Exception ex)
            {
                _syncMetadataService.UpdateSyncResult(author.Id, success: false, error: ex.Message, httpStatus: (int?)fetched.HttpStatus ?? 0, localFailure: true);
                throw;
            }
        }

        public bool ReconcileAuthorBlob(Author localAuthor, Author authoritativeRemoteAuthor,
            IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds = null,
            IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds = null,
            CancellationToken cancellationToken = default)
        {
            if (localAuthor == null || localAuthor.Id <= 0 || authoritativeRemoteAuthor == null)
            {
                return false;
            }

            localAuthor = ReconcilePublishedAuthorIdentity(localAuthor, authoritativeRemoteAuthor);
            return RefreshEntityInfoIfRemoteDataAvailable(localAuthor, authoritativeRemoteAuthor, true, false, null,
                cancellationToken, new AuthorRefreshContext
                {
                    RequestedNarrationIds = requestedNarrationIds,
                    RequestedNarrationWorkIds = requestedNarrationWorkIds
                });
        }

        protected override RemoteData GetRemoteData(Author local, List<Author> remote, Author data, RefreshContext context)
        {
            var result = new RemoteData();

            if (data != null)
            {
                result.Entity = data;
                // Metadata is now integrated into Author
            }

            return result;
        }

        protected override bool ShouldDelete(Author local)
        {
            // NEVER delete authors - this is a discovery/mapping system
            // Authors should only be removed by explicit user action
            return false;
        }

        protected override void LogProgress(Author local)
        {
            var progress = BulkAuthorRefreshProgressContext.Current;
            if (progress != null)
            {
                _logger.ProgressInfo("Processing author {0}/{1}: {2}", progress.CurrentAuthorIndex, progress.TotalAuthors, local.Name);
                return;
            }

            _logger.ProgressInfo("Checking Info for {0}", local.Name);
        }

        protected override bool IsMerge(Author local, Author remote)
        {
            _logger.Trace($"local: {local.Id} remote: {remote.Id}");
            // Published identity reconciliation owns author merges before this generic refresh.
            return false;
        }

        protected override UpdateResult UpdateEntity(Author local, Author remote)
        {
            var result = UpdateResult.None;

            // Check if metadata has changed
            if (!local.Name.Equals(remote.Name) || !local.Overview.Equals(remote.Overview))
            {
                result = UpdateResult.UpdateTags;
            }

            local.UseMetadataFrom(remote);
            local.Series = remote.Series;
            local.LastInfoSync = DateTime.UtcNow;

            try
            {
                local.Path = new DirectoryInfo(local.Path).FullName;
                local.Path = local.Path.GetActualCasing();
            }
            catch (Exception e)
            {
                _logger.Warn(e, "Couldn't update author path for " + local.Path);
            }

            return result;
        }

        protected override UpdateResult MoveEntity(Author local, Author remote)
        {
            _logger.Debug($"Updating foreign id for {local} to {remote}");

            // Update local author with remote metadata
            local.UseMetadataFrom(remote);

            MoveAuthorImportExclusions(local, remote);

            // Do the standard update
            UpdateEntity(local, remote);

            // We know we need to update tags as author id has changed
            return UpdateResult.UpdateTags;
        }

        protected override UpdateResult MergeEntity(Author local, Author target, Author remote)
        {
            if (!TryMergeAuthorsWithoutDataLoss(local, target, GetPreferredAuthorIdentifier(remote), out var reason, remote))
                throw new InvalidOperationException(reason);
            return UpdateResult.UpdateTags;
        }

        protected override Author GetEntityByForeignId(Author local)
        {
            // Try to find by any provider ID
            {
                if (!string.IsNullOrEmpty(local.HardcoverAuthorId))
                {
                    var author = _authorService.FindByProviderId("hc", local.HardcoverAuthorId);
                    if (author != null) return author;
                }
                if (!string.IsNullOrEmpty(local.GoodreadsAuthorId))
                {
                    var author = _authorService.FindByProviderId("gr", local.GoodreadsAuthorId);
                    if (author != null) return author;
                }
                if (!string.IsNullOrEmpty(local.OpenLibraryAuthorId))
                {
                    var author = _authorService.FindByProviderId("ol", local.OpenLibraryAuthorId);
                    if (author != null) return author;
                }
                if (!string.IsNullOrEmpty(local.GoogleBooksAuthorId))
                {
                    var author = _authorService.FindByProviderId("gb", local.GoogleBooksAuthorId);
                    if (author != null) return author;
                }
            }

            return null;
        }

        protected override void SaveEntity(Author local)
        {
            _authorService.UpdateAuthor(local);
        }

        protected override void DeleteEntity(Author local, bool deleteFiles)
        {
            _authorService.DeleteAuthor(local.Id, deleteFiles);
        }

                protected override List<Book> GetRemoteChildren(Author local, Author remote, RefreshContext context)
                {
                    var run = (AuthorRefreshContext)context;
                    run.RehomeBlueprint = remote?.Books?
                        .Where(book => book != null)
                        .Select(book => RefreshEntityCopy.CloneBook(book, includeEditions: true))
                        .ToList() ?? new List<Book>();

                    return NormalizeRemoteBooks(
                        local,
                        remote?.Books,
                        remote?.Series,
                        _metadataProfileService,
                        _importListExclusionService,
                        _logger,
                        audiobookMetadataProfileIdOverride: null,
                        ebookMetadataProfileIdOverride: null,
                        editionSelector: null,
                        retainEditions: true,
                        logContext: "GetRemoteChildren",
                        requestedNarrationIds: run.RequestedNarrationIds,
                        requestedNarrationWorkIds: run.RequestedNarrationWorkIds);
                }

                internal static List<Book> NormalizeRemoteBooks(
                    Author local,
                    IEnumerable<Book> remoteBooks,
                    IEnumerable<Series> remoteSeries,
                    IMetadataProfileService metadataProfileService,
                    IImportListExclusionService importListExclusionService,
                    Logger logger,
                    int? audiobookMetadataProfileIdOverride,
                    int? ebookMetadataProfileIdOverride,
                    IEditionSelector editionSelector,
                    bool retainEditions,
                    string logContext,
                    IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds = null,
                    IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds = null)
                {
                    var allBooks = (remoteBooks ?? Enumerable.Empty<Book>())
                        .Where(book => book != null)
                        .ToList();

                    logger?.Debug("{0}: Processing {1} books from API for author {2}", logContext, allBooks.Count, local?.Name ?? "Unknown");

                    var uniqueBooks = ExcludeBooksWithoutEditions(allBooks, logger, local, logContext);

                    // Pockets are mirrored exactly as the server sent them; a pocket vanishes ONLY because the
                    // user's metadata profile / exclusions left it with no editions. Pockets that merely SHARE a
                    // provider ID are NOT treated as the same work — they STAY, surfacing the server's identity
                    // bug upstream. The only de-dup is dropping a bit-for-bit identical pocket the server returned
                    // twice (same work tokens AND same editions), applied post-filter below.
                    var profileFiltered = ApplyMetadataProfileFiltering(
                        local,
                        uniqueBooks,
                        remoteSeries,
                        metadataProfileService,
                        logger,
                        audiobookMetadataProfileIdOverride,
                        ebookMetadataProfileIdOverride, requestedNarrationIds, requestedNarrationWorkIds);

                    var afterExclusions = ApplyImportListExclusions(profileFiltered, importListExclusionService);

                    if (retainEditions)
                    {
                        ApplyRetainedEditionSelection(afterExclusions, editionSelector ?? new EditionSelector(logger));
                        afterExclusions = ExcludeBooksWithoutEditions(afterExclusions, logger, local, $"{logContext}:retention");
                    }

                    afterExclusions = CoalesceIdenticalRemoteBookPockets(afterExclusions, logger, local, logContext);

                    logger?.Info("{0}: Returning {1} books (from {2} total, {3} de-duped, {4} profile-filtered, {5} excluded{6})",
                        logContext,
                        afterExclusions.Count,
                        allBooks.Count,
                        uniqueBooks.Count,
                        profileFiltered.Count,
                        profileFiltered.Count - afterExclusions.Count,
                        retainEditions ? ", retained editions applied" : string.Empty);

                    return afterExclusions;
                }

                private static List<Book> ExcludeBooksWithoutEditions(IEnumerable<Book> books, Logger logger, Author local, string logContext)
                {
                    var filtered = new List<Book>();
                    var skipped = 0;

                    foreach (var book in books ?? Enumerable.Empty<Book>())
                    {
                        if (book?.Editions != null && book.Editions.Any())
                        {
                            filtered.Add(book);
                            continue;
                        }

                        skipped++;
                        logger?.Debug("[REMOTE-BOOK-DATA] {0}: skipping remote book '{1}' for author '{2}' because it has no editions. MediaType={3}, ProviderIds=[{4}]",
                            logContext,
                            book?.Title ?? "Unknown",
                            local?.Name ?? "Unknown",
                            book?.MediaType,
                            DescribeBookIdentity(book));
                    }

                    if (skipped > 0)
                    {
                        logger?.Warn("[REMOTE-BOOK-DATA] {0}: skipped {1} remote books with no editions for author '{2}'",
                            logContext,
                            skipped,
                            local?.Name ?? "Unknown");
                    }

                    return filtered;
                }

                private static string DescribeBookIdentity(Book book)
                {
                    if (book == null)
                    {
                        return "none";
                    }

                    var ids = BookIdentity.GetStableWorkProviderIdentityTokens(book)
                        .Concat(book.RemoteProviderIds ?? Enumerable.Empty<string>())
                        .Concat(BookEditionIdentity.GetCanonicalEditionProviderIds(book))
                        .Where(id => id.IsNotNullOrWhiteSpace())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    return ids.Any() ? string.Join(", ", ids) : "none";
                }

                private static List<Book> ApplyMetadataProfileFiltering(
                    Author local,
                    List<Book> books,
                    IEnumerable<Series> remoteSeries,
                    IMetadataProfileService metadataProfileService,
                    Logger logger,
                    int? audiobookMetadataProfileIdOverride,
                    int? ebookMetadataProfileIdOverride,
                    IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationIds,
                    IReadOnlyDictionary<BookMediaType, IReadOnlySet<string>> requestedNarrationWorkIds)
                {
                    var profileFiltered = new List<Book>();
                    var audiobookProfileId = audiobookMetadataProfileIdOverride ?? local?.AudiobookMetadataProfileId;
                    var ebookProfileId = ebookMetadataProfileIdOverride ?? local?.EbookMetadataProfileId;
                    var seriesList = remoteSeries?.Where(series => series != null).ToList() ?? new List<Series>();

                    void FilterMediaType(BookMediaType mediaType, int? profileId)
                    {
                        var groupBooks = books.Where(book => book.MediaType == mediaType).ToList();
                        if (!groupBooks.Any())
                        {
                            return;
                        }

                        if (!profileId.HasValue || profileId.Value <= 0)
                        {
                            logger?.Info("[METADATA-PROFILE] No {0} metadata profile configured for author '{1}'. Treating {0}s as disabled and excluding {2} remote books; unprotected local {0}s will be pruned on refresh.",
                                mediaType,
                                local?.Name ?? "Unknown",
                                groupBooks.Count);
                            return;
                        }

                        if (metadataProfileService == null || !metadataProfileService.Exists(profileId.Value))
                        {
                            logger?.Warn("[METADATA-PROFILE] Metadata profile id {0} not found for author '{1}' ({2}). Treating {2}s as disabled and excluding {3} remote books; unprotected local {2}s will be pruned on refresh.",
                                profileId.Value,
                                local?.Name ?? "Unknown",
                                mediaType,
                                groupBooks.Count);
                            return;
                        }

                        var authorForFilter = new Author
                        {
                            Id = local?.Id ?? 0,
                            Name = local?.Name,
                            Books = groupBooks,
                            Series = seriesList.Where(series => series.MediaType == mediaType).ToList()
                        };

                        profileFiltered.AddRange((requestedNarrationIds?.Count > 0 || requestedNarrationWorkIds?.Count > 0)
                            ? metadataProfileService.FilterBooks(authorForFilter, profileId.Value, requestedNarrationIds, requestedNarrationWorkIds)
                            : metadataProfileService.FilterBooks(authorForFilter, profileId.Value));
                    }

                    FilterMediaType(BookMediaType.Audiobook, audiobookProfileId);
                    FilterMediaType(BookMediaType.Ebook, ebookProfileId);

                    return profileFiltered;
                }

                private static List<Book> ApplyImportListExclusions(List<Book> books, IImportListExclusionService importListExclusionService)
                {
                    if (books == null || books.Count == 0 || importListExclusionService == null)
                    {
                        return books ?? new List<Book>();
                    }

                    var exclusions = importListExclusionService.FindByForeignId(books
                        .SelectMany(ImportListExclusionBookMatcher.GetLookupIds)
                        .Distinct()
                        .ToList());

                    return books.Where(book =>
                    {
                        return !exclusions.Any(exclusion => ImportListExclusionBookMatcher.AppliesToBook(exclusion, book));
                    }).ToList();
                }

                private static void ApplyRetainedEditionSelection(IEnumerable<Book> books, IEditionSelector editionSelector)
                {
                    foreach (var book in books ?? Enumerable.Empty<Book>())
                    {
                        if (book?.Editions == null || book.Editions.Count == 0)
                        {
                            continue;
                        }

                        var selection = editionSelector.SelectRetainedEditions(book.MediaType, book.Editions.ToList());
                        var retainedEditions = selection?.RetainedEditions?.Where(edition => edition != null).ToList();
                        book.Editions = retainedEditions ?? new List<Edition>();
                    }
                }

                // Drop ONLY bit-for-bit identical duplicate pockets — a pocket the server returned twice with
                // the SAME work-identity tokens AND the SAME edition set. ANY difference (an extra edition, a
                // different or extra provider ID) keeps BOTH pockets: a provider ID that merely spans two works
                // is never treated as "same work", so we never merge or drop on it. The V5 response is a
                // blueprint we mirror unmutated; duplicates/overlaps surface the server's own identity bugs
                // upstream ([SERVER-BUG-CANDIDATE]) instead of being papered over client-side.
                // Binding: runbooks/chaptarr/chaptarr-server-refresh-contract.md.
                internal static List<Book> CoalesceIdenticalRemoteBookPockets(List<Book> books, Logger logger = null, Author local = null, string logContext = null)
                {
                    if (books == null || books.Count <= 1)
                    {
                        return books ?? new List<Book>();
                    }

                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var result = new List<Book>(books.Count);
                    var dropped = 0;

                    foreach (var book in books)
                    {
                        if (book == null)
                        {
                            continue;
                        }

                        var fingerprint = BookIdentity.BuildIdenticalPocketFingerprint(book);

                        // null fingerprint = not provably identical to anything (no clean identity, or an edition
                        // without a foreign id) -> always keep. "When in doubt, keep both."
                        if (fingerprint != null && !seen.Add(fingerprint))
                        {
                            dropped++;
                            continue;
                        }

                        result.Add(book);
                    }

                    if (dropped > 0)
                    {
                        logger?.Warn("[REMOTE-DEDUP] Dropped {0} bit-for-bit identical duplicate pocket(s) for author '{1}' during {2}. Non-identical same-id pockets are kept and surfaced upstream.",
                            dropped,
                            local?.Name ?? "Unknown",
                            logContext ?? "refresh");
                    }

                    return result;
                }

                internal List<int> RepairOverMergedBookEditions(int authorId, List<Book> remoteBlueprint) =>
                    RepairOverMergedBookEditions(authorId, remoteBlueprint, out _);

                private List<int> RepairOverMergedBookEditions(int authorId, List<Book> remoteBlueprint, out bool changed)
                {
                    changed = false;
                    var deletedSourceBookIds = new List<int>();

                    if (authorId <= 0 || remoteBlueprint == null || remoteBlueprint.Count == 0)
                    {
                        return deletedSourceBookIds;
                    }

                    // Map edition provider tokens to a remote book identity key (work key + media type).
                    // This allows us to deterministically split a local, file-attached book whose editions now belong to
                    // multiple remote works after an upstream un-merge, without unlinking/deleting or re-matching files.
                    var editionTokenToRemoteKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    var remoteBookByKey = new Dictionary<string, Book>(StringComparer.OrdinalIgnoreCase);

                    void AddRemoteEditionToken(string token, string remoteKey)
                    {
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(remoteKey))
                        {
                            return;
                        }

                        if (!editionTokenToRemoteKeys.TryGetValue(token, out var keys))
                        {
                            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            editionTokenToRemoteKeys[token] = keys;
                        }

                        keys.Add(remoteKey);
                    }

                    foreach (var remoteBook in remoteBlueprint)
                    {
                        if (remoteBook == null)
                        {
                            continue;
                        }

                        var baseWorkKey = GetUnmergeWorkKey(remoteBook);
                        if (string.IsNullOrWhiteSpace(baseWorkKey))
                        {
                            continue;
                        }

                        var remoteKey = $"{baseWorkKey}::{(int)remoteBook.MediaType}";
                        remoteBookByKey[remoteKey] = remoteBook;

                        if (remoteBook.Editions == null || remoteBook.Editions.Count == 0)
                        {
                            continue;
                        }

                        foreach (var edition in remoteBook.Editions)
                        {
                            foreach (var token in BookEditionIdentity.GetRemoteEditionRehomeTokens(edition))
                            {
                                AddRemoteEditionToken(token, remoteKey);
                            }
                        }
                    }

                    if (editionTokenToRemoteKeys.Count == 0)
                    {
                        _logger.Debug("[UNMERGE] No remote edition provider tokens available for authorId={0}; skipping edition re-home repair.", authorId);
                        return deletedSourceBookIds;
                    }

                    var localBooks = _bookService.GetBooksByAuthorId(authorId) ?? new List<Book>();
                    var localBookIds = localBooks
                        .Where(b => b != null && b.Id > 0 && b.AddOptions?.AddType != BookAddType.Manual)
                        .Select(b => b.Id)
                        .Distinct()
                        .ToList();
                    if (!localBookIds.Any())
                    {
                        return deletedSourceBookIds;
                    }

                    var localEditions = _editionService.GetEditionsByBook(localBookIds) ?? new List<Edition>();
                    if (!localEditions.Any())
                    {
                        _logger.Debug("[UNMERGE] No local editions available for authorId={0}; skipping edition re-home repair.", authorId);
                        return deletedSourceBookIds;
                    }

                    var editionsByBookId = localEditions
                        .Where(e => e != null && e.BookId > 0)
                        .GroupBy(e => e.BookId)
                        .ToDictionary(g => g.Key, g => g.Where(x => x != null).ToList());

                    foreach (var book in localBooks.Where(book => book != null))
                        book.Editions = editionsByBookId.GetValueOrDefault(book.Id) ?? new List<Edition>();

                    var editionById = localEditions
                        .Where(e => e != null && e.Id > 0)
                        .GroupBy(e => e.Id)
                        .ToDictionary(g => g.Key, g => g.First());

                    var localBookById = localBooks
                        .Where(b => b != null && b.Id > 0)
                        .GroupBy(b => b.Id)
                        .ToDictionary(g => g.Key, g => g.First());

                    static bool RemoteKeyMatchesMediaType(string remoteKey, BookMediaType mediaType)
                    {
                        return !string.IsNullOrWhiteSpace(remoteKey) &&
                               remoteKey.EndsWith($"::{(int)mediaType}", StringComparison.OrdinalIgnoreCase);
                    }

                    bool TryResolveRemoteKeyForEdition(Edition edition, BookMediaType mediaType, out string remoteKey)
                    {
                        remoteKey = null;

                        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var tokens = BookEditionIdentity.GetEditionRehomeTokens(edition);
                        foreach (var token in tokens)
                        {
                            if (editionTokenToRemoteKeys.TryGetValue(token, out var remoteKeys))
                            {
                                keys.UnionWith(remoteKeys);
                            }
                        }

                        var mediaScopedKeys = keys
                            .Where(key => RemoteKeyMatchesMediaType(key, mediaType))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        if (mediaScopedKeys.Count == 0)
                        {
                            _logger.Debug("[UNMERGE] No media-scoped remote key for edition '{0}' (ID={1}, mediaType={2}) for authorId={3}. Tokens=[{4}], allRemoteKeys=[{5}].",
                                edition?.Title ?? edition?.ForeignEditionId ?? "Unknown",
                                edition?.Id ?? 0,
                                mediaType,
                                authorId,
                                string.Join(", ", tokens),
                                string.Join(", ", keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
                            return false;
                        }

                        if (mediaScopedKeys.Count > 1)
                        {
                            _logger.Warn("[UNMERGE] Skipping ambiguous edition '{0}' (ID={1}) for authorId={2}: tokens [{3}] point to multiple remote keys [{4}].",
                                edition?.Title ?? edition?.ForeignEditionId ?? "Unknown",
                                edition?.Id ?? 0,
                                authorId,
                                string.Join(", ", tokens),
                                string.Join(", ", mediaScopedKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
                            return false;
                        }

                        remoteKey = mediaScopedKeys.First();
                        return true;
                    }

                    // Count mapped files per (local book, remote key) based on edition provider tokens.
                    var files = _mediaFileService.GetFilesByBooks(localBookIds) ?? new List<BookFile>();
                    _logger.Debug("[UNMERGE] Evaluating edition re-home for authorId={0}: remoteTokens={1}, remoteKeys={2}, localBooks={3}, localEditions={4}, files={5}.",
                        authorId,
                        editionTokenToRemoteKeys.Count,
                        remoteBookByKey.Count,
                        localBookIds.Count,
                        localEditions.Count,
                        files.Count);

                    var fileCountsByBookIdAndRemoteKey = new Dictionary<int, Dictionary<string, int>>();
                    var editionIdToRemoteKey = new Dictionary<int, string>();

                    void IncrementFileCount(int bookId, string remoteKey)
                    {
                        if (!fileCountsByBookIdAndRemoteKey.TryGetValue(bookId, out var counts))
                        {
                            counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            fileCountsByBookIdAndRemoteKey[bookId] = counts;
                        }

                        counts.TryGetValue(remoteKey, out var current);
                        counts[remoteKey] = current + 1;
                    }

                    foreach (var file in files)
                    {
                        if (file == null || file.EditionId <= 0)
                        {
                            continue;
                        }

                        if (!editionById.TryGetValue(file.EditionId, out var edition))
                        {
                            continue;
                        }

                        if (!localBookById.TryGetValue(edition.BookId, out var sourceBook))
                        {
                            continue;
                        }

                        if (!editionIdToRemoteKey.TryGetValue(edition.Id, out var remoteKey))
                        {
                            if (!TryResolveRemoteKeyForEdition(edition, sourceBook.MediaType, out remoteKey))
                            {
                                continue;
                            }

                            editionIdToRemoteKey[edition.Id] = remoteKey;
                        }

                        IncrementFileCount(edition.BookId, remoteKey);
                    }

                    if (fileCountsByBookIdAndRemoteKey.Count == 0)
                    {
                        _logger.Debug("[UNMERGE] No file-backed editions resolved to remote keys for authorId={0}; skipping edition re-home repair.", authorId);
                        return deletedSourceBookIds;
                    }

                    // Index local books by their current provider-ID-derived identity keys.
                    var localBooksByRemoteKey = new Dictionary<string, List<Book>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var book in localBooks.Where(b => b != null && b.Id > 0))
                    {
                        if (book.AddOptions?.AddType == BookAddType.Manual)
                        {
                            continue;
                        }

                        var baseKeys = GetUnmergeWorkKeys(book);
                        if (!baseKeys.Any())
                        {
                            continue;
                        }

                        foreach (var baseKey in baseKeys)
                        {
                            var key = $"{baseKey}::{(int)book.MediaType}";
                            if (!localBooksByRemoteKey.TryGetValue(key, out var list))
                            {
                                list = new List<Book>();
                                localBooksByRemoteKey[key] = list;
                            }

                            list.Add(book);
                        }
                    }

                    var pocketIndex = BookRefreshMatchingIndex.Build(localBooks, _logger, remoteBlueprint);
                    var populatedBookIds = files.Where(file => file != null && editionById.ContainsKey(file.EditionId))
                        .Select(file => editionById[file.EditionId].BookId).ToHashSet();
                    var identityUpdates = new List<Book>();
                    var ownershipMoves = new List<LibraryIdentityWriter.EditionMove>();
                    var filelessMerges = new Dictionary<int, int>();
                    var repairedSourceBookIds = new HashSet<int>();

                    Book FindTargetBook(string remoteKey, Book sourceBook)
                    {
                        Book targetBook = null;

                        if (localBooksByRemoteKey.TryGetValue(remoteKey, out var candidates))
                        {
                            var filtered = candidates.Where(b => b.Id != sourceBook.Id && !populatedBookIds.Contains(b.Id)).ToList();
                            if (filtered.Any())
                            {
                                targetBook = filtered.OrderBy(b => b.Id).First();
                            }
                        }

                        if (targetBook == null && remoteBookByKey.TryGetValue(remoteKey, out var remoteBook))
                        {
                            var fallback = BookIdentity.FindWorkFirstMatches(
                                localBooks
                                    .Where(b => b != null && b.Id > 0 && b.Id != sourceBook.Id)
                                    .Where(b => b.AddOptions?.AddType != BookAddType.Manual)
                                    .Where(b => b.MediaType == remoteBook.MediaType && !populatedBookIds.Contains(b.Id)),
                                remoteBook);

                            if (fallback.Any())
                            {
                                targetBook = fallback.OrderBy(b => b.Id).First();
                            }
                        }

                        return targetBook;
                    }

                    bool SourceAlreadyMatchesRemoteKey(Book sourceBook, string remoteKey)
                    {
                        if (sourceBook == null || string.IsNullOrWhiteSpace(remoteKey))
                        {
                            return false;
                        }

                        if (remoteBookByKey.TryGetValue(remoteKey, out var remoteBook))
                        {
                            return BookIdentity.MatchesByStableWorkProviderId(sourceBook, remoteBook);
                        }

                        var sourceKeys = GetUnmergeWorkKeys(sourceBook)
                            .Select(key => $"{key}::{(int)sourceBook.MediaType}");

                        return sourceKeys.Contains(remoteKey, StringComparer.OrdinalIgnoreCase);
                    }

                    Edition FindEquivalentTargetEdition(Edition sourceEdition, Book targetBook)
                    {
                        if (sourceEdition == null || targetBook == null || targetBook.Id <= 0)
                        {
                            return null;
                        }

                        if (!editionsByBookId.TryGetValue(targetBook.Id, out var targetEditions) || targetEditions.Count == 0)
                        {
                            return null;
                        }

                        var sourceStableTokens = BookEditionIdentity.GetStableEditionRehomeTokens(sourceEdition)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var sourceAmazonTokens = BookEditionIdentity.GetAmazonEditionRehomeTokens(sourceEdition)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var matches = targetEditions
                            .Where(e => e != null && e.Id > 0 && e.Id != sourceEdition.Id)
                            .Where(e =>
                            {
                                var targetStableTokens = BookEditionIdentity.GetStableEditionRehomeTokens(e)
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                                if (sourceStableTokens.Count > 0)
                                {
                                    return targetStableTokens.Count > 0 && sourceStableTokens.Overlaps(targetStableTokens);
                                }

                                var targetTokens = BookEditionIdentity.GetRemoteEditionRehomeTokens(e)
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                                return sourceAmazonTokens.Count > 0 && sourceAmazonTokens.Overlaps(targetTokens);
                            })
                            .ToList();

                        if (matches.Count == 1)
                        {
                            return matches[0];
                        }

                        if (matches.Count > 1)
                        {
                            _logger.Warn("[UNMERGE] Skipping edition collision repair for edition '{0}' (ID={1}): target book '{2}' (ID={3}) has multiple equivalent editions [{4}].",
                                sourceEdition.Title ?? sourceEdition.ForeignEditionId ?? "Unknown",
                                sourceEdition.Id,
                                targetBook.Title,
                                targetBook.Id,
                                string.Join(", ", matches.Select(e => e.Id).OrderBy(id => id)));
                        }

                        return null;
                    }

                    foreach (var sourceBook in localBooks.Where(b => b != null && b.Id > 0))
                    {
                        if (sourceBook.AddOptions?.AddType == BookAddType.Manual)
                        {
                            continue;
                        }

                        if (!fileCountsByBookIdAndRemoteKey.TryGetValue(sourceBook.Id, out var counts) || counts.Count == 0)
                        {
                            continue;
                        }

                        if (!editionsByBookId.TryGetValue(sourceBook.Id, out var sourceEditions) || sourceEditions.Count == 0)
                        {
                            continue;
                        }

                        var orderedKeys = counts
                            .OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(kv => kv.Key)
                            .ToList();

                        if (orderedKeys.Count == 1 && remoteBookByKey.TryGetValue(orderedKeys[0], out var servedPocket))
                        {
                            // A published merge changes provider identity, not the number of local recordings.
                            if ((pocketIndex.MatchesMultipleRemotePockets(sourceBook) || !SourceAlreadyMatchesRemoteKey(sourceBook, orderedKeys[0])) &&
                                ((sourceBook.BaseBookId, sourceBook.HardcoverBookId, sourceBook.GoodreadsWorkId, sourceBook.OpenLibraryWorkId) !=
                                 (servedPocket.BaseBookId, servedPocket.HardcoverBookId, servedPocket.GoodreadsWorkId, servedPocket.OpenLibraryWorkId) ||
                                 !BookIdentity.GetStableWorkProviderIdentityTokens(sourceBook).SetEquals(BookIdentity.GetStableWorkProviderIdentityTokens(servedPocket))))
                            {
                                var metadata = RefreshEntityCopy.CloneBook(servedPocket, false);
                                metadata.UseDbFieldsFrom(sourceBook);
                                sourceBook.UseMetadataFrom(metadata);
                                identityUpdates.Add(sourceBook);
                            }
                            if (localBooksByRemoteKey.TryGetValue(orderedKeys[0], out var duplicates))
                            {
                                foreach (var duplicate in duplicates.Where(book => book.Id != sourceBook.Id &&
                                    BookIdentity.CanAbsorbDuplicate(book, populatedBookIds.Contains(book.Id)) &&
                                    !pocketIndex.MatchesMultipleRemotePockets(book)))
                                    filelessMerges.TryAdd(duplicate.Id, sourceBook.Id);
                            }
                            continue;
                        }

                        var primaryKey = orderedKeys.Count > 1 ? orderedKeys.First() : null;

                        var remoteKeysToMove = orderedKeys.Count > 1
                            ? orderedKeys.Where(key => !string.Equals(key, primaryKey, StringComparison.OrdinalIgnoreCase)).ToList()
                            : orderedKeys.Where(key => !SourceAlreadyMatchesRemoteKey(sourceBook, key)).ToList();

                        foreach (var remoteKey in remoteKeysToMove)
                        {
                            var targetBook = FindTargetBook(remoteKey, sourceBook);

                            if (targetBook == null)
                            {
                                _logger.Warn("[UNMERGE] Unable to repair split for authorId={0}: could not find a local target for remoteKey '{1}' (source book '{2}' ID={3}).",
                                    authorId, remoteKey, sourceBook.Title, sourceBook.Id);
                                continue;
                            }

                            var moveThese = sourceEditions
                                .Where(e => e != null && e.Id > 0)
                                .Where(e => editionIdToRemoteKey.TryGetValue(e.Id, out var key) &&
                                            string.Equals(key, remoteKey, StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            if (!moveThese.Any())
                            {
                                continue;
                            }

                            _logger.Info("[UNMERGE] Splitting over-merged book '{0}' (ID={1}): repairing {2} edition(s) -> '{3}' (ID={4}) [{5}] (kept primary [{6}]).",
                                sourceBook.Title, sourceBook.Id, moveThese.Count, targetBook.Title, targetBook.Id, remoteKey, primaryKey ?? "none");

                            foreach (var edition in moveThese)
                            {
                                repairedSourceBookIds.Add(sourceBook.Id);
                                var equivalentTargetEdition = FindEquivalentTargetEdition(edition, targetBook);
                                if (equivalentTargetEdition != null)
                                {
                                    var filesForEdition = files
                                        .Where(f => f != null && f.EditionId == edition.Id)
                                        .ToList();

                                    foreach (var file in filesForEdition)
                                    {
                                        file.EditionId = equivalentTargetEdition.Id;
                                        file.Edition = equivalentTargetEdition;
                                    }

                                    ownershipMoves.Add(new LibraryIdentityWriter.EditionMove(edition.Id, targetBook.Id, equivalentTargetEdition.Id));
                                    _logger.Info("[UNMERGE] Repointing {0} file(s) from duplicate edition '{1}' (ID={2}) to existing edition '{3}' (ID={4}).",
                                        filesForEdition.Count,
                                        edition.Title ?? edition.ForeignEditionId ?? "Unknown",
                                        edition.Id,
                                        equivalentTargetEdition.Title ?? equivalentTargetEdition.ForeignEditionId ?? "Unknown",
                                        equivalentTargetEdition.Id);
                                    continue;
                                }

                                ownershipMoves.Add(new LibraryIdentityWriter.EditionMove(edition.Id, targetBook.Id));
                                edition.BookId = targetBook.Id;
                                edition.Book = targetBook;
                            }
                        }
                    }

                    // A move destination may be fileless at load time, but receives files
                    // in this transaction. Never also absorb a participant in that move.
                    var moveBookIds = repairedSourceBookIds.Concat(ownershipMoves.Select(move => move.TargetBookId)).ToHashSet();
                    foreach (var sourceId in filelessMerges.Where(pair => moveBookIds.Contains(pair.Key) || moveBookIds.Contains(pair.Value))
                        .Select(pair => pair.Key).ToList())
                        filelessMerges.Remove(sourceId);

                    changed = identityUpdates.Count > 0 || ownershipMoves.Count > 0 || filelessMerges.Count > 0;
                    if (changed)
                        deletedSourceBookIds.AddRange(ApplyEditionOwnershipMoves(authorId, ownershipMoves, repairedSourceBookIds, identityUpdates, filelessMerges));

                    return deletedSourceBookIds;
                }

        internal virtual List<int> ApplyEditionOwnershipMoves(int authorId, IReadOnlyCollection<LibraryIdentityWriter.EditionMove> moves,
            IReadOnlyCollection<int> sourceBookIds, IReadOnlyCollection<Book> identityUpdates, IReadOnlyDictionary<int, int> filelessMerges)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var deleted = new List<int>();
            _bookRepository.UpdateManyAtomic(identityUpdates.ToList(), (connection, transaction) =>
            {
                var writer = new LibraryIdentityWriter(connection, transaction, _mainDatabase.DatabaseType, _editionSelector);
                deleted.AddRange(writer.MoveEditions(moves));
                foreach (var pair in filelessMerges)
                    if (writer.MergeBook(pair.Key, pair.Value)) deleted.Add(pair.Key);
            });
            PublishIdentityChange(new LibraryIdentityChangedEvent(new[] { authorId },
                sourceBookIds.Concat(moves.Select(move => move.TargetBookId)).Concat(identityUpdates.Select(book => book.Id))
                    .Concat(filelessMerges.Keys).Concat(filelessMerges.Values), deleted));
            _logger.Debug("[EDITION-MOVE] Author {0}: {1} moves, {2} identity updates, {3} merge candidates, {4} removed rows in {5} ms",
                authorId, moves.Count, identityUpdates.Count, filelessMerges.Count, deleted.Count, timer.ElapsedMilliseconds);
            return deleted;
        }

            private static string GetUnmergeWorkKey(Book book)
            {
                return GetUnmergeWorkKeys(book).FirstOrDefault();
            }

            private static List<string> GetUnmergeWorkKeys(Book book)
            {
                if (book == null)
                {
                    return new List<string>();
                }

                return BookIdentity.GetStableWorkProviderIdentityTokens(book)
                    .OrderBy(token => token, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            protected override List<Book> GetLocalChildren(Author entity, List<Book> remoteChildren, RefreshContext context)
            {
                var books = _bookService.GetBooksForRefresh(entity.Id, foreignIds: null);
                HydrateLocalChildrenForRefresh(entity, books);
                InitializeBookRefreshMatchingIndex(books, remoteChildren, context);
                return books;
            }

            protected void InitializeBookRefreshMatchingIndex(List<Book> localChildren, List<Book> remoteChildren, RefreshContext context)
            {
                ((AuthorRefreshContext)context).MatchingIndex = BookRefreshMatchingIndex.Build(localChildren, _logger, remoteChildren);
            }

            private void HydrateLocalChildrenForRefresh(Author entity, List<Book> books)
            {
                if (books == null || books.Count == 0)
                {
                    return;
                }

                var stopwatch = Stopwatch.StartNew();
                var bookIds = books
                    .Where(book => book != null && book.Id > 0)
                    .Select(book => book.Id)
                    .Distinct()
                    .ToList();

                if (!bookIds.Any())
                {
                    return;
                }

                var editions = _editionService.GetEditionsByBook(bookIds) ?? new List<Edition>();
                var editionsById = editions
                    .Where(edition => edition != null && edition.Id > 0)
                    .GroupBy(edition => edition.Id)
                    .ToDictionary(group => group.Key, group => group.First());

                var editionsByBookId = editions
                    .Where(edition => edition != null && edition.BookId > 0)
                    .GroupBy(edition => edition.BookId)
                    .ToDictionary(group => group.Key, group => group.ToList());

                var files = _mediaFileService.GetFilesByBooks(bookIds) ?? new List<BookFile>();
                var filesByBookId = new Dictionary<int, List<BookFile>>();
                var filesByEditionId = new Dictionary<int, List<BookFile>>();

                foreach (var file in files.Where(file => file != null))
                {
                    if (file.EditionId > 0 &&
                        editionsById.TryGetValue(file.EditionId, out var edition))
                    {
                        file.Edition = edition;
                    }

                    if (file.EditionId > 0)
                    {
                        if (!filesByEditionId.TryGetValue(file.EditionId, out var editionFiles))
                        {
                            editionFiles = new List<BookFile>();
                            filesByEditionId[file.EditionId] = editionFiles;
                        }

                        editionFiles.Add(file);
                    }

                    var bookId = file.Edition?.BookId ?? 0;
                    if (bookId <= 0)
                    {
                        continue;
                    }

                    if (!filesByBookId.TryGetValue(bookId, out var bookFiles))
                    {
                        bookFiles = new List<BookFile>();
                        filesByBookId[bookId] = bookFiles;
                    }

                    bookFiles.Add(file);
                }

                foreach (var book in books.Where(book => book != null && book.Id > 0))
                {
                    book.Editions = editionsByBookId.TryGetValue(book.Id, out var bookEditions)
                        ? bookEditions
                        : new List<Edition>();
                    book.BookFiles = filesByBookId.TryGetValue(book.Id, out var bookFiles)
                        ? bookFiles
                        : new List<BookFile>();

                    foreach (var edition in book.Editions)
                    {
                        edition.Book = book;
                        edition.BookFiles = filesByEditionId.TryGetValue(edition.Id, out var editionFiles)
                            ? editionFiles
                            : new List<BookFile>();
                    }
                }

                stopwatch.Stop();
                _logger.Debug("[AUTHOR-REFRESH-TIMING] Hydrated {0} local books for author '{1}': {2} editions, {3} files in {4}ms",
                    books.Count,
                    entity?.Name ?? "Unknown",
                    editions.Count,
                    files.Count,
                    stopwatch.ElapsedMilliseconds);
            }

        private bool HasKnownFiles(Book book)
        {
            if (book == null)
            {
                return false;
            }

            if (book.BookFiles != null)
            {
                return book.BookFiles.Any();
            }

            return book.Id > 0 && _mediaFileService.GetFilesByBook(book.Id).Any();
        }

        private bool ShouldPreserveLocalBookInstance(Book book)
        {
            return !BookIdentity.CanAbsorbDuplicate(book, HasKnownFiles(book));
        }

        private static BookRefreshMatchingIndex GetBookRefreshMatchingIndex(List<Book> existingChildren, RefreshContext context)
        {
            var index = ((AuthorRefreshContext)context).MatchingIndex;
            if (index?.Source == existingChildren)
            {
                return index;
            }

            throw new InvalidOperationException("Book refresh matching requires the complete local and remote snapshots from GetLocalChildren.");
        }

        private sealed class BookRefreshMatchingIndex
        {
            private readonly Dictionary<string, List<Book>> _booksByProviderToken;
            private readonly Dictionary<int, int> _sourceOrderByBookId;
            private readonly HashSet<int> _activeBookIds;
            private readonly Dictionary<int, (int Count, int BestRank)> _remotePocketMatches = new();
            private readonly Dictionary<int, Book> _matchedRemotePockets = new Dictionary<int, Book>();

            private BookRefreshMatchingIndex(
                List<Book> source,
                Dictionary<string, List<Book>> booksByProviderToken,
                Dictionary<int, int> sourceOrderByBookId,
                HashSet<int> activeBookIds)
            {
                Source = source;
                _booksByProviderToken = booksByProviderToken;
                _sourceOrderByBookId = sourceOrderByBookId;
                _activeBookIds = activeBookIds;
            }

            public List<Book> Source { get; }

            public static BookRefreshMatchingIndex Build(List<Book> existingChildren, Logger logger, List<Book> remoteChildren)
            {
                ArgumentNullException.ThrowIfNull(remoteChildren);
                var stopwatch = Stopwatch.StartNew();
                long matchedPairs = 0;
                var source = existingChildren ?? new List<Book>();
                var booksByProviderToken = new Dictionary<string, List<Book>>(StringComparer.OrdinalIgnoreCase);
                var sourceOrderByBookId = new Dictionary<int, int>();
                var activeBookIds = new HashSet<int>();

                for (var i = 0; i < source.Count; i++)
                {
                    var book = source[i];
                    if (book == null || book.Id <= 0)
                    {
                        continue;
                    }

                    activeBookIds.Add(book.Id);
                    sourceOrderByBookId[book.Id] = i;

                    foreach (var token in BookIdentity.GetProviderIdentityTokens(book))
                    {
                        if (!booksByProviderToken.TryGetValue(token, out var tokenBooks))
                        {
                            tokenBooks = new List<Book>();
                            booksByProviderToken[token] = tokenBooks;
                        }

                        tokenBooks.Add(book);
                    }
                }

                var index = new BookRefreshMatchingIndex(source, booksByProviderToken, sourceOrderByBookId, activeBookIds);
                // Count against the complete filtered snapshot before the sorter consumes any rows.
                // Keep ambiguous edition matches in this conservative superset: consumption can
                // make them unique for a later pocket. Overlap alone does not prove a duplicate.
                foreach (var remote in remoteChildren)
                {
                    var matches = index.GetMatches(remote);
                    foreach (var book in matches.Matches)
                    {
                        var previous = index._remotePocketMatches.GetValueOrDefault(book.Id);
                        index._remotePocketMatches[book.Id] = (previous.Count + 1,
                            Math.Max(previous.BestRank, BookIdentity.GetPocketMatchRank(book, remote)));
                        matchedPairs++;
                    }
                }

                logger?.Debug("[AUTHOR-REFRESH-TIMING] Built book provider lookup for {0} local books, {1} remote pockets, {2} provider tokens and {3} matched pairs in {4}ms",
                    source.Count, remoteChildren.Count, booksByProviderToken.Count, matchedPairs, stopwatch.ElapsedMilliseconds);
                return index;
            }

            public bool MatchesMultipleRemotePockets(Book book) => _remotePocketMatches.GetValueOrDefault(book.Id).Count > 1;

            public int GetRelativePocketMatchRank(Book book, Book remote) => MatchesMultipleRemotePockets(book)
                ? BookIdentity.GetPocketMatchRank(book, remote) - _remotePocketMatches[book.Id].BestRank
                : 0;

            public Book GetMatchedRemotePocket(Book book) => _matchedRemotePockets.GetValueOrDefault(book.Id);

            public WorkFirstMatchResult GetMatches(Book remote) => BookIdentity.FindWorkFirstMatchResult(
                GetCandidates(remote).Where(book => book.MediaType == remote.MediaType), remote);

            public List<Book> GetCandidates(Book remote)
            {
                var remoteTokens = BookIdentity.GetProviderIdentityTokens(remote);
                if (remoteTokens.Count == 0)
                {
                    return new List<Book>();
                }

                var candidatesById = new Dictionary<int, Book>();
                foreach (var token in remoteTokens)
                {
                    if (!_booksByProviderToken.TryGetValue(token, out var tokenBooks))
                    {
                        continue;
                    }

                    foreach (var book in tokenBooks)
                    {
                        if (book == null || book.Id <= 0 || !_activeBookIds.Contains(book.Id))
                        {
                            continue;
                        }

                        candidatesById[book.Id] = book;
                    }
                }

                return candidatesById.Values
                    .OrderBy(book => _sourceOrderByBookId.TryGetValue(book.Id, out var order) ? order : int.MaxValue)
                    .ToList();
            }

            public void Consume(Book existingChild, IEnumerable<Book> mergedChildren, Book remote)
            {
                if (existingChild?.Id > 0)
                {
                    _activeBookIds.Remove(existingChild.Id);
                    _matchedRemotePockets[existingChild.Id] = remote;
                }

                foreach (var child in mergedChildren ?? Enumerable.Empty<Book>())
                {
                    if (child?.Id > 0)
                    {
                        _activeBookIds.Remove(child.Id);
                    }
                }
            }
        }

        protected override Tuple<Book, List<Book>> GetMatchingExistingChildren(List<Book> existingChildren, Book remote, RefreshContext context)
        {
            // Match work-first. Shared edition aliases (ASIN, GB, providerIdsAll, etc.) are not enough to merge
            // distinct remote works; edition fallback is only used when there is no stable work identity.
            var matchingIndex = GetBookRefreshMatchingIndex(existingChildren, context);
            var matchResult = matchingIndex.GetMatches(remote);
            var matchingBooks = matchResult.Matches.ToList();
            if (matchResult.Disposition == WorkFirstMatchDisposition.EditionAmbiguous &&
                !IsSameRemotePocketCopySet(matchingBooks, remote))
            {
                // Shared edition aliases are not work identity. Only the author-refresh add gate may
                // consume ambiguity, and only when every row is demonstrably another local instance
                // of the exact same server pocket. Other callers retain the fail-closed behavior.
                matchingBooks.Clear();
            }

            static int ProviderScore(Book book)
            {
                if (book == null) return 0;

                if (!string.IsNullOrWhiteSpace(book.HardcoverBookId)) return 90;
                if (!string.IsNullOrWhiteSpace(book.GoodreadsWorkId)) return 80;
                if (!string.IsNullOrWhiteSpace(book.OpenLibraryWorkId)) return 60;
                return 0;
            }

            var mergeChildren = new List<Book>();
            Book existingChild = null;

            // Use uncontested rows first; only competing pockets need affinity ordering.
            // For a consolidated pocket, files outrank a fileless duplicate's old pocket key.
            // Reserve stronger evidence for the other pocket; without pocket evidence, a contested
            // fileless row must be used before taking an owned row solely because it has files.
            if (matchingBooks.Any())
            {
                existingChild = matchingBooks
                    .OrderBy(matchingIndex.MatchesMultipleRemotePockets)
                    .ThenByDescending(book => matchingIndex.GetRelativePocketMatchRank(book, remote))
                    .ThenBy(book => matchingIndex.MatchesMultipleRemotePockets(book) &&
                        BookIdentity.GetPocketMatchRank(book, remote) == 0 && HasKnownFiles(book))
                    .ThenByDescending(HasKnownFiles)
                    .ThenBy(b => b.AddOptions?.AddType == BookAddType.Manual)
                    .ThenByDescending(ProviderScore)
                    .ThenBy(b => b.Id)
                    .First();

                foreach (var dup in matchingBooks.Where(b => b.Id != existingChild.Id))
                {
                    if (matchingIndex.MatchesMultipleRemotePockets(dup) || ShouldPreserveLocalBookInstance(dup))
                    {
                        continue;
                    }

                    mergeChildren.Add(dup);
                }

                matchingIndex.Consume(existingChild, mergeChildren, remote);
                return Tuple.Create(existingChild, mergeChildren);
            }

            return Tuple.Create(existingChild, mergeChildren);
        }

        internal static bool IsSameRemotePocketCopySet(IReadOnlyCollection<Book> candidates, Book remote)
        {
            if (remote == null || candidates == null || candidates.Count < 2 ||
                BookIdentity.GetStableWorkProviderIdentityTokens(remote).Count > 0)
            {
                return false;
            }

            var baseBookId = NormalizePocketId(remote.BaseBookId);
            if (string.IsNullOrWhiteSpace(baseBookId))
            {
                return false;
            }

            return candidates.All(candidate =>
                candidate != null &&
                candidate.MediaType == remote.MediaType &&
                BookIdentity.GetStableWorkProviderIdentityTokens(candidate).Count == 0 &&
                string.Equals(NormalizePocketId(candidate.BaseBookId), baseBookId, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizePocketId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId) || ProviderIdHelper.ContainsProviderIdArtifact(providerId))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.Canonicalize(providerId.Trim(), expectedPrefix: null);
            }
            catch
            {
                return null;
            }
        }

        private string ExtractBaseBookId(string foreignBookId)
        {
            // Extract base ID from incremented IDs like "book_id_2" -> "book_id"
            var lastUnderscore = foreignBookId.LastIndexOf('_');
            if (lastUnderscore > 0 && int.TryParse(foreignBookId.Substring(lastUnderscore + 1), out _))
            {
                return foreignBookId.Substring(0, lastUnderscore);
            }

            return foreignBookId;
        }

        protected override void PrepareNewChild(Book child, Author entity)
        {
            child.Author = entity;
            child.AuthorId = entity.Id;
            child.Added = DateTime.UtcNow;
            child.LastInfoSync = null;
            // New-row monitoring is applied in ProcessChildren, after the refresh
            // sorter has identified the pre-existing catalog. Start unmonitored so
            // the persistent author policy is the only source of this decision.
            child.AudiobookMonitored = false;
            child.EbookMonitored = false;
        }

        protected override void PrepareExistingChild(Book local, Book remote, Author entity)
        {
            if (local?.Id > 0 && entity?.Id > 0 && local.AuthorId != entity.Id)
            {
                _bookService.ReassignAuthor(local, entity);
                return;
            }

            local.Author = entity;
        }

        protected override bool AreChildrenUpToDate(Book local, Book remote)
        {
            if (local == null || remote == null)
            {
                return false;
            }

            var remoteForCompare = RefreshEntityCopy.CloneBook(remote, includeEditions: false);
            remoteForCompare.UseDbFieldsFrom(local);
            return local.Equals(remoteForCompare);
        }

        protected override Book CreateChildForAdd(Book remoteChild, Author entity)
        {
            return RefreshEntityCopy.CloneBook(remoteChild, includeEditions: true);
        }

        protected override void ProcessChildren(Author entity, SortedChildren children)
        {
            _logger.Debug("ProcessChildren: Processing {0} new books for author {1}", children.Added.Count, entity.Name);

            foreach (var book in children.Added)
            {
                // Store original narrator for logging
                var originalNarrator = book.Narrator;

                var audioMonitor = ShouldMonitorNewBook(book, entity, isAudiobook: true);
                var ebookMonitor = ShouldMonitorNewBook(book, entity, isAudiobook: false);

                // Set monitoring flags (metadata profile filtering will happen after insertion)
                book.AudiobookMonitored = audioMonitor;
                book.EbookMonitored = ebookMonitor;

                _logger.Debug("[MONITORING-REFRESH] Initial monitoring for '{0}': Audio={1}, Ebook={2}", book.Title, audioMonitor, ebookMonitor);

                // IMPORTANT: Missing books should NOT have incremented IDs
                // Only physical copies should get _2, _3, etc. suffixes

                // This prevents narrator pollution from metadata sources
                book.Narrator = null;
                book.NarratorId = null;

                _logger.Info("MISSING_BOOK_CREATED: '{0}' - Original narrator '{1}' -> NULL (preventing metadata pollution)", book.Title, originalNarrator ?? "NULL");
            }
        }

        private bool ShouldMonitorNewBook(Book addedBook, Author entity, bool isAudiobook)
        {
            if (addedBook == null || entity == null || addedBook.MediaType != (isAudiobook ? BookMediaType.Audiobook : BookMediaType.Ebook))
            {
                return false;
            }

            // AddOptions remains present until the initial disk scan completes.
            // Those rows belong to the one-time initial-book choice, which the scan
            // handler applies after file links are known; the ongoing policy must
            // not preempt it during the initial metadata refresh.
            if (entity.AddOptions != null)
            {
                return false;
            }

            var monitorNewItems = entity.GetMonitorNewItemsForMediaType(isAudiobook);
            return monitorNewItems.HasValue &&
                   _monitorNewBookService.ShouldMonitorNewBook(addedBook, entity, monitorNewItems.Value);
        }

                protected override void AddChildren(List<Book> children)
                {
                    if (children == null || !children.Any())
                    {
                        return;
                    }

                    // Children are already filtered/deduped at the remote/identity layer.
                    // Insert all added books; do not perform additional provider-ID-based suppression here, as it can
                    // block legitimate upstream un-merges where a previously over-merged local row temporarily shares
                    // provider IDs with multiple remote works.
                    var invalidBooks = children
                        .Where(b => b != null && (b.Editions == null || !b.Editions.Any()))
                        .ToList();

                    foreach (var book in invalidBooks)
                    {
                        _logger.Warn("Skipping remote book '{0}' during author refresh because no retained editions remained; no book row will be created.", book.Title);
                    }

                    var newBooks = children
                        .Where(b => b != null && b.Editions != null && b.Editions.Any())
                        .ToList();

                    if (!newBooks.Any())
                    {
                        return;
                    }

                    // Normalize before insertion so the canonical edition writer sees
                    // one selection and independent retention pins need no second write.
                    foreach (var book in newBooks)
                    {
                        _editionSelector.EnsureSingleMonitoredEdition(book.Editions, mediaType: book.MediaType, anyEditionOk: book.AnyEditionOk);
                        book.ForeignEditionId = book.Editions.FirstOrDefault(edition => edition.Monitored)?.ForeignEditionId;
                    }

                    _logger.Debug("AddChildren: Inserting {0} new books", newBooks.Count);
                    _bookService.InsertMany(newBooks);

                    // Extract all editions from the newly inserted books
                    var allEditions = new List<Edition>();
                    foreach (var book in newBooks)
                    {
                        // Update edition.BookId now that the book has been saved
                        foreach (var edition in book.Editions)
                        {
                            edition.BookId = book.Id;
                            edition.Book = book;
                            allEditions.Add(edition);
                        }
                    }

                    _logger.Debug("AddChildren: Inserting {0} editions for {1} new books", allEditions.Count, newBooks.Count);
                    _editionService.InsertMany(allEditions);
                }

        protected override void PrepareChildrenForRefresh(Author entity, SortedChildren children, List<Book> remote, RefreshContext context)
        {
            var run = (AuthorRefreshContext)context;
            var blueprint = run.RehomeBlueprint?.Any() == true ? run.RehomeBlueprint : remote;
            bool changed;
            try
            {
                RepairOverMergedBookEditions(entity.Id, blueprint, out changed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "[UNMERGE] Could not repair edition ownership for author {0}; continuing other books, refresh remains incomplete", entity.Id);
                run.IdentityFailures.Add(ex);
                return;
            }
            if (!changed) return;

            // Repair can adopt identities or remove fileless duplicates. Sort the freshly
            // loaded rows with the same sorter, never feed stale pocket choices to book refresh.
            var addedIds = children.Added.Select(book => book.Id).ToHashSet();
            var current = ClassifyChildren(entity, remote, context);
            ProcessChildren(entity, current);
            AddChildren(current.Added);
            children.Added = current.Future.Where(book => addedIds.Contains(book.Id)).Concat(current.Added).DistinctBy(book => book.Id).ToList();
            children.UpToDate = current.UpToDate.Where(book => !addedIds.Contains(book.Id)).ToList();
            children.Updated = current.Updated.Where(book => !addedIds.Contains(book.Id)).ToList();
            children.Matched = current.Matched;
            children.Merged = current.Merged;
            children.Deleted = current.Deleted;
        }

            protected override bool RefreshChildren(SortedChildren localChildren, List<Book> remoteChildren, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, RefreshContext context)
            {
                var run = (AuthorRefreshContext)context;
                var deletedSourceIds = new HashSet<int>();
                var identityFailures = run.IdentityFailures;

                if (localChildren.Merged.Any())
                {
                    // Defensive: avoid double-processing the same merge-source row.
                    var seenSourceIds = new HashSet<int>();
                    foreach (var merge in localChildren.Merged
                                 .Where(m => m?.Item1 != null && m.Item1.Id > 0 && m.Item2 != null && m.Item2.Id > 0)
                                 .Where(m => seenSourceIds.Add(m.Item1.Id)))
                    {
                        var source = merge.Item1;
                        var target = merge.Item2;

                        // Only merge/delete persisted duplicates into persisted targets.
                        if (source == null || source.Id <= 0 || target == null || target.Id <= 0)
                        {
                            continue;
                        }

                        // Only delete unpinned, fileless duplicates. Filed/manual/narrator/strict-edition
                        // rows are local instances and must survive refresh even when they share provider IDs.
                        if (ShouldPreserveLocalBookInstance(source))
                        {
                            continue;
                        }

                        try
                        {
                            using (var connection = _mainDatabase.OpenConnection())
                            using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                            {
                                if (!new LibraryIdentityWriter(connection, transaction, _mainDatabase.DatabaseType, _editionSelector).MergeBook(source.Id, target.Id))
                                    continue;
                                transaction.Commit();
                            }
                            target.AudiobookMonitored |= source.AudiobookMonitored;
                            target.EbookMonitored |= source.EbookMonitored;
                            target.Editions = _editionService.GetEditionsByBook(target.Id);
                            target.ForeignEditionId = target.Editions.SingleOrDefault(edition => edition.Monitored)?.ForeignEditionId;
                            deletedSourceIds.Add(source.Id);
                            PublishIdentityChange(new LibraryIdentityChangedEvent(new[] { source.AuthorId, target.AuthorId }, new[] { source.Id, target.Id }, new[] { source.Id }));
                            _logger.Info("[BOOK-MERGE] Absorbed fileless book {0} into {1}", source.Id, target.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "[BOOK-MERGE] Could not merge book {0} into {1} for author {2}; continuing other books, refresh remains incomplete", source.Id, target.Id, source.AuthorId);
                            identityFailures.Add(ex);
                        }
                    }
                }

                // IMPORTANT: Do not send merged (duplicate) rows through RefreshBookService, otherwise they'll
                // re-match against the same remote book and never get pruned.
                var booksToRefresh = localChildren.UpToDate
                    .Concat(localChildren.Added)
                    .Concat(localChildren.Updated)
                    .Concat(localChildren.Deleted)
                    .ToList();

                if (deletedSourceIds.Count > 0)
                {
                    booksToRefresh = booksToRefresh
                        .Where(b => b != null && b.Id > 0 && !deletedSourceIds.Contains(b.Id))
                        .ToList();
                }

                // Defensive: the same DB row can be added to multiple buckets; de-dupe by ID.
                booksToRefresh = booksToRefresh
                    .Where(b => b != null && b.Id > 0)
                    .GroupBy(b => b.Id)
                    .Select(g => g.First())
                    .ToList();

                // Reuse the complete local snapshot, including rows inserted above, for all slug checks.
                var authorIdForSlugs = booksToRefresh.FirstOrDefault()?.AuthorId ?? 0;
                var titleSlugs = authorIdForSlugs > 0
                    ? new BookTitleSlugIndex(authorIdForSlugs, localChildren.All.Where(book => book != null && !deletedSourceIds.Contains(book.Id)), remoteChildren)
                    : null;

                // Reuse the reconciliation decision: a damaged row can still carry identities
                // from another pocket, so independently matching it again can undo that decision.
                var updated = false;
                foreach (var group in booksToRefresh.GroupBy(book => run.MatchingIndex?.GetMatchedRemotePocket(book), BookReferenceComparer.Instance))
                {
                    var snapshot = group.Key == null ? remoteChildren : new List<Book> { group.Key };
                    updated |= run.RequestedNarrationIds?.Count > 0
                        ? _refreshBookService.RefreshBookInfo(group.ToList(), snapshot, remoteData, forceChildRefresh, forceUpdateFileTags, lastUpdate, titleSlugs, run.RequestedNarrationIds)
                        : _refreshBookService.RefreshBookInfo(group.ToList(), snapshot, remoteData, forceChildRefresh, forceUpdateFileTags, lastUpdate, titleSlugs);
                }

                return updated;
            }

        protected override void ValidateChildrenRefresh(Author author, RefreshContext context)
        {
            var identityFailures = ((AuthorRefreshContext)context).IdentityFailures;
            if (identityFailures.Count > 0)
            {
                // Fetch success is not local reconciliation success. Do not let the
                // next bulk diff skip a payload whose ownership changes rolled back.
                const string error = "Author identity reconciliation is incomplete; successful pairs were retained, failed pairs will retry on refresh.";
                _syncMetadataService?.UpdateSyncResult(author.Id, success: false, error: error, localFailure: true);
                throw new AuthorIdentityUnverifiedException(error, new AggregateException(identityFailures));
            }
        }

        protected override void PublishEntityUpdatedEvent(Author entity)
        {
            _eventAggregator.PublishEvent(new AuthorUpdatedEvent(entity));
        }

        protected override void PublishRefreshCompleteEvent(Author entity, Author remoteData)
        {
            if (entity.AddOptions?.AwaitingMetadata == true)
            {
                entity.AddOptions.AwaitingMetadata = false;
                entity.AddOptions.AudiobookAwaitingMetadata = false;
                entity.AddOptions.EbookAwaitingMetadata = false;
                entity.AddOptions.MetadataUnavailable = false;
                _authorService.SetAddOptions(entity);
            }

            _logger.Debug("[SERIES-DEBUG] PublishRefreshCompleteEvent called for author '{0}' (ID: {1})",
                entity.Name, entity.Id);
            _logger.Debug("[SERIES-DEBUG] Author has {0} series to refresh", entity.Series?.Count ?? 0);

                // IMPORTANT: We don't process series here anymore because books might not be committed to DB yet
                // Series processing is now handled in a separate event handler after books are guaranteed to be in the database

            _eventAggregator.PublishEvent(new AuthorRefreshCompleteEvent(entity, remoteData));
        }

        protected override void PublishChildrenUpdatedEvent(Author entity, List<Book> newChildren, List<Book> updateChildren, List<Book> deleteChildren)
        {
            _eventAggregator.PublishEvent(new BookInfoRefreshedEvent(entity, newChildren, updateChildren, deleteChildren));
        }

        private void Rescan(List<int> authorIds, bool isNew, CommandTrigger trigger, bool infoUpdated, bool isFromImport = false, string mediaType = "all", bool rescanWhenMetadataUnchanged = false, bool scopeToSingleAuthor = false,
            Author initialSetupAuthor = null, List<RootFolder> loadedRootFolders = null, bool initialSetupOnly = false)
        {

            // The import process will handle a single comprehensive scan at the end
            if (isFromImport)
            {
                _logger.Debug("[IMPORT-RESCAN-SKIP] Skipping per-author rescan during import for {0} authors. A comprehensive scan will occur after all imports complete.", authorIds.Count);
                return;
            }

            if (!isNew || initialSetupOnly)
            {
                // A recovered (including valid empty) catalog still owes its initial
                // scan. Ordinary refresh preferences cannot consume or postpone it.
                var existingAuthors = _authorService.GetExistingAuthors(authorIds);
                var initialSetups = existingAuthors
                    .Where(author => author.AddOptions is { AwaitingMetadata: false, MetadataUnavailable: false })
                    .ToList();
                var initialSetupIds = initialSetups.Select(author => author.Id).ToHashSet();
                var setupRootFolders = initialSetups.Count > 0 ? _rootFolderService.All() : null;
                foreach (var author in initialSetups)
                {
                    Rescan(new List<int> { author.Id }, true, trigger, infoUpdated, false, mediaType,
                        rescanWhenMetadataUnchanged: true, scopeToSingleAuthor: true,
                        initialSetupAuthor: author, loadedRootFolders: setupRootFolders);
                }

                authorIds = existingAuthors.Where(author => !initialSetupIds.Contains(author.Id)).Select(author => author.Id).ToList();
                if (initialSetupOnly || authorIds.Count == 0) return;
            }

            var rescanAfterRefresh = _configService.RescanAfterRefresh;
            var shouldRescan = true;

            if (isNew)
            {
                _logger.Debug("[FLOW-DEBUG] RESCAN-RUN: New author added.");
                shouldRescan = true;
            }
            else if (rescanAfterRefresh == RescanAfterRefreshType.Never)
            {
                _logger.Debug("[FLOW-DEBUG] RESCAN-SKIP: configured to never rescan after refresh.");
                shouldRescan = false;
            }
            else if (rescanAfterRefresh == RescanAfterRefreshType.AfterManual && trigger != CommandTrigger.Manual)
            {
                _logger.Debug("[FLOW-DEBUG] RESCAN-SKIP: configured to rescan after manual refreshes only; trigger={0}.", trigger);
                shouldRescan = false;
            }
            else if (!infoUpdated)
            {
                if (rescanWhenMetadataUnchanged)
                {
                    _logger.Debug("[FLOW-DEBUG] RESCAN-RUN: metadata unchanged, but this was an explicit author refresh and scan.");
                    shouldRescan = true;
                }
                else
                {
                    _logger.Debug("[FLOW-DEBUG] RESCAN-SKIP: metadata unchanged. Use the scan/maintenance task to scan folders without metadata changes.");
                    shouldRescan = false;
                }
            }

            if (shouldRescan)
            {
                // some metadata has updated so rescan unmatched
                // (but don't add new authors to reduce repeated searches against api)
                var requestedMediaType = (mediaType ?? "all").Trim().ToLowerInvariant();

                Author scopedAuthor = null;
                if (scopeToSingleAuthor)
                {
                    if (authorIds.Count != 1)
                    {
                        throw new InvalidOperationException("A single-author rescan scope requires exactly one author id.");
                    }

                    scopedAuthor = initialSetupAuthor ?? _authorService.GetAuthor(authorIds[0]);
                    requestedMediaType = scopedAuthor.AddOptions?.GetInitialScanMediaType() ?? requestedMediaType;
                }

                var rootFolders = loadedRootFolders ?? _rootFolderService.All();
                if (requestedMediaType == "audiobook")
                {
                    rootFolders = rootFolders.Where(r => r.FolderType == FolderType.Audiobook || r.FolderType == FolderType.Mixed).ToList();
                }
                else if (requestedMediaType == "ebook")
                {
                    rootFolders = rootFolders.Where(r => r.FolderType == FolderType.Ebook || r.FolderType == FolderType.Mixed).ToList();
                }

                List<string> folders;
                var scopeLabel = "root";
                if (scopeToSingleAuthor)
                {
                    var author = scopedAuthor;
                    folders = GetAuthorScopedRescanFolders(author, requestedMediaType, rootFolders);
                    scopeLabel = "author-evidenced";

                    if (!folders.Any())
                    {
                        _logger.Debug("[FLOW-DEBUG] RESCAN-SKIP: No folder evidence is available for author '{0}' (ID: {1}), mediaType={2}. Unmapped-only folders remain covered by watcher or root scans.", author.Name, author.Id, requestedMediaType);
                        _eventAggregator.PublishEvent(new AuthorScanSkippedEvent(author, AuthorScanSkippedReason.NoFolderEvidence));
                        return;
                    }
                }
                else
                {
                    folders = rootFolders.Select(x => x.Path).ToList();
                }

                var command = new RescanFoldersCommand(folders, FilterFilesType.Matched, authorIds)
                {
                    MediaType = requestedMediaType
                };

                _logger.Debug("[FLOW-DEBUG] RESCAN-QUEUE: Queueing {0} rescan for {1} folder(s), {2} author(s), mediaType={3}.", scopeLabel, folders.Count, authorIds.Count, requestedMediaType);
                _commandQueueManager.Push(command);
            }
        }

        private List<string> GetAuthorScopedRescanFolders(Author author, string requestedMediaType, List<RootFolder> rootFolders)
        {
            var candidates = author.GetScanPaths(requestedMediaType);

            foreach (var file in _mediaFileService.GetMappedFilePathEvidenceByAuthor(author.Id, requestedMediaType))
            {
                var directory = file?.Path.IsNotNullOrWhiteSpace() == true
                    ? Path.GetDirectoryName(file.Path)
                    : null;

                if (directory.IsNotNullOrWhiteSpace())
                {
                    candidates.Add(directory);
                }
            }

            var boundedCandidates = new List<string>();
            foreach (var candidate in candidates
                         .Where(path => path.IsNotNullOrWhiteSpace())
                         .Distinct(PathEqualityComparer.Instance)
                         .OrderBy(path => path.Length))
            {
                var rootFolder = _rootFolderService.GetBestRootFolder(candidate, rootFolders);
                if (rootFolder == null)
                {
                    _logger.Debug("[FLOW-DEBUG] RESCAN-EVIDENCE-SKIP: Ignoring '{0}' because it is outside every configured root folder.", candidate);
                    continue;
                }

                if (rootFolder.Path.PathEquals(candidate))
                {
                    _logger.Debug("[FLOW-DEBUG] RESCAN-EVIDENCE-SKIP: Ignoring root-folder evidence '{0}' so a single-author refresh cannot widen to a root scan.", candidate);
                    continue;
                }

                boundedCandidates.Add(candidate);
            }

            var collapsed = new List<string>();
            foreach (var candidate in boundedCandidates)
            {
                if (collapsed.Any(parent => parent.IsParentPath(candidate)))
                {
                    continue;
                }

                collapsed.Add(candidate);
            }

            return collapsed;
        }

        private bool ProcessBulkSync(List<int> authorIds, bool forceRefresh, CancellationToken cancellationToken, CommandTrigger trigger)
        {
            var bulkStopwatch = Stopwatch.StartNew();
            try
            {
                _logger.Debug("[BULK-SYNC] Starting bulk sync for {0} authors", authorIds.Count);
                _logger.ProgressInfo("Checking {0} authors for metadata changes...", authorIds.Count);

                // Get all authors with their metadata
                var authors = _authorService.GetAuthors(authorIds);
                var reportingAuthors = authors.ToList();
                var syncMetadataByAuthorId = _syncMetadataService
                    .GetSyncMetadataForAuthors(authors.Select(a => a.Id).ToList())
                    .GroupBy(m => m.AuthorId)
                    .ToDictionary(g => g.Key, g => g.Last());
                var authorsWithETags = new List<MetadataSource.BookInfo.V5.V5AuthorETag>();
                var authorDiffItemGroups = new List<(int AuthorId, List<MetadataSource.BookInfo.V5.V5AuthorETag> Items)>();
                var requestedIdsByAuthorId = new Dictionary<int, HashSet<string>>();

                foreach (var author in authors)
                {
                    if (author.AddOptions?.MetadataUnavailable == true)
                    {
                        LogDeferredSetupRefresh(author, trigger);
                        continue;
                    }
                    var providerIds = GetAuthorDiffProviderIds(author);
                    if (providerIds.Count == 0)
                    {
                        _logger.Warn("[BULK-SYNC] Author {0} has no provider IDs, skipping", author.Name);
                        continue;
                    }

                    var etag = author.AddOptions?.AwaitingMetadata != true && syncMetadataByAuthorId.TryGetValue(author.Id, out var syncMetadata)
                        ? syncMetadata?.ETag ?? ""
                        : "";

                    var authorItems = providerIds
                        .Select(providerId => new MetadataSource.BookInfo.V5.V5AuthorETag
                        {
                            RequestedId = providerId,
                            ETag = etag
                        })
                        .ToList();

                    requestedIdsByAuthorId[author.Id] = new HashSet<string>(providerIds, StringComparer.OrdinalIgnoreCase);
                    authorDiffItemGroups.Add((author.Id, authorItems));
                    authorsWithETags.AddRange(authorItems);

                    _logger.Debug("[BULK-SYNC] Author {0}: [{1}], ETag: {2}", author.Name, string.Join(", ", providerIds), etag);
                }

                if (!authorsWithETags.Any())
                {
                    _logger.Warn("[BULK-SYNC] No authors with provider IDs found");
                    _logger.ProgressInfo("No refreshable authors found.");
                    return false;
                }

                if (_authorInfo is not BookInfoProxy bookInfoProxy)
                {
                    _logger.Error("[BULK-SYNC] Bulk author diff requires BookInfoProxy; no legacy author refresh fallback is available.");
                    _logger.ProgressInfo("Bulk author sync endpoint unavailable.");
                    return false;
                }

                // Call bulk changes endpoint
                _logger.Debug("[BULK-SYNC] Checking {0} provider id(s) across {1} authors", authorsWithETags.Count, requestedIdsByAuthorId.Count);
                var changesStopwatch = Stopwatch.StartNew();
                var changesResponse = GetBulkAuthorChangesInChunks(bookInfoProxy.GetBulkAuthorChanges, authorDiffItemGroups, _logger);
                changesStopwatch.Stop();
                _logger.Debug("[AUTHOR-REFRESH-TIMING] Bulk author changes check for {0} provider id(s) finished in {1}ms",
                    authorsWithETags.Count,
                    changesStopwatch.ElapsedMilliseconds);

                if (changesResponse == null)
                {
                    _logger.Warn("[BULK-SYNC] No response from bulk changes endpoint");
                    return false;
                }

                _logger.Debug("[BULK-SYNC] Bulk diff returned {0} refresh candidate(s), {1} deleted, {2} merged",
                    changesResponse.Changed.Count,
                    changesResponse.Deleted.Count,
                    changesResponse.Merged.Count);

                var blockedIds = ApplyServerMerges(changesResponse, authors);
                var updated = false;
                var actionableChangesByAuthorId = new Dictionary<int, (Author LocalAuthor, MetadataSource.BookInfo.V5.V5ChangedAuthor Change, bool BypassEtag)>();
                var affectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var change in changesResponse.Changed)
                {
                    if (!change.RequestedId.IsNullOrWhiteSpace())
                    {
                        affectedIds.Add(change.RequestedId);
                    }

                    if (!change.CanonicalId.IsNullOrWhiteSpace())
                    {
                        affectedIds.Add(change.CanonicalId);
                    }

                    if (blockedIds.Contains(change.RequestedId) || blockedIds.Contains(change.CanonicalId))
                    {
                        _logger.Warn("[BULK-SYNC] Skipping refresh for {0} -> {1}; merge resolution was blocked to avoid data loss",
                            change.RequestedId, change.CanonicalId);
                        continue;
                    }

                    var localAuthor = ResolveChangedAuthor(authors, change);
                    if (localAuthor == null)
                    {
                        _logger.Warn("[BULK-SYNC] Could not find local author for diff item {0} -> {1}", change.RequestedId, change.CanonicalId);
                        continue;
                    }

                    AddActionableAuthorChange(actionableChangesByAuthorId, localAuthor, change, bypassEtag: false);
                }

                foreach (var merge in changesResponse.Merged)
                {
                    if (merge == null || merge.From.IsNullOrWhiteSpace() || merge.To.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    if (blockedIds.Contains(merge.From) || blockedIds.Contains(merge.To))
                    {
                        affectedIds.Add(merge.From);
                        affectedIds.Add(merge.To);
                        continue;
                    }

                    var source = FindAuthorByPrefixedId(authors, merge.From);
                    var target = FindAuthorByPrefixedId(authors, merge.To);

                    if (IsAlreadyKnownProviderAlias(source, target))
                    {
                        _logger.Debug("[BULK-SYNC] Provider alias {0} -> {1} is already attached to local author {2}; no identity repair needed",
                            merge.From,
                            merge.To,
                            source.Name);
                        continue;
                    }

                    affectedIds.Add(merge.From);
                    affectedIds.Add(merge.To);

                    if (source == null)
                    {
                        continue;
                    }

                    if (target != null && target.Id != source.Id)
                    {
                        continue;
                    }

                    _logger.Warn("[BULK-SYNC] Server canonicalized author {0}: {1} -> {2}; scheduling identity repair", source.Name, merge.From, merge.To);
                    AddActionableAuthorChange(actionableChangesByAuthorId,
                        source,
                        new MetadataSource.BookInfo.V5.V5ChangedAuthor
                        {
                            RequestedId = merge.From,
                            CanonicalId = merge.To
                        },
                        bypassEtag: true);
                }

                foreach (var id in changesResponse.Deleted)
                {
                    if (!id.IsNullOrWhiteSpace())
                    {
                        affectedIds.Add(id);
                    }
                }

                foreach (var rejection in changesResponse.Rejected)
                {
                    if (!rejection.RequestedId.IsNullOrWhiteSpace())
                    {
                        affectedIds.Add(rejection.RequestedId);
                    }
                }

                foreach (var id in blockedIds)
                {
                    affectedIds.Add(id);
                }

                var authorsNotReturnedByBulkDiff = authors
                    .Where(author =>
                    {
                        return requestedIdsByAuthorId.TryGetValue(author.Id, out var requestedIds) &&
                               requestedIds.Count > 0 &&
                               !requestedIds.Any(affectedIds.Contains);
                    })
                    .ToList();

                StampAuthorsChecked(authorsNotReturnedByBulkDiff);

                var actionableChanges = actionableChangesByAuthorId.Values.ToList();
                if (!actionableChanges.Any())
                {
                    _logger.ProgressInfo("All {0} authors are in sync. No author metadata refresh needed.", requestedIdsByAuthorId.Count);
                }
                else
                {
                    _logger.ProgressInfo("Bulk check found {0} author refresh candidate(s). Verifying each author...", actionableChanges.Count);
                }

                var fetchedAuthors = 0;
                var unchangedAuthors = 0;
                var locallyUpdatedAuthors = 0;
                var failedAuthors = 0;

                for (var i = 0; i < actionableChanges.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var localAuthor = actionableChanges[i].LocalAuthor;
                    var change = actionableChanges[i].Change;
                    var refreshAuthorId = GetAuthorRefreshId(change);

                    try
                    {
                        _logger.Debug("[BULK-SYNC] Refreshing author {0} ({1} -> {2})", localAuthor.Name, change.RequestedId, change.CanonicalId);
                        using var progressScope = BulkAuthorRefreshProgressContext.Begin(i + 1, actionableChanges.Count);
                        var originalAuthorId = localAuthor.Id;
                        var data = GetSkyhookData(ref localAuthor, forceRefresh: forceRefresh, expectedPublishedETag: change.ETag, bypassEtag: actionableChanges[i].BypassEtag, authorIdentifierOverride: refreshAuthorId);
                        if (localAuthor.Id != originalAuthorId)
                        {
                            var selectedIndex = authorIds.IndexOf(originalAuthorId);
                            if (selectedIndex >= 0) authorIds[selectedIndex] = localAuthor.Id;
                        }
                        if (data == null)
                        {
                            unchangedAuthors++;
                            _logger.Debug("[BULK-SYNC] Author {0} was not modified after candidate verification.", localAuthor.Name);
                            continue;
                        }

                        fetchedAuthors++;
                        var authorUpdated = ApplyFetchedAuthor(localAuthor, data, cancellationToken);
                        if (authorUpdated)
                        {
                            locallyUpdatedAuthors++;
                            updated = true;
                        }
                        else
                        {
                            unchangedAuthors++;
                            _logger.Debug("[BULK-SYNC] Author {0} returned metadata but no local author data changed.", localAuthor.Name);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        failedAuthors++;
                        _logger.Error(e, "[BULK-SYNC] Failed to refresh author {0}", localAuthor.Name);
                    }
                }

                if (actionableChanges.Any())
                {
                    _logger.Debug("[BULK-SYNC] Candidate verification complete: {0} candidate(s), {1} fetched, {2} unchanged/no local change, {3} local update(s), {4} failed.",
                        actionableChanges.Count,
                        fetchedAuthors,
                        unchangedAuthors,
                        locallyUpdatedAuthors,
                        failedAuthors);

                    if (failedAuthors > 0)
                    {
                        _logger.ProgressInfo("Author update finished with {0} failed, {1} updated, {2} unchanged.", failedAuthors, locallyUpdatedAuthors, unchangedAuthors);
                    }
                    else if (locallyUpdatedAuthors > 0)
                    {
                        _logger.ProgressInfo("Author update complete: {0} author(s) updated, {1} unchanged.", locallyUpdatedAuthors, unchangedAuthors);
                    }
                    else
                    {
                        _logger.ProgressInfo("All authors are in sync. No local author metadata changed.");
                    }
                }

                var deletedIdsToReport = GetDeletedIdsToReport(changesResponse.Deleted, id => FindAuthorByPrefixedId(reportingAuthors, id), requestedIdsByAuthorId, _logger);
                if (deletedIdsToReport.Any())
                {
                    ReportServerDeletedAuthors(deletedIdsToReport, reportingAuthors);
                }

                return updated;
            }
            catch (Exception e)
            {
                _logger.Error(e, "[BULK-SYNC] Failed to process bulk sync");
                return false;
            }
            finally
            {
                bulkStopwatch.Stop();
                _logger.Debug("[AUTHOR-REFRESH-TIMING] Bulk sync for {0} requested authors finished in {1}ms", authorIds.Count, bulkStopwatch.ElapsedMilliseconds);
            }
        }

        private void StampAuthorsChecked(IEnumerable<Author> authors)
        {
            var checkedAuthors = authors?
                .Where(author => author != null && author.Id > 0)
                .DistinctBy(author => author.Id)
                .ToList();

            if (checkedAuthors == null || checkedAuthors.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var author in checkedAuthors)
            {
                author.LastInfoSync = now;
            }

            if (_mainDatabase == null)
            {
                _logger.Debug("[BULK-SYNC] Marked {0} unchanged authors as checked in memory; database handle was unavailable.", checkedAuthors.Count);
                return;
            }

            var ids = checkedAuthors.Select(author => author.Id).ToArray();
            var chunkSize = _mainDatabase.DatabaseType == DatabaseType.SQLite
                ? SqliteVariableLimit.MaxParameters - 1
                : 5000;

            try
            {
                using var connection = _mainDatabase.OpenConnection();
                foreach (var chunk in ids.Chunk(chunkSize))
                {
                    connection.Execute(
                        BuildStampAuthorsCheckedSql(_mainDatabase.DatabaseType),
                        new { LastInfoSync = now, Ids = chunk });
                }

                _authorService.ClearAuthorCache();
                _logger.Debug("[BULK-SYNC] Marked {0} unchanged authors as checked at {1:O}", checkedAuthors.Count, now);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[BULK-SYNC] Failed to mark {0} unchanged authors as checked; refresh will continue.", checkedAuthors.Count);
            }
        }

        internal static string BuildStampAuthorsCheckedSql(DatabaseType databaseType)
        {
            var idPredicate = databaseType == DatabaseType.PostgreSQL ? @"= ANY(@Ids)" : @"IN @Ids";

            return $@"UPDATE ""Authors""
                      SET ""LastInfoSync"" = @LastInfoSync
                      WHERE ""Id"" {idPredicate};";
        }

        internal static MetadataSource.BookInfo.V5.V5AuthorChangesResponse GetBulkAuthorChangesInChunks(
            Func<List<MetadataSource.BookInfo.V5.V5AuthorETag>, MetadataSource.BookInfo.V5.V5AuthorChangesResponse> fetchChunk,
            List<(int AuthorId, List<MetadataSource.BookInfo.V5.V5AuthorETag> Items)> authorDiffItemGroups,
            Logger logger)
        {
            var aggregate = new MetadataSource.BookInfo.V5.V5AuthorChangesResponse();
            var current = new List<MetadataSource.BookInfo.V5.V5AuthorETag>();
            var chunkIndex = 0;

            foreach (var group in authorDiffItemGroups)
            {
                if (group.Items == null || group.Items.Count == 0)
                {
                    continue;
                }

                if (group.Items.Count > BulkAuthorDiffMaxItemsPerRequest)
                {
                    logger.Warn("[BULK-SYNC] Author {0} has {1} provider id(s), which exceeds the per-request target of {2}. Keeping the author identity set together.",
                        group.AuthorId,
                        group.Items.Count,
                        BulkAuthorDiffMaxItemsPerRequest);
                }

                if (current.Count > 0 && current.Count + group.Items.Count > BulkAuthorDiffMaxItemsPerRequest)
                {
                    chunkIndex++;
                    if (!AppendBulkAuthorChangesChunk(fetchChunk, current, chunkIndex, aggregate, logger))
                    {
                        return null;
                    }

                    current = new List<MetadataSource.BookInfo.V5.V5AuthorETag>();
                }

                current.AddRange(group.Items);
            }

            if (current.Count > 0)
            {
                chunkIndex++;
                if (!AppendBulkAuthorChangesChunk(fetchChunk, current, chunkIndex, aggregate, logger))
                {
                    return null;
                }
            }

            return aggregate;
        }

        private static bool AppendBulkAuthorChangesChunk(
            Func<List<MetadataSource.BookInfo.V5.V5AuthorETag>, MetadataSource.BookInfo.V5.V5AuthorChangesResponse> fetchChunk,
            List<MetadataSource.BookInfo.V5.V5AuthorETag> items,
            int chunkIndex,
            MetadataSource.BookInfo.V5.V5AuthorChangesResponse aggregate,
            Logger logger)
        {
            logger.Debug("[BULK-SYNC] Requesting author diff chunk {0}: {1} provider id(s)", chunkIndex, items.Count);
            var response = fetchChunk(items);
            if (response == null)
            {
                logger.Warn("[BULK-SYNC] Author diff chunk {0} returned no response", chunkIndex);
                return false;
            }

            aggregate.Changed.AddRange(response.Changed ?? new List<MetadataSource.BookInfo.V5.V5ChangedAuthor>());
            aggregate.Merged.AddRange(response.Merged ?? new List<MetadataSource.BookInfo.V5.V5MergedAuthor>());
            aggregate.Deleted.AddRange(response.Deleted ?? new List<string>());
            aggregate.Rejected.AddRange(response.Rejected ?? new List<MetadataSource.BookInfo.V5.V5RejectedAuthor>());

            return true;
        }

        private bool TryGetSingleAuthorDiffHint(Author author, out string expectedPublishedETag, out bool deletedOnServer, out string refreshAuthorId, out bool bypassEtag)
        {
            expectedPublishedETag = null;
            deletedOnServer = false;
            refreshAuthorId = null;
            bypassEtag = false;

            if (_authorInfo is not BookInfoProxy bookInfoProxy)
            {
                return false;
            }

            var providerIds = GetAuthorDiffProviderIds(author);
            if (providerIds.Count == 0)
            {
                return false;
            }

            var syncMetadata = _syncMetadataService.GetSyncMetadata(author.Id);
            var response = bookInfoProxy.GetBulkAuthorChanges(providerIds
                .Select(providerId => new MetadataSource.BookInfo.V5.V5AuthorETag
                {
                    RequestedId = providerId,
                    ETag = syncMetadata?.ETag ?? string.Empty
                })
                .ToList());

            if (response == null)
            {
                return false;
            }

            var deletedIds = new HashSet<string>(response.Deleted ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            deletedOnServer = providerIds.All(deletedIds.Contains) &&
                              !response.Changed.Any() &&
                              !response.Merged.Any();
            if (deletedOnServer)
            {
                return true;
            }

            var change = response.Changed.FirstOrDefault(c =>
                providerIds.Contains(c.RequestedId, StringComparer.OrdinalIgnoreCase) ||
                providerIds.Contains(c.CanonicalId, StringComparer.OrdinalIgnoreCase));

            if (change != null)
            {
                expectedPublishedETag = change.ETag;
                refreshAuthorId = GetAuthorRefreshId(change);
                return true;
            }

            var merge = response.Merged.FirstOrDefault(m =>
                providerIds.Contains(m.From, StringComparer.OrdinalIgnoreCase) ||
                providerIds.Contains(m.To, StringComparer.OrdinalIgnoreCase));

            if (merge != null && !merge.To.IsNullOrWhiteSpace())
            {
                refreshAuthorId = merge.To;
                bypassEtag = true;
                return true;
            }

            return true;
        }

        private void ReportServerDeletedAuthors(IEnumerable<string> deletedIds, List<Author> authors)
        {
            var deletedList = deletedIds?
                .Where(id => !id.IsNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (deletedList == null || deletedList.Count == 0)
            {
                return;
            }

            foreach (var deletedId in deletedList)
            {
                var localAuthor = FindAuthorByPrefixedId(authors, deletedId);
                if (localAuthor != null)
                {
                    _logger.Error("[BULK-SYNC] Metadata server reports local author '{0}' ({1}) as deleted. Keeping the local author and skipping automatic removal.",
                        localAuthor.Name, deletedId);
                }
                else
                {
                    _logger.Error("[BULK-SYNC] Metadata server reported deleted author id {0}. No matching local author row was found during this refresh.",
                        deletedId);
                }
            }

            _logger.ProgressInfo("{0} authors were reported deleted on the metadata server. Local authors were kept. See logs for details.", deletedList.Count);
        }

        internal static List<string> GetDeletedIdsToReport(IEnumerable<string> deletedIds, Func<string, Author> findAuthor, Dictionary<int, HashSet<string>> requestedIdsByAuthorId, Logger logger)
        {
            var deletedSet = new HashSet<string>(
                deletedIds?.Where(id => !id.IsNullOrWhiteSpace()) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (deletedSet.Count == 0)
            {
                return new List<string>();
            }

            var reportable = new List<string>();
            foreach (var deletedId in deletedSet)
            {
                var localAuthor = findAuthor(deletedId);
                if (localAuthor == null ||
                    !requestedIdsByAuthorId.TryGetValue(localAuthor.Id, out var requestedIds) ||
                    requestedIds.Count == 0)
                {
                    reportable.Add(deletedId);
                    continue;
                }

                if (requestedIds.All(deletedSet.Contains))
                {
                    reportable.Add(deletedId);
                }
                else
                {
                    logger.Debug("[BULK-SYNC] Provider id {0} was reported deleted, but another id for the same local author resolved; not reporting the author as deleted.", deletedId);
                }
            }

            return reportable;
        }

        internal static bool IsAlreadyKnownProviderAlias(Author source, Author target)
        {
            return source != null && target != null && source.Id == target.Id;
        }

        internal static List<string> GetAuthorDiffProviderIds(Author author)
        {
            return EnumerateAuthorProviderIds(author)
                .Where(id => !id.IsNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static string GetAuthorRefreshId(MetadataSource.BookInfo.V5.V5ChangedAuthor change)
        {
            if (change == null)
            {
                return null;
            }

            return !change.CanonicalId.IsNullOrWhiteSpace()
                ? change.CanonicalId
                : change.RequestedId;
        }

        internal static void AddActionableAuthorChange(
            IDictionary<int, (Author LocalAuthor, MetadataSource.BookInfo.V5.V5ChangedAuthor Change, bool BypassEtag)> changesByAuthorId,
            Author localAuthor,
            MetadataSource.BookInfo.V5.V5ChangedAuthor change,
            bool bypassEtag)
        {
            if (changesByAuthorId == null || localAuthor == null || change == null)
            {
                return;
            }

            if (!changesByAuthorId.TryGetValue(localAuthor.Id, out var existing))
            {
                changesByAuthorId[localAuthor.Id] = (localAuthor, change, bypassEtag);
                return;
            }

            if (change.ETag.IsNullOrWhiteSpace() && existing.Change != null && !existing.Change.ETag.IsNullOrWhiteSpace())
            {
                change.ETag = existing.Change.ETag;
            }

            if (bypassEtag || existing.Change == null || existing.Change.CanonicalId.IsNullOrWhiteSpace())
            {
                changesByAuthorId[localAuthor.Id] = (localAuthor, change, existing.BypassEtag || bypassEtag);
            }
        }

        private Author FindAuthorByPrefixedId(List<Author> authors, string prefixedId)
        {
            if (string.IsNullOrEmpty(prefixedId) || !prefixedId.Contains(":"))
                return null;

            var colonIndex = prefixedId.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= prefixedId.Length - 1)
            {
                return null;
            }

            var provider = prefixedId.Substring(0, colonIndex).Trim().ToLowerInvariant();
            var normalizedPrefixedId = ProviderIdHelper.Normalize(prefixedId, provider);

            var author = authors.FirstOrDefault(a =>
                (provider == "hc" && ProviderIdHelper.Normalize(a.HardcoverAuthorId, "hc") == normalizedPrefixedId) ||
                (provider == "gr" && ProviderIdHelper.Normalize(a.GoodreadsAuthorId, "gr") == normalizedPrefixedId) ||
                (provider == "ol" && ProviderIdHelper.Normalize(a.OpenLibraryAuthorId, "ol") == normalizedPrefixedId) ||
                (provider == "gb" && ProviderIdHelper.Normalize(a.GoogleBooksAuthorId, "gb") == normalizedPrefixedId) ||
                (provider == "az" && ProviderIdHelper.Normalize(a.AudnexusAuthorId, "az") == normalizedPrefixedId));

            if (author != null)
            {
                return author;
            }

            var providerId = ProviderIdHelper.StripPrefix(normalizedPrefixedId);
            return _authorService.FindByProviderId(provider, providerId);
        }

        private Author ResolveChangedAuthor(List<Author> authors, MetadataSource.BookInfo.V5.V5ChangedAuthor change)
        {
            if (change == null)
            {
                return null;
            }

            return FindAuthorByPrefixedId(authors, change.RequestedId) ??
                   FindAuthorByPrefixedId(authors, change.CanonicalId);
        }

        private HashSet<string> ApplyServerMerges(MetadataSource.BookInfo.V5.V5AuthorChangesResponse response, List<Author> authors)
        {
            var blockedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (response?.Merged == null || !response.Merged.Any())
            {
                return blockedIds;
            }

            var processedSourceIds = new HashSet<int>();

            foreach (var merge in response.Merged)
            {
                if (merge == null || merge.From.IsNullOrWhiteSpace() || merge.To.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var source = FindAuthorByPrefixedId(authors, merge.From);
                var target = FindAuthorByPrefixedId(authors, merge.To);

                if (source != null && target == null)
                {
                    _logger.Debug("[BULK-SYNC] Merge {0} -> {1} points this local author at a new canonical provider id; refresh will repair stored identity", merge.From, merge.To);
                    continue;
                }

                if (source == null || target == null)
                {
                    blockedIds.Add(merge.From);
                    blockedIds.Add(merge.To);
                    _logger.Warn("[BULK-SYNC] Cannot auto-merge {0} -> {1}; one or both local author rows are missing", merge.From, merge.To);
                    continue;
                }

                if (source.Id == target.Id)
                {
                    continue;
                }

                if (!processedSourceIds.Add(source.Id))
                {
                    continue;
                }

                if (!CanAutoMergeAuthors(source, target, out var reason))
                {
                    blockedIds.Add(merge.From);
                    blockedIds.Add(merge.To);
                    _logger.Warn("[BULK-SYNC] Blocking auto-merge {0} -> {1}: {2}", merge.From, merge.To, reason);
                    continue;
                }

                if (!TryMergeAuthorsWithoutDataLoss(source, target, merge.To, out reason))
                {
                    blockedIds.Add(merge.From);
                    blockedIds.Add(merge.To);
                    _logger.Warn("[BULK-SYNC] Failed auto-merge {0} -> {1}: {2}", merge.From, merge.To, reason);
                    try { _syncMetadataService.UpdateSyncResult(source.Id, false, error: reason); }
                    catch (Exception retryError)
                    {
                        _logger.Error(retryError, "Could not persist the automatic refresh retry for author {0}; the merge remains unchanged", source.Id);
                    }
                    continue;
                }

                authors.RemoveAll(a => a.Id == source.Id);

                try
                {
                    var refreshedTarget = _authorService.GetAuthor(target.Id);
                    var targetIndex = authors.FindIndex(a => a.Id == target.Id);
                    if (targetIndex >= 0)
                    {
                        authors[targetIndex] = refreshedTarget;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[BULK-SYNC] Merged author {0} into {1}, but failed to reload the survivor", merge.From, merge.To);
                }

                _logger.Warn("[BULK-SYNC] Auto-merged duplicate author {0} into {1} without deleting files", merge.From, merge.To);
            }

            return blockedIds;
        }

        public Author ReconcilePublishedAuthorIdentity(Author local, Author remote)
        {
            var publishedIds = AuthorIdentity.GetProviderIdentityTokens(remote);
            local = ReconcilePublishedAuthorSplit(local, remote);
            var canonicalId = GetPreferredAuthorIdentifier(remote);
            if (canonicalId == null) return local;
            var authors = _authorService.GetAllAuthors() ?? new List<Author>();
            var candidates = authors.Where(author => AuthorIdentity.GetProviderIdentityTokens(author).Overlaps(publishedIds)).ToList();
            if (candidates.All(author => author.Id != local.Id) && (authors.Count == 0 || authors.Any(author => author.Id == local.Id))) candidates.Add(local);
            // A previous item in the same bulk refresh may already have absorbed this row.
            if (candidates.Count < 2) return candidates.SingleOrDefault() ?? local;

            var primaries = candidates.Where(author => string.Equals(GetPreferredAuthorIdentifier(author), canonicalId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (primaries.Count != 1)
            {
                primaries = candidates.Where(author => AuthorIdentity.GetProviderIdentityTokens(author).Contains(canonicalId)).ToList();
            }
            if (primaries.Count != 1)
                throw new InvalidOperationException($"Cannot identify a unique primary author for published identity {canonicalId}.");

            var target = primaries[0];
            var writers = new Stack<IDisposable>();
            try
            {
                foreach (var author in candidates.OrderBy(author => author.Id))
                    writers.Push(BookService.EnterWantedEditionLock(author, _logger, default));
                foreach (var source in candidates.Where(author => author.Id != target.Id))
                {
                    if (!CanAutoMergeAuthors(source, target, out var reason) ||
                        !TryMergeAuthorsWithoutDataLoss(source, target, canonicalId, out reason, remote))
                        throw new InvalidOperationException($"Cannot merge author {source.Id} into {target.Id}: {reason}");
                    target = _authorService.GetAuthor(target.Id);
                }
            }
            finally
            {
                while (writers.Count > 0) writers.Pop().Dispose();
            }
            return target;
        }

        private bool CanAutoMergeAuthors(Author source, Author target, out string reason)
        {
            if (_mainDatabase == null)
            {
                reason = "main database access is unavailable";
                return false;
            }

            reason = null;
            return true;
        }

        private bool TryMergeAuthorsWithoutDataLoss(Author source, Author target, string canonicalId, out string reason, Author published = null)
        {
            // Cached catalog objects must not expose an uncommitted identity/settings change.
            try
            {
                if (published == null)
                {
                    var response = _authorInfo.RefreshAuthorInfo(canonicalId, bypassEtag: true);
                    published = response?.HasChanges == true ? response.Author : null;
                }
                if (published == null || !AuthorIdentity.GetProviderIdentityTokens(published).Contains(canonicalId))
                    throw new InvalidOperationException($"Cannot verify current identity for {canonicalId}; the merge will retry without changing local data.");

                using var firstWriter = BookService.EnterWantedEditionLock(source.Id < target.Id ? source : target, _logger, default);
                using var secondWriter = BookService.EnterWantedEditionLock(source.Id < target.Id ? target : source, _logger, default);
                source = _authorRepository.Find(source.Id);
                target = _authorRepository.Get(target.Id);
                if (source == null)
                {
                    // Another refresh completed this same published merge while we waited.
                    reason = null;
                    return true;
                }
                var settings = DescribeMergeSettings(target, source);
                MergeIntoSurvivor(target, source);
                ApplyPublishedProviderIds(target, published);
                _authorService.UpdateAuthor(target, (connection, transaction) =>
                {
                    MoveAuthorImportExclusions(source, target, connection, transaction);

                    var movedBooks = ReassignAuthorId(connection, transaction, "Books", source.Id, target.Id);
                    settings = $"{movedBooks} books transferred; files stayed in place. " + settings;
                    ReassignAuthorId(connection, transaction, "AuthorSeries", source.Id, target.Id);
                    ReassignAuthorId(connection, transaction, "MetadataFiles", source.Id, target.Id);
                    ReassignAuthorId(connection, transaction, "OtherFiles", source.Id, target.Id);
                    ReassignAuthorId(connection, transaction, "DownloadHistory", source.Id, target.Id);
                    ReassignAuthorId(connection, transaction, "ExtraFiles", source.Id, target.Id);
                    ReassignAuthorId(connection, transaction, "Blacklist", source.Id, target.Id);
                    ReassignAuthorId(connection, transaction, "History", source.Id, target.Id);
                    ReassignAuthorId(connection, transaction, "PendingReleases", source.Id, target.Id);
                    ReassignAuthorId(connection, transaction, "PendingImport", source.Id, target.Id);

                    MergeSyncMetadata(connection, transaction, source.Id, target.Id, canonicalId);
                    WriteIdentityHistory(connection, transaction, source, target, false, settings);

                    connection.Execute(
                        @"DELETE FROM ""Authors"" WHERE ""Id"" = @Id;",
                        new { source.Id },
                        transaction);
                });
                _authorService.ClearAuthorCache();
                _logger.Info("[AUTHOR-MERGE] {0} (author {1}) into {2} (author {3}), canonical {4}. {5}", source.Name, source.Id, target.Name, target.Id, canonicalId, settings);
                PublishIdentityChange(new LibraryIdentityChangedEvent(new[] { source.Id, target.Id }, Array.Empty<int>())
                    { SourceAuthor = source, TargetAuthor = target, SettingsResolution = settings });

                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[BULK-SYNC] Failed to merge author {0} into {1}", source, target);
                reason = ex.Message;
                return false;
            }
        }

        private void PublishIdentityChange(LibraryIdentityChangedEvent message)
        {
            try { _eventAggregator.PublishEvent(message); }
            catch (Exception ex) { _logger.Warn(ex, "Library identity committed, but a reader could not refresh; the next poll/read will retry"); }
        }

        private static string DescribeMergeSettings(Author primary, Author absorbed)
        {
            var names = new[] { nameof(Author.AudiobookRootFolderPath), nameof(Author.EbookRootFolderPath),
                nameof(Author.AudiobookPath), nameof(Author.EbookPath), nameof(Author.AudiobookQualityProfileId), nameof(Author.EbookQualityProfileId),
                nameof(Author.AudiobookMetadataProfileId), nameof(Author.EbookMetadataProfileId), nameof(Author.AudiobookMonitorNewItems),
                nameof(Author.EbookMonitorNewItems), nameof(Author.AudiobookTags), nameof(Author.EbookTags), nameof(Author.Tags), nameof(Author.SyncMonitoredAcrossFormats) };
            var changes = new List<string>();
            foreach (var name in names)
            {
                var property = typeof(Author).GetProperty(name);
                var left = System.Text.Json.JsonSerializer.Serialize(property.GetValue(primary));
                var right = System.Text.Json.JsonSerializer.Serialize(property.GetValue(absorbed));
                if (left != right) changes.Add($"{name}: primary={left}, absorbed={right}");
            }
            return "Primary settings retained; only unconfigured format setup inherited. Tags and author monitoring combined; book selections unchanged. " + string.Join("; ", changes);
        }

        private void WriteIdentityHistory(IDbConnection connection, IDbTransaction transaction, Author source, Author target, bool split, string details)
        {
            var history = new EntityHistory
            {
                AuthorId = target.Id, Date = DateTime.UtcNow,
                EventType = split ? EntityHistoryEventType.AuthorSplit : EntityHistoryEventType.AuthorsMerged,
                SourceTitle = $"{source.Name} → {target.Name}", Quality = new NzbDrone.Core.Qualities.QualityModel(),
                Data = new Dictionary<string, string>
                {
                    ["sourceAuthorId"] = source.Id.ToString(), ["targetAuthorId"] = target.Id.ToString(),
                    ["sourceAuthorName"] = source.Name, ["targetAuthorName"] = target.Name,
                    ["details"] = details
                }
            };
            _historyRepository.InsertMany(new[] { history }, connection, transaction);
        }

        private void MergeIntoSurvivor(Author survivor, Author source)
        {
            // The primary author's configured sides win, including inherited defaults and explicit None.
            // Only an entirely unconfigured side inherits the absorbed author's setup.
            if (string.IsNullOrWhiteSpace(survivor.AudiobookRootFolderPath))
            {
                survivor.AudiobookRootFolderPath = source.AudiobookRootFolderPath;
                survivor.AudiobookPath = source.AudiobookPath;
                survivor.AudiobookQualityProfileId = source.AudiobookQualityProfileId;
                survivor.AudiobookMetadataProfileId = source.AudiobookMetadataProfileId;
                survivor.AudiobookMonitorNewItems = source.AudiobookMonitorNewItems;
                survivor.AudiobookTags = source.AudiobookTags == null ? null : new HashSet<int>(source.AudiobookTags);
                survivor.AudiobookSettingsManuallyOverridden = source.AudiobookSettingsManuallyOverridden;
            }

            if (string.IsNullOrWhiteSpace(survivor.EbookRootFolderPath))
            {
                survivor.EbookRootFolderPath = source.EbookRootFolderPath;
                survivor.EbookPath = source.EbookPath;
                survivor.EbookQualityProfileId = source.EbookQualityProfileId;
                survivor.EbookMetadataProfileId = source.EbookMetadataProfileId;
                survivor.EbookMonitorNewItems = source.EbookMonitorNewItems;
                survivor.EbookTags = source.EbookTags == null ? null : new HashSet<int>(source.EbookTags);
                survivor.EbookSettingsManuallyOverridden = source.EbookSettingsManuallyOverridden;
            }

            survivor.AudiobookMonitored = MergeMonitoring(survivor.AudiobookMonitored, source.AudiobookMonitored);
            survivor.EbookMonitored = MergeMonitoring(survivor.EbookMonitored, source.EbookMonitored);
            survivor.Monitored = survivor.IsMonitoredFromMediaSettings();

            survivor.Tags = MergeTagSet(survivor.Tags, source.Tags);
            survivor.AudiobookTags = MergeTagSet(survivor.AudiobookTags, source.AudiobookTags);
            survivor.EbookTags = MergeTagSet(survivor.EbookTags, source.EbookTags);
            survivor.LastSelectedMediaType ??= source.LastSelectedMediaType;

        }

        private static HashSet<int> MergeTagSet(HashSet<int> primary, HashSet<int> absorbed) =>
            primary == null && absorbed == null ? null : (primary ?? new HashSet<int>()).Union(absorbed ?? new HashSet<int>()).ToHashSet();

        private static bool? MergeMonitoring(bool? primary, bool? absorbed)
        {
            return primary == true || absorbed == true ? true : primary ?? absorbed;
        }

        private void MoveAuthorImportExclusions(Author source, Author target, IDbConnection connection = null, IDbTransaction transaction = null)
        {
            var providerIds = AuthorIdentity.GetProviderIdentityTokenList(source);
            var exclusions = connection == null
                ? _importListExclusionService.FindByForeignId(providerIds)
                : _importListExclusionService.FindByForeignId(providerIds, connection, transaction);
            foreach (var scope in exclusions.Select(exclusion => exclusion.MediaType).Distinct())
            {
                if (connection == null) _importListExclusionService.AddAuthorExclusions(new[] { target }, scope);
                else _importListExclusionService.AddAuthorExclusions(new[] { target }, scope, connection, transaction);
            }
        }

        private void MergeSyncMetadata(IDbConnection connection, IDbTransaction transaction, int sourceAuthorId, int targetAuthorId, string canonicalId)
        {
            var metadata = connection.Query<AuthorSyncMetadata>(
                @"SELECT * FROM ""AuthorSyncMetadata"" WHERE ""AuthorId"" = @SourceId OR ""AuthorId"" = @TargetId",
                new { SourceId = sourceAuthorId, TargetId = targetAuthorId }, transaction);
            var sourceMetadata = metadata.SingleOrDefault(row => row.AuthorId == sourceAuthorId);
            var targetMetadata = metadata.SingleOrDefault(row => row.AuthorId == targetAuthorId);

            if (sourceMetadata == null && targetMetadata == null)
            {
                return;
            }

            if (sourceMetadata != null && targetMetadata == null)
            {
                connection.Execute(
                    @"UPDATE ""AuthorSyncMetadata""
                      SET ""AuthorId"" = @TargetAuthorId,
                          ""ExternalAuthorId"" = @ExternalAuthorId
                      WHERE ""Id"" = @Id;",
                    new
                    {
                        TargetAuthorId = targetAuthorId,
                        ExternalAuthorId = canonicalId ?? sourceMetadata.ExternalAuthorId,
                        sourceMetadata.Id
                    },
                    transaction);

                return;
            }

            if (sourceMetadata == null || targetMetadata == null)
            {
                return;
            }

            if (sourceMetadata.LastSyncAttempt > targetMetadata.LastSyncAttempt)
            {
                targetMetadata.LastSyncAttempt = sourceMetadata.LastSyncAttempt;
                targetMetadata.LastSyncStatus = sourceMetadata.LastSyncStatus;
                targetMetadata.LastHttpStatus = sourceMetadata.LastHttpStatus;
                targetMetadata.LastError = sourceMetadata.LastError;
                targetMetadata.LastSyncDurationMs = sourceMetadata.LastSyncDurationMs;
                targetMetadata.NextSyncNotBefore = sourceMetadata.NextSyncNotBefore;
            }

            if (sourceMetadata.LastSuccessfulSync > targetMetadata.LastSuccessfulSync)
            {
                targetMetadata.LastSuccessfulSync = sourceMetadata.LastSuccessfulSync;
            }

            if (targetMetadata.ETag.IsNullOrWhiteSpace())
            {
                targetMetadata.ETag = sourceMetadata.ETag;
            }

            if (targetMetadata.ServerVersion.IsNullOrWhiteSpace())
            {
                targetMetadata.ServerVersion = sourceMetadata.ServerVersion;
            }

            targetMetadata.ExternalAuthorId = canonicalId ?? targetMetadata.ExternalAuthorId ?? sourceMetadata.ExternalAuthorId;
            targetMetadata.SyncFailureCount = Math.Max(targetMetadata.SyncFailureCount, sourceMetadata.SyncFailureCount);

            connection.Execute(
                @"DELETE FROM ""AuthorSyncMetadata"" WHERE ""Id"" = @Id;",
                new { sourceMetadata.Id },
                transaction);
            connection.Execute(
                @"UPDATE ""AuthorSyncMetadata""
                  SET ""ExternalAuthorId"" = @ExternalAuthorId,
                      ""ETag"" = @ETag,
                      ""ServerVersion"" = @ServerVersion,
                      ""LastSyncAttempt"" = @LastSyncAttempt,
                      ""LastSuccessfulSync"" = @LastSuccessfulSync,
                      ""LastSyncStatus"" = @LastSyncStatus,
                      ""LastHttpStatus"" = @LastHttpStatus,
                      ""SyncFailureCount"" = @SyncFailureCount,
                      ""LastError"" = @LastError,
                      ""LastSyncDurationMs"" = @LastSyncDurationMs,
                      ""NextSyncNotBefore"" = @NextSyncNotBefore
                  WHERE ""Id"" = @Id;",
                targetMetadata,
                transaction);


        }

        private static int ReassignAuthorId(IDbConnection connection, IDbTransaction transaction, string table, int sourceAuthorId, int targetAuthorId)
        {
            if (sourceAuthorId <= 0 || targetAuthorId <= 0 || sourceAuthorId == targetAuthorId)
            {
                return 0;
            }

            return connection.Execute(
                $@"UPDATE ""{table}""
                   SET ""AuthorId"" = @TargetAuthorId
                   WHERE ""AuthorId"" = @SourceAuthorId;",
                new { SourceAuthorId = sourceAuthorId, TargetAuthorId = targetAuthorId },
                transaction);
        }

        private static IEnumerable<string> EnumerateAuthorProviderIds(Author author)
        {
            return AuthorIdentity.GetProviderIdentityTokenList(author);
        }


        private void RefreshSelectedAuthors(List<int> authorIds, bool isNew, CommandTrigger trigger, bool isFromImport = false, bool refreshMetadata = true, bool rescanFolders = true, string mediaType = "all", CancellationToken cancellationToken = default, bool rescanWhenMetadataUnchanged = false, bool forceRefresh = false, bool useBulkSyncWhenNotForced = false, bool scopeRescanToSingleAuthor = false, IReadOnlyDictionary<int, AuthorSyncMetadata> retries = null)
        {
            _logger.Debug("[FLOW-DEBUG] ========== RefreshSelectedAuthors START ==========");
            _logger.Debug("[FLOW-DEBUG] AuthorIds: [{0}]", string.Join(", ", authorIds));
            _logger.Debug("[FLOW-DEBUG] Flags: isNew={0}, trigger={1}, isFromImport={2}, refreshMetadata={3}, rescanFolders={4}, forceRefresh={5}, useBulkSyncWhenNotForced={6}", isNew, trigger, isFromImport, refreshMetadata, rescanFolders, forceRefresh, useBulkSyncWhenNotForced);

            // The comprehensive import process already handles author/book discovery.
            // A forced refresh is an explicit local reconciliation request and must still run.
            if (isFromImport && !forceRefresh)
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: isFromImport=true - SKIPPING ENTIRE REFRESH");
                _logger.Debug("[FLOW-DEBUG] REASON: Import process handles discovery comprehensively");
                _logger.Debug("[FLOW-DEBUG] ========== RefreshSelectedAuthors END (SKIPPED) ==========");
                return;
            }

            if (isFromImport)
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: isFromImport=true but forceRefresh=true - running metadata reconciliation and preserving import rescan skip behavior");
            }

            var updated = false;

            // Only refresh metadata if requested
            if (refreshMetadata)
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: Will refresh metadata (refreshMetadata=true)");

                // Bulk-selected/scheduled refreshes use the ETag diff path unless explicitly forced.
                if (useBulkSyncWhenNotForced && !forceRefresh)
                {
                    _logger.Debug("[BULK-SYNC] Using bulk sync endpoint for {0} authors", authorIds.Count);
                    updated = ProcessBulkSync(authorIds, forceRefresh: false, cancellationToken, trigger);
                }
                else
                {
                    // Local reconciliation path: fetch each published payload and compare it to local DB.
                    var authors = _authorService.GetAuthors(authorIds);
                    foreach (var selectedAuthor in authors)
                    {
                        var author = selectedAuthor;
                        cancellationToken.ThrowIfCancellationRequested();

                        if (retries != null && !CanRetryAuthorRefresh()) break;

                        if (author.AddOptions?.MetadataUnavailable == true)
                        {
                            LogDeferredSetupRefresh(author, trigger);
                            _logger.Debug("Author {0} initial setup is unavailable; its retained Wanted Authors request owns Retry/Delete.", author.Id);
                            continue;
                        }

                        try
                        {
                            if (retries != null) _syncMetadataService.DeferRetry(retries[author.Id]);
                            _logger.Debug("[FLOW-DEBUG] ACTION: Refreshing metadata for author: {0}", author.Name);

                            // Start timing for this author
                            var authorStopwatch = System.Diagnostics.Stopwatch.StartNew();

                            // Count existing books/editions before refresh
                            var existingBooks = _bookService.GetBooksByAuthor(author.Id);
                            var existingBookIds = existingBooks
                                .Where(book => book != null && book.Id > 0)
                                .Select(book => book.Id)
                                .Distinct()
                                .ToList();
                            var existingEditionCount = existingBookIds.Any()
                                ? (_editionService.GetEditionsByBook(existingBookIds)?.Count ?? 0)
                                : 0;

                            string expectedPublishedETag = null;
                            string refreshAuthorId = null;
                            var deletedOnServer = false;
                            var bypassEtagForIdentityRepair = false;
                            var diffChecked = false;

                            if (!forceRefresh && author.AddOptions?.AwaitingMetadata != true)
                            {
                                diffChecked = TryGetSingleAuthorDiffHint(author, out expectedPublishedETag, out deletedOnServer, out refreshAuthorId, out bypassEtagForIdentityRepair);
                            }
                            else
                            {
                                _logger.Debug("[FLOW-DEBUG] Bypassing local diff/ETag shortcut for author '{0}': forced={1}, awaiting metadata={2}",
                                    author.Name, forceRefresh, author.AddOptions?.AwaitingMetadata == true);
                            }

                            if (deletedOnServer)
                            {
                                ReportServerDeletedAuthors(GetAuthorDiffProviderIds(author), new List<Author> { author });
                                continue;
                            }

                            if (diffChecked && expectedPublishedETag.IsNullOrWhiteSpace() && refreshAuthorId.IsNullOrWhiteSpace())
                            {
                                _logger.Debug("[BULK-SYNC] Single-author diff says {0} is already up to date", author.Name);
                                _logger.ProgressInfo("{0} is up to date.", author.Name);
                                StampAuthorsChecked(new[] { author });
                                continue;
                            }

                            var originalAuthorId = author.Id;
                            var data = GetSkyhookData(ref author, forceRefresh: forceRefresh, expectedPublishedETag: expectedPublishedETag, bypassEtag: forceRefresh || bypassEtagForIdentityRepair, authorIdentifierOverride: refreshAuthorId);

                            if (author.Id != originalAuthorId)
                                authorIds = authorIds.Select(id => id == originalAuthorId ? author.Id : id).Distinct().ToList();
                            var refreshStopwatch = System.Diagnostics.Stopwatch.StartNew();
                            updated |= ApplyFetchedAuthor(author, data, cancellationToken);
                            refreshStopwatch.Stop();

                            // Count books/editions after refresh
                            var newBooks = _bookService.GetBooksByAuthor(author.Id);
                            var newBookIds = newBooks
                                .Where(book => book != null && book.Id > 0)
                                .Select(book => book.Id)
                                .Distinct()
                                .ToList();
                            var newEditionCount = newBookIds.Any()
                                ? (_editionService.GetEditionsByBook(newBookIds)?.Count ?? 0)
                                : 0;

                            authorStopwatch.Stop();

                            // Log comprehensive timing
                            _logger.Debug("[DB-TIMING] Author '{0}' refresh complete:", author.Name);
                            _logger.Debug("[DB-TIMING]   - Total time: {0}ms", authorStopwatch.ElapsedMilliseconds);
                            _logger.Debug("[DB-TIMING]   - RefreshEntityInfo time: {0}ms", refreshStopwatch.ElapsedMilliseconds);
                            _logger.Debug("[DB-TIMING]   - Books: {0} → {1} (added {2})",
                                existingBooks.Count, newBooks.Count, newBooks.Count - existingBooks.Count);
                            _logger.Debug("[DB-TIMING]   - Editions: {0} → {1} (added {2})",
                                existingEditionCount, newEditionCount, newEditionCount - existingEditionCount);

                            // Special logging for authors with many editions
                            if (newEditionCount > 100)
                            {
                                _logger.Debug("[DB-TIMING] Large author detected: '{0}' has {1} editions across {2} books",
                                    author.Name, newEditionCount, newBooks.Count);
                                _logger.Debug("[DB-TIMING]   - Average editions per book: {0:F1}",
                                    (double)newEditionCount / Math.Max(1, newBooks.Count));
                                _logger.Debug("[DB-TIMING]   - Time per edition: {0:F1}ms",
                                    (double)authorStopwatch.ElapsedMilliseconds / Math.Max(1, newEditionCount));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            _logger.Error(e, "[FLOW-DEBUG] ERROR: Couldn't refresh info for {0}", author);
                        }
                    }
                }

                _logger.Debug("[FLOW-DEBUG] RESULT: Metadata refresh complete, updated={0}", updated);
            }
            else
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: Skipping metadata refresh (refreshMetadata=false)");
            }

            // Initial setup still owes its scan when metadata recovery succeeds.
            if (rescanFolders || refreshMetadata)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.Debug("[FLOW-DEBUG] Evaluating post-refresh scans: requested={0}, initialSetupOnly={1}, metadataUpdated={2}", rescanFolders, !rescanFolders, updated);
                Rescan(authorIds, isNew, trigger, updated, isFromImport, mediaType, rescanWhenMetadataUnchanged, scopeRescanToSingleAuthor, initialSetupOnly: !rescanFolders);
            }
            else
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: Skipping folder rescan (rescanFolders=false)");
            }

            _logger.Debug("[FLOW-DEBUG] ========== RefreshSelectedAuthors END ==========");
        }

        private void LogDeferredSetupRefresh(Author author, CommandTrigger trigger)
        {
            if (trigger == CommandTrigger.Manual)
            {
                _logger.Info("Author {0} ({1}) has a refused initial setup request. Metadata refresh is deferred; use Wanted > Authors > Retry to reactivate it.", author.Name, author.Id);
            }
        }

        public void Execute(RetryAuthorRefreshCommand message) => Execute(message, CancellationToken.None);

        public void Execute(RetryAuthorRefreshCommand message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var setupRetries = _authorRepository.GetDueInitialFormatSync(DateTime.UtcNow);
                if (setupRetries.Count > 0)
                    _commandQueueManager.Push(new BulkSyncFormatMonitoringCommand(setupRetries)
                    {
                        InitialSetupAttempt = 1
                    }, trigger: CommandTrigger.Scheduled);
                var failures = Interlocked.Exchange(ref _setupRetryQueueFailures, 0);
                if (failures > 0)
                    _logger.Info("Initial format-sync retry queue recovered after {0} failed attempts.", failures);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failures = Interlocked.Increment(ref _setupRetryQueueFailures);
                var level = AuthorRetrySchedule.ShouldWarn(failures) ? LogLevel.Warn : LogLevel.Debug;
                _logger.Log(level, ex, "Initial format-sync retry could not be queued (attempt {0}); it will be checked again next minute. Continuing metadata retries.", failures);
            }
            if (!CanRetryAuthorRefresh()) return;
            var timer = Stopwatch.StartNew();
            var due = _syncMetadataService.GetDueForSync();
            if (due.Count == 0) return;
            var authors = _authorService.GetAuthors(due.Select(item => item.AuthorId).Distinct().ToList())
                .ToDictionary(author => author.Id);
            // Pending/refused initial setup is owned by Wanted Authors, including its
            // Retry/Delete controls. This task must not resurrect a dismissed request.
            var held = due.Where(item => !authors.TryGetValue(item.AuthorId, out var author) ||
                author.AddOptions?.AwaitingMetadata == true || author.AddOptions?.MetadataUnavailable == true).ToList();
            foreach (var item in held) item.NextSyncNotBefore = null;
            _syncMetadataService.BulkUpdateSyncMetadata(held);
            var ids = due.Except(held).Select(item => item.AuthorId).Distinct().ToList();
            try
            {
                if (ids.Count > 0)
                    RefreshSelectedAuthors(ids, false, CommandTrigger.Scheduled, rescanFolders: false,
                        cancellationToken: cancellationToken, forceRefresh: true,
                        retries: due.Except(held).ToDictionary(item => item.AuthorId));
            }
            finally
            {
                _logger.Debug("Author refresh retry: {0} due, {1} eligible, {2} held for Wanted Authors; elapsed {3} ms",
                    due.Count, ids.Count, held.Count, timer.ElapsedMilliseconds);
            }
        }

        private bool CanRetryAuthorRefresh()
        {
            if (_metadataServerHealthGate.CanBeginRequest(out var retryAfter)) return true;
            _logger.Debug("Author refresh retry waiting for metadata server {0}; retry after {1}",
                _metadataServerHealthGate.SourceName, MetadataServerHealthGate.FormatRetryAfter(retryAfter));
            return false;
        }

        public void Execute(BulkRefreshAuthorCommand message)
        {
            Execute(message, CancellationToken.None);
        }

        public void Execute(BulkRefreshAuthorCommand message, CancellationToken cancellationToken)
        {
            _logger.Debug("[FLOW-DEBUG] ========== Execute(BulkRefreshAuthorCommand) ==========");
            var requestedMediaType = (message.MediaType ?? "all").Trim().ToLowerInvariant();

            var authorIds = message.AuthorIds ?? new List<int>();
            if (authorIds.Count == 0)
            {
                authorIds = GetAuthorIdsForMediaType(requestedMediaType);
                _logger.Debug("[FLOW-DEBUG] Bulk refresh requested with empty AuthorIds; expanding to {0} authors for mediaType='{1}'", authorIds.Count, requestedMediaType);
            }

            _logger.Debug("[FLOW-DEBUG] AuthorIds Count: {0}", authorIds.Count);
            _logger.Debug("[FLOW-DEBUG] MediaType: {0}", requestedMediaType);
            _logger.Debug("[FLOW-DEBUG] Flags: AreNewAuthors={0}, Trigger={1}, IsFromImport={2}, RefreshMetadata={3}, RescanFolders={4}, ForceRefresh={5}", message.AreNewAuthors, message.Trigger, message.IsFromImport, message.RefreshMetadata, message.RescanFolders, message.ForceRefresh);

            RefreshSelectedAuthors(authorIds, message.AreNewAuthors, message.Trigger, message.IsFromImport, message.RefreshMetadata, message.RescanFolders, requestedMediaType, cancellationToken, forceRefresh: message.ForceRefresh, useBulkSyncWhenNotForced: true);
        }

        private List<int> GetAuthorIdsForMediaType(string mediaType)
        {
            var requestedMediaType = (mediaType ?? "all").Trim().ToLowerInvariant();

            static bool HasAudiobookRootFolder(Author author)
            {
                return author != null &&
                       (!string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath) ||
                        !string.IsNullOrWhiteSpace(author.AudiobookPath));
            }

            static bool HasEbookRootFolder(Author author)
            {
                return author != null &&
                       (!string.IsNullOrWhiteSpace(author.EbookRootFolderPath) ||
                        !string.IsNullOrWhiteSpace(author.EbookPath));
            }

            var authors = _authorService.GetAllAuthors();

            IEnumerable<Author> filtered;
            if (requestedMediaType == "audiobook")
            {
                filtered = authors.Where(HasAudiobookRootFolder);
            }
            else if (requestedMediaType == "ebook")
            {
                filtered = authors.Where(HasEbookRootFolder);
            }
            else
            {
                filtered = authors.Where(a => HasAudiobookRootFolder(a) || HasEbookRootFolder(a));
            }

            return filtered.Select(a => a.Id).ToList();
        }

        public void Execute(RefreshAuthorCommand message)
        {
            Execute(message, CancellationToken.None);
        }

        public void Execute(RefreshAuthorCommand message, CancellationToken cancellationToken)
        {
            _logger.Debug("[FLOW-DEBUG] ========== Execute(RefreshAuthorCommand) ==========");
            _logger.Debug("[FLOW-DEBUG] AuthorId: {0}", message.AuthorId.HasValue ? message.AuthorId.Value.ToString() : "NULL (refresh all)");
            _logger.Debug("[FLOW-DEBUG] Flags: IsNewAuthor={0}, Trigger={1}, IsFromImport={2}, RefreshMetadata={3}, RescanFolders={4}, ForceRefresh={5}", message.IsNewAuthor, message.Trigger, message.IsFromImport, message.RefreshMetadata, message.RescanFolders, message.ForceRefresh);

            var trigger = message.Trigger;
            var isNew = message.IsNewAuthor;

            if (message.AuthorId.HasValue)
            {
                var effectiveForceRefresh = message.ForceRefresh ||
                                            (trigger == CommandTrigger.Manual && !message.IsFromImport);
                var rescanWhenMetadataUnchanged =
                    trigger == CommandTrigger.Manual &&
                    message.RescanFolders &&
                    !message.IsFromImport;

                RefreshSelectedAuthors(new List<int> { message.AuthorId.Value }, isNew, trigger, message.IsFromImport, message.RefreshMetadata, message.RescanFolders, "all", cancellationToken, rescanWhenMetadataUnchanged, forceRefresh: effectiveForceRefresh, useBulkSyncWhenNotForced: false, scopeRescanToSingleAuthor: true);
            }
            else
            {
                var authors = _authorService.GetAllAuthors().OrderBy(c => c.Name).ToList();
                var authorIds = authors.Select(x => x.Id).ToList();

                RefreshSelectedAuthors(authorIds, isNew, trigger, message.IsFromImport, message.RefreshMetadata, message.RescanFolders, "all", cancellationToken, forceRefresh: message.ForceRefresh, useBulkSyncWhenNotForced: true);
            }
        }
    }
}
