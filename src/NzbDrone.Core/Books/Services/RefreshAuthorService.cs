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
        bool ReconcileAuthorBlob(Author localAuthor, Author authoritativeRemoteAuthor) => false;
    }

    public class RefreshAuthorService : RefreshEntityServiceBase<Author, Book>,
        IRefreshAuthorService,
        IExecute<RefreshAuthorCommand>,
        IExecute<BulkRefreshAuthorCommand>
    {
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly IRefreshBookService _refreshBookService;
        private readonly IRefreshSeriesService _refreshSeriesService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IMediaFileService _mediaFileService;
        private readonly IHistoryService _historyService;
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
        private readonly Logger _logger;
        private BookRefreshMatchingIndex _bookRefreshMatchingIndex;
        private List<Book> _authorRefreshRehomeBlueprint;

        private static readonly SemaphoreSlim AuthorMetadataRefreshGate = new SemaphoreSlim(1, 1);
        // SMS /authors/diff hard-rejects requests above 10,000 items and caps the body at 1MB;
        // 9,000 leaves headroom for one oversize author id-set appended to a nearly-full chunk.
        internal const int BulkAuthorDiffMaxItemsPerRequest = 9000;

            public RefreshAuthorService(IProvideAuthorInfo authorInfo,
                                        IAuthorService authorService,
                                        IBookService bookService,
                                        IEditionService editionService,
                                        IMetadataProfileService metadataProfileService,
                                    IRefreshBookService refreshBookService,
                                    IRefreshSeriesService refreshSeriesService,
                                    IEventAggregator eventAggregator,
                                    IManageCommandQueue commandQueueManager,
                                    IMediaFileService mediaFileService,
                                    IHistoryService historyService,
                                    IRootFolderService rootFolderService,
                                    ICheckIfAuthorShouldBeRefreshed checkIfAuthorShouldBeRefreshed,
                                    IMonitorNewBookService monitorNewBookService,
                                    IConfigService configService,
                                    IImportListExclusionService importListExclusionService,
                                    IAuthorSyncMetadataService syncMetadataService,
                                    IAuthorSyncQueueService syncQueueService,
                                    IRootFolderSettingsResolver rootFolderSettingsResolver,
                                    Logger logger,
                                    IEditionSelector editionSelector = null,
                                    IMainDatabase mainDatabase = null)
        : base(logger)
        {
            _authorInfo = authorInfo;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _metadataProfileService = metadataProfileService;
            _refreshBookService = refreshBookService;
            _refreshSeriesService = refreshSeriesService;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _mediaFileService = mediaFileService;
            _historyService = historyService;
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
        }

        private IDisposable EnterAuthorMetadataRefreshGate(string context, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            if (AuthorMetadataRefreshGate.CurrentCount == 0)
            {
                _logger.Debug("[AUTHOR-REFRESH-GATE] Waiting for active author metadata refresh to finish before starting {0}", context);
            }

            AuthorMetadataRefreshGate.Wait(cancellationToken);
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds > 0)
            {
                _logger.Debug("[AUTHOR-REFRESH-GATE] Acquired author metadata refresh gate for {0} after {1}ms", context, stopwatch.ElapsedMilliseconds);
            }

            return new GateReleaser(() =>
            {
                AuthorMetadataRefreshGate.Release();
                _logger.Debug("[AUTHOR-REFRESH-GATE] Released author metadata refresh gate for {0}", context);
            });
        }

        private sealed class GateReleaser : IDisposable
        {
            private Action _release;

            public GateReleaser(Action release)
            {
                _release = release;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _release, null)?.Invoke();
            }
        }

        private Author GetSkyhookData(Author author, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false, string authorIdentifierOverride = null)
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

                try
                {
                    // Get the current ETag from AuthorSyncMetadata if available
                    var syncMetadata = _syncMetadataService.GetSyncMetadata(author.Id);
                    var currentETag = syncMetadata?.ETag;

                    if (!string.IsNullOrEmpty(authorIdentifier) &&
                        (syncMetadata == null || !string.Equals(syncMetadata.ExternalAuthorId, authorIdentifier, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.Debug("[SYNC-METADATA] Ensuring sync metadata maps author {0} to {1}", author.Name, authorIdentifier);
                        syncMetadata = _syncMetadataService.CreateOrUpdateSyncMetadata(author.Id, authorIdentifier, currentETag);
                        currentETag = syncMetadata?.ETag;
                    }

                    var refreshResult = _authorInfo.RefreshAuthorInfo(authorIdentifier, currentETag, forceRefresh, expectedPublishedETag, bypassEtag);

                    if (refreshResult.HasChanges && refreshResult.Author != null)
                    {
                        var refreshedAuthorIdentifier = GetPreferredAuthorIdentifier(refreshResult.Author) ?? authorIdentifier;
                        var updatedETag = refreshResult.ETag ?? expectedPublishedETag;
                        _syncMetadataService.CreateOrUpdateSyncMetadata(author.Id, refreshedAuthorIdentifier, updatedETag);

                        _logger.Info($"Author {author.Name} was updated from API (ETag: {updatedETag ?? "none"})");

                        // Store the new ETag after successful refresh
                        _syncMetadataService.UpdateSyncResult(
                            author.Id,
                            success: true,
                            etag: updatedETag,
                            httpStatus: refreshResult.HttpStatus.HasValue ? (int)refreshResult.HttpStatus.Value : 200
                        );

                        return refreshResult.Author;
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
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to refresh author info for {0}", author.Name);
                    // Fallback to existing GetAuthorInfo method on error
                    try
                    {
                        var remoteAuthor = _authorInfo.GetAuthorInfo(authorIdentifier);
                        if (remoteAuthor != null)
                        {
                            _logger.Info($"Author {author.Name} was updated from API using fallback method");
                            return remoteAuthor;
                        }
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.Error(fallbackEx, "Fallback author refresh also failed for {0}", author.Name);
                    }
                }

                // Return null to skip refresh — returning the local author (Books == null)
                // would cause mass book deletion via SortChildren.
                return null;
            }
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

        private bool RefreshEntityInfoIfRemoteDataAvailable(Author author, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
        {
            if (remoteData == null)
            {
                _logger.Debug("Skipping local refresh for {0}; metadata source returned no changed author data.", author.Name);
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                return RefreshEntityInfo(author, null, remoteData, forceChildRefresh, forceUpdateFileTags, lastUpdate);
            }
            finally
            {
                stopwatch.Stop();
                _logger.Debug("[AUTHOR-REFRESH-TIMING] Local reconciliation for author '{0}' finished in {1}ms", author?.Name ?? "Unknown", stopwatch.ElapsedMilliseconds);
            }
        }

        public bool ReconcileAuthorBlob(Author localAuthor, Author authoritativeRemoteAuthor)
        {
            if (localAuthor?.Id <= 0 || authoritativeRemoteAuthor == null)
            {
                return false;
            }

            return RefreshEntityInfoIfRemoteDataAvailable(
                localAuthor,
                authoritativeRemoteAuthor,
                forceChildRefresh: true,
                forceUpdateFileTags: false,
                lastUpdate: null);
        }

        protected override RemoteData GetRemoteData(Author local, List<Author> remote, Author data)
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
            // Authors are now unique entities, no merging needed
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

            // Update list exclusions for all provider IDs
            var localProviderIds = new List<string>();
            if (!string.IsNullOrEmpty(local.HardcoverAuthorId)) localProviderIds.Add(local.HardcoverAuthorId);
            if (!string.IsNullOrEmpty(local.GoodreadsAuthorId)) localProviderIds.Add(local.GoodreadsAuthorId);
            if (!string.IsNullOrEmpty(local.OpenLibraryAuthorId)) localProviderIds.Add(local.OpenLibraryAuthorId);
            if (!string.IsNullOrEmpty(local.GoogleBooksAuthorId)) localProviderIds.Add(local.GoogleBooksAuthorId);

            foreach (var providerId in localProviderIds)
            {
                var importExclusion = _importListExclusionService.FindByForeignId(providerId);
                if (importExclusion != null)
                {
                    // Update to the new primary provider ID
                    var newProviderId = remote.HardcoverAuthorId ??
                                       remote.GoodreadsAuthorId?.ToString() ??
                                       remote.OpenLibraryAuthorId ??
                                       remote.GoogleBooksAuthorId;
                    if (!string.IsNullOrEmpty(newProviderId))
                    {
                        importExclusion.ForeignId = newProviderId;
                        _importListExclusionService.Update(importExclusion);
                    }
                }
            }

            // Do the standard update
            UpdateEntity(local, remote);

            // We know we need to update tags as author id has changed
            return UpdateResult.UpdateTags;
        }

        protected override UpdateResult MergeEntity(Author local, Author target, Author remote)
        {
            _logger.Warn($"Author {local} was replaced with {remote} because the original was a duplicate.");

            // Update list exclusions when merging authors
            var localProviderIds = new List<string>();
            if (!string.IsNullOrEmpty(local.HardcoverAuthorId)) localProviderIds.Add(local.HardcoverAuthorId);
            if (!string.IsNullOrEmpty(local.GoodreadsAuthorId)) localProviderIds.Add(local.GoodreadsAuthorId);
            if (!string.IsNullOrEmpty(local.OpenLibraryAuthorId)) localProviderIds.Add(local.OpenLibraryAuthorId);
            if (!string.IsNullOrEmpty(local.GoogleBooksAuthorId)) localProviderIds.Add(local.GoogleBooksAuthorId);

            foreach (var providerId in localProviderIds)
            {
                var importExclusionLocal = _importListExclusionService.FindByForeignId(providerId);
                if (importExclusionLocal != null)
                {
                    // Check if target already has an exclusion
                    var targetProviderIds = new List<string>();
                    if (!string.IsNullOrEmpty(target.HardcoverAuthorId)) targetProviderIds.Add(target.HardcoverAuthorId);
                    if (!string.IsNullOrEmpty(target.GoodreadsAuthorId)) targetProviderIds.Add(target.GoodreadsAuthorId);
                    if (!string.IsNullOrEmpty(target.OpenLibraryAuthorId)) targetProviderIds.Add(target.OpenLibraryAuthorId);
                    if (!string.IsNullOrEmpty(target.GoogleBooksAuthorId)) targetProviderIds.Add(target.GoogleBooksAuthorId);

                    var hasTargetExclusion = false;
                    foreach (var targetId in targetProviderIds)
                    {
                        if (_importListExclusionService.FindByForeignId(targetId) != null)
                        {
                            hasTargetExclusion = true;
                            break;
                        }
                    }

                    if (!hasTargetExclusion)
                    {
                        // Update to the remote's primary provider ID
                        var newProviderId = remote.HardcoverAuthorId ??
                                           remote.GoodreadsAuthorId?.ToString() ??
                                           remote.OpenLibraryAuthorId ??
                                           remote.GoogleBooksAuthorId;
                        if (!string.IsNullOrEmpty(newProviderId))
                        {
                            importExclusionLocal.ForeignId = newProviderId;
                            _importListExclusionService.Update(importExclusionLocal);
                        }
                    }
                }
            }

            // move any books over to the new author and remove the local author
            var books = _bookService.GetBooksByAuthor(local.Id);
            _bookService.ReassignAuthor(books, target);
            _authorService.DeleteAuthor(local.Id, false);

            // Update history entries to new id
            var items = _historyService.GetByAuthor(local.Id, null);
            items.ForEach(x => x.AuthorId = target.Id);
            _historyService.UpdateMany(items);

            // We know we need to update tags as author id has changed
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

                protected override List<Book> GetRemoteChildren(Author local, Author remote)
                {
                    _authorRefreshRehomeBlueprint = remote?.Books?
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
                        logContext: "GetRemoteChildren");
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
                    string logContext)
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
                        ebookMetadataProfileIdOverride);

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
                        logger?.Error("[REMOTE-BOOK-DATA] {0}: skipping remote book '{1}' for author '{2}' because it has no editions. MediaType={3}, ProviderIds=[{4}]",
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
                    int? ebookMetadataProfileIdOverride)
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

                        profileFiltered.AddRange(metadataProfileService.FilterBooks(authorForFilter, profileId.Value));
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

                        var fingerprint = BuildIdenticalPocketFingerprint(book);

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

                // Fingerprint = media type + the full set of stable work tokens + the full set of edition
                // identities (ForeignEditionId). Two pockets collapse ONLY when both are equal — i.e. the server
                // literally returned the same work with the same editions twice. Returns null (-> keep) for any
                // pocket we cannot fully fingerprint, so non-identical pockets are never merged or dropped.
                private static string BuildIdenticalPocketFingerprint(Book book)
                {
                    if (book?.Editions == null || book.Editions.Count == 0)
                    {
                        return null;
                    }

                    var editionIds = new List<string>(book.Editions.Count);
                    foreach (var edition in book.Editions)
                    {
                        if (edition == null || string.IsNullOrWhiteSpace(edition.ForeignEditionId))
                        {
                            return null;
                        }

                        editionIds.Add(edition.ForeignEditionId.Trim().ToLowerInvariant());
                    }

                    editionIds.Sort(StringComparer.Ordinal);

                    var workTokens = BookIdentity.GetStableWorkProviderIdentityTokens(book)
                        .Where(token => !string.IsNullOrWhiteSpace(token))
                        .Select(token => token.Trim().ToLowerInvariant())
                        .OrderBy(token => token, StringComparer.Ordinal);

                    return $"{book.MediaType}::W[{string.Join(",", workTokens)}]::E[{string.Join(",", editionIds)}]";
                }

                internal List<int> RepairOverMergedBookEditions(int authorId, List<Book> remoteBlueprint)
                {
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

                    var editionById = localEditions
                        .Where(e => e != null && e.Id > 0)
                        .GroupBy(e => e.Id)
                        .ToDictionary(g => g.Key, g => g.First());

                    var originalBookIdsWithManualEditions = localEditions
                        .Where(e => e != null && e.BookId > 0 && e.ManualAdd)
                        .Select(e => e.BookId)
                        .ToHashSet();

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

                    bool HasFilesForKey(Book book, string remoteKey)
                    {
                        return book != null &&
                               book.Id > 0 &&
                               fileCountsByBookIdAndRemoteKey.TryGetValue(book.Id, out var counts) &&
                               counts.TryGetValue(remoteKey, out var count) &&
                               count > 0;
                    }

                    var editionsToMove = new List<Edition>();
                    var editionsToDelete = new List<Edition>();
                    var filesToUpdate = new List<BookFile>();
                    var repairedSourceBookIds = new HashSet<int>();

                    Book FindTargetBook(string remoteKey, Book sourceBook)
                    {
                        Book targetBook = null;

                        if (localBooksByRemoteKey.TryGetValue(remoteKey, out var candidates))
                        {
                            var filtered = candidates.Where(b => b.Id != sourceBook.Id).ToList();
                            if (filtered.Any())
                            {
                                targetBook = filtered
                                    .OrderByDescending(b => HasFilesForKey(b, remoteKey))
                                    .ThenByDescending(b => b.AddOptions?.AddType == BookAddType.Manual)
                                    .ThenBy(b => b.Id)
                                    .First();
                            }
                        }

                        if (targetBook == null && remoteBookByKey.TryGetValue(remoteKey, out var remoteBook))
                        {
                            var fallback = BookIdentity.FindWorkFirstMatches(
                                localBooks
                                    .Where(b => b != null && b.Id > 0 && b.Id != sourceBook.Id)
                                    .Where(b => b.AddOptions?.AddType != BookAddType.Manual)
                                    .Where(b => b.MediaType == remoteBook.MediaType),
                                remoteBook);

                            if (fallback.Any())
                            {
                                targetBook = fallback
                                    .OrderByDescending(b => HasFilesForKey(b, remoteKey))
                                    .ThenBy(b => b.Id)
                                    .First();
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

                                    filesToUpdate.AddRange(filesForEdition);
                                    editionsToDelete.Add(edition);
                                    _logger.Info("[UNMERGE] Repointing {0} file(s) from duplicate edition '{1}' (ID={2}) to existing edition '{3}' (ID={4}).",
                                        filesForEdition.Count,
                                        edition.Title ?? edition.ForeignEditionId ?? "Unknown",
                                        edition.Id,
                                        equivalentTargetEdition.Title ?? equivalentTargetEdition.ForeignEditionId ?? "Unknown",
                                        equivalentTargetEdition.Id);
                                    continue;
                                }

                                edition.BookId = targetBook.Id;
                                edition.Book = targetBook;
                                editionsToMove.Add(edition);
                            }
                        }
                    }

                    var dedupedFiles = filesToUpdate
                        .Where(f => f != null && f.Id > 0)
                        .GroupBy(f => f.Id)
                        .Select(g => g.First())
                        .ToList();
                    if (dedupedFiles.Any())
                    {
                        _mediaFileService.Update(dedupedFiles);
                    }

                    var dedupedDeletes = editionsToDelete
                        .Where(e => e != null && e.Id > 0)
                        .GroupBy(e => e.Id)
                        .Select(g => g.First())
                        .ToList();
                    if (dedupedDeletes.Any())
                    {
                        _editionService.DeleteMany(dedupedDeletes);
                    }

                    var dedupedMoves = editionsToMove
                        .Where(e => e != null && e.Id > 0)
                        .GroupBy(e => e.Id)
                        .Select(g => g.First())
                        .ToList();
                    if (dedupedMoves.Any())
                    {
                        _editionService.UpdateMany(dedupedMoves);
                    }

                    var deletedEditionIds = dedupedDeletes
                        .Select(e => e.Id)
                        .ToHashSet();

                    int GetCurrentFileBookId(BookFile file)
                    {
                        if (file == null || file.EditionId <= 0)
                        {
                            return 0;
                        }

                        if (file.Edition != null && file.Edition.Id == file.EditionId)
                        {
                            return file.Edition.BookId;
                        }

                        if (editionById.TryGetValue(file.EditionId, out var edition))
                        {
                            return edition.BookId;
                        }

                        return 0;
                    }

                    var remainingEditionCountsByBookId = localEditions
                        .Where(e => e != null && e.Id > 0 && !deletedEditionIds.Contains(e.Id))
                        .GroupBy(e => e.BookId)
                        .ToDictionary(g => g.Key, g => g.Count());

                    var remainingFileCountsByBookId = files
                        .Where(f => f != null && f.Id > 0)
                        .Select(GetCurrentFileBookId)
                        .Where(bookId => bookId > 0)
                        .GroupBy(bookId => bookId)
                        .ToDictionary(g => g.Key, g => g.Count());

                    foreach (var sourceBookId in repairedSourceBookIds.OrderBy(id => id))
                    {
                        if (!localBookById.TryGetValue(sourceBookId, out var sourceBook))
                        {
                            continue;
                        }

                        if (HasLocalBookInstancePreservationMarker(sourceBook) || originalBookIdsWithManualEditions.Contains(sourceBookId))
                        {
                            continue;
                        }

                        remainingEditionCountsByBookId.TryGetValue(sourceBookId, out var remainingEditionCount);
                        remainingFileCountsByBookId.TryGetValue(sourceBookId, out var remainingFileCount);

                        if (remainingEditionCount > 0 || remainingFileCount > 0)
                        {
                            continue;
                        }

                        _logger.Info("[UNMERGE] Deleting empty source book '{0}' (ID={1}) after re-home repair; no editions or files remain.",
                            sourceBook.Title,
                            sourceBook.Id);
                        _bookService.DeleteBook(sourceBook.Id, false);
                        deletedSourceBookIds.Add(sourceBook.Id);
                    }

                    return deletedSourceBookIds;
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

            protected override List<Book> GetLocalChildren(Author entity, List<Book> remoteChildren)
            {
                var books = _bookService.GetBooksForRefresh(entity.Id, foreignIds: null);
                HydrateLocalChildrenForRefresh(entity, books);
                return books;
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

        private bool HasLocalBookInstancePreservationMarker(Book book)
        {
            if (book == null)
            {
                return true;
            }

            if (book.AddOptions?.AddType == BookAddType.Manual)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(book.UnitKeyHash))
            {
                // UnitKeyHash is the durable identity of an intentional multi-copy row. Refresh
                // may update it from the server pocket, but must never merge it away as a duplicate.
                return true;
            }

            if (!book.AnyEditionOk)
            {
                return true;
            }

            return book.Editions?.Any(edition => edition?.ManualAdd == true) == true;
        }

        private bool ShouldPreserveLocalBookInstance(Book book)
        {
            return HasLocalBookInstancePreservationMarker(book) || HasKnownFiles(book);
        }

        private BookRefreshMatchingIndex GetBookRefreshMatchingIndex(List<Book> existingChildren)
        {
            if (_bookRefreshMatchingIndex?.Source == existingChildren)
            {
                return _bookRefreshMatchingIndex;
            }

            _bookRefreshMatchingIndex = BookRefreshMatchingIndex.Build(existingChildren, _logger);
            return _bookRefreshMatchingIndex;
        }

        private sealed class BookRefreshMatchingIndex
        {
            private readonly Dictionary<string, List<Book>> _booksByProviderToken;
            private readonly Dictionary<int, int> _sourceOrderByBookId;
            private readonly HashSet<int> _activeBookIds;

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

            public static BookRefreshMatchingIndex Build(List<Book> existingChildren, Logger logger)
            {
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

                logger?.Debug("[AUTHOR-REFRESH-TIMING] Built book provider lookup for {0} local books with {1} provider tokens",
                    source.Count,
                    booksByProviderToken.Count);

                return new BookRefreshMatchingIndex(source, booksByProviderToken, sourceOrderByBookId, activeBookIds);
            }

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

            public void Consume(Book existingChild, IEnumerable<Book> mergedChildren)
            {
                if (existingChild?.Id > 0)
                {
                    _activeBookIds.Remove(existingChild.Id);
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

        protected override Tuple<Book, List<Book>> GetMatchingExistingChildren(List<Book> existingChildren, Book remote)
        {
            // Match work-first. Shared edition aliases (ASIN, GB, providerIdsAll, etc.) are not enough to merge
            // distinct remote works; edition fallback is only used when there is no stable work identity.
            var matchingIndex = GetBookRefreshMatchingIndex(existingChildren);
            var candidates = matchingIndex
                .GetCandidates(remote)
                .Where(x => x.MediaType == remote.MediaType)
                .ToList();

            var matchResult = BookIdentity.FindWorkFirstMatchResult(candidates, remote);
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

            // Multiple local matches for a single remote book can happen due to legacy imports and/or upstream ID changes.
            // Prefer the canonical (non-wanted-narrator) copy and the pinned copy (has files / manual) so that the
            // remote refresh updates the intended in-library book, and mark only truly-unwanted duplicates for deletion.
            if (matchingBooks.Any())
            {
                existingChild = matchingBooks
                    .OrderByDescending(HasKnownFiles)
                    .ThenBy(b => b.AddOptions?.AddType == BookAddType.Manual)
                    .ThenByDescending(ProviderScore)
                    .ThenBy(b => b.Id)
                    .First();

                foreach (var dup in matchingBooks.Where(b => b.Id != existingChild.Id))
                {
                    if (ShouldPreserveLocalBookInstance(dup))
                    {
                        continue;
                    }

                    mergeChildren.Add(dup);
                }

                matchingIndex.Consume(existingChild, mergeChildren);
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
            // Use MonitorExisting for new books - these are existing books in metadata, not future releases
            // Convert tri-state int? to bool for default monitoring: 0/2/NULL=false, 1=true
            // "Selected" mode (2) should not auto-monitor new books; selections are persisted per-book.
            child.AudiobookMonitored = (entity.AudiobookMonitorExisting ?? 0) == 1;
            child.EbookMonitored = (entity.EbookMonitorExisting ?? 0) == 1;
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

            var useRootFolderMonitoringFallback = !entity.AudiobookMonitorExisting.HasValue && !entity.EbookMonitorExisting.HasValue;
            bool? fallbackAudioMonitor = null;
            bool? fallbackEbookMonitor = null;

            if (useRootFolderMonitoringFallback)
            {
                var rootFolder = _rootFolderService.GetBestRootFolder(entity.Path);
                if (rootFolder != null)
                {
                    var audiobookSettings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, BookMediaType.Audiobook);
                    var ebookSettings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, BookMediaType.Ebook);

                    fallbackAudioMonitor = audiobookSettings.IsConfigured ? (audiobookSettings.MonitorExisting ?? 0) == 1 : false;
                    fallbackEbookMonitor = ebookSettings.IsConfigured ? (ebookSettings.MonitorExisting ?? 0) == 1 : false;

                    _logger.Debug("[MONITORING-REFRESH] Using resolver settings for {0} new books under author '{1}' - audiobook: {2}, ebook: {3}",
                        children.Added.Count,
                        entity.Name,
                        fallbackAudioMonitor,
                        fallbackEbookMonitor);
                }
                else
                {
                    fallbackAudioMonitor = false;
                    fallbackEbookMonitor = false;
                    _logger.Debug("[MONITORING-REFRESH] No root folder found for author '{0}', defaulting {1} new books to unmonitored",
                        entity.Name,
                        children.Added.Count);
                }
            }

            foreach (var book in children.Added)
            {
                // Store original narrator for logging
                var originalNarrator = book.Narrator;

                // Set initial monitoring from author settings
                // For NEW books being added, use MonitorExisting (these are existing books in the metadata, not future releases)
                // Convert tri-state int? to bool for default monitoring: 0/2=false, 1=true
                var audioMonitor = (entity.AudiobookMonitorExisting ?? 0) == 1;
                var ebookMonitor = (entity.EbookMonitorExisting ?? 0) == 1;

                // If no specific settings, use root folder fallback via resolver
                if (useRootFolderMonitoringFallback)
                {
                    audioMonitor = fallbackAudioMonitor ?? false;
                    ebookMonitor = fallbackEbookMonitor ?? false;
                }

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

                    // Set monitored editions for the newly inserted books in one batch.
                    // This mirrors SetMonitored(..., false): exactly one monitored edition per book,
                    // non-selected editions lose ManualAdd, and the selected edition is not marked manual.
                    var editionsToUpdate = new List<Edition>();
                    foreach (var book in newBooks)
                    {
                        var bookEditions = book.Editions?
                            .Where(e => e != null)
                            .ToList() ?? new List<Edition>();
                        var monitoredEdition = bookEditions
                            .Where(e => e.Monitored)
                            .OrderBy(e => e.Id)
                            .FirstOrDefault()
                            ?? _editionSelector.SelectBestEdition(bookEditions, book.MediaType);
                        if (monitoredEdition == null)
                        {
                            continue;
                        }

                        book.ForeignEditionId = monitoredEdition.ForeignEditionId;
                        foreach (var edition in bookEditions)
                        {
                            if (ReferenceEquals(edition, monitoredEdition) || (edition.Id > 0 && edition.Id == monitoredEdition.Id))
                            {
                                edition.Monitored = true;
                            }
                            else
                            {
                                edition.Monitored = false;
                                edition.ManualAdd = false;
                            }
                        }

                        editionsToUpdate.AddRange(bookEditions);
                    }

                    if (editionsToUpdate.Any())
                    {
                        _editionService.UpdateMany(editionsToUpdate);
                    }
                }

            protected override bool RefreshChildren(SortedChildren localChildren, List<Book> remoteChildren, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
            {
                var deletedSourceIds = new HashSet<int>();

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

                        _logger.Info("Deleting duplicate book {0} (ID: {1}) merged into {2} (ID: {3})",
                            source.Title, source.Id, target.Title, target.Id);

                        // DeleteBook is idempotent (double-delete safe).
                        _bookService.DeleteBook(source.Id, false);
                        deletedSourceIds.Add(source.Id);
                    }
                }

                // IMPORTANT: Do not send merged (duplicate) rows through RefreshBookService, otherwise they'll
                // re-match against the same remote book and never get pruned.
                var booksToRefresh = localChildren.UpToDate
                    .Concat(localChildren.Added)
                    .Concat(localChildren.Updated)
                    .Concat(localChildren.Deleted)
                    .ToList();

                // Repair upstream un-merges safely: if a local, file-attached book currently owns editions that now
                // belong to multiple remote works, re-parent editions (and therefore files) to the correct local book
                // rows using the remote metadata snapshot. This preserves local state and avoids unlink+delete+rescan churn.
                try
                {
                    var authorId = booksToRefresh.FirstOrDefault(b => b?.AuthorId > 0)?.AuthorId ?? 0;
                    var rehomeBlueprint = _authorRefreshRehomeBlueprint?.Any() == true
                        ? _authorRefreshRehomeBlueprint
                        : remoteChildren;
                    foreach (var deletedBookId in RepairOverMergedBookEditions(authorId, rehomeBlueprint))
                    {
                        deletedSourceIds.Add(deletedBookId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[UNMERGE] Failed to repair over-merged editions during author refresh");
                }
                finally
                {
                    _authorRefreshRehomeBlueprint = null;
                }

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

                return _refreshBookService.RefreshBookInfo(booksToRefresh, remoteChildren, remoteData, forceChildRefresh, forceUpdateFileTags, lastUpdate);
            }

        protected override void PublishEntityUpdatedEvent(Author entity)
        {
            _eventAggregator.PublishEvent(new AuthorUpdatedEvent(entity));
        }

        protected override void PublishRefreshCompleteEvent(Author entity)
        {
            _logger.Debug("[SERIES-DEBUG] PublishRefreshCompleteEvent called for author '{0}' (ID: {1})",
                entity.Name, entity.Id);
            _logger.Debug("[SERIES-DEBUG] Author has {0} series to refresh", entity.Series?.Count ?? 0);

                // IMPORTANT: We don't process series here anymore because books might not be committed to DB yet
                // Series processing is now handled in a separate event handler after books are guaranteed to be in the database

            _eventAggregator.PublishEvent(new AuthorRefreshCompleteEvent(entity));
        }

        protected override void PublishChildrenUpdatedEvent(Author entity, List<Book> newChildren, List<Book> updateChildren, List<Book> deleteChildren)
        {
            _eventAggregator.PublishEvent(new BookInfoRefreshedEvent(entity, newChildren, updateChildren, deleteChildren));
        }

        private void Rescan(List<int> authorIds, bool isNew, CommandTrigger trigger, bool infoUpdated, bool isFromImport = false, string mediaType = "all", bool rescanWhenMetadataUnchanged = false, bool scopeToSingleAuthor = false)
        {

            // The import process will handle a single comprehensive scan at the end
            if (isFromImport)
            {
                _logger.Debug("[IMPORT-RESCAN-SKIP] Skipping per-author rescan during import for {0} authors. A comprehensive scan will occur after all imports complete.", authorIds.Count);
                return;
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

                var rootFolders = _rootFolderService.All();
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
                    if (authorIds.Count != 1)
                    {
                        throw new InvalidOperationException("A single-author rescan scope requires exactly one author id.");
                    }

                    var author = _authorService.GetAuthor(authorIds[0]);
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
            var candidates = new List<string>();
            var includeAudiobooks = requestedMediaType != "ebook";
            var includeEbooks = requestedMediaType != "audiobook";

            if (includeAudiobooks && author.AudiobookPath.IsNotNullOrWhiteSpace())
            {
                candidates.Add(author.AudiobookPath);
            }

            if (includeEbooks && author.EbookPath.IsNotNullOrWhiteSpace())
            {
                candidates.Add(author.EbookPath);
            }

            if (author.AudiobookPath.IsNullOrWhiteSpace() &&
                author.EbookPath.IsNullOrWhiteSpace() &&
                author.Path.IsNotNullOrWhiteSpace())
            {
                candidates.Add(author.Path);
            }

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

        private bool ProcessBulkSync(List<int> authorIds, bool forceRefresh, CancellationToken cancellationToken)
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
                    var providerIds = GetAuthorDiffProviderIds(author);
                    if (providerIds.Count == 0)
                    {
                        _logger.Warn("[BULK-SYNC] Author {0} has no provider IDs, skipping", author.Name);
                        continue;
                    }

                    var etag = syncMetadataByAuthorId.TryGetValue(author.Id, out var syncMetadata)
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
                        var data = GetSkyhookData(localAuthor, forceRefresh: forceRefresh, expectedPublishedETag: change.ETag, bypassEtag: actionableChanges[i].BypassEtag, authorIdentifierOverride: refreshAuthorId);
                        if (data == null)
                        {
                            unchangedAuthors++;
                            _logger.Debug("[BULK-SYNC] Author {0} was not modified after candidate verification.", localAuthor.Name);
                            continue;
                        }

                        fetchedAuthors++;
                        var authorUpdated = RefreshEntityInfoIfRemoteDataAvailable(localAuthor, data, true, false, null);
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

        private bool CanAutoMergeAuthors(Author source, Author target, out string reason)
        {
            if (_mainDatabase == null)
            {
                reason = "main database access is unavailable";
                return false;
            }

            if (HasConflictingPath(source.Path, target.Path))
            {
                reason = "different library paths";
                return false;
            }

            if (HasConflictingPath(source.AudiobookRootFolderPath, target.AudiobookRootFolderPath))
            {
                reason = "different audiobook root folders";
                return false;
            }

            if (HasConflictingPath(source.EbookRootFolderPath, target.EbookRootFolderPath))
            {
                reason = "different ebook root folders";
                return false;
            }

            if (HasConflictingPath(source.AudiobookPath, target.AudiobookPath))
            {
                reason = "different discovered audiobook paths";
                return false;
            }

            if (HasConflictingPath(source.EbookPath, target.EbookPath))
            {
                reason = "different discovered ebook paths";
                return false;
            }

            if (HasConflict(source.AudiobookQualityProfileId, target.AudiobookQualityProfileId) ||
                HasConflict(source.EbookQualityProfileId, target.EbookQualityProfileId))
            {
                reason = "different quality profiles";
                return false;
            }

            if (HasConflict(source.AudiobookMetadataProfileId, target.AudiobookMetadataProfileId) ||
                HasConflict(source.EbookMetadataProfileId, target.EbookMetadataProfileId))
            {
                reason = "different metadata profiles";
                return false;
            }

            if (HasConflict(source.AudiobookMonitorExisting, target.AudiobookMonitorExisting) ||
                HasConflict(source.EbookMonitorExisting, target.EbookMonitorExisting) ||
                HasConflict(source.AudiobookMonitorFuture, target.AudiobookMonitorFuture) ||
                HasConflict(source.EbookMonitorFuture, target.EbookMonitorFuture) ||
                HasConflict(source.SyncMonitoredAcrossFormats, target.SyncMonitoredAcrossFormats))
            {
                reason = "different monitoring settings";
                return false;
            }

            reason = null;
            return true;
        }

        private bool TryMergeAuthorsWithoutDataLoss(Author source, Author target, string canonicalId, out string reason)
        {
            try
            {
                MergeIntoSurvivor(target, source, canonicalId);
                _authorService.UpdateAuthor(target);

                using var connection = _mainDatabase.OpenConnection();
                using var transaction = connection.BeginTransaction();

                ReassignAuthorId(connection, transaction, "Books", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "AuthorSeries", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "MetadataFiles", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "OtherFiles", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "DownloadHistory", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "ExtraFiles", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "Blacklist", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "History", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "PendingReleases", source.Id, target.Id);
                ReassignAuthorId(connection, transaction, "author_trigrams", source.Id, target.Id);

                MergeSyncMetadata(connection, transaction, source.Id, target.Id, canonicalId);

                connection.Execute(
                    @"DELETE FROM ""Authors"" WHERE ""Id"" = @Id;",
                    new { source.Id },
                    transaction);

                transaction.Commit();
                _authorService.ClearAuthorCache();

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

        private void MergeIntoSurvivor(Author survivor, Author source, string canonicalId)
        {
            survivor.HardcoverAuthorId = PreferCanonicalProviderId(survivor.HardcoverAuthorId, source.HardcoverAuthorId, canonicalId, "hc");
            survivor.GoodreadsAuthorId ??= source.GoodreadsAuthorId;
            survivor.AudnexusAuthorId ??= source.AudnexusAuthorId;
            survivor.OpenLibraryAuthorId ??= source.OpenLibraryAuthorId;
            survivor.GoogleBooksAuthorId ??= source.GoogleBooksAuthorId;
            survivor.RemoteProviderIds = AuthorIdentity.MergeProviderIdentityTokens(survivor, source);

            survivor.AudiobookRootFolderPath ??= source.AudiobookRootFolderPath;
            survivor.EbookRootFolderPath ??= source.EbookRootFolderPath;
            survivor.AudiobookPath ??= source.AudiobookPath;
            survivor.EbookPath ??= source.EbookPath;

            survivor.AudiobookQualityProfileId ??= source.AudiobookQualityProfileId;
            survivor.EbookQualityProfileId ??= source.EbookQualityProfileId;
            survivor.AudiobookMetadataProfileId ??= source.AudiobookMetadataProfileId;
            survivor.EbookMetadataProfileId ??= source.EbookMetadataProfileId;

            survivor.AudiobookMonitorExisting ??= source.AudiobookMonitorExisting;
            survivor.AudiobookMonitorFuture ??= source.AudiobookMonitorFuture;
            survivor.EbookMonitorExisting ??= source.EbookMonitorExisting;
            survivor.EbookMonitorFuture ??= source.EbookMonitorFuture;
            survivor.SyncMonitoredAcrossFormats ??= source.SyncMonitoredAcrossFormats;

            survivor.AudiobookSettingsManuallyOverridden |= source.AudiobookSettingsManuallyOverridden;
            survivor.EbookSettingsManuallyOverridden |= source.EbookSettingsManuallyOverridden;
            survivor.Monitored |= source.Monitored;

            survivor.Tags = MergeTagSet(survivor.Tags, source.Tags);
            survivor.AudiobookTags = MergeTagSet(survivor.AudiobookTags, source.AudiobookTags);
            survivor.EbookTags = MergeTagSet(survivor.EbookTags, source.EbookTags);

            survivor.LastSelectedMediaType ??= source.LastSelectedMediaType;

            MoveAuthorImportExclusions(source, target: survivor, canonicalId);
        }

        private void MoveAuthorImportExclusions(Author source, Author target, string canonicalId)
        {
            var sourceProviderIds = EnumerateAuthorProviderIds(source).ToList();
            var targetProviderIds = new HashSet<string>(EnumerateAuthorProviderIds(target), StringComparer.OrdinalIgnoreCase);
            var destinationId = canonicalId ?? GetPreferredAuthorIdentifier(target);

            if (destinationId.IsNullOrWhiteSpace())
            {
                return;
            }

            foreach (var providerId in sourceProviderIds)
            {
                var exclusion = _importListExclusionService.FindByForeignId(providerId);
                if (exclusion == null)
                {
                    continue;
                }

                if (targetProviderIds.Any(targetId => _importListExclusionService.FindByForeignId(targetId) != null))
                {
                    _importListExclusionService.Delete(exclusion.Id);
                    continue;
                }

                exclusion.ForeignId = destinationId;
                _importListExclusionService.Update(exclusion);
            }
        }

        private void MergeSyncMetadata(IDbConnection connection, IDbTransaction transaction, int sourceAuthorId, int targetAuthorId, string canonicalId)
        {
            var sourceMetadata = _syncMetadataService.GetSyncMetadata(sourceAuthorId);
            var targetMetadata = _syncMetadataService.GetSyncMetadata(targetAuthorId);

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

            connection.Execute(
                @"DELETE FROM ""AuthorSyncMetadata"" WHERE ""Id"" = @Id;",
                new { sourceMetadata.Id },
                transaction);
        }

        private static void ReassignAuthorId(IDbConnection connection, IDbTransaction transaction, string table, int sourceAuthorId, int targetAuthorId)
        {
            if (sourceAuthorId <= 0 || targetAuthorId <= 0 || sourceAuthorId == targetAuthorId)
            {
                return;
            }

            connection.Execute(
                $@"UPDATE ""{table}""
                   SET ""AuthorId"" = @TargetAuthorId
                   WHERE ""AuthorId"" = @SourceAuthorId;",
                new { SourceAuthorId = sourceAuthorId, TargetAuthorId = targetAuthorId },
                transaction);
        }

        private static HashSet<int> MergeTagSet(HashSet<int> left, HashSet<int> right)
        {
            var merged = new HashSet<int>(left ?? new HashSet<int>());
            if (right != null)
            {
                merged.UnionWith(right);
            }

            return merged;
        }

        private static IEnumerable<string> EnumerateAuthorProviderIds(Author author)
        {
            return AuthorIdentity.GetProviderIdentityTokenList(author);
        }

        private static string PreferCanonicalProviderId(string survivorId, string sourceId, string canonicalId, string prefix)
        {
            var normalizedCanonical = ProviderIdHelper.Normalize(canonicalId, prefix);
            if (!string.IsNullOrWhiteSpace(normalizedCanonical))
            {
                return normalizedCanonical;
            }

            return survivorId ?? sourceId;
        }

        private static bool HasConflictingPath(string left, string right)
        {
            if (left.IsNullOrWhiteSpace() || right.IsNullOrWhiteSpace())
            {
                return false;
            }

            return !left.PathEquals(right);
        }

        private static bool HasConflict(int? left, int? right)
        {
            return left.HasValue && right.HasValue && left.Value != right.Value;
        }

        private static bool HasConflict(bool? left, bool? right)
        {
            return left.HasValue && right.HasValue && left.Value != right.Value;
        }

        private void RefreshSelectedAuthors(List<int> authorIds, bool isNew, CommandTrigger trigger, bool isFromImport = false, bool refreshMetadata = true, bool rescanFolders = true, string mediaType = "all", CancellationToken cancellationToken = default, bool rescanWhenMetadataUnchanged = false, bool forceRefresh = false, bool useBulkSyncWhenNotForced = false, bool scopeRescanToSingleAuthor = false)
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
                using var refreshGate = EnterAuthorMetadataRefreshGate($"selected author refresh ({authorIds.Count} author(s))", cancellationToken);

                // Bulk-selected/scheduled refreshes use the ETag diff path unless explicitly forced.
                if (useBulkSyncWhenNotForced && !forceRefresh)
                {
                    _logger.Debug("[BULK-SYNC] Using bulk sync endpoint for {0} authors", authorIds.Count);
                    updated = ProcessBulkSync(authorIds, forceRefresh: false, cancellationToken);
                }
                else
                {
                    // Local reconciliation path: fetch each published payload and compare it to local DB.
                    var authors = _authorService.GetAuthors(authorIds);
                    foreach (var author in authors)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
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

                            if (!forceRefresh)
                            {
                                diffChecked = TryGetSingleAuthorDiffHint(author, out expectedPublishedETag, out deletedOnServer, out refreshAuthorId, out bypassEtagForIdentityRepair);
                            }
                            else
                            {
                                _logger.Debug("[FLOW-DEBUG] ForceRefresh=true - bypassing local diff/ETag shortcut for author '{0}'", author.Name);
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

                            var data = GetSkyhookData(author, forceRefresh: forceRefresh, expectedPublishedETag: expectedPublishedETag, bypassEtag: forceRefresh || bypassEtagForIdentityRepair, authorIdentifierOverride: refreshAuthorId);

                            var refreshStopwatch = System.Diagnostics.Stopwatch.StartNew();
                            updated |= RefreshEntityInfoIfRemoteDataAvailable(author, data, true, false, null);
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

            // Only rescan folders if requested
            if (rescanFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.Debug("[FLOW-DEBUG] DECISION: Folder rescan requested; evaluating rescan rules.");
                _logger.Debug("[FLOW-DEBUG] ACTION: Calling Rescan() with metadataUpdated={0}", updated);
                Rescan(authorIds, isNew, trigger, updated, isFromImport, mediaType, rescanWhenMetadataUnchanged, scopeRescanToSingleAuthor);
            }
            else
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: Skipping folder rescan (rescanFolders=false)");
            }

            _logger.Debug("[FLOW-DEBUG] ========== RefreshSelectedAuthors END ==========");
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
