using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Data.Sqlite;
using Npgsql;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books.Services
{
    public interface IAuthorLibraryService
    {
        // The ONLY way to add data to the library
        Task<Author> AddAuthorAsync(string providerId, MonitoringConfig config = null);
        Task<Author> AddAuthorMonitoringBookAsync(string authorProviderId, string bookProviderId);
        Task<List<Author>> AddAuthorsMonitoringSeriesAsync(string[] authorProviderIds, string seriesProviderId);
        Task<Author> RefreshAuthorAsync(int authorId);
        Task RemoveAuthorAsync(int authorId);
        Task<UserSelectedEditionMaterialization> MaterializeUserSelectedEditionAsync(
            UserSelectedRemoteEdition selection,
            MonitoringConfig config) => throw new NotImplementedException();
    }

    public sealed class UserSelectedRemoteEdition
    {
        public string AuthorProviderId { get; init; }
        public string WorkProviderId { get; init; }
        public string EditionProviderId { get; init; }
        public BookMediaType MediaType { get; init; }
    }

    public sealed class UserSelectedEditionMaterialization
    {
        public Author Author { get; init; }
        public Book Book { get; init; }
        public Edition Edition { get; init; }
    }

    public class MonitoringConfig
    {
        // Flag to indicate this is a manual addition from modal (not disc scan)
        public bool IsManualAddition { get; set; } = false;

        // Author-side monitoring gates are intentionally separate from the policy for
        // newly discovered catalog rows. The per-side MonitorExistingMode is the
        // one-time action for the catalog being added; MonitorNewItems is the ongoing
        // policy for later rows.
        public bool? AudiobookMonitored { get; set; }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }
        public MonitorTypes? AudiobookMonitorExistingMode { get; set; }
        public bool? EbookMonitored { get; set; }
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }
        public MonitorTypes? EbookMonitorExistingMode { get; set; }

        public int? AudiobookQualityProfileId { get; set; }
        public int? EbookQualityProfileId { get; set; }
        public int? AudiobookMetadataProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }
        public string AudiobookRootFolderPath { get; set; }
        public string EbookRootFolderPath { get; set; }

        // FOLDER-PRESERVATION: Discovered author folder path to preserve existing structure
        public string DiscoveredAuthorFolderPath { get; set; }

        // Pending import support
        public bool QueueIfUnavailable { get; set; } = false;
        public bool IsFromQueue { get; set; } = false;  // Prevents re-queuing when processing from queue
        public bool CreateAudiobook { get; set; } = true;
        public bool CreateEbook { get; set; } = true;
        public List<string> AudiobookBooksToMonitor { get; set; }
        public List<string> EbookBooksToMonitor { get; set; }
        public List<string> AudiobookBooksToSearch { get; set; }
        public List<string> EbookBooksToSearch { get; set; }
        public HashSet<int> AudiobookTags { get; set; }
        public HashSet<int> EbookTags { get; set; }
        public HashSet<int> Tags { get; set; }
        public bool? SearchForMissingBooks { get; set; }
        public string RequestedBy { get; set; }
        public string AuthorName { get; set; }
        
        // Specific book monitoring context (for "None, except this one" or series monitoring)
        public MonitorTypes? MonitorMode { get; set; }
        public HashSet<string> SpecificBookProviderIds { get; set; }  // e.g., ["hc:495645", "hc:495646"]
        public BookMediaType? SpecificBookMediaType { get; set; }

        public void MergeTagsForMediaType(BookMediaType mediaType, IEnumerable<int> tags)
        {
            if (tags == null)
            {
                return;
            }

            var values = mediaType == BookMediaType.Audiobook
                ? AudiobookTags ?? new HashSet<int>()
                : EbookTags ?? new HashSet<int>();
            values.UnionWith(tags);

            if (mediaType == BookMediaType.Audiobook)
            {
                AudiobookTags = values;
            }
            else
            {
                EbookTags = values;
            }
        }
    }

    public class AuthorLibraryService : IAuthorLibraryService
    {
        // Network fetch can be parallel; SQLite writes cannot. Split the two.
        private static readonly SemaphoreSlim AuthorFetchGate = new SemaphoreSlim(12, 12);
        private static readonly SemaphoreSlim SqliteWriteGate = new SemaphoreSlim(1, 1);

        private readonly IAuthorService _authorService;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IBookService _bookService;
        private readonly IRefreshSeriesService _refreshSeriesService;
        private readonly IEditionService _editionService;
        private readonly INarratorLinkService _narratorLinkService;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IBuildAuthorPaths _authorPathBuilder;
        private readonly IRootFolderService _rootFolderService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly IPendingAuthorImportService _pendingImportService;
        private readonly IMainDatabase _mainDatabase;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly IAuthorSyncMetadataService _syncMetadataService;
        private readonly IEditionSelector _editionSelector;
        private readonly IEditionMetadataProfileFilter _editionMetadataProfileFilter;
        private readonly IRefreshAuthorService _refreshAuthorService;
        private readonly Logger _logger;

        public AuthorLibraryService(
            IAuthorService authorService,
            IProvideAuthorInfo authorInfo,
            IBookService bookService,
            IRefreshSeriesService refreshSeriesService,
            IEditionService editionService,
            INarratorLinkService narratorLinkService,
            IMetadataProfileService metadataProfileService,
            IQualityProfileService qualityProfileService,
            IBuildAuthorPaths authorPathBuilder,
            IRootFolderService rootFolderService,
            IManageCommandQueue commandQueueManager,
            IEventAggregator eventAggregator,
            IPendingAuthorImportService pendingImportService,
            IMainDatabase mainDatabase,
            IImportListExclusionService importListExclusionService,
            IEditionMetadataProfileFilter editionMetadataProfileFilter,
            IAuthorSyncMetadataService syncMetadataService,
            Logger logger,
            IEditionSelector editionSelector = null,
            IRefreshAuthorService refreshAuthorService = null)
        {
            _authorService = authorService;
            _authorInfo = authorInfo;
            _bookService = bookService;
            _refreshSeriesService = refreshSeriesService;
            _editionService = editionService;
            _narratorLinkService = narratorLinkService;
            _metadataProfileService = metadataProfileService;
            _qualityProfileService = qualityProfileService;
            _authorPathBuilder = authorPathBuilder;
            _rootFolderService = rootFolderService;
            // Removed EditionSelectionService dependency (ParsedTrackInfo path)
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
            _pendingImportService = pendingImportService;
            _mainDatabase = mainDatabase;
            _importListExclusionService = importListExclusionService;
            _syncMetadataService = syncMetadataService;
            _editionMetadataProfileFilter = editionMetadataProfileFilter;
            _refreshAuthorService = refreshAuthorService;
            _logger = logger;
            _editionSelector = editionSelector ?? new EditionSelector(logger);
        }

        private async Task<Author> FetchAuthorBlobAsync(string providerId)
        {
            await AuthorFetchGate.WaitAsync();
            try
            {
                if (_authorInfo is IProvideAuthorInfoAsync asyncAuthorInfo)
                {
                    return await asyncAuthorInfo.GetAuthorInfoAsync(providerId, false);
                }

                return _authorInfo.GetAuthorInfo(providerId, false);
            }
            finally
            {
                AuthorFetchGate.Release();
            }
        }

        public async Task<Author> AddAuthorAsync(string providerId, MonitoringConfig config = null)
        {
            _logger.Debug("AddAuthorAsync called with provider ID: {0}", providerId);
            var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Materialize the selected root's fill-only defaults before the remote
            // fetch. A not-ready response can enqueue this same config, so the
            // immediate and pending paths must carry identical settings.
            if (config != null)
            {
                NormalizeMonitoringConfigFromExplicitRootFolders(config);
            }

            Author author;

            try
            {
                // Fetch author info from metadata server
                _logger.Debug("Calling GetAuthorInfo with provider ID: {0}", providerId);
                var apiStopwatch = System.Diagnostics.Stopwatch.StartNew();
                author = await FetchAuthorBlobAsync(providerId);
                apiStopwatch.Stop();
                _logger.Debug("GetAuthorInfo returned author: {0} (ID: {1}) in {2}ms", 
                    author?.Name, author?.Id, apiStopwatch.ElapsedMilliseconds);
                _logger.Debug("[DB-TIMING] API fetch for author '{0}' took {1}ms, returned {2} books and {3} total editions",
                    author?.Name, apiStopwatch.ElapsedMilliseconds, 
                    author?.Books?.Count ?? 0,
                    author?.Books?.Sum(b => b.Editions?.Count ?? 0) ?? 0);
            }
            catch (AuthorNotFoundException)
            {
                // Don't re-queue if we're already processing from the queue
                if (config?.IsFromQueue == true)
                {
                    _logger.Debug("Author {0} not found while processing from queue, not re-queuing", providerId);
                    throw;
                }

                // Queue if configured to do so
                if (config?.QueueIfUnavailable == true)
                {
                    _logger.Info("Author {0} not available on metadata server, queuing for later import", providerId);

                        var pendingId = await _pendingImportService.EnqueueAsync(
                            providerId,
                            config,
                            config.RequestedBy ?? "UserInterface"
                        );

                            // EnqueueAsync should return the pending import ID when queued (or when an existing pending record exists).
                            // If it returns 0, the author may have been added locally between checks, or the queue was skipped.
                            if (pendingId <= 0)
                            {
                                // Author exists locally (e.g., metadata server is behind): treat as a no-op add and return it.
                                try
                                {
                                    var colonIndex = providerId?.IndexOf(':') ?? -1;
                                    if (colonIndex > 0)
                                    {
                                        var prefix = providerId.Substring(0, colonIndex).ToLowerInvariant();
                                        var rawId = providerId.Substring(colonIndex + 1);
                                        var existingAuthor = _authorService.FindByProviderId(prefix, rawId);
                                        if (existingAuthor != null)
                                        {
                                            _logger.Info("Author {0} already exists locally (ID: {1}); skipping pending queue", providerId, existingAuthor.Id);
                                            return existingAuthor;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn(ex, "Failed to re-check local author existence for {0} after enqueue returned 0", providerId);
                                }

                                var existingPending = _pendingImportService.GetByProviderId(providerId);
                                if (existingPending != null &&
                                    (existingPending.OverallStatus == PendingImportStatus.Pending ||
                                     existingPending.OverallStatus == PendingImportStatus.InProgress ||
                                 existingPending.OverallStatus == PendingImportStatus.Retrying))
                        {
                            pendingId = existingPending.Id;
                        }
                    }

                    if (pendingId <= 0)
                    {
                        throw new InvalidOperationException($"Unable to queue author {providerId} for pending import");
                    }

                    // Return marker to indicate pending (negative ID convention)
                    return new Author
                    {
                        Id = -pendingId,
                        Name = config.AuthorName ?? "Pending Import",
                        Status = AuthorStatusType.Continuing,
                        Path = "pending",
                        CleanName = config.AuthorName?.CleanAuthorName() ?? "pending_import"
                    };
                }

                // If not configured to queue, re-throw the exception
                throw;
            }

            // CRITICAL: Verify dual-record creation is happening
            _logger.Debug("[DUAL-RECORD] Author '{0}' fetched with {1} books from API",
                author.Name, author.Books?.Count ?? 0);

            if (config != null)
            {
                ApplyMonitoringConfig(author, config);
            }

            // Feed the full server catalog into the canonical add/refresh normalizer. CreateAudiobook/CreateEbook
            // decide which per-media settings are applied; metadata profiles decide which configured rows survive.
            var remoteBooks = author.Books?.Where(b => b != null).ToList() ?? new List<Book>();
            var remoteSeries = author.Series?.Where(s => s != null).ToList() ?? new List<Series>();

            SemaphoreSlim writeGate = null;
            if (_mainDatabase != null && _mainDatabase.DatabaseType == DatabaseType.SQLite)
            {
                writeGate = SqliteWriteGate;
                await writeGate.WaitAsync();
            }

            try
            {
                // Provider IDs are identity. Display names are not: distinct authors can share one.
                var existingAuthor = FindExistingAuthor(providerId, author);
                if (existingAuthor != null)
                {
                    var handledAuthor = await HandleExistingAuthorAsync(existingAuthor, author, config);
                    return await QueueMissingRequestedWorks(handledAuthor, providerId, config);
                }

                var usesDiscoveredPath = !string.IsNullOrWhiteSpace(config?.DiscoveredAuthorFolderPath) &&
                                         new[] { author.Path, author.AudiobookPath, author.EbookPath }
                                             .Any(path => path.PathEquals(config.DiscoveredAuthorFolderPath));
                if (!usesDiscoveredPath)
                {
                    DisambiguateGeneratedAuthorPaths(author);
                }

                // Preserve NULL as "this media side was not part of the add" and an
                // empty set as the caller's explicit "no tags" choice. The legacy
                // shared field is only a fallback for sides this request creates.
                if (config != null)
                {
                    author.AudiobookTags = config.CreateAudiobook
                        ? CloneTags(config.AudiobookTags ?? config.Tags)
                        : null;
                    author.EbookTags = config.CreateEbook
                        ? CloneTags(config.EbookTags ?? config.Tags)
                        : null;

                    author.Tags = (author.AudiobookTags ?? new HashSet<int>()).Concat(author.EbookTags ?? new HashSet<int>()).ToHashSet();
                }

                // Add author
                author.Added = DateTime.UtcNow;
                author.CleanName = author.Name.CleanAuthorName();
                // IMPORTANT: Set doRefresh=false to prevent scan loop when called from ImportOrchestrator
                var authorInsertStopwatch = System.Diagnostics.Stopwatch.StartNew();
                Author addedAuthor;
                try
                {
                    addedAuthor = _authorService.AddAuthor(author, false);
                }
                catch (SqliteException ex) when (IsAuthorProviderIdUniqueViolation(ex))
                {
                    _logger.Warn(ex, "[AUTHOR-DEDUP] Unique provider ID constraint hit while inserting author '{0}'. Reloading existing author row.", author.Name);
                    var existingAfterConflict = FindExistingAuthor(providerId, author);
                    if (existingAfterConflict != null)
                    {
                        var handledAuthor = await HandleExistingAuthorAsync(existingAfterConflict, author, config);
                        return await QueueMissingRequestedWorks(handledAuthor, providerId, config);
                    }

                    throw;
                }
                catch (PostgresException ex) when (IsAuthorProviderIdUniqueViolation(ex))
                {
                    _logger.Warn(ex, "[AUTHOR-DEDUP] Unique provider ID constraint hit while inserting author '{0}'. Reloading existing author row.", author.Name);
                    var existingAfterConflict = FindExistingAuthor(providerId, author);
                    if (existingAfterConflict != null)
                    {
                        var handledAuthor = await HandleExistingAuthorAsync(existingAfterConflict, author, config);
                        return await QueueMissingRequestedWorks(handledAuthor, providerId, config);
                    }

                    throw;
                }
                authorInsertStopwatch.Stop();
                _logger.Debug("[DB-TIMING] Author '{0}' database insertion took {1}ms",
                    addedAuthor.Name, authorInsertStopwatch.ElapsedMilliseconds);
                SeedAuthorSyncMetadata(addedAuthor, author, providerId);

                        try
                        {
                            if (config != null && !config.IsManualAddition)
                            {
                        var commandId = ProgressMessageContext.CommandModel?.Id;
                        if (commandId.HasValue)
                        {
                            ImportSessionProgressTracker.Activate(commandId.Value);
                            ImportSessionProgressTracker.MarkAuthorImported(commandId.Value, addedAuthor.Id);
                        }
                            }
                        }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[IMPORT-TRACKER] Failed to record imported author for progress tracking: {0}", addedAuthor.Name);
                }

                // Process books and series
                if (remoteBooks.Any())
                {
                    var booksProcessingStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    await ProcessBooksForAuthor(addedAuthor, remoteBooks, config, remoteSeries);
                    booksProcessingStopwatch.Stop();
                    _logger.Debug("[DB-TIMING] Processing books/editions for author '{0}' took {1}ms total",
                        addedAuthor.Name, booksProcessingStopwatch.ElapsedMilliseconds);
                }

                if (remoteSeries.Any())
                {
                    await ProcessSeriesForAuthor(addedAuthor, remoteSeries, config);
                }

                // SCAN-LOOP-FIX: Do NOT trigger a FULL refresh here!
                // When called from ImportOrchestrator during a folder scan, triggering a full refresh
                // would cause AuthorAddedHandler to start another folder scan, which could find
                // the same file that's still being processed, creating an infinite loop.
                // However, we DO need to fire events so books appear in the GUI.

                // Fire AuthorRefreshCompleteEvent directly to update GUI
                // This bypasses the refresh command entirely, avoiding any scan loops
                _eventAggregator.PublishEvent(new AuthorRefreshCompleteEvent(addedAuthor));

                overallStopwatch.Stop();
                _logger.Debug("[DB-TIMING] COMPLETE AddAuthorAsync for '{0}' took {1}ms total ({2} books, {3} editions)",
                    addedAuthor.Name, overallStopwatch.ElapsedMilliseconds,
                    author?.Books?.Count ?? 0,
                    author?.Books?.Sum(b => b.Editions?.Count ?? 0) ?? 0);

                // Special logging for large authors
                var totalEditions = author?.Books?.Sum(b => b.Editions?.Count ?? 0) ?? 0;
                if (totalEditions > 100)
                {
                    _logger.Debug("[DB-TIMING][LARGE-AUTHOR] '{0}' with {1} editions completed in {2}ms ({3}ms per edition avg)",
                        addedAuthor.Name, totalEditions, overallStopwatch.ElapsedMilliseconds,
                        overallStopwatch.ElapsedMilliseconds / totalEditions);
                }

                return await QueueMissingRequestedWorks(addedAuthor, providerId, config);
            }
            finally
            {
                writeGate?.Release();
            }
        }

        private async Task<Author> QueueMissingRequestedWorks(Author author, string authorProviderId, MonitoringConfig config)
        {
            if (author == null || author.Id <= 0 || config?.QueueIfUnavailable != true || config.IsFromQueue)
            {
                return author;
            }

            var targets = new[]
            {
                (BookMediaType.Audiobook, config.AudiobookBooksToMonitor),
                (BookMediaType.Audiobook, config.AudiobookBooksToSearch),
                (BookMediaType.Ebook, config.EbookBooksToMonitor),
                (BookMediaType.Ebook, config.EbookBooksToSearch)
            }
                .SelectMany(entry => (entry.Item2 ?? new List<string>())
                    .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
                    .Select(providerId => (MediaType: entry.Item1, ProviderId: providerId.Trim())))
                .Distinct()
                .ToList();

            if (!targets.Any())
            {
                return author;
            }

            var missing = targets.Where(target => !RequestedWorkExists(author.Id, target.ProviderId, target.MediaType)).ToList();
            if (!missing.Any())
            {
                return author;
            }

            _logger.Info(
                "Author {0} is available, but requested work(s) [{1}] are not yet present in its authoritative catalog; retaining the request",
                authorProviderId,
                string.Join(",", missing.Select(target => target.ProviderId)));

            var pendingId = await _pendingImportService.EnqueueAsync(
                authorProviderId,
                config,
                config.RequestedBy ?? "UserInterface");
            if (pendingId <= 0)
            {
                throw new InvalidOperationException($"Unable to retain requested work(s) for author {authorProviderId}.");
            }

            return new Author
            {
                Id = -pendingId,
                Name = config.AuthorName ?? author.Name ?? "Pending Import",
                Status = AuthorStatusType.Continuing,
                Path = "pending",
                CleanName = (config.AuthorName ?? author.Name)?.CleanAuthorName() ?? "pending_import"
            };
        }

        private bool RequestedWorkExists(int authorId, string workProviderId, BookMediaType mediaType)
        {
            if (!ProviderIdHelper.TryNormalize(workProviderId, defaultPrefix: null, out var normalized))
            {
                return false;
            }

            var separator = normalized.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            return _bookService.FindAllByWorkProviderId(
                    normalized.Substring(0, separator),
                    ProviderIdHelper.StripPrefix(normalized),
                    mediaType)
                .Any(book => book.AuthorId == authorId);
        }

        public async Task<UserSelectedEditionMaterialization> MaterializeUserSelectedEditionAsync(
            UserSelectedRemoteEdition selection,
            MonitoringConfig config)
        {
            if (selection == null)
            {
                throw new ArgumentNullException(nameof(selection));
            }

            var authorProviderId = NormalizeRequiredProviderId(selection.AuthorProviderId, "author");
            var workProviderId = NormalizeRequiredProviderId(selection.WorkProviderId, "work");
            var editionProviderId = NormalizeRequiredProviderId(selection.EditionProviderId, "edition");
            var remoteAuthor = await FetchAuthorBlobAsync(authorProviderId);
            var remoteTarget = ResolveUniqueRemoteUserSelection(
                remoteAuthor,
                workProviderId,
                editionProviderId,
                selection.MediaType);

            var localAuthor = FindExistingAuthor(authorProviderId, remoteAuthor);
            if (localAuthor == null)
            {
                localAuthor = await AddAuthorAsync(authorProviderId, config);
            }
            else if (_refreshAuthorService != null)
            {
                // First converge everything the normal profile permits. The exact selected row is
                // seeded below only if that ordinary authoritative reconciliation did not retain it.
                _refreshAuthorService.ReconcileAuthorBlob(localAuthor, remoteAuthor);
                localAuthor = _authorService.GetAuthor(localAuthor.Id) ?? localAuthor;
            }

            if (localAuthor?.Id <= 0)
            {
                throw new InvalidOperationException(
                    $"Authoritative metadata author '{authorProviderId}' is not locally available yet.");
            }

            var localBook = ResolveLocalBookForUserSelection(
                localAuthor,
                remoteTarget.Book,
                workProviderId,
                selection.MediaType);
            var localEdition = ResolveLocalEditionForUserSelection(
                localAuthor,
                localBook,
                remoteTarget.Edition,
                editionProviderId,
                selection.MediaType);

            (localBook, localEdition) = SeedUserSelectedEdition(
                localAuthor,
                localBook,
                localEdition,
                remoteTarget.Book,
                remoteTarget.Edition,
                selection.MediaType);

            // The seed is already a durable manual pin. Re-run the ordinary full-blob reconciliation
            // so series links, provider ownership/re-homing, aliases, and retained companion editions
            // converge through the same canonical lifecycle as every other author refresh.
            if (_refreshAuthorService != null)
            {
                _refreshAuthorService.ReconcileAuthorBlob(localAuthor, remoteAuthor);
                localAuthor = _authorService.GetAuthor(localAuthor.Id) ?? localAuthor;
                localBook = ResolveLocalBookForUserSelection(
                    localAuthor,
                    remoteTarget.Book,
                    workProviderId,
                    selection.MediaType);
                localEdition = ResolveLocalEditionForUserSelection(
                    localAuthor,
                    localBook,
                    remoteTarget.Edition,
                    editionProviderId,
                    selection.MediaType);
            }

            if (localBook == null || localEdition == null)
            {
                throw new InvalidOperationException(
                    $"The authoritative author catalog could not materialize selected edition '{editionProviderId}' under work '{workProviderId}'.");
            }

            if (localEdition.BookId != localBook.Id)
            {
                (localBook, localEdition) = SeedUserSelectedEdition(
                    localAuthor,
                    localBook,
                    localEdition,
                    remoteTarget.Book,
                    remoteTarget.Edition,
                    selection.MediaType);
            }

            PinUserSelectedBookAndEdition(localBook, localEdition, selection.MediaType);
            return new UserSelectedEditionMaterialization
            {
                Author = localAuthor,
                Book = _bookService.GetBook(localBook.Id) ?? localBook,
                Edition = _editionService.GetEdition(localEdition.Id) ?? localEdition
            };
        }

        internal sealed class RemoteUserSelection
        {
            public Book Book { get; init; }
            public Edition Edition { get; init; }
        }

        private static string NormalizeRequiredProviderId(string providerId, string label)
        {
            if (!ProviderIdHelper.TryNormalize(providerId, defaultPrefix: null, out var normalized))
            {
                throw new InvalidOperationException(
                    $"User-selected metadata did not provide a valid provider-owned {label} ID.");
            }

            return normalized;
        }

        internal static RemoteUserSelection ResolveUniqueRemoteUserSelection(
            Author remoteAuthor,
            string workProviderId,
            string editionProviderId,
            BookMediaType mediaType)
        {
            var matches = (remoteAuthor?.Books ?? new List<Book>())
                .Where(book => book != null &&
                               book.MediaType == mediaType &&
                               RemoteBookHasWorkProviderId(book, workProviderId))
                .SelectMany(book => (book.Editions ?? new List<Edition>())
                    .Where(edition => BookEditionIdentity.EditionMatchesProviderId(edition, editionProviderId))
                    .Select(edition => new RemoteUserSelection { Book = book, Edition = edition }))
                .ToList();

            if (matches.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The authoritative author blob does not contain edition '{editionProviderId}' under work '{workProviderId}' for {mediaType}.");
            }

            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The authoritative author blob maps edition '{editionProviderId}' and work '{workProviderId}' to {matches.Count} rows. Select a local edition to resolve the ambiguity.");
            }

            return matches[0];
        }

        private static bool RemoteBookHasWorkProviderId(Book book, string providerId)
        {
            var workIds = new HashSet<string>(
                BookEditionIdentity.GetCanonicalWorkProviderIds(book),
                StringComparer.OrdinalIgnoreCase);

            foreach (var alias in book?.RemoteProviderIds ?? Enumerable.Empty<string>())
            {
                if (IsWorkScopedRemoteAlias(alias) &&
                    ProviderIdHelper.TryNormalize(alias, defaultPrefix: null, out var normalizedAlias))
                {
                    workIds.Add(normalizedAlias);
                }
            }

            return workIds.Contains(providerId);
        }

        private static bool IsWorkScopedRemoteAlias(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            var trimmed = providerId.Trim();
            return !trimmed.StartsWith("az:", StringComparison.OrdinalIgnoreCase) &&
                   !trimmed.StartsWith("hc:edition:", StringComparison.OrdinalIgnoreCase) &&
                   !trimmed.StartsWith("edition:", StringComparison.OrdinalIgnoreCase);
        }

        private Book ResolveLocalBookForUserSelection(
            Author localAuthor,
            Book remoteBook,
            string workProviderId,
            BookMediaType mediaType)
        {
            var lookupIds = new HashSet<string>(
                BookEditionIdentity.GetCanonicalWorkProviderIds(remoteBook),
                StringComparer.OrdinalIgnoreCase)
            {
                workProviderId
            };
            foreach (var alias in remoteBook?.RemoteProviderIds ?? Enumerable.Empty<string>())
            {
                if (IsWorkScopedRemoteAlias(alias) &&
                    ProviderIdHelper.TryNormalize(alias, defaultPrefix: null, out var normalizedAlias))
                {
                    lookupIds.Add(normalizedAlias);
                }
            }

            var candidates = new List<Book>();
            foreach (var lookupId in lookupIds)
            {
                var separator = lookupId.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                candidates.AddRange(_bookService.FindAllByWorkProviderId(
                    lookupId.Substring(0, separator),
                    ProviderIdHelper.StripPrefix(lookupId),
                    mediaType));
            }

            candidates = candidates
                .Where(book => book != null &&
                               book.Id > 0 &&
                               book.AuthorId == localAuthor.Id &&
                               book.MediaType == mediaType)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();
            if (candidates.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Selected work '{workProviderId}' resolves to multiple local {mediaType} book rows.");
            }

            return candidates.SingleOrDefault();
        }

        private List<Edition> FindLocalEditionsByProviderId(string providerId)
        {
            var separator = providerId.IndexOf(':');
            return separator <= 0
                ? new List<Edition>()
                : _editionService.GetEditionsByProviderAndId(
                    providerId.Substring(0, separator),
                    ProviderIdHelper.StripPrefix(providerId)) ?? new List<Edition>();
        }

        private Edition ResolveLocalEditionForUserSelection(
            Author localAuthor,
            Book localBook,
            Edition remoteEdition,
            string editionProviderId,
            BookMediaType mediaType)
        {
            var matches = FindLocalEditionsByProviderId(editionProviderId)
                .Where(edition => edition?.BookId > 0)
                .Select(edition => new
                {
                    Edition = edition,
                    Book = _bookService.GetBook(edition.BookId)
                })
                .Where(pair => pair.Book != null &&
                               pair.Book.AuthorId == localAuthor.Id &&
                               pair.Book.MediaType == mediaType)
                .Select(pair => pair.Edition)
                .GroupBy(edition => edition.Id)
                .Select(group => group.First())
                .ToList();
            if (matches.Count == 0 && localBook?.Id > 0)
            {
                matches = (_editionService.GetEditionsByBook(localBook.Id) ?? new List<Edition>())
                    .Where(edition => BookEditionIdentity.EditionsMatch(edition, remoteEdition))
                    .GroupBy(edition => edition.Id)
                    .Select(group => group.First())
                    .ToList();
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Selected edition '{editionProviderId}' resolves to multiple local edition rows.");
            }

            return matches.SingleOrDefault();
        }

        private (Book Book, Edition Edition) SeedUserSelectedEdition(
            Author localAuthor,
            Book localBook,
            Edition localEdition,
            Book remoteBook,
            Edition remoteEdition,
            BookMediaType mediaType)
        {
            var book = localBook ?? RefreshEntityCopy.CloneBook(remoteBook, includeEditions: false);
            var edition = localEdition ?? RefreshEntityCopy.CloneEdition(remoteEdition);
            var isNewBook = book.Id <= 0;
            var isNewEdition = edition.Id <= 0;
            var previousBookId = isNewEdition ? 0 : edition.BookId;

            book.AuthorId = localAuthor.Id;
            book.Author = localAuthor;
            book.AudiobookMonitored = mediaType == BookMediaType.Audiobook;
            book.EbookMonitored = mediaType == BookMediaType.Ebook;
            book.AnyEditionOk = false;
            book.AddOptions ??= new AddBookOptions();
            book.AddOptions.AddType = BookAddType.Manual;
            book.AddOptions.SearchForNewBook = false;
            book.ForeignEditionId = remoteEdition.ForeignEditionId;
            edition.Monitored = true;
            edition.ManualAdd = true;
            edition.Book = book;

            using var connection = _mainDatabase.OpenConnection();
            using var transaction = connection.BeginTransaction();
            try
            {
                if (isNewBook)
                {
                    book.Editions = new List<Edition> { edition };
                    _bookService.InsertMany(new List<Book> { book }, connection, transaction);
                }
                else
                {
                    connection.Execute(
                        @"UPDATE ""Books""
                          SET ""AnyEditionOk"" = @AnyEditionOk,
                              ""AudiobookMonitored"" = @AudiobookMonitored,
                              ""EbookMonitored"" = @EbookMonitored,
                              ""ForeignEditionId"" = @ForeignEditionId
                          WHERE ""Id"" = @BookId",
                        new
                        {
                            AnyEditionOk = false,
                            AudiobookMonitored = mediaType == BookMediaType.Audiobook,
                            EbookMonitored = mediaType == BookMediaType.Ebook,
                            ForeignEditionId = remoteEdition.ForeignEditionId,
                            BookId = book.Id
                        },
                        transaction);
                }

                connection.Execute(
                    @"UPDATE ""Editions""
                      SET ""Monitored"" = @Monitored,
                          ""ManualAdd"" = @ManualAdd
                      WHERE ""BookId"" = @BookId",
                    new { Monitored = false, ManualAdd = false, BookId = book.Id },
                    transaction);

                edition.BookId = book.Id;
                if (isNewEdition)
                {
                    _editionService.InsertMany(new List<Edition> { edition }, connection, transaction);
                }
                else
                {
                    connection.Execute(
                        @"UPDATE ""Editions""
                          SET ""BookId"" = @BookId,
                              ""Monitored"" = @Monitored,
                              ""ManualAdd"" = @ManualAdd
                          WHERE ""Id"" = @EditionId",
                        new
                        {
                            BookId = book.Id,
                            Monitored = true,
                            ManualAdd = true,
                            EditionId = edition.Id
                        },
                        transaction);
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            _bookService.SetAddOptions(new[] { book });
            book.Editions = _editionService.GetEditionsByBook(book.Id);
            _bookService.RefreshProviderAliases(book);
            if (previousBookId > 0 && previousBookId != book.Id)
            {
                try
                {
                    var previousBook = _bookService.GetBook(previousBookId);
                    previousBook.Editions = _editionService.GetEditionsByBook(previousBook.Id);
                    _bookService.RefreshProviderAliases(previousBook);
                }
                catch (ModelNotFoundException)
                {
                    // Ordinary reconciliation may already have removed an empty source pocket.
                }
            }

            _narratorLinkService?.UpsertEditionNarratorLinks(new[] { edition });
            return (book, _editionService.GetEdition(edition.Id) ?? edition);
        }

        private void PinUserSelectedBookAndEdition(Book book, Edition edition, BookMediaType mediaType)
        {
            book.AudiobookMonitored = mediaType == BookMediaType.Audiobook;
            book.EbookMonitored = mediaType == BookMediaType.Ebook;
            book.AnyEditionOk = false;
            book.AddOptions ??= new AddBookOptions();
            book.AddOptions.AddType = BookAddType.Manual;
            book.AddOptions.SearchForNewBook = false;
            _bookService.UpdateBook(book);
            _bookService.SetAddOptions(new[] { book });
            _editionService.SetMonitored(edition, true);
            book.Editions = _editionService.GetEditionsByBook(book.Id);
            _bookService.RefreshProviderAliases(book);
        }

        private Author FindExistingAuthor(string requestedProviderId, Author remoteAuthor)
        {
            // 1) Requested provider ID (caller's intent)
            var existing = FindExistingAuthorByProviderId(requestedProviderId);
            if (existing != null)
            {
                return existing;
            }

            // 2) Any provider IDs known on the remote payload (author might have gained new IDs upstream)
            existing = FindExistingAuthorByProviderId(remoteAuthor?.HardcoverAuthorId, defaultProvider: "hc");
            if (existing != null)
            {
                return existing;
            }

            existing = FindExistingAuthorByProviderId(remoteAuthor?.GoodreadsAuthorId, defaultProvider: "gr");
            if (existing != null)
            {
                return existing;
            }

            existing = FindExistingAuthorByProviderId(remoteAuthor?.AudnexusAuthorId, defaultProvider: "az");
            if (existing != null)
            {
                return existing;
            }

            existing = FindExistingAuthorByProviderId(remoteAuthor?.OpenLibraryAuthorId, defaultProvider: "ol");
            if (existing != null)
            {
                return existing;
            }

            existing = FindExistingAuthorByProviderId(remoteAuthor?.GoogleBooksAuthorId, defaultProvider: "gb");
            if (existing != null)
            {
                return existing;
            }

            if (remoteAuthor?.RemoteProviderIds != null)
            {
                foreach (var pid in remoteAuthor.RemoteProviderIds)
                {
                    existing = FindExistingAuthorByProviderId(pid);
                    if (existing != null)
                    {
                        return existing;
                    }
                }
            }

            return null;
        }

        private Author FindExistingAuthorByProviderId(string providerId, string defaultProvider = null)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            providerId = providerId.Trim();
            var provider = defaultProvider?.Trim().ToLowerInvariant();
            var rawId = providerId;

            var idx = providerId.IndexOf(':');
            if (idx > 0 && idx < providerId.Length - 1)
            {
                provider = providerId.Substring(0, idx).Trim().ToLowerInvariant();
                rawId = providerId.Substring(idx + 1).Trim();
            }

            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(rawId))
            {
                return null;
            }

            return _authorService.FindByProviderId(provider, rawId);
        }

        private async Task<Author> HandleExistingAuthorAsync(Author existingAuthor, Author remoteAuthor, MonitoringConfig config)
        {
            existingAuthor = MergeMissingProviderIds(existingAuthor, remoteAuthor);
            existingAuthor = ApplyAuthorMetadata(existingAuthor, remoteAuthor);
            SeedAuthorSyncMetadata(existingAuthor, remoteAuthor, null);

            if (config != null)
            {
                NormalizeMonitoringConfigFromExplicitRootFolders(config);
            }

            // Books must see the same fill-missing settings that will be persisted for this media side.
            existingAuthor = ApplyExistingAuthorProgressiveSettings(existingAuthor, config);

            // Ensure locally missing media types are backfilled when an effective metadata profile exists.
            // CreateAudiobook/CreateEbook controls settings intent, not whether the server catalog may enter
            // the canonical profile/retention pipeline.
            var existingBooks = _bookService.GetBooksByAuthor(existingAuthor.Id) ?? new List<Book>();

            var requestedProviderIds = GetRequestedBookProviderIdsByMediaType(config);
            if (requestedProviderIds.Any(requested =>
                    remoteAuthor?.Books?.Any(remoteBook =>
                        remoteBook != null &&
                        remoteBook.MediaType == requested.Key &&
                        requested.Value.Any(providerId => BookMatchesProviderId(remoteBook, providerId))) == true) &&
                _refreshAuthorService != null)
            {
                // The full author blob remains authoritative. A specific-work request may ask an
                // existing author to reconcile a Book that is absent locally, but it never hydrates
                // from a direct work/edition lookup or inserts a hand-built row.
                _logger.Debug(
                    "[HYDRATE] Reconciling authoritative author blob for existing author '{0}' before resolving requested work(s) [{1}]",
                    existingAuthor.Name,
                    string.Join(",", requestedProviderIds
                        .SelectMany(requested => requested.Value.Select(providerId => $"{requested.Key}:{providerId}"))
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)));
                _refreshAuthorService.ReconcileAuthorBlob(existingAuthor, remoteAuthor);
                existingAuthor = _authorService.GetAuthor(existingAuthor.Id) ?? existingAuthor;
                existingBooks = _bookService.GetBooksByAuthor(existingAuthor.Id) ?? new List<Book>();
            }

            var hasAudiobook = existingBooks.Any(b => b.MediaType == BookMediaType.Audiobook);
            var hasEbook = existingBooks.Any(b => b.MediaType == BookMediaType.Ebook);

            var needAudiobook = !hasAudiobook && GetEffectiveMetadataProfileId(existingAuthor, config, BookMediaType.Audiobook).HasValue;
            var needEbook = !hasEbook && GetEffectiveMetadataProfileId(existingAuthor, config, BookMediaType.Ebook).HasValue;

            if ((needAudiobook || needEbook) && (remoteAuthor?.Books?.Any() == true))
            {
                var booksToBackfill = remoteAuthor.Books
                    .Where(b =>
                        (needAudiobook && b.MediaType == BookMediaType.Audiobook) ||
                        (needEbook && b.MediaType == BookMediaType.Ebook))
                    .ToList();

                var seriesToBackfill = (remoteAuthor.Series ?? new List<Series>())
                    .Where(s =>
                        (needAudiobook && s.MediaType == BookMediaType.Audiobook) ||
                        (needEbook && s.MediaType == BookMediaType.Ebook))
                    .ToList();

                if (booksToBackfill.Any())
                {
                    _logger.Debug("[HYDRATE] Existing author '{0}' missing profile-enabled media types (audiobook={1}, ebook={2}); backfilling {3} books",
                        existingAuthor.Name, needAudiobook, needEbook, booksToBackfill.Count);
                    await ProcessBooksForAuthor(existingAuthor, booksToBackfill, config, seriesToBackfill);
                }

                if (seriesToBackfill.Any())
                {
                    await ProcessSeriesForAuthor(existingAuthor, seriesToBackfill, config);
                }

                _eventAggregator.PublishEvent(new AuthorRefreshCompleteEvent(existingAuthor));
                _logger.Debug("[HYDRATE] Backfill complete for existing author '{0}'", existingAuthor.Name);
            }
            else
            {
                _logger.Info("Author '{0}' already exists in database with {1} books (audiobook={2}, ebook={3})",
                    existingAuthor.Name, existingBooks.Count, hasAudiobook, hasEbook);
            }

            return existingAuthor;
        }

        private Author ApplyAuthorMetadata(Author existingAuthor, Author remoteAuthor)
        {
            if (existingAuthor == null || remoteAuthor == null)
            {
                return existingAuthor;
            }

            // Use the same metadata-copy contract as refresh, while leaving provider-ID
            // attachment to MergeMissingProviderIds and its uniqueness safeguards.
            var hardcoverAuthorId = existingAuthor.HardcoverAuthorId;
            var goodreadsAuthorId = existingAuthor.GoodreadsAuthorId;
            var audnexusAuthorId = existingAuthor.AudnexusAuthorId;
            var openLibraryAuthorId = existingAuthor.OpenLibraryAuthorId;
            var googleBooksAuthorId = existingAuthor.GoogleBooksAuthorId;

            existingAuthor.UseMetadataFrom(remoteAuthor);
            existingAuthor.HardcoverAuthorId = hardcoverAuthorId;
            existingAuthor.GoodreadsAuthorId = goodreadsAuthorId;
            existingAuthor.AudnexusAuthorId = audnexusAuthorId;
            existingAuthor.OpenLibraryAuthorId = openLibraryAuthorId;
            existingAuthor.GoogleBooksAuthorId = googleBooksAuthorId;
            existingAuthor.LastInfoSync = DateTime.UtcNow;

            return _authorService.UpdateAuthor(existingAuthor);
        }

        private Author ApplyExistingAuthorProgressiveSettings(Author existingAuthor, MonitoringConfig config)
        {
            if (config == null)
            {
                return existingAuthor;
            }

            try
            {
                var updated = _authorService.UpdateAuthorProgressiveSettings(
                    existingAuthor,
                    config.CreateAudiobook ? config.AudiobookQualityProfileId : null,
                    config.CreateAudiobook ? config.AudiobookMetadataProfileId : null,
                    config.CreateAudiobook ? config.AudiobookMonitored : null,
                    config.CreateAudiobook ? config.AudiobookMonitorNewItems : null,
                    config.CreateEbook ? config.EbookQualityProfileId : null,
                    config.CreateEbook ? config.EbookMetadataProfileId : null,
                    config.CreateEbook ? config.EbookMonitored : null,
                    config.CreateEbook ? config.EbookMonitorNewItems : null,
                    // Single path param: prefer audiobook path if present else ebook
                    config.AudiobookRootFolderPath ?? config.EbookRootFolderPath);

                var tagsChanged = false;
                if (config.CreateAudiobook && updated.AudiobookTags == null && config.AudiobookTags != null)
                {
                    updated.AudiobookTags = new HashSet<int>(config.AudiobookTags);
                    tagsChanged = true;
                }

                if (config.CreateEbook && updated.EbookTags == null && config.EbookTags != null)
                {
                    updated.EbookTags = new HashSet<int>(config.EbookTags);
                    tagsChanged = true;
                }

                if (tagsChanged)
                {
                    updated.Tags = (updated.AudiobookTags ?? new HashSet<int>())
                        .Concat(updated.EbookTags ?? new HashSet<int>())
                        .ToHashSet();
                    updated = _authorService.UpdateAuthor(updated);
                }

                // Fill discovered per-type author folder paths when empty.
                if (!string.IsNullOrWhiteSpace(config.DiscoveredAuthorFolderPath))
                {
                    var changed = false;
                    if (config.CreateAudiobook && string.IsNullOrWhiteSpace(updated.AudiobookPath))
                    {
                        updated.AudiobookPath = config.DiscoveredAuthorFolderPath;
                        changed = true;
                    }

                    if (config.CreateEbook && string.IsNullOrWhiteSpace(updated.EbookPath))
                    {
                        updated.EbookPath = config.DiscoveredAuthorFolderPath;
                        changed = true;
                    }

                    if (changed)
                    {
                        updated = _authorService.UpdateAuthor(updated);
                    }
                }

                _eventAggregator.PublishEvent(new AuthorRefreshCompleteEvent(updated));
                return updated;
            }
            catch (Exception mergeEx)
            {
                _logger.Warn(mergeEx, "[DUAL-RECORD] Failed to merge media-type settings onto existing author '{0}'", existingAuthor.Name);
                return existingAuthor;
            }
        }

        private void SeedAuthorSyncMetadata(Author persistedAuthor, Author remoteAuthor, string requestedProviderId)
        {
            if (_syncMetadataService == null || persistedAuthor?.Id <= 0)
            {
                return;
            }

            var externalAuthorId = GetPreferredAuthorIdentifier(remoteAuthor) ??
                                   GetPreferredAuthorIdentifier(persistedAuthor) ??
                                   NormalizeRequestedProviderId(requestedProviderId);

            if (externalAuthorId.IsNullOrWhiteSpace())
            {
                return;
            }

            try
            {
                var syncMetadata = _syncMetadataService.CreateOrUpdateSyncMetadata(
                    persistedAuthor.Id,
                    externalAuthorId,
                    remoteAuthor?.RemoteMetadataETag);

                _syncMetadataService.UpdateSyncResult(
                    persistedAuthor.Id,
                    success: true,
                    etag: remoteAuthor?.RemoteMetadataETag,
                    httpStatus: 200);

                _logger.Debug("[SYNC-METADATA] Seeded author {0} ({1}) with import ETag {2}",
                    persistedAuthor.Name,
                    syncMetadata.ExternalAuthorId,
                    syncMetadata.ETag ?? "none");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[SYNC-METADATA] Failed to seed import ETag for author {0}", persistedAuthor.Name);
            }
        }

        private static string GetPreferredAuthorIdentifier(Author author)
        {
            if (author == null)
            {
                return null;
            }

            return ProviderIdHelper.Normalize(author.HardcoverAuthorId, "hc") ??
                   ProviderIdHelper.Normalize(author.GoodreadsAuthorId, "gr") ??
                   ProviderIdHelper.Normalize(author.OpenLibraryAuthorId, "ol") ??
                   ProviderIdHelper.Normalize(author.GoogleBooksAuthorId, "gb") ??
                   ProviderIdHelper.Normalize(author.AudnexusAuthorId, "az");
        }

        private static string NormalizeRequestedProviderId(string providerId)
        {
            try
            {
                return ProviderIdHelper.Normalize(providerId, null);
            }
            catch
            {
                return null;
            }
        }

        private void NormalizeMonitoringConfigFromExplicitRootFolders(MonitoringConfig config)
        {
            if (config == null)
            {
                return;
            }

            // Older callers expressed an exact-book request with only the provider-ID
            // list. Preserve that intent before root defaults fill an otherwise-null
            // initial mode. An explicit mode always wins; in particular, All may carry
            // the same IDs solely so a missing requested work can be rescued later.
            if (!config.AudiobookMonitorExistingMode.HasValue && config.AudiobookBooksToMonitor?.Any() == true)
            {
                config.AudiobookMonitorExistingMode = MonitorTypes.SpecificBook;
            }

            if (!config.EbookMonitorExistingMode.HasValue && config.EbookBooksToMonitor?.Any() == true)
            {
                config.EbookMonitorExistingMode = MonitorTypes.SpecificBook;
            }

            config.AudiobookQualityProfileId = NormalizeProfileId(config.AudiobookQualityProfileId);
            config.AudiobookMetadataProfileId = NormalizeProfileId(config.AudiobookMetadataProfileId);
            config.EbookQualityProfileId = NormalizeProfileId(config.EbookQualityProfileId);
            config.EbookMetadataProfileId = NormalizeProfileId(config.EbookMetadataProfileId);

            NormalizeMonitoringConfigForMediaType(config, BookMediaType.Audiobook);
            NormalizeMonitoringConfigForMediaType(config, BookMediaType.Ebook);
        }

        private void NormalizeMonitoringConfigForMediaType(MonitoringConfig config, BookMediaType mediaType)
        {
            var enabled = mediaType == BookMediaType.Audiobook ? config.CreateAudiobook : config.CreateEbook;
            if (!enabled)
            {
                return;
            }

            var rootFolderPath = mediaType == BookMediaType.Audiobook ? config.AudiobookRootFolderPath : config.EbookRootFolderPath;
            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return;
            }

            if (_rootFolderService == null)
            {
                throw BuildMonitoringConfigValidationException(mediaType, RootFolderPropertyName(mediaType), "Selected root folder cannot be resolved in this context.");
            }

            var rootFolder = _rootFolderService.GetBestRootFolder(rootFolderPath);
            if (rootFolder == null || !rootFolder.Path.PathEquals(rootFolderPath))
            {
                throw BuildMonitoringConfigValidationException(mediaType, RootFolderPropertyName(mediaType), $"Selected root folder '{rootFolderPath}' is not configured in Chaptarr");
            }

            if (!IsCompatibleRootFolder(rootFolder, mediaType))
            {
                var expected = mediaType == BookMediaType.Audiobook ? "Audiobook or Mixed" : "Ebook or Mixed";
                throw BuildMonitoringConfigValidationException(mediaType, RootFolderPropertyName(mediaType), $"Selected root folder is {IncompatibleRootFolderLabel(rootFolder)}-only; choose an {expected} root folder");
            }

            var settings = mediaType == BookMediaType.Audiobook ? rootFolder.GetAudiobookSettings() : rootFolder.GetEbookSettings();
            if (settings == null)
            {
                throw BuildMonitoringConfigValidationException(mediaType, RootFolderPropertyName(mediaType), $"Selected root folder '{rootFolder.Path}' does not have {MediaTypeLabel(mediaType)} defaults configured");
            }

            if (mediaType == BookMediaType.Audiobook)
            {
                config.AudiobookQualityProfileId ??= NormalizeProfileId(settings.QualityProfileId);
                config.AudiobookMetadataProfileId ??= NormalizeProfileId(settings.MetadataProfileId);
                config.AudiobookMonitored ??= settings.Monitored;
                config.AudiobookMonitorNewItems ??= settings.MonitorNewItems;
                config.AudiobookMonitorExistingMode ??= RootFolderSettingsResolver.ResolveInitialMonitorMode(settings.MonitorExistingMode);
                config.AudiobookTags ??= settings.Tags == null ? null : new HashSet<int>(settings.Tags);

                if (!config.AudiobookQualityProfileId.HasValue)
                {
                    throw BuildMonitoringConfigValidationException(mediaType, nameof(config.AudiobookQualityProfileId), $"Selected root folder '{rootFolder.Path}' is missing an audiobook quality profile default");
                }

                if (!config.AudiobookMetadataProfileId.HasValue)
                {
                    throw BuildMonitoringConfigValidationException(mediaType, nameof(config.AudiobookMetadataProfileId), $"Selected root folder '{rootFolder.Path}' is missing an audiobook metadata profile default");
                }

                ValidateMonitoringProfileIds(mediaType, nameof(config.AudiobookQualityProfileId), config.AudiobookQualityProfileId.Value, nameof(config.AudiobookMetadataProfileId), config.AudiobookMetadataProfileId.Value);
                ValidateMonitorMode(mediaType, nameof(config.AudiobookMonitorExistingMode), config.AudiobookMonitorExistingMode);
            }
            else
            {
                config.EbookQualityProfileId ??= NormalizeProfileId(settings.QualityProfileId);
                config.EbookMetadataProfileId ??= NormalizeProfileId(settings.MetadataProfileId);
                config.EbookMonitored ??= settings.Monitored;
                config.EbookMonitorNewItems ??= settings.MonitorNewItems;
                config.EbookMonitorExistingMode ??= RootFolderSettingsResolver.ResolveInitialMonitorMode(settings.MonitorExistingMode);
                config.EbookTags ??= settings.Tags == null ? null : new HashSet<int>(settings.Tags);

                if (!config.EbookQualityProfileId.HasValue)
                {
                    throw BuildMonitoringConfigValidationException(mediaType, nameof(config.EbookQualityProfileId), $"Selected root folder '{rootFolder.Path}' is missing an ebook quality profile default");
                }

                if (!config.EbookMetadataProfileId.HasValue)
                {
                    throw BuildMonitoringConfigValidationException(mediaType, nameof(config.EbookMetadataProfileId), $"Selected root folder '{rootFolder.Path}' is missing an ebook metadata profile default");
                }

                ValidateMonitoringProfileIds(mediaType, nameof(config.EbookQualityProfileId), config.EbookQualityProfileId.Value, nameof(config.EbookMetadataProfileId), config.EbookMetadataProfileId.Value);
                ValidateMonitorMode(mediaType, nameof(config.EbookMonitorExistingMode), config.EbookMonitorExistingMode);
            }
        }

        private void ValidateMonitoringProfileIds(BookMediaType mediaType, string qualityPropertyName, int qualityProfileId, string metadataPropertyName, int metadataProfileId)
        {
            if (!_qualityProfileService.Exists(qualityProfileId))
            {
                throw BuildMonitoringConfigValidationException(mediaType, qualityPropertyName, $"Selected quality profile {qualityProfileId} does not exist");
            }

            if (!_metadataProfileService.Exists(metadataProfileId))
            {
                throw BuildMonitoringConfigValidationException(mediaType, metadataPropertyName, $"Selected metadata profile {metadataProfileId} does not exist");
            }
        }

        private static void ValidateMonitorMode(BookMediaType mediaType, string propertyName, MonitorTypes? monitorMode)
        {
            if (!monitorMode.HasValue || Enum.IsDefined(typeof(MonitorTypes), monitorMode.Value))
            {
                return;
            }

            throw BuildMonitoringConfigValidationException(mediaType, propertyName, "Monitor mode is invalid");
        }

        private static int? NormalizeProfileId(int? profileId)
        {
            return profileId.HasValue && profileId.Value > 0 ? profileId : null;
        }

        private static HashSet<int> CloneTags(HashSet<int> tags)
        {
            return tags == null ? null : new HashSet<int>(tags);
        }

        private static bool IsCompatibleRootFolder(RootFolder rootFolder, BookMediaType mediaType)
        {
            if (rootFolder == null)
            {
                return false;
            }

            return rootFolder.FolderType == FolderType.Mixed ||
                   (mediaType == BookMediaType.Audiobook && rootFolder.FolderType == FolderType.Audiobook) ||
                   (mediaType == BookMediaType.Ebook && rootFolder.FolderType == FolderType.Ebook);
        }

        private static string RootFolderPropertyName(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Audiobook
                ? nameof(MonitoringConfig.AudiobookRootFolderPath)
                : nameof(MonitoringConfig.EbookRootFolderPath);
        }

        private static string MediaTypeLabel(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
        }

        private static string IncompatibleRootFolderLabel(RootFolder rootFolder)
        {
            return rootFolder?.FolderType == FolderType.Ebook ? "Ebook" : "Audiobook";
        }

        private static ValidationException BuildMonitoringConfigValidationException(BookMediaType mediaType, string propertyName, string message)
        {
            return new ValidationException(new[]
            {
                new ValidationFailure(propertyName, message)
                {
                    ErrorCode = $"{MediaTypeLabel(mediaType).ToUpperInvariant()}_ROOT_FOLDER_INHERITANCE"
                }
            });
        }

            private Author MergeMissingProviderIds(Author existingAuthor, Author remoteAuthor)
            {
                if (existingAuthor == null || remoteAuthor == null)
                {
                    return existingAuthor;
                }

                var changed = false;

                if (existingAuthor.HardcoverAuthorId.IsNullOrWhiteSpace() && !remoteAuthor.HardcoverAuthorId.IsNullOrWhiteSpace())
                {
                    existingAuthor.HardcoverAuthorId = remoteAuthor.HardcoverAuthorId;
                    changed = true;
                }

                if (existingAuthor.GoodreadsAuthorId.IsNullOrWhiteSpace() && !remoteAuthor.GoodreadsAuthorId.IsNullOrWhiteSpace())
                {
                    existingAuthor.GoodreadsAuthorId = remoteAuthor.GoodreadsAuthorId;
                    changed = true;
                }

                if (existingAuthor.AudnexusAuthorId.IsNullOrWhiteSpace() && !remoteAuthor.AudnexusAuthorId.IsNullOrWhiteSpace())
                {
                    existingAuthor.AudnexusAuthorId = remoteAuthor.AudnexusAuthorId;
                    changed = true;
                }

                if (existingAuthor.OpenLibraryAuthorId.IsNullOrWhiteSpace() && !remoteAuthor.OpenLibraryAuthorId.IsNullOrWhiteSpace())
                {
                    existingAuthor.OpenLibraryAuthorId = remoteAuthor.OpenLibraryAuthorId;
                    changed = true;
                }

                if (existingAuthor.GoogleBooksAuthorId.IsNullOrWhiteSpace() && !remoteAuthor.GoogleBooksAuthorId.IsNullOrWhiteSpace())
                {
                    existingAuthor.GoogleBooksAuthorId = remoteAuthor.GoogleBooksAuthorId;
                    changed = true;
                }

                if (!changed)
                {
                    return existingAuthor;
                }

                try
                {
                    return _authorService.UpdateAuthor(existingAuthor);
                }
                catch (SqliteException ex) when (IsAuthorProviderIdUniqueViolation(ex))
                {
                    _logger.Warn(ex, "[AUTHOR-DEDUP] Unique provider ID constraint hit while merging provider IDs onto existing author '{0}' (Id={1}). Keeping existing provider IDs.",
                        existingAuthor.Name, existingAuthor.Id);
                }
                catch (PostgresException ex) when (IsAuthorProviderIdUniqueViolation(ex))
                {
                    _logger.Warn(ex, "[AUTHOR-DEDUP] Unique provider ID constraint hit while merging provider IDs onto existing author '{0}' (Id={1}). Keeping existing provider IDs.",
                        existingAuthor.Name, existingAuthor.Id);
                }

                return existingAuthor;
            }

        private static readonly string[] AuthorProviderIdColumns =
        {
            "HardcoverAuthorId",
            "GoodreadsAuthorId",
            "AudnexusAuthorId",
            "OpenLibraryAuthorId",
            "GoogleBooksAuthorId"
        };

        private static bool IsAuthorProviderIdUniqueViolation(SqliteException ex)
        {
            const int sqliteConstraintUnique = 2067; // SQLITE_CONSTRAINT_UNIQUE
            if (ex.SqliteExtendedErrorCode != sqliteConstraintUnique)
            {
                return false;
            }

            // Example: "UNIQUE constraint failed: Authors.HardcoverAuthorId"
            if (!ex.Message.Contains("Authors.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return AuthorProviderIdColumns.Any(c => ex.Message.Contains($"Authors.{c}", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsAuthorProviderIdUniqueViolation(PostgresException ex)
        {
            if (!string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ex.TableName) &&
                !string.Equals(ex.TableName, "Authors", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var blob = $"{ex.ConstraintName} {ex.Detail}";
            return blob.Contains("UX_Authors_", StringComparison.OrdinalIgnoreCase) ||
                   AuthorProviderIdColumns.Any(c => blob.Contains(c, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<Author> AddAuthorMonitoringBookAsync(string authorProviderId, string bookProviderId)
        {
            _logger.Info("Adding author {0} while monitoring specific book {1}", authorProviderId, bookProviderId);

            // First add the author with all books
            var author = await AddAuthorAsync(authorProviderId);

            // Normalize the requested provider ID into (prefix, rawId)
            var providerPrefix = string.Empty;
            var providerRawId = string.Empty;
            if (!string.IsNullOrWhiteSpace(bookProviderId))
            {
                var trimmed = bookProviderId.Trim();
                var idx = trimmed.IndexOf(':');
                if (idx > 0 && idx < trimmed.Length - 1)
                {
                    providerPrefix = trimmed.Substring(0, idx).ToLowerInvariant();
                    providerRawId = trimmed.Substring(idx + 1);
                }
                else
                {
                    providerRawId = trimmed;
                }
            }

            // Find the specific book instances (audiobook and ebook)
            var targetBooks = _bookService.GetBooksByAuthor(author.Id)
                .Where(b =>
                    MatchesProviderId(b, providerPrefix, bookProviderId, providerRawId))
                .ToList();

            if (targetBooks.Any())
            {
                // Monitor only the requested book
                foreach (var book in targetBooks)
                {
                    if (book.MediaType == BookMediaType.Audiobook)
                    {
                        book.AudiobookMonitored = true;
                    }
                    else if (book.MediaType == BookMediaType.Ebook)
                    {
                        book.EbookMonitored = true;
                    }
                    // Removed incorrect overrides that set both fields
                }
                _bookService.UpdateMany(targetBooks);
            }

            return author;
        }

        private bool MatchesProviderId(Book book, string providerPrefix, string providerId, string rawId)
        {
            if (book == null || providerId == null)
            {
                return false;
            }

            var normalizedProviderId = providerId.Trim();
            var normalizedRawId = rawId?.Trim();

            if (BookIdentity.GetProviderIdentityTokens(book).Contains(normalizedProviderId))
            {
                return true;
            }

            bool Matches(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return false;
                }

                candidate = candidate.Trim();

                if (candidate.Equals(normalizedProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return !string.IsNullOrWhiteSpace(normalizedRawId) &&
                       candidate.Equals(normalizedRawId, StringComparison.OrdinalIgnoreCase);
            }

            switch (providerPrefix)
            {
                case "hc":
                    return BookEditionIdentity.HasCanonicalWorkProviderId(book, normalizedProviderId) ||
                           BookEditionIdentity.HasCanonicalEditionProviderId(book, normalizedProviderId, _logger, "AuthorLibraryService.ProviderMatch");

                case "gr":
                    return BookEditionIdentity.HasCanonicalWorkProviderId(book, normalizedProviderId) ||
                           BookEditionIdentity.HasCanonicalEditionProviderId(book, normalizedProviderId, _logger, "AuthorLibraryService.ProviderMatch") ||
                           Matches(book.ForeignEditionId);

                case "ol":
                    return BookEditionIdentity.HasCanonicalWorkProviderId(book, normalizedProviderId) ||
                           BookEditionIdentity.HasCanonicalEditionProviderId(book, normalizedProviderId, _logger, "AuthorLibraryService.ProviderMatch");

                case "gb":
                    return BookEditionIdentity.HasCanonicalEditionProviderId(book, normalizedProviderId, _logger, "AuthorLibraryService.ProviderMatch");

                case "az":
                case "ax":
                    return BookEditionIdentity.HasCanonicalEditionProviderId(book, normalizedProviderId, _logger, "AuthorLibraryService.ProviderMatch");
            }

            // Fallback: try all known provider fields
            return BookEditionIdentity.HasCanonicalWorkProviderId(book, normalizedProviderId) ||
                   BookEditionIdentity.HasCanonicalEditionProviderId(book, normalizedProviderId, _logger, "AuthorLibraryService.ProviderMatch") ||
                   Matches(book.ForeignEditionId);
        }

        public async Task<List<Author>> AddAuthorsMonitoringSeriesAsync(string[] authorProviderIds, string seriesProviderId)
        {
            _logger.Info("Adding {0} authors for series {1}", authorProviderIds.Length, seriesProviderId);

            var addedAuthors = new List<Author>();

            foreach (var authorProviderId in authorProviderIds)
            {
                var author = await AddAuthorAsync(authorProviderId);
                addedAuthors.Add(author);

                // Find and monitor books in the series
                // Provider IDs are now stored with prefixes
                var seriesBooks = _bookService.GetBooksByAuthor(author.Id)
                    .Where(b => b.SeriesLinks?.Any(sl =>
                        sl.Series?.Value?.GoodreadsSeriesId == seriesProviderId ||
                        sl.Series?.Value?.AmazonSeriesAsin == seriesProviderId ||
                        sl.Series?.Value?.HardcoverSeriesId == seriesProviderId ||
                        sl.Series?.Value?.OpenLibrarySeriesId == seriesProviderId) == true)
                    .ToList();

                foreach (var book in seriesBooks)
                {
                    if (book.MediaType == BookMediaType.Audiobook)
                    {
                        book.AudiobookMonitored = true;
                    }
                    else if (book.MediaType == BookMediaType.Ebook)
                    {
                        book.EbookMonitored = true;
                    }
                    // Removed incorrect overrides that set both fields
                }

                if (seriesBooks.Any())
                {
                    _bookService.UpdateMany(seriesBooks);
                }
            }

            return addedAuthors;
        }

        public Task<Author> RefreshAuthorAsync(int authorId)
        {
            _logger.Info("Refreshing author with ID: {0}", authorId);
            _commandQueueManager.Push(new RefreshAuthorCommand(authorId) { ForceRefresh = true });

            // Return updated author
            return Task.FromResult(_authorService.GetAuthor(authorId));
        }

        public Task RemoveAuthorAsync(int authorId)
        {
            _logger.Info("Removing author with ID: {0}", authorId);

            var author = _authorService.GetAuthor(authorId);
            if (author == null)
            {
                throw new ArgumentException($"Author with ID {authorId} not found");
            }

            // Delete all books
            var books = _bookService.GetBooksByAuthor(authorId);
            _bookService.DeleteMany(books);

            // Delete author
            _authorService.DeleteAuthor(authorId, false, false);

            return Task.CompletedTask;
        }

        private void ApplyMonitoringConfig(Author author, MonitoringConfig config)
        {
            if (config == null)
            {
                return;
            }

            // Preserve the one-time book-row policy until the initial disk scan has
            // attached files. Specific-book requests are applied by provider ID and
            // must not be widened by this post-scan pass.
            var audiobookInitialMode = config.CreateAudiobook && config.AudiobookMonitorExistingMode != MonitorTypes.SpecificBook
                ? config.AudiobookMonitorExistingMode
                : null;
            var ebookInitialMode = config.CreateEbook && config.EbookMonitorExistingMode != MonitorTypes.SpecificBook
                ? config.EbookMonitorExistingMode
                : null;
            if (audiobookInitialMode.HasValue || ebookInitialMode.HasValue || config.SearchForMissingBooks == true)
            {
                author.AddOptions ??= new AddAuthorOptions();
                author.AddOptions.AudiobookMonitor = audiobookInitialMode;
                author.AddOptions.EbookMonitor = ebookInitialMode;
                author.AddOptions.SearchForMissingBooks = config.SearchForMissingBooks == true;
            }

            // Apply audiobook settings only when this add request configured audiobook support.
            if (config.CreateAudiobook && config.AudiobookQualityProfileId.HasValue)
            {
                author.AudiobookQualityProfileId = config.AudiobookQualityProfileId;
            }

            // Apply the independent audiobook author gate and ongoing new-row policy.
            if (config.CreateAudiobook && config.AudiobookMonitored.HasValue)
            {
                author.AudiobookMonitored = config.AudiobookMonitored;
            }
            else if (config.CreateAudiobook && config.IsManualAddition)
            {
                // A manual add with no explicit gate is an explicit pause for the
                // configured side; it must not inherit the other side's state.
                author.AudiobookMonitored = false;
            }

            if (config.CreateAudiobook && config.AudiobookMonitorNewItems.HasValue)
            {
                author.AudiobookMonitorNewItems = config.AudiobookMonitorNewItems;
            }

            // Apply audiobook metadata profile independently of quality profile
            if (config.CreateAudiobook && config.AudiobookMetadataProfileId.HasValue)
            {
                author.AudiobookMetadataProfileId = config.AudiobookMetadataProfileId;
            }

            // Apply ebook settings only when this add request configured ebook support.
            if (config.CreateEbook && config.EbookQualityProfileId.HasValue)
            {
                author.EbookQualityProfileId = config.EbookQualityProfileId;
            }

            // Apply the independent ebook author gate and ongoing new-row policy.
            if (config.CreateEbook && config.EbookMonitored.HasValue)
            {
                author.EbookMonitored = config.EbookMonitored;
            }
            else if (config.CreateEbook && config.IsManualAddition)
            {
                author.EbookMonitored = false;
            }

            if (config.CreateEbook && config.EbookMonitorNewItems.HasValue)
            {
                author.EbookMonitorNewItems = config.EbookMonitorNewItems;
            }

            // Apply ebook metadata profile independently of quality profile
            if (config.CreateEbook && config.EbookMetadataProfileId.HasValue)
            {
                author.EbookMetadataProfileId = config.EbookMetadataProfileId;
            }

            // Do NOT cross-populate metadata profiles between types
            // The generic MetadataProfileId should only be set from a generic source
            // Each media type has its own metadata profile field

            // Monitored is a compatibility projection only. The media-side gates
            // above are the source of truth and may legitimately leave this false
            // when both sides are unconfigured.
            author.Monitored = author.IsMonitoredFromMediaSettings();

            if (config.CreateAudiobook && !string.IsNullOrWhiteSpace(config.AudiobookRootFolderPath))
            {
                author.AudiobookRootFolderPath = config.AudiobookRootFolderPath;
            }

            if (config.CreateEbook && !string.IsNullOrWhiteSpace(config.EbookRootFolderPath))
            {
                author.EbookRootFolderPath = config.EbookRootFolderPath;
            }

            ApplyAuthorPaths(author, config);
            
            // Mark as manually configured if this is a manual addition from modal
            // This prevents future root folder setting changes from overriding user choices
            if (config?.IsManualAddition == true)
            {
                // Only mark the media type(s) that were actually configured in this request.
                if (!string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath) || config.CreateAudiobook)
                {
                    author.AudiobookSettingsManuallyOverridden = true;
                }

                if (!string.IsNullOrWhiteSpace(author.EbookRootFolderPath) || config.CreateEbook)
                {
                    author.EbookSettingsManuallyOverridden = true;
                }

                _logger.Debug("Author '{0}' marked as manually configured (modal addition): audiobook={1}, ebook={2}",
                    author.Name, author.AudiobookSettingsManuallyOverridden, author.EbookSettingsManuallyOverridden);
            }
        }

        private void ApplyAuthorPaths(Author author, MonitoringConfig config)
        {
            // FOLDER-PRESERVATION: If we have a discovered author folder, use it instead of generating a new one.
            if (!string.IsNullOrWhiteSpace(config.DiscoveredAuthorFolderPath))
            {
                var discovered = config.DiscoveredAuthorFolderPath;

                // SAFETY: Never allow an author folder to be the same as a configured root folder.
                // This can happen when files are directly in the root, and would make "Delete author files"
                // delete the entire root folder.
                var rootFolders = _rootFolderService.All();
                var discoveredIsRoot = rootFolders.Any(r => r.Path.PathEquals(discovered) || discovered.IsParentPath(r.Path)) ||
                                       (!string.IsNullOrWhiteSpace(config.AudiobookRootFolderPath) && discovered.PathEquals(config.AudiobookRootFolderPath)) ||
                                       (!string.IsNullOrWhiteSpace(config.EbookRootFolderPath) && discovered.PathEquals(config.EbookRootFolderPath));

                if (discoveredIsRoot)
                {
                    _logger.Warn("[FOLDER-PRESERVATION] Discovered author folder '{0}' for '{1}' matches a configured root folder. Ignoring discovered folder and generating an author folder instead.",
                        discovered, author.Name);
                }
                else
                {
                    author.Path = discovered;

                    if (config.CreateAudiobook && string.IsNullOrWhiteSpace(author.AudiobookPath))
                    {
                        author.AudiobookPath = discovered;
                        _logger.Debug("[FOLDER-PRESERVATION] Using discovered audiobook folder: {0} for author: {1}",
                            discovered, author.Name);
                    }

                    if (config.CreateEbook && string.IsNullOrWhiteSpace(author.EbookPath))
                    {
                        author.EbookPath = discovered;
                        _logger.Debug("[FOLDER-PRESERVATION] Using discovered ebook folder: {0} for author: {1}",
                            discovered, author.Name);
                    }

                    return;
                }
            }

            // If neither media type has a root folder set yet, choose defaults based on configured root folders.
            // IMPORTANT: Only fill defaults when BOTH are missing; if one is set (e.g. user adding ebooks),
            // keep the other null to represent "unconfigured" for that media type.
            if (string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath) && string.IsNullOrWhiteSpace(author.EbookRootFolderPath))
            {
                var rootFolders = _rootFolderService.All();
                if (!rootFolders.Any())
                {
                    throw new InvalidOperationException("No root folders are configured. Add one in Settings → Media Management.");
                }

                var wantAudiobooks = config?.CreateAudiobook ?? true;
                var wantEbooks = config?.CreateEbook ?? true;

                if (wantAudiobooks && !wantEbooks)
                {
                    var audiobookRoot = rootFolders.FirstOrDefault(r => r.FolderType == FolderType.Audiobook) ??
                                        rootFolders.FirstOrDefault(r => r.FolderType == FolderType.Mixed);

                    if (audiobookRoot == null)
                    {
                        throw new InvalidOperationException("No audiobook root folders are configured. Add one in Settings → Media Management.");
                    }

                    author.AudiobookRootFolderPath = audiobookRoot.Path;
                }
                else if (wantEbooks && !wantAudiobooks)
                {
                    var ebookRoot = rootFolders.FirstOrDefault(r => r.FolderType == FolderType.Ebook) ??
                                    rootFolders.FirstOrDefault(r => r.FolderType == FolderType.Mixed);

                    if (ebookRoot == null)
                    {
                        throw new InvalidOperationException("No ebook root folders are configured. Add one in Settings → Media Management.");
                    }

                    author.EbookRootFolderPath = ebookRoot.Path;
                }
                else
                {
                    // Mixed/default: prefer type-specific folders; fall back to Mixed when present.
                    var mixed = rootFolders.FirstOrDefault(r => r.FolderType == FolderType.Mixed);
                    var audiobookRoot = rootFolders.FirstOrDefault(r => r.FolderType == FolderType.Audiobook) ?? mixed;
                    var ebookRoot = rootFolders.FirstOrDefault(r => r.FolderType == FolderType.Ebook) ?? mixed;

                    if (audiobookRoot == null && ebookRoot == null)
                    {
                        throw new InvalidOperationException("No root folders are configured. Add one in Settings → Media Management.");
                    }

                    if (audiobookRoot != null)
                    {
                        author.AudiobookRootFolderPath = audiobookRoot.Path;
                    }

                    if (ebookRoot != null)
                    {
                        author.EbookRootFolderPath = ebookRoot.Path;
                    }
                }
            }

            // Build per-media-type paths and primary author.Path using the shared AuthorPathBuilder logic.
            // This ensures the database receives a valid, non-null author folder (root + author folder name).
            if (string.IsNullOrWhiteSpace(author.AudiobookPath) && !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath))
            {
                author.AudiobookPath = _authorPathBuilder.BuildPathForQuality(author, Quality.MP3, useExistingRelativeFolder: false);
            }

            if (string.IsNullOrWhiteSpace(author.EbookPath) && !string.IsNullOrWhiteSpace(author.EbookRootFolderPath))
            {
                author.EbookPath = _authorPathBuilder.BuildPathForQuality(author, Quality.EPUB, useExistingRelativeFolder: false);
            }

            author.Path = _authorPathBuilder.BuildPath(author, useExistingRelativeFolder: false);
        }

        private void DisambiguateGeneratedAuthorPaths(Author author)
        {
            var paths = new[] { author.Path, author.AudiobookPath, author.EbookPath }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!paths.Any() || !paths.Any(_authorService.AuthorPathExists))
            {
                return;
            }

            var suffix = string.IsNullOrWhiteSpace(author.Disambiguation)
                ? string.Empty
                : $" ({author.Disambiguation.Trim()})";

            if (string.IsNullOrEmpty(suffix) || paths.Any(path => _authorService.AuthorPathExists(path + suffix)))
            {
                var baseSuffix = suffix;
                var index = 1;
                do
                {
                    suffix = $"{baseSuffix} ({index++})";
                }
                while (paths.Any(path => _authorService.AuthorPathExists(path + suffix)));
            }

            author.Path += suffix;

            if (!string.IsNullOrWhiteSpace(author.AudiobookPath))
            {
                author.AudiobookPath += suffix;
            }

            if (!string.IsNullOrWhiteSpace(author.EbookPath))
            {
                author.EbookPath += suffix;
            }

            _logger.Debug("Disambiguated generated folders for author '{0}' with suffix '{1}'", author.Name, suffix);
        }

            private static int? GetEffectiveMetadataProfileId(Author author, MonitoringConfig config, BookMediaType mediaType)
            {
                if (author == null)
                {
                    return null;
                }

                if (mediaType == BookMediaType.Audiobook)
                {
                    var authorProfileId = author.AudiobookMetadataProfileId.HasValue && author.AudiobookMetadataProfileId.Value > 0
                        ? author.AudiobookMetadataProfileId
                        : null;
                    var configProfileId = config == null || config.CreateAudiobook ? config?.AudiobookMetadataProfileId : null;
                    var profileId = authorProfileId ?? configProfileId;
                    return profileId.HasValue && profileId.Value > 0 ? profileId.Value : null;
                }

                if (mediaType == BookMediaType.Ebook)
                {
                    var authorProfileId = author.EbookMetadataProfileId.HasValue && author.EbookMetadataProfileId.Value > 0
                        ? author.EbookMetadataProfileId
                        : null;
                    var configProfileId = config == null || config.CreateEbook ? config?.EbookMetadataProfileId : null;
                    var profileId = authorProfileId ?? configProfileId;
                    return profileId.HasValue && profileId.Value > 0 ? profileId.Value : null;
                }

                return null;
            }

        private Task ProcessBooksForAuthor(Author author, List<Book> books, MonitoringConfig config, List<Series> seriesForFiltering)
        {
            if (author == null || books == null || !books.Any())
            {
                return Task.CompletedTask;
            }

            books = RefreshAuthorService.NormalizeRemoteBooks(
                author,
                books,
                seriesForFiltering,
                _metadataProfileService,
                _importListExclusionService,
                _logger,
                audiobookMetadataProfileIdOverride: GetEffectiveMetadataProfileId(author, config, BookMediaType.Audiobook),
                ebookMetadataProfileIdOverride: GetEffectiveMetadataProfileId(author, config, BookMediaType.Ebook),
                editionSelector: _editionSelector,
                retainEditions: true,
                logContext: "ProcessBooksForAuthor");

            if (!books.Any())
            {
                return Task.CompletedTask;
            }

            // CRITICAL: Log book media types to verify dual-record creation
            _logger.Debug("[DUAL-RECORD] Processing {0} books for author '{1}'", books.Count, author.Name);
            var booksByType = books.GroupBy(b => b.MediaType).Select(g => $"{g.Key}: {g.Count()}");
            _logger.Debug("[DUAL-RECORD] Book breakdown by MediaType: {0}", string.Join(", ", booksByType));

            // Log the discovery context
            if (config != null)
            {
                _logger.Debug("[DISCOVERY-CONTEXT] Author discovered via: AudiobookRoot={0}, EbookRoot={1}",
                    !string.IsNullOrWhiteSpace(config.AudiobookRootFolderPath) ? config.AudiobookRootFolderPath : "NOT SET",
                    !string.IsNullOrWhiteSpace(config.EbookRootFolderPath) ? config.EbookRootFolderPath : "NOT SET");
            }

            var allEditions = new List<Edition>();
            var booksToPersist = new List<Book>();
            var skippedMalformedBooks = 0;

            foreach (var book in books)
            {
                book.AuthorId = author.Id;
                book.Author = author;
                book.Added = DateTime.UtcNow;

                // Check if this is a specific book monitoring scenario. Import-list additions carry
                // per-media selected book IDs in AudiobookBooksToMonitor/EbookBooksToMonitor rather than
                // the manual-add SpecificBookProviderIds shape, so support both here.
                bool shouldMonitorThisBook = false;
                var specificBookProviderIds = GetSpecificBookProviderIdsForBook(config, book.MediaType);
                if (specificBookProviderIds.Any())
                {
                    _logger.Debug("[SPECIFIC-BOOK-TRACE] ProcessBooksForAuthor checking book '{0}' (HC:{1}, GR:{2}) against specific IDs",
                        book.Title, book.HardcoverBookId, book.GoodreadsBookId);
                    _logger.Debug("[SPECIFIC-BOOK-TRACE] SpecificBookProviderIds: {0}",
                        string.Join(", ", specificBookProviderIds));
                    
                    // Check if this book matches any of the specific provider IDs
                    foreach (var providerId in specificBookProviderIds)
                    {
                        var bookHcId = ExtractRawId(book.HardcoverBookId);
                        var bookGrId = ExtractRawId(book.GoodreadsBookId);
                        _logger.Debug("[SPECIFIC-BOOK-MATCH] Checking '{0}' against book '{1}' (HC:{2}, GR:{3})", 
                            providerId, book.Title, bookHcId, bookGrId);
                        
                        if (BookMatchesProviderId(book, providerId))
                        {
                            shouldMonitorThisBook = true;
                            _logger.Debug("[SPECIFIC-BOOK-MATCH] Book '{0}' matches provider ID '{1}' - will be monitored",
                                book.Title, providerId);
                            break;
                        }
                    }
                }

                // Seed the book row from the one-time current-catalog action. This is
                // deliberately independent of the author gate: turning a side off
                // pauses eligibility without rewriting its row selections.
                var monitorMode = GetInitialMonitorMode(config, book.MediaType);
                // Explicit provider-ID targets are an exact current-catalog intent and
                // take precedence over a root's generic None/All seed mode.
                var hasSpecificBookMode = specificBookProviderIds.Any() || monitorMode == MonitorTypes.SpecificBook;
                var monitorThisBook = hasSpecificBookMode
                    ? shouldMonitorThisBook
                    : ShouldMonitorCurrentBook(book, books, monitorMode);

                if (book.MediaType == BookMediaType.Audiobook)
                {
                    book.AudiobookMonitored = monitorThisBook;
                    book.EbookMonitored = false;
                }
                else if (book.MediaType == BookMediaType.Ebook)
                {
                    book.EbookMonitored = monitorThisBook;
                    book.AudiobookMonitored = false;
                }


                // Ensure clean IDs for insertion
                book.Id = 0;

                if (book.Editions == null || !book.Editions.Any())
                {
                    skippedMalformedBooks++;
                    _logger.Error("[REMOTE-BOOK-DATA] Skipping book '{0}' for author '{1}' because metadata returned no editions. ProviderId: {2}",
                        book.Title,
                        author.Name,
                        BookEditionIdentity.GetCanonicalWorkProviderIds(book).FirstOrDefault()
                        ?? BookEditionIdentity.GetCanonicalEditionProviderIds(book, _logger, "AuthorLibraryService.MissingEditions").FirstOrDefault()
                        ?? "NO_PROVIDER_ID");
                    continue;
                }

                booksToPersist.Add(book);

                foreach (var edition in book.Editions)
                {
                    edition.Id = 0;
                    edition.BookId = 0;
                    allEditions.Add(edition);
                }
            }

            books = booksToPersist;

            if (skippedMalformedBooks > 0)
            {
                _logger.Warn("[REMOTE-BOOK-DATA] Skipped {0} malformed books with no editions while adding author '{1}'", skippedMalformedBooks, author.Name);
                _logger.ProgressError("Metadata for author '{0}' omitted {1} books with no editions. See logs for details.", author.Name, skippedMalformedBooks);
            }

            if (!books.Any())
            {
                return Task.CompletedTask;
            }

            var bookInsertStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _bookService.InsertMany(books);
            bookInsertStopwatch.Stop();
            _logger.Debug("Successfully saved {0} books to database in {1}ms ({2}ms per book avg)",
                books.Count, bookInsertStopwatch.ElapsedMilliseconds,
                books.Count > 0 ? bookInsertStopwatch.ElapsedMilliseconds / books.Count : 0);
            _logger.Debug("[DB-TIMING] Book insertion: {0} books in {1}ms", books.Count, bookInsertStopwatch.ElapsedMilliseconds);

            // Save editions after books have been saved
            if (allEditions.Any())
            {
                // Update BookId for editions now that books have been saved
                var editionPrepStopwatch = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < books.Count; i++)
                {
                    var book = books[i];
                    if (book.Editions != null)
                    {
                        foreach (var edition in book.Editions)
                        {
                            edition.BookId = book.Id;
                        }
                    }
                }
                editionPrepStopwatch.Stop();

                var editionInsertStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _editionService.InsertMany(allEditions);
                editionInsertStopwatch.Stop();
                _logger.Debug("Successfully saved {0} editions to database in {1}ms ({2}ms per edition avg)",
                    allEditions.Count, editionInsertStopwatch.ElapsedMilliseconds,
                    allEditions.Count > 0 ? editionInsertStopwatch.ElapsedMilliseconds / allEditions.Count : 0);
                _logger.Debug("[DB-TIMING] Edition insertion: {0} editions in {1}ms (prep took {2}ms)",
                    allEditions.Count, editionInsertStopwatch.ElapsedMilliseconds, editionPrepStopwatch.ElapsedMilliseconds);
                
                // Special logging for large edition counts
                if (allEditions.Count > 100)
                {
                    _logger.Debug("[DB-TIMING][LARGE-EDITION-SET] Inserted {0} editions in {1}ms ({2}ms/edition, {3} editions/sec)",
                        allEditions.Count, 
                        editionInsertStopwatch.ElapsedMilliseconds,
                        editionInsertStopwatch.ElapsedMilliseconds / allEditions.Count,
                        (allEditions.Count * 1000.0) / editionInsertStopwatch.ElapsedMilliseconds);
                }

                // Persist narrator identity + edition links from upstream narrator credits/name lists.
                _narratorLinkService?.UpsertEditionNarratorLinks(allEditions);
            }

            // Select monitored editions based on type-specific metadata profiles
            SelectMonitoredEditions(books, author, config);

            // Rebuild book-level narrator links after monitored edition selection, so primary narrators
            // follow user/metadata-profile monitored choices.
            _narratorLinkService?.RebuildBookNarratorLinks(books.Select(b => b.Id));

            return Task.CompletedTask;
        }

        private static MonitorTypes? GetInitialMonitorMode(MonitoringConfig config, BookMediaType mediaType)
        {
            if (config == null)
            {
                return null;
            }

            var perMediaMode = mediaType == BookMediaType.Audiobook
                ? config.AudiobookMonitorExistingMode
                : config.EbookMonitorExistingMode;

            return perMediaMode ?? config.MonitorMode;
        }

        private static bool ShouldMonitorCurrentBook(Book book, List<Book> books, MonitorTypes? monitorMode)
        {
            if (book == null || !monitorMode.HasValue)
            {
                return false;
            }

            var sameMediaBooks = books
                .Where(candidate => candidate != null && candidate.MediaType == book.MediaType)
                .ToList();

            var hasFiles = book.HasFiles;
            switch (monitorMode.Value)
            {
                case MonitorTypes.All:
                    return true;
                case MonitorTypes.Future:
                    return !hasFiles && (!book.ReleaseDate.HasValue || book.ReleaseDate.Value > DateTime.UtcNow);
                case MonitorTypes.Missing:
                    return !hasFiles;
                case MonitorTypes.Existing:
                    return hasFiles;
                case MonitorTypes.Latest:
                    return sameMediaBooks
                        .OrderByDescending(candidate => candidate.ReleaseDate)
                        .ThenByDescending(candidate => candidate.Id)
                        .FirstOrDefault() == book;
                case MonitorTypes.First:
                    return sameMediaBooks
                        .OrderBy(candidate => candidate.ReleaseDate)
                        .ThenBy(candidate => candidate.Id)
                        .FirstOrDefault() == book;
                case MonitorTypes.None:
                case MonitorTypes.SpecificBook:
                case MonitorTypes.Unknown:
                default:
                    return false;
            }
        }

        private Task ProcessSeriesForAuthor(Author author, List<Series> series, MonitoringConfig config)
        {
            if (author == null || series == null || !series.Any())
            {
                return Task.CompletedTask;
            }

            // Enforce Goodreads-backed series only.
            var originalCount = series.Count;
            series = series
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.GoodreadsSeriesId))
                .ToList();

            if (series.Count != originalCount)
            {
                _logger.Debug("[SERIES] Filtered {0} non-Goodreads series for author '{1}' during persistence",
                    originalCount - series.Count, author.Name);
            }

            if (!series.Any())
            {
                return Task.CompletedTask;
            }

            var audiobookEnabled = GetEffectiveMetadataProfileId(author, config, BookMediaType.Audiobook).HasValue;
            var ebookEnabled = GetEffectiveMetadataProfileId(author, config, BookMediaType.Ebook).HasValue;

            var beforeProfileFilter = series.Count;
            series = series
                .Where(s =>
                    s != null &&
                    (s.MediaType != BookMediaType.Audiobook || audiobookEnabled) &&
                    (s.MediaType != BookMediaType.Ebook || ebookEnabled))
                .ToList();

            if (series.Count != beforeProfileFilter)
            {
                _logger.Debug("[METADATA-PROFILE] Filtered series for '{0}': {1} -> {2} based on enabled media types (audiobook={3}, ebook={4})",
                    author.Name, beforeProfileFilter, series.Count, audiobookEnabled, ebookEnabled);
            }

            if (!series.Any())
            {
                return Task.CompletedTask;
            }

            // Delegate to the canonical series refresh engine so repeated imports do not create duplicates,
            // and link reconciliation remains consistent with the rest of the app.
            _refreshSeriesService.RefreshSeriesInfo(author.Id, series, new Author { Id = author.Id }, false, false, null);

            return Task.CompletedTask;
        }
        private string ExtractRawId(string providerId)
        {
            if (string.IsNullOrEmpty(providerId))
                return string.Empty;
            
            // If the ID contains a colon, extract the part after it
            var colonIndex = providerId.IndexOf(':');
            if (colonIndex > 0)
                return providerId.Substring(colonIndex + 1);
            
            // Otherwise return the whole ID
            return providerId;
        }

        private IReadOnlyList<string> GetSpecificBookProviderIdsForBook(MonitoringConfig config, BookMediaType mediaType)
        {
            if (config == null)
            {
                return Array.Empty<string>();
            }

            if (GetInitialMonitorMode(config, mediaType) != MonitorTypes.SpecificBook)
            {
                return Array.Empty<string>();
            }

            if (mediaType == BookMediaType.Audiobook && config.AudiobookBooksToMonitor?.Any() == true)
            {
                return config.AudiobookBooksToMonitor;
            }

            if (mediaType == BookMediaType.Ebook && config.EbookBooksToMonitor?.Any() == true)
            {
                return config.EbookBooksToMonitor;
            }

            if (config.MonitorMode == MonitorTypes.SpecificBook && config.SpecificBookProviderIds?.Any() == true)
            {
                if (config.SpecificBookMediaType.HasValue && config.SpecificBookMediaType.Value != mediaType)
                {
                    return Array.Empty<string>();
                }

                return config.SpecificBookProviderIds.ToList();
            }

            return Array.Empty<string>();
        }

        private static Dictionary<BookMediaType, HashSet<string>> GetRequestedBookProviderIdsByMediaType(MonitoringConfig config)
        {
            var requested = new Dictionary<BookMediaType, HashSet<string>>();
            if (config == null)
            {
                return requested;
            }

            AddRequestedBookProviderIds(
                requested,
                BookMediaType.Audiobook,
                config.AudiobookBooksToMonitor,
                config.AudiobookBooksToSearch);
            AddRequestedBookProviderIds(
                requested,
                BookMediaType.Ebook,
                config.EbookBooksToMonitor,
                config.EbookBooksToSearch);

            if (config.SpecificBookProviderIds?.Any() == true)
            {
                if (config.SpecificBookMediaType.HasValue)
                {
                    AddRequestedBookProviderIds(requested, config.SpecificBookMediaType.Value, config.SpecificBookProviderIds);
                }
                else
                {
                    AddRequestedBookProviderIds(requested, BookMediaType.Audiobook, config.SpecificBookProviderIds);
                    AddRequestedBookProviderIds(requested, BookMediaType.Ebook, config.SpecificBookProviderIds);
                }
            }

            return requested;
        }

        private static void AddRequestedBookProviderIds(
            IDictionary<BookMediaType, HashSet<string>> requested,
            BookMediaType mediaType,
            params IEnumerable<string>[] providerIdLists)
        {
            var providerIds = providerIdLists
                .Where(ids => ids != null)
                .SelectMany(ids => ids)
                .Where(id => !string.IsNullOrWhiteSpace(id));

            foreach (var providerId in providerIds)
            {
                if (!requested.TryGetValue(mediaType, out var mediaTypeIds))
                {
                    mediaTypeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    requested[mediaType] = mediaTypeIds;
                }

                mediaTypeIds.Add(providerId);
            }
        }

        private bool BookMatchesProviderId(Book book, string providerId)
        {
            if (string.IsNullOrEmpty(providerId))
                return false;

            // Provider IDs are in format "prefix:id" (e.g., "hc:495645", "gr:12345")
            var parts = providerId.Split(':');
            if (parts.Length != 2)
                return false;

            // Normalize prefix and id for defensive parsing
            var prefix = parts[0].Trim().ToLowerInvariant();
            var id = parts[1].Trim();
            var normalizedProviderId = $"{prefix}:{id}";

            if (BookIdentity.GetProviderIdentityTokens(book).Contains(normalizedProviderId))
            {
                return true;
            }

            // Extract raw IDs from book fields (they might have prefixes)
            return prefix switch
            {
                "hc" => ExtractRawId(book.HardcoverBookId) == id,
                "gr" => BookEditionIdentity.GetCanonicalWorkProviderIds(book).Concat(BookEditionIdentity.GetCanonicalEditionProviderIds(book, _logger, "AuthorLibraryService.BookMatchesProviderId")).Any(p => ExtractRawId(p) == id),
                "ol" => ExtractRawId(book.OpenLibraryWorkId) == id,
                "gb" => BookEditionIdentity.GetCanonicalEditionProviderIds(book, _logger, "AuthorLibraryService.BookMatchesProviderId").Any(p => ExtractRawId(p) == id),
                "az" => BookEditionIdentity.GetCanonicalEditionProviderIds(book, _logger, "AuthorLibraryService.BookMatchesProviderId").Any(p => ExtractRawId(p).Equals(id, StringComparison.OrdinalIgnoreCase)),
                _ => false
            };
        }

        private void SelectMonitoredEditions(List<Book> books, Author author, MonitoringConfig config)
        {
            try
            {
                _logger.Debug("Selecting monitored editions for {0} books for author '{1}'", books.Count, author.Name);

                // Group books by media type
                var booksByMediaType = books.GroupBy(b => b.MediaType);

                foreach (var mediaTypeGroup in booksByMediaType)
                {
                    var mediaType = mediaTypeGroup.Key;
                    var booksOfType = mediaTypeGroup.ToList();

                    // Get the appropriate metadata profile for this media type (config overrides author).
                    var metadataProfileId = GetEffectiveMetadataProfileId(author, config, mediaType);
                    _logger.Debug("Using metadata profile {0} for {1} {2}s", metadataProfileId, booksOfType.Count, mediaType);

                    if (!metadataProfileId.HasValue)
                    {
                        _logger.Warn("Author '{0}' has no metadata profile, skipping edition selection for {1}", author.Name, mediaType);
                        continue;
                    }
                    
                    if (!_metadataProfileService.Exists(metadataProfileId.Value))
                    {
                        _logger.Warn("Metadata profile id {0} not found for author '{1}', skipping edition selection for {2}", metadataProfileId.Value, author.Name, mediaType);
                        continue;
                    }

                    // Process books of this media type with the appropriate profile
                    var metadataProfile = _metadataProfileService.Get(metadataProfileId.Value);
                    SelectMonitoredEditionsForMediaType(booksOfType, metadataProfile);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error selecting monitored editions");
            }
        }

        private void SelectMonitoredEditionsForMediaType(List<Book> books, MetadataProfile metadataProfile)
        {
            // PERFORMANCE: batch-load editions for this media type in one query.
            // The prior implementation did 1 query per book here, then another query inside SetMonitored().
            var bookIds = books.Select(b => b.Id).Where(id => id > 0).Distinct().ToList();
            if (!bookIds.Any())
            {
                return;
            }

            var allDbEditions = _editionService.GetEditionsByBook(bookIds);
            var editionsByBookId = allDbEditions
                .GroupBy(e => e.BookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var editionsToUpdate = new List<Edition>();
            var booksToUpdate = new List<Book>();

            foreach (var book in books)
            {
                if (book == null || book.Id <= 0)
                {
                    continue;
                }

                if (!editionsByBookId.TryGetValue(book.Id, out var dbEditions) || !dbEditions.Any())
                {
                    _logger.Debug("No editions found in database for book {0}", book.Title);
                    continue;
                }

                // Preserve user selection: if a manual edition is monitored, don't override it during automatic import.
                if (dbEditions.Any(e => e.Monitored && e.ManualAdd))
                {
                    _logger.Debug("Book {0} has a manually selected monitored edition, skipping automatic edition selection", book.Title);
                    continue;
                }

                var filteredEditions = _editionMetadataProfileFilter.Apply(dbEditions, metadataProfile);
                var retainedSelection = _editionSelector.SelectRetainedEditions(
                    book.MediaType,
                    filteredEditions);

                var candidateEditions = retainedSelection?.RetainedEditions?.ToList() ?? filteredEditions;
                if (!candidateEditions.Any())
                {
                    // If a book is now out-of-profile (e.g., user tightened AllowedLanguages), keep the existing
                    // selected edition and let the next author refresh prune the book (unless pinned by files/manual).
                    // BUT: never leave a book with 0 monitored editions, since many call sites assume a single
                    // monitored edition exists for display/notifications.
                    var existingSelected = dbEditions.FirstOrDefault(e => e.Monitored);
                    if (existingSelected != null)
                    {
                        _logger.Debug("No editions pass metadata profile filters for book '{0}'. Keeping existing selected edition '{1}' (EditionId={2}); book will be pruned on next refresh unless pinned.",
                            book.Title, existingSelected.Title, existingSelected.Id);
                        continue;
                    }

                    _logger.Warn("No editions pass metadata profile filters for book '{0}' and no edition is currently monitored; selecting fallback edition to satisfy invariant",
                        book.Title);
                    candidateEditions = dbEditions;
                }

                var selectedEdition = _editionSelector.SelectBestEdition(candidateEditions, book.MediaType);

                if (selectedEdition == null)
                {
                    continue;
                }

                _logger.Debug("Setting edition {0} ({1}) as monitored for book {2}", selectedEdition.Title, selectedEdition.Format, book.Title);

                // Update monitored flags in-memory and persist as a single batch.
                // Only update rows that actually change.
                foreach (var edition in dbEditions)
                {
                    var shouldBeMonitored = edition.Id == selectedEdition.Id;
                    if (edition.Monitored != shouldBeMonitored)
                    {
                        edition.Monitored = shouldBeMonitored;
                        editionsToUpdate.Add(edition);
                    }
                }

                if (selectedEdition.ForeignEditionId.IsNotNullOrWhiteSpace() &&
                    !string.Equals(book.ForeignEditionId, selectedEdition.ForeignEditionId, StringComparison.OrdinalIgnoreCase))
                {
                    book.ForeignEditionId = selectedEdition.ForeignEditionId;
                    booksToUpdate.Add(book);
                }
            }

            if (editionsToUpdate.Any())
            {
                _editionService.UpdateMany(editionsToUpdate);
            }

            if (booksToUpdate.Any())
            {
                _bookService.UpdateMany(booksToUpdate);
            }
        }
    }
}
