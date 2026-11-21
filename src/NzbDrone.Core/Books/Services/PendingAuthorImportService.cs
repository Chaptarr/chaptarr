using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using NLog;
using Npgsql;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books.Services
{
    public interface IPendingAuthorImportService
    {
        Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication);
        List<PendingAuthorImport> GetAll();
        PendingAuthorImport Get(int id);
        PagingSpec<PendingAuthorImport> GetPaged(PagingSpec<PendingAuthorImport> pagingSpec);
        List<PendingAuthorImport> GetDueForProcessing(int limit = 10);
        PendingAuthorImport GetByProviderId(string providerId);
        bool UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error, long? expectedVersion = null);
        bool SaveProgress(PendingAuthorImport item, PendingAuthorImport previous);
        void ScheduleRetry(PendingAuthorImport item, string error);
        void Cancel(int id);
        bool IsActiveRequest(int id, long version);
        void RetryNow(int id);
        void CleanupOldCompleted();
        void Delete(int id);
        bool TryDeleteIfUnchanged(PendingAuthorImport item)
        {
            Delete(item.Id);
            return true;
        }
    }

    public class PendingAuthorImportService : IPendingAuthorImportService, IHandle<AuthorDeletedEvent>, IHandle<AuthorEditedEvent>,
                                             IHandle<AuthorSetupMetadataRefusedEvent>, IHandle<PendingAuthorImportFailedEvent>, IHandle<PendingAuthorImportCancelledEvent>
    {
        private readonly IPendingAuthorImportRepository _repository;
        private readonly IAuthorService _authorService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly IProviderAliasService _providerAliases;

        public PendingAuthorImportService(
            IPendingAuthorImportRepository repository,
            IAuthorService authorService,
            IEventAggregator eventAggregator,
            Logger logger,
            IProviderAliasService providerAliases = null)
        {
            _repository = repository;
            _authorService = authorService;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _providerAliases = providerAliases;
        }

        public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
        {
            // Normalize and validate provider ID
            providerId = NormalizeProviderId(providerId);
            config ??= new MonitoringConfig();
            config.AudiobookNarrations = config.AudiobookNarrations?.Where(request => request != null).ToList();
            config.RequestedBy ??= sourceApplication;
            var explicitSearches = GetExplicitSearches(config);
            var incomingAudiobookTags = ResolveMediaTags(config.CreateAudiobook, config.AudiobookTags, config.Tags);
            var incomingEbookTags = ResolveMediaTags(config.CreateEbook, config.EbookTags, config.Tags);

            // Existing authors can still need catalog rescue or first-time media settings.
            // Skip author-only requests only when no supplied format setup is missing.
            if (!HasRequestedBooks(config))
            {
                try
                {
                    var prefix = ExtractPrefix(providerId);
                    var rawId = ExtractIdWithoutPrefix(providerId);
                    var existingAuthor = _authorService.FindByProviderId(prefix, rawId);
                    if (existingAuthor != null &&
                        existingAuthor.AddOptions?.AwaitingMetadata != true &&
                        !(config.CreateAudiobook && !existingAuthor.HasMediaTypeSettings(BookMediaType.Audiobook) &&
                            config.AudiobookQualityProfileId.GetValueOrDefault() > 0 && config.AudiobookMetadataProfileId.GetValueOrDefault() > 0 &&
                            !string.IsNullOrWhiteSpace(config.AudiobookRootFolderPath) ||
                          config.CreateEbook && !existingAuthor.HasMediaTypeSettings(BookMediaType.Ebook) &&
                            config.EbookQualityProfileId.GetValueOrDefault() > 0 && config.EbookMetadataProfileId.GetValueOrDefault() > 0 &&
                            !string.IsNullOrWhiteSpace(config.EbookRootFolderPath)))
                    {
                        _logger.Debug("[PENDING-IMPORT] Author already exists in database, skipping author-only queue: {0} (ID: {1})", providerId, existingAuthor.Id);
                        return Task.FromResult(0);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[PENDING-IMPORT] Failed to check existing author for {0}; proceeding cautiously", providerId);
                }
            }

            while (true)
            {
                // Check for existing active pending
                var existing = _repository.GetActiveByProviderId(providerId);
                if (existing != null)
                {
                    _logger.Debug("Found existing pending import for {0}, merging configuration", providerId);

                    // Keep explicit author setup choices when automatic list demand shares this request.
                    var authored = existing.IsAuthorSettingsRequest && !config.IsAuthorSettingsRequest;
                    var initialChoices = (existing.AudiobookMonitorExistingMode, existing.EbookMonitorExistingMode);
                    var newChoices = (existing.AudiobookMonitorNewItems, existing.EbookMonitorNewItems);
                    var gates = (existing.AudiobookMonitored, existing.EbookMonitored);
                    // Merge compatible settings (widen scope if needed)
                    bool updated = false;
                    var explicitJson = RequestedBookSearch.Write(RequestedBookSearch.Read(existing.ExplicitBookSearches).Concat(explicitSearches));
                    if (explicitJson != (existing.ExplicitBookSearches ?? "[]"))
                    {
                        existing.ExplicitBookSearches = explicitJson;
                        updated = true;
                    }
                    var nameChanged = !string.IsNullOrWhiteSpace(config.AuthorName) && config.AuthorName != existing.AuthorName;
                    if (nameChanged) existing.AuthorName = config.AuthorName;
                    if (config.CreateAudiobook && existing.HasAudiobook() && existing.AudiobookBooksToMonitor == null && config.AudiobookBooksToMonitor != null ||
                        config.CreateEbook && existing.HasEbook() && existing.EbookBooksToMonitor == null && config.EbookBooksToMonitor != null)
                    {
                        // Preserve legacy typed monitoring before an explicit incoming phase separates new search-only demand.
                        var legacyMonitoring = ParseRequestedNarrations(existing.AudiobookNarrations, activeRequest: true)
                            .ToLookup(request => request.MediaType, request => request.WorkProviderId);
                        if (config.CreateAudiobook && existing.HasAudiobook() && existing.AudiobookBooksToMonitor == null &&
                            config.AudiobookBooksToMonitor != null && legacyMonitoring[BookMediaType.Audiobook].Any())
                        {
                            existing.AudiobookBooksToMonitor = JsonConvert.SerializeObject(legacyMonitoring[BookMediaType.Audiobook].Distinct(StringComparer.OrdinalIgnoreCase));
                            updated = true;
                        }
                        if (config.CreateEbook && existing.HasEbook() && existing.EbookBooksToMonitor == null &&
                            config.EbookBooksToMonitor != null && legacyMonitoring[BookMediaType.Ebook].Any())
                        {
                            existing.EbookBooksToMonitor = JsonConvert.SerializeObject(legacyMonitoring[BookMediaType.Ebook].Distinct(StringComparer.OrdinalIgnoreCase));
                            updated = true;
                        }
                    }
                    if (config.AudiobookNarrations?.Any() == true)
                    {
                        var json = MergeRequestedNarrations(existing.AudiobookNarrations, config.AudiobookNarrations);
                        if (json != existing.AudiobookNarrations)
                        {
                            existing.AudiobookNarrations = json;
                            updated = true;
                        }
                    }

                    // Explicit requests take precedence over automatic list demand when they share a queue row.
                    // Preserve each side's origin before changing the row's display origin.
                    var audioOrigin = MergeRequestOrigin(existing.HasAudiobook() ? existing.AudiobookRequestedBy ?? existing.RequestedBy : null,
                        config.CreateAudiobook ? config.AudiobookRequestedBy ?? config.RequestedBy : null);
                    var ebookOrigin = MergeRequestOrigin(existing.HasEbook() ? existing.EbookRequestedBy ?? existing.RequestedBy : null,
                        config.CreateEbook ? config.EbookRequestedBy ?? config.RequestedBy : null);
                    if (existing.AudiobookRequestedBy != audioOrigin) { existing.AudiobookRequestedBy = audioOrigin; updated = true; }
                    if (existing.EbookRequestedBy != ebookOrigin) { existing.EbookRequestedBy = ebookOrigin; updated = true; }
                    if (MonitoringConfig.IsImportListRequestOrigin(existing.RequestedBy) &&
                        !string.IsNullOrWhiteSpace(config.RequestedBy) &&
                        !MonitoringConfig.IsImportListRequestOrigin(config.RequestedBy))
                    {
                        existing.RequestedBy = config.RequestedBy;
                        updated = true;
                    }

                    // Enable audiobook if requested and not already
                    if (config.CreateAudiobook && !existing.HasAudiobook())
                    {
                        existing.AudiobookStatus = PendingImportStatus.Pending;
                        existing.AudiobookMonitored = config.AudiobookMonitored;
                        existing.AudiobookMonitorNewItems = config.AudiobookMonitorNewItems;
                        existing.AudiobookMonitorExistingMode = config.AudiobookMonitorExistingMode;
                        existing.AudiobookQualityProfileId = config.AudiobookQualityProfileId;
                        existing.AudiobookMetadataProfileId = config.AudiobookMetadataProfileId;
                        existing.AudiobookRootFolderPath = config.AudiobookRootFolderPath;

                        if (config.AudiobookBooksToMonitor != null)
                        {
                            existing.AudiobookBooksToMonitor = JsonConvert.SerializeObject(config.AudiobookBooksToMonitor);
                        }
                        updated = true;
                    }
                    else if (config.CreateAudiobook && existing.HasAudiobook() && TryMergeProviderIds(
                            existing.AudiobookBooksToMonitor,
                            config.AudiobookBooksToMonitor,
                            nameof(existing.AudiobookBooksToMonitor),
                            providerId,
                            out var audiobookBooksToMonitor, config.AudiobookBooksToAdd))
                    {
                        existing.AudiobookBooksToMonitor = audiobookBooksToMonitor;
                        updated = true;
                    }

                    if (config.CreateAudiobook && TryMergeProviderIds(existing.AudiobookBooksToAdd,
                            config.AudiobookBooksToAdd, nameof(existing.AudiobookBooksToAdd), providerId, out var audiobookBooksToAdd, config.AudiobookBooksToMonitor))
                    {
                        existing.AudiobookBooksToAdd = audiobookBooksToAdd;
                        updated = true;
                    }

                    if (config.CreateAudiobook && MergeExistingMonitorMode(
                            existing.AudiobookMonitorExistingMode,
                            config.AudiobookMonitorExistingMode,
                            out var audiobookMonitorExistingMode))
                    {
                        existing.AudiobookMonitorExistingMode = audiobookMonitorExistingMode;
                        updated = true;
                    }

                    // An additional request may enable the side, but never turns an
                    // already-enabled side off. This is important when two exact-book
                    // requests for the same unavailable author merge into one row.
                    if (config.CreateAudiobook && config.AudiobookMonitored == true && existing.AudiobookMonitored != true)
                    {
                        existing.AudiobookMonitored = true;
                        updated = true;
                    }

                    if (config.CreateAudiobook && MergeNewItemMonitorType(
                            existing.AudiobookMonitorNewItems,
                            config.AudiobookMonitorNewItems,
                            out var audiobookMonitorNewItems))
                    {
                        existing.AudiobookMonitorNewItems = audiobookMonitorNewItems;
                        updated = true;
                    }

                    if (config.CreateAudiobook && TryMergeTags(
                            existing.AudiobookTags,
                            incomingAudiobookTags,
                            nameof(existing.AudiobookTags),
                            providerId,
                            out var audiobookTags))
                    {
                        existing.AudiobookTags = audiobookTags;
                        updated = true;
                    }

                    // Enable ebook if requested and not already
                    if (config.CreateEbook && !existing.HasEbook())
                    {
                        existing.EbookStatus = PendingImportStatus.Pending;
                        existing.EbookMonitored = config.EbookMonitored;
                        existing.EbookMonitorNewItems = config.EbookMonitorNewItems;
                        existing.EbookMonitorExistingMode = config.EbookMonitorExistingMode;
                        existing.EbookQualityProfileId = config.EbookQualityProfileId;
                        existing.EbookMetadataProfileId = config.EbookMetadataProfileId;
                        existing.EbookRootFolderPath = config.EbookRootFolderPath;

                        if (config.EbookBooksToMonitor != null)
                        {
                            existing.EbookBooksToMonitor = JsonConvert.SerializeObject(config.EbookBooksToMonitor);
                        }
                        updated = true;
                    }
                    else if (config.CreateEbook && existing.HasEbook() && TryMergeProviderIds(
                            existing.EbookBooksToMonitor,
                            config.EbookBooksToMonitor,
                            nameof(existing.EbookBooksToMonitor),
                            providerId,
                            out var ebookBooksToMonitor, config.EbookBooksToAdd))
                    {
                        existing.EbookBooksToMonitor = ebookBooksToMonitor;
                        updated = true;
                    }

                    if (config.CreateAudiobook && TryMergeProviderIds(
                            existing.AudiobookBooksToSearch,
                            config.AudiobookBooksToSearch,
                            nameof(existing.AudiobookBooksToSearch),
                            providerId,
                            out var audiobookBooksToSearch))
                    {
                        existing.AudiobookBooksToSearch = audiobookBooksToSearch;
                        updated = true;
                    }

                    if (config.CreateEbook && config.EbookMonitored == true && existing.EbookMonitored != true)
                    {
                        existing.EbookMonitored = true;
                        updated = true;
                    }

                    if (config.CreateEbook && MergeNewItemMonitorType(
                            existing.EbookMonitorNewItems,
                            config.EbookMonitorNewItems,
                            out var ebookMonitorNewItems))
                    {
                        existing.EbookMonitorNewItems = ebookMonitorNewItems;
                        updated = true;
                    }

                    if (config.CreateEbook && TryMergeProviderIds(existing.EbookBooksToAdd,
                            config.EbookBooksToAdd, nameof(existing.EbookBooksToAdd), providerId, out var ebookBooksToAdd, config.EbookBooksToMonitor))
                    {
                        existing.EbookBooksToAdd = ebookBooksToAdd;
                        updated = true;
                    }

                    if (config.CreateEbook && MergeExistingMonitorMode(
                            existing.EbookMonitorExistingMode,
                            config.EbookMonitorExistingMode,
                            out var ebookMonitorExistingMode))
                    {
                        existing.EbookMonitorExistingMode = ebookMonitorExistingMode;
                        updated = true;
                    }

                    if (config.CreateEbook && TryMergeProviderIds(
                            existing.EbookBooksToSearch,
                            config.EbookBooksToSearch,
                            nameof(existing.EbookBooksToSearch),
                            providerId,
                            out var ebookBooksToSearch))
                    {
                        existing.EbookBooksToSearch = ebookBooksToSearch;
                        updated = true;
                    }

                    if (config.CreateEbook && TryMergeTags(
                            existing.EbookTags,
                            incomingEbookTags,
                            nameof(existing.EbookTags),
                            providerId,
                            out var ebookTags))
                    {
                        existing.EbookTags = ebookTags;
                        updated = true;
                    }

                    if (TryMergeTags(existing.Tags, config.Tags, nameof(existing.Tags), providerId, out var tags))
                    {
                        existing.Tags = tags;
                        updated = true;
                    }

                    if (config.SearchForMissingBooks == true && !existing.SearchForMissingBooks)
                    {
                        existing.SearchForMissingBooks = true;
                        updated = true;
                    }

                    if (!string.IsNullOrWhiteSpace(config.LastSelectedMediaType) &&
                        !string.Equals(existing.LastSelectedMediaType, config.LastSelectedMediaType, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.LastSelectedMediaType = config.LastSelectedMediaType;
                        updated = true;
                    }

                    // Preserve discovered author folder path if provided and not already set
                    if (string.IsNullOrWhiteSpace(existing.DiscoveredAuthorFolderPath) && !string.IsNullOrWhiteSpace(config.DiscoveredAuthorFolderPath))
                    {
                        existing.DiscoveredAuthorFolderPath = config.DiscoveredAuthorFolderPath;
                        updated = true;
                    }

                    if (authored)
                    {
                        existing.AudiobookMonitorExistingMode = initialChoices.AudiobookMonitorExistingMode ?? existing.AudiobookMonitorExistingMode;
                        existing.EbookMonitorExistingMode = initialChoices.EbookMonitorExistingMode ?? existing.EbookMonitorExistingMode;
                        existing.AudiobookMonitorNewItems = newChoices.AudiobookMonitorNewItems ?? existing.AudiobookMonitorNewItems;
                        existing.EbookMonitorNewItems = newChoices.EbookMonitorNewItems ?? existing.EbookMonitorNewItems;
                        existing.AudiobookMonitored = gates.AudiobookMonitored ?? existing.AudiobookMonitored;
                        existing.EbookMonitored = gates.EbookMonitored ?? existing.EbookMonitored;
                    }
                    if ((config.IsAuthorSettingsRequest || config.RequestedBy == MonitoringConfig.NativeAuthorAddOrigin) &&
                        config.SyncMonitoredAcrossFormats.HasValue && existing.SyncMonitoredAcrossFormats != config.SyncMonitoredAcrossFormats)
                    {
                        existing.SyncMonitoredAcrossFormats = config.SyncMonitoredAcrossFormats;
                        updated = true;
                    }
                    if (config.IsAuthorSettingsRequest)
                    {
                        if (!existing.IsAuthorSettingsRequest) { existing.IsAuthorSettingsRequest = true; updated = true; }
                        if (existing.SyncMonitoredAcrossFormats == true)
                        {
                            // Pending requests accumulate demand through the existing widening
                            // rules. Keep the resulting choices shared without narrowing them.
                            MergeExistingMonitorMode(existing.AudiobookMonitorExistingMode, existing.EbookMonitorExistingMode, out var initial);
                            MergeExistingMonitorMode(initial, config.AudiobookMonitorExistingMode ?? config.EbookMonitorExistingMode, out initial);
                            MergeNewItemMonitorType(existing.AudiobookMonitorNewItems, existing.EbookMonitorNewItems, out var ongoing);
                            MergeNewItemMonitorType(ongoing, config.AudiobookMonitorNewItems ?? config.EbookMonitorNewItems, out ongoing);
                            if (existing.HasAudiobook() && existing.AudiobookMonitorExistingMode != initial) { existing.AudiobookMonitorExistingMode = initial; updated = true; }
                            if (existing.HasEbook() && existing.EbookMonitorExistingMode != initial) { existing.EbookMonitorExistingMode = initial; updated = true; }
                            if (existing.AudiobookMonitorNewItems != ongoing) { existing.AudiobookMonitorNewItems = ongoing; updated = true; }
                            if (existing.EbookMonitorNewItems != ongoing) { existing.EbookMonitorNewItems = ongoing; updated = true; }
                        }
                    }

                    if (updated || nameChanged)
                    {
                        existing.UpdatedAt = DateTime.UtcNow;
                        if (updated)
                        {
                            // Changed request intent needs processing; a display-name update preserves the schedule.
                            existing.NextAttemptAt = DateTime.UtcNow;
                            existing.UpdateOverallStatus();
                        }
                        var expectedVersion = existing.Version;
                        if (_repository.TryUpdateRequest(existing, expectedVersion))
                        {
                            return Task.FromResult(existing.Id);
                        }

                        // Another request changed this row after it was read. Reload and merge the
                        // incoming targets into the newer value instead of overwriting them.
                        continue;
                    }

                    // Nothing changed; return existing pending ID so caller can inform user.
                    // Every incoming target was already present in this snapshot.
                    return Task.FromResult(existing.Id);
                }

                // Create new pending record
                var pending = new PendingAuthorImport
                {
                    ProviderId = providerId,
                    ProviderPrefix = ExtractPrefix(providerId),
                    AuthorName = string.IsNullOrWhiteSpace(config.AuthorName) ? null : config.AuthorName,
                    DiscoveredAuthorFolderPath = config.DiscoveredAuthorFolderPath,
                    CreatedAt = DateTime.UtcNow,
                    NextAttemptAt = DateTime.UtcNow,
                    // Zero is the persisted/API representation for an unbounded retry lifecycle.
                    // Declared terminal outcomes are stopped by ProcessPendingImportsCommandHandler;
                    // transient "not ready" states must not age into a synthetic terminal.
                    MaxAttempts = 0,
                    SourceApplication = sourceApplication,
                    RequestedBy = config.RequestedBy,
                    ExplicitBookSearches = RequestedBookSearch.Write(explicitSearches),
                    AudiobookRequestedBy = config.CreateAudiobook ? config.AudiobookRequestedBy ?? config.RequestedBy : null,
                    EbookRequestedBy = config.CreateEbook ? config.EbookRequestedBy ?? config.RequestedBy : null,
                    SearchForMissingBooks = config.SearchForMissingBooks ?? false,
                    SyncMonitoredAcrossFormats = config.SyncMonitoredAcrossFormats,
                    IsAuthorSettingsRequest = config.IsAuthorSettingsRequest,
                    AudiobookMonitorNewItems = config.AudiobookMonitorNewItems,
                    EbookMonitorNewItems = config.EbookMonitorNewItems,
                    LastSelectedMediaType = config.LastSelectedMediaType,
                    Version = 1
                };

                // Set audiobook configuration if requested
                if (config.CreateAudiobook)
                {
                    pending.AudiobookStatus = PendingImportStatus.Pending;
                    pending.AudiobookMonitored = config.AudiobookMonitored;
                    pending.AudiobookMonitorNewItems = config.AudiobookMonitorNewItems;
                    pending.AudiobookMonitorExistingMode = config.AudiobookMonitorExistingMode;
                    pending.AudiobookQualityProfileId = config.AudiobookQualityProfileId;
                    pending.AudiobookMetadataProfileId = config.AudiobookMetadataProfileId;
                    pending.AudiobookRootFolderPath = config.AudiobookRootFolderPath;
                    pending.AudiobookBooksToAdd = config.AudiobookBooksToAdd == null ? null : JsonConvert.SerializeObject(config.AudiobookBooksToAdd);
                    pending.AudiobookTags = SerializeTags(incomingAudiobookTags);

                    if (config.AudiobookBooksToMonitor != null)
                    {
                        pending.AudiobookBooksToMonitor = JsonConvert.SerializeObject(config.AudiobookBooksToMonitor);
                    }

                    if (config.AudiobookBooksToSearch?.Any() == true)
                    {
                        pending.AudiobookBooksToSearch = JsonConvert.SerializeObject(config.AudiobookBooksToSearch);
                    }
                }

                // Set ebook configuration if requested
                if (config.CreateEbook)
                {
                    pending.EbookStatus = PendingImportStatus.Pending;
                    pending.EbookMonitored = config.EbookMonitored;
                    pending.EbookMonitorNewItems = config.EbookMonitorNewItems;
                    pending.EbookMonitorExistingMode = config.EbookMonitorExistingMode;
                    pending.EbookQualityProfileId = config.EbookQualityProfileId;
                    pending.EbookMetadataProfileId = config.EbookMetadataProfileId;
                    pending.EbookRootFolderPath = config.EbookRootFolderPath;
                    pending.EbookBooksToAdd = config.EbookBooksToAdd == null ? null : JsonConvert.SerializeObject(config.EbookBooksToAdd);
                    pending.EbookTags = SerializeTags(incomingEbookTags);

                    if (config.EbookBooksToMonitor != null)
                    {
                        pending.EbookBooksToMonitor = JsonConvert.SerializeObject(config.EbookBooksToMonitor);
                    }

                    if (config.EbookBooksToSearch?.Any() == true)
                    {
                        pending.EbookBooksToSearch = JsonConvert.SerializeObject(config.EbookBooksToSearch);
                    }
                }

                pending.AudiobookNarrations = config.AudiobookNarrations?.Any() == true ? JsonConvert.SerializeObject(config.AudiobookNarrations) : null;

                // Set common fields
                if (config.Tags != null)
                {
                    pending.Tags = SerializeTags(config.Tags);
                }

                pending.UpdateOverallStatus();

                try
                {
                    var failed = FindFailedRequest(pending);
                    if (failed != null)
                    {
                        pending.Id = failed.Id;
                        pending.CreatedAt = failed.CreatedAt;
                        pending.UpdatedAt = DateTime.UtcNow;
                        if (string.IsNullOrWhiteSpace(pending.AuthorName)) pending.AuthorName = failed.AuthorName;
                        pending.SourceApplication = failed.SourceApplication;
                        pending.CorrelationId = failed.CorrelationId;
                        if (config.AudiobookNarrations?.Any() == true)
                        {
                            pending.AudiobookNarrations = MergeRequestedNarrations(failed.AudiobookNarrations, config.AudiobookNarrations);
                        }
                        if (!_repository.TryUpdateRequest(pending, failed.Version))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        _repository.Insert(pending);
                    }

                    _logger.Info("Queued author {0} for pending import (ID: {1}), NextAttemptAt={2:o}", providerId, pending.Id, pending.NextAttemptAt);

                    // Fire event for UI updates
                    _eventAggregator.PublishEvent(new PendingAuthorImportQueuedEvent(pending));

                    return Task.FromResult(pending.Id);
                }
                catch (SqliteException ex) when (IsActiveProviderUniqueViolation(ex))
                {
                    // A concurrent request inserted the active row. Reload it and merge.
                }
                catch (PostgresException ex) when (IsActiveProviderUniqueViolation(ex))
                {
                    // A concurrent request inserted the active row. Reload it and merge.
                }
            }
        }

        private static List<RequestedBookSearch> GetExplicitSearches(MonitoringConfig config)
        {
            // Routed requests carry their original per-target permission, never the row's display origin.
            if (config.ExplicitBookSearches != null) return config.ExplicitBookSearches;
            if (config.IsFromQueue || config.IsAuthorSettingsRequest) return new();
            var searches = new List<RequestedBookSearch>();
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                var audio = media == BookMediaType.Audiobook;
                if (!(audio ? config.CreateAudiobook : config.CreateEbook) ||
                    MonitoringConfig.IsImportListRequestOrigin((audio ? config.AudiobookRequestedBy : config.EbookRequestedBy) ?? config.RequestedBy)) continue;
                var ids = ((audio ? config.AudiobookBooksToSearch : config.EbookBooksToSearch) ?? new())
                    .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var narration in (config.AudiobookNarrations ?? new()).Where(request => request != null && request.MediaType == media && !string.IsNullOrWhiteSpace(request.WorkProviderId)))
                {
                    var genericSearch = ids.Remove(narration.WorkProviderId);
                    if (narration.Search || genericSearch) searches.Add(new RequestedBookSearch
                    {
                        MediaType = media, WorkProviderId = narration.WorkProviderId, EditionProviderId = narration.EditionProviderId
                    });
                }
                searches.AddRange(ids.Select(id => new RequestedBookSearch { MediaType = media, WorkProviderId = id }));
            }
            return searches;
        }

        private PendingAuthorImport FindFailedRequest(PendingAuthorImport incoming)
        {
            var failedRequests = _repository.GetFailedByProviderId(incoming.ProviderId);
            if (failedRequests.Count == 0)
            {
                return null;
            }

            var audiobookAdd = ParseRequestedProviderIds(incoming.AudiobookBooksToAdd);
            var audioMonitor = ParseRequestedProviderIds(incoming.AudiobookBooksToMonitor);
            var audioSearch = ParseRequestedProviderIds(incoming.AudiobookBooksToSearch);
            var ebookAdd = ParseRequestedProviderIds(incoming.EbookBooksToAdd);
            var ebookMonitor = ParseRequestedProviderIds(incoming.EbookBooksToMonitor);
            var ebookSearch = ParseRequestedProviderIds(incoming.EbookBooksToSearch);
            var narrations = ParseRequestedNarrations(incoming.AudiobookNarrations);
            var incomingExplicit = RequestedBookSearch.Read(incoming.ExplicitBookSearches);
            foreach (var failed in failedRequests)
            {
                if (failed.HasAudiobook() != incoming.HasAudiobook() || failed.HasEbook() != incoming.HasEbook() ||
                    failed.SearchForMissingBooks != incoming.SearchForMissingBooks ||
                    incoming.HasAudiobook() && MonitoringConfig.IsImportListRequestOrigin(failed.AudiobookRequestedBy ?? failed.RequestedBy) != MonitoringConfig.IsImportListRequestOrigin(incoming.AudiobookRequestedBy ?? incoming.RequestedBy) ||
                    incoming.HasEbook() && MonitoringConfig.IsImportListRequestOrigin(failed.EbookRequestedBy ?? failed.RequestedBy) != MonitoringConfig.IsImportListRequestOrigin(incoming.EbookRequestedBy ?? incoming.RequestedBy))
                {
                    continue;
                }

                try
                {
                    var failedExplicit = RequestedBookSearch.Read(failed.ExplicitBookSearches);
                    if (incomingExplicit.Count != failedExplicit.Count || incomingExplicit.Any(request => !failedExplicit.Any(request.Matches))) continue;
                    if (audioMonitor.SetEquals(ParseRequestedProviderIds(failed.AudiobookBooksToMonitor)) &&
                        audioSearch.SetEquals(ParseRequestedProviderIds(failed.AudiobookBooksToSearch)) &&
                        ebookMonitor.SetEquals(ParseRequestedProviderIds(failed.EbookBooksToMonitor)) &&
                        ebookSearch.SetEquals(ParseRequestedProviderIds(failed.EbookBooksToSearch)) &&
                        audiobookAdd.SetEquals(ParseRequestedProviderIds(failed.AudiobookBooksToAdd)) &&
                        ebookAdd.SetEquals(ParseRequestedProviderIds(failed.EbookBooksToAdd)) &&
                        HaveSameRequestedNarrations(narrations, ParseRequestedNarrations(failed.AudiobookNarrations)))
                    {
                        return failed;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.Warn(ex, "Unable to compare failed pending import {0}; preserving its recorded request", failed.Id);
                }
            }

            return null;
        }

        private static HashSet<string> ParseRequestedProviderIds(string json)
        {
            return (string.IsNullOrWhiteSpace(json) ? new List<string>() : JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<RequestedNarration> ParseRequestedNarrations(string json, bool activeRequest = false)
        {
            var requests = string.IsNullOrWhiteSpace(json) ? new List<RequestedNarration>() : JsonConvert.DeserializeObject<List<RequestedNarration>>(json) ?? new List<RequestedNarration>();
            if (!activeRequest && requests.Any(request => request == null))
            {
                throw new JsonSerializationException("Requested narration history contains a null request.");
            }

            return requests
                .Where(request => request != null)
                .OrderBy(request => request.MediaType)
                .ThenBy(request => request.WorkProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(request => request.EditionProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(request => request.Search).ToList();
        }

        private static string MergeRequestedNarrations(string savedJson, IEnumerable<RequestedNarration> incoming)
        {
            var saved = string.IsNullOrEmpty(savedJson) ? new List<RequestedNarration>() :
                JsonConvert.DeserializeObject<List<RequestedNarration>>(savedJson) ?? new List<RequestedNarration>();
            var merged = saved.Concat(incoming).Where(request => request != null)
                .GroupBy(request => (request.MediaType, request.WorkProviderId.ToLowerInvariant(), request.EditionProviderId.ToLowerInvariant()))
                .Select(group => new RequestedNarration
                {
                    MediaType = group.Key.MediaType,
                    WorkProviderId = group.First().WorkProviderId,
                    EditionProviderId = group.First().EditionProviderId,
                    NarratorNames = group.LastOrDefault(request => request.NarratorNames?.Count > 0)?.NarratorNames.ToList() ?? new List<string>(),
                    RequiresEdition = group.Any(request => request.RequiresEdition),
                    Search = group.Any(request => request.Search)
                }).ToList();
            return JsonConvert.SerializeObject(merged);
        }

        private static bool HaveSameRequestedNarrations(List<RequestedNarration> incoming, List<RequestedNarration> saved)
        {
            return incoming.Count == saved.Count && incoming.Zip(saved).All(pair =>
                SameNarrationTarget(pair.First, pair.Second) &&
                pair.First.Search == pair.Second.Search &&
                // Metadata may have filled an absent cast after the request was queued.
                (pair.First.NarratorNames?.Count > 0 != true || pair.Second.NarratorNames?.Count > 0 != true ||
                 pair.First.NarratorNames.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(pair.Second.NarratorNames)));
        }

        private static bool HasRequestedBooks(MonitoringConfig config)
        {
            return config?.EbookBooksToAdd?.Any() == true || config?.AudiobookBooksToAdd?.Any() == true || config?.AudiobookNarrations?.Any() == true || config?.AudiobookBooksToMonitor?.Any() == true ||
                   config?.AudiobookBooksToSearch?.Any() == true ||
                   config?.EbookBooksToMonitor?.Any() == true ||
                   config?.EbookBooksToSearch?.Any() == true;
        }

        // A queued request can combine several lists or user actions, and the widest monitor choice wins without recording who asked for it.
        // I'll revisit this on the next update after considering how to handle colliding user preferences and options.
        private static bool MergeExistingMonitorMode(
            MonitorTypes? existing,
            MonitorTypes? incoming,
            out MonitorTypes? merged)
        {
            merged = existing;
            if (!incoming.HasValue)
            {
                return false;
            }

            if (!existing.HasValue || existing == MonitorTypes.None)
            {
                merged = incoming;
                return existing != incoming;
            }

            // An all-books seed is the strongest current-book intent and must not
            // be narrowed by a later exact/none request. Other existing modes are
            // retained unless the incoming request explicitly widens them to All.
            if (incoming == MonitorTypes.All && existing != MonitorTypes.All)
            {
                merged = MonitorTypes.All;
                return true;
            }

            return false;
        }

        private static bool MergeNewItemMonitorType(
            NewItemMonitorTypes? existing,
            NewItemMonitorTypes? incoming,
            out NewItemMonitorTypes? merged)
        {
            merged = existing;
            if (!incoming.HasValue)
            {
                return false;
            }

            if (!existing.HasValue || existing == NewItemMonitorTypes.None)
            {
                merged = incoming;
                return existing != incoming;
            }

            if (incoming == NewItemMonitorTypes.All && existing != NewItemMonitorTypes.All)
            {
                merged = NewItemMonitorTypes.All;
                return true;
            }

            return false;
        }

        private static bool IsActiveProviderUniqueViolation(SqliteException ex)
        {
            return ex?.SqliteErrorCode == 19 &&
                   ex.Message?.Contains("PendingAuthorImport.ProviderId", StringComparison.OrdinalIgnoreCase) == true;
        }

        private static bool IsActiveProviderUniqueViolation(PostgresException ex)
        {
            return string.Equals(ex?.SqlState, "23505", StringComparison.Ordinal) &&
                   (string.Equals(ex.ConstraintName, "UX_PendingAuthorImport_Active", StringComparison.OrdinalIgnoreCase) ||
                    ex.MessageText?.Contains("PendingAuthorImport", StringComparison.OrdinalIgnoreCase) == true);
        }

        private bool TryMergeProviderIds(
            string existingJson,
            IEnumerable<string> incoming,
            string fieldName,
            string providerId,
            out string mergedJson,
            IEnumerable<string> removedIds = null)
        {
            mergedJson = existingJson;
            var removed = new HashSet<string>(removedIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var incomingIds = incoming?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (incomingIds?.Any() != true && removed.Count == 0)
            {
                return false;
            }

            try
            {
                var existingIds = string.IsNullOrWhiteSpace(existingJson)
                    ? new List<string>()
                    : JsonConvert.DeserializeObject<List<string>>(existingJson) ?? new List<string>();
                var merged = existingIds
                    .Concat(incomingIds ?? Enumerable.Empty<string>())
                    .Where(id => !removed.Contains(id))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (merged.SequenceEqual(existingIds, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }

                mergedJson = JsonConvert.SerializeObject(merged);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to merge {0} for existing pending import {1}", fieldName, providerId);
                return false;
            }
        }

        private bool TryMergeTags(
            string existingJson,
            IEnumerable<int> incoming,
            string fieldName,
            string providerId,
            out string mergedJson)
        {
            mergedJson = existingJson;
            if (incoming == null)
            {
                return false;
            }

            try
            {
                var existingTags = string.IsNullOrWhiteSpace(existingJson)
                    ? new HashSet<int>()
                    : JsonConvert.DeserializeObject<HashSet<int>>(existingJson) ?? new HashSet<int>();
                var mergedTags = new HashSet<int>(existingTags);
                mergedTags.UnionWith(incoming);
                var serialized = SerializeTags(mergedTags);

                if (string.Equals(existingJson, serialized, StringComparison.Ordinal))
                {
                    return false;
                }

                mergedJson = serialized;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to merge {0} for existing pending import {1}", fieldName, providerId);
                return false;
            }
        }

        private static HashSet<int> ResolveMediaTags(bool createMediaType, HashSet<int> mediaTags, HashSet<int> legacyTags)
        {
            if (!createMediaType)
            {
                return null;
            }

            var tags = mediaTags ?? legacyTags;
            return tags == null ? null : new HashSet<int>(tags);
        }

        private static string SerializeTags(IEnumerable<int> tags)
        {
            return tags == null
                ? null
                : JsonConvert.SerializeObject(tags.Distinct().OrderBy(tag => tag));
        }

        public bool UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error, long? expectedVersion = null)
        {
            while (true)
            {
                var current = _repository.Find(item.Id);
                if (current == null || !current.IsActive()) return false;
                if (expectedVersion.HasValue && (current.Id != item.Id || current.Version != expectedVersion.Value))
                {
                    _logger.Debug("Pending import {0} changed while its status was being updated; retaining version {1}", item.Id, current.Version);
                    return false;
                }

                current.LastError = error;
                current.LastAttemptAt = DateTime.UtcNow;
                if (current.Version == item.Version)
                {
                    CopyProcessedRequestProgress(item, current);
                }

                if (status == PendingImportStatus.Succeeded)
                {
                    current.AudiobookStatus = current.HasAudiobook() ? PendingImportStatus.Succeeded : PendingImportStatus.NotRequested;
                    current.EbookStatus = current.HasEbook() ? PendingImportStatus.Succeeded : PendingImportStatus.NotRequested;
                }
                else if (status == PendingImportStatus.Failed)
                {
                    current.AudiobookStatus = current.HasAudiobook() ? PendingImportStatus.Failed : PendingImportStatus.NotRequested;
                    current.EbookStatus = current.HasEbook() ? PendingImportStatus.Failed : PendingImportStatus.NotRequested;
                }

                if (status == PendingImportStatus.PartialSuccess)
                {
                    current.AudiobookStatus = item.AudiobookStatus;
                    current.EbookStatus = item.EbookStatus;
                }
                current.UpdateOverallStatus();
                current.UpdatedAt = DateTime.UtcNow;
                if (!_repository.TryUpdateRequest(current, current.Version))
                {
                    if (expectedVersion.HasValue || _repository.GetActiveByProviderId(item.ProviderId) == null)
                    {
                        _logger.Debug("Pending import {0} changed or became inactive while its status was being updated", item.Id);
                        return false;
                    }

                    continue;
                }

                CopyMutableState(current, item);
                _logger.Info("Updated pending import {0} status to {1}", current.Id, status);
                return true;
            }
        }

        public bool SaveProgress(PendingAuthorImport item, PendingAuthorImport previous)
        {
            if (item.EbookBooksToAdd == previous.EbookBooksToAdd &&
                item.AudiobookBooksToAdd == previous.AudiobookBooksToAdd &&
                item.AudiobookBooksToMonitor == previous.AudiobookBooksToMonitor && item.EbookBooksToMonitor == previous.EbookBooksToMonitor &&
                item.AudiobookBooksToSearch == previous.AudiobookBooksToSearch && item.EbookBooksToSearch == previous.EbookBooksToSearch &&
                item.AudiobookNarrations == previous.AudiobookNarrations && item.SearchForMissingBooks == previous.SearchForMissingBooks &&
                item.ExplicitBookSearches == previous.ExplicitBookSearches)
                return true;

            while (true)
            {
                var current = _repository.Find(item.Id);
                if (current == null || !current.IsActive()) return false;
                var unchanged = current.Version == item.Version;
                var savedExplicit = current.ExplicitBookSearches;
                var savedProgress = (current.AudiobookBooksToMonitor, current.EbookBooksToMonitor, current.AudiobookBooksToSearch,
                    current.EbookBooksToSearch, current.AudiobookNarrations, current.SearchForMissingBooks, current.AudiobookBooksToAdd, current.EbookBooksToAdd);
                if (unchanged)
                {
                    CopyProcessedRequestProgress(item, current);
                }
                else
                {
                    var beforeExplicit = RequestedBookSearch.Read(previous.ExplicitBookSearches);
                    var remainingExplicit = RequestedBookSearch.Read(item.ExplicitBookSearches);
                    var currentExplicit = RequestedBookSearch.Read(current.ExplicitBookSearches);
                    var newExplicit = currentExplicit.Where(request => !beforeExplicit.Any(request.Matches)).ToList();
                    var completedExplicit = beforeExplicit.Where(request => !remainingExplicit.Any(request.Matches)).ToList();
                    current.ExplicitBookSearches = RequestedBookSearch.Write(currentExplicit.Where(request => !completedExplicit.Any(request.Matches)));
                    current.AudiobookBooksToAdd = SubtractCompletedIds(current.AudiobookBooksToAdd, previous.AudiobookBooksToAdd, item.AudiobookBooksToAdd);
                    current.AudiobookBooksToMonitor = SubtractCompletedIds(current.AudiobookBooksToMonitor, previous.AudiobookBooksToMonitor, item.AudiobookBooksToMonitor);
                    current.EbookBooksToAdd = SubtractCompletedIds(current.EbookBooksToAdd, previous.EbookBooksToAdd, item.EbookBooksToAdd);
                    current.EbookBooksToMonitor = SubtractCompletedIds(current.EbookBooksToMonitor, previous.EbookBooksToMonitor, item.EbookBooksToMonitor);
                    current.AudiobookBooksToSearch = SubtractCompletedIds(current.AudiobookBooksToSearch, previous.AudiobookBooksToSearch, item.AudiobookBooksToSearch, newExplicit.Where(request => request.MediaType == BookMediaType.Audiobook).Select(request => request.WorkProviderId));
                    current.EbookBooksToSearch = SubtractCompletedIds(current.EbookBooksToSearch, previous.EbookBooksToSearch, item.EbookBooksToSearch, newExplicit.Where(request => request.MediaType == BookMediaType.Ebook).Select(request => request.WorkProviderId));
                    if (previous.AudiobookNarrations != item.AudiobookNarrations)
                    {
                        var before = ParseRequestedNarrations(previous.AudiobookNarrations, activeRequest: true);
                        var after = ParseRequestedNarrations(item.AudiobookNarrations, activeRequest: true);
                        var saved = ParseRequestedNarrations(current.AudiobookNarrations, activeRequest: true);
                        var removed = before.Where(old => !after.Any(next => SameNarrationTarget(old, next))).ToList();
                        var searched = before.Where(old => old.Search && after.Any(next => !next.Search && SameNarrationTarget(old, next))).ToList();
                        saved.RemoveAll(request => !newExplicit.Any(next => next.Matches(request.MediaType, request.WorkProviderId, request.EditionProviderId)) && removed.Any(old => SameNarrationIntent(old, request) && (old.Search || !request.Search)));
                        foreach (var request in saved.Where(request => request.Search && !newExplicit.Any(next => next.Matches(request.MediaType, request.WorkProviderId, request.EditionProviderId)) && searched.Any(old => SameNarrationIntent(old, request)))) request.Search = false;
                        if (removed.Count > 0 || searched.Count > 0 ||
                            (current.AudiobookBooksToMonitor, current.EbookBooksToMonitor, current.AudiobookBooksToSearch, current.EbookBooksToSearch) !=
                            (savedProgress.Item1, savedProgress.Item2, savedProgress.Item3, savedProgress.Item4))
                        {
                            // Carry prepared metadata into later acknowledgements only after a request phase completed.
                            static (BookMediaType, string, string) TargetKey(RequestedNarration request) =>
                                (request.MediaType, request.WorkProviderId?.ToLowerInvariant(), request.EditionProviderId?.ToLowerInvariant());
                            var originals = before.ToLookup(TargetKey);
                            var prepared = after.ToLookup(TargetKey);
                            foreach (var request in saved)
                            {
                                var key = TargetKey(request);
                                if (!originals[key].Any(old => SameNarrationIntent(old, request))) continue;
                                var next = prepared[key].FirstOrDefault();
                                if (next == null) continue;
                                request.RequiresEdition = next.RequiresEdition;
                                request.NarratorNames = next.NarratorNames?.ToList();
                            }
                        }
                        current.AudiobookNarrations = JsonConvert.SerializeObject(saved);
                    }
                }
                if ((current.AudiobookBooksToMonitor, current.EbookBooksToMonitor, current.AudiobookBooksToSearch,
                    current.EbookBooksToSearch, current.AudiobookNarrations, current.SearchForMissingBooks, current.AudiobookBooksToAdd, current.EbookBooksToAdd) == savedProgress && current.ExplicitBookSearches == savedExplicit) return true;
                current.UpdatedAt = DateTime.UtcNow;
                if (!_repository.TryUpdateRequest(current, current.Version))
                {
                    _logger.Debug("Pending import {0} changed while saving progress at version {1}; retrying", item.Id, current.Version);
                    continue;
                }
                // Advancing an obsolete worker across a concurrent merge would let its final
                // status/delete consume new demand that this attempt never processed.
                if (unchanged) item.Version = current.Version;
                return true;
            }
        }

        private static string SubtractCompletedIds(string current, string previous, string remaining, IEnumerable<string> retained = null)
        {
            if (previous == remaining) return current;
            var removed = ParseRequestedProviderIds(previous);
            removed.ExceptWith(ParseRequestedProviderIds(remaining));
            removed.ExceptWith(retained ?? Array.Empty<string>());
            if (current == null) return null;
            var saved = ParseRequestedProviderIds(current);
            return saved.RemoveWhere(removed.Contains) == 0 ? current : JsonConvert.SerializeObject(saved);
        }

        private static bool SameNarrationIntent(RequestedNarration left, RequestedNarration right) =>
            SameNarrationTarget(left, right) && left.RequiresEdition == right.RequiresEdition &&
            (left.NarratorNames ?? new List<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right.NarratorNames ?? new List<string>());

        private static bool SameNarrationTarget(RequestedNarration left, RequestedNarration right) =>
            left.MediaType == right.MediaType &&
            string.Equals(left.WorkProviderId, right.WorkProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.EditionProviderId, right.EditionProviderId, StringComparison.OrdinalIgnoreCase);

        public void ScheduleRetry(PendingAuthorImport item, string error)
        {
            while (true)
            {
                var current = _repository.Find(item.Id);
                if (current == null || !current.IsActive()) return;
                // A concurrent merge can contain new search intent. Only narrow the unchanged
                // request that this worker processed; keep newer merged requests intact.
                if (current.Version == item.Version)
                {
                    CopyProcessedRequestProgress(item, current);
                }
                current.AttemptCount++;
                current.MaxAttempts = 0;
                current.LastAttemptAt = DateTime.UtcNow;
                current.LastError = error;
                current.NextAttemptAt = AuthorRetrySchedule.NextAttempt(current.AttemptCount);
                current.OverallStatus = PendingImportStatus.Retrying;
                current.UpdatedAt = DateTime.UtcNow;

                var expectedVersion = current.Version;
                if (!_repository.TryUpdateRequest(current, expectedVersion))
                {
                    if (_repository.GetActiveByProviderId(item.ProviderId) == null)
                    {
                        _logger.Debug("Pending import {0} became inactive while a retry was being scheduled", item.Id);
                        return;
                    }

                    continue;
                }

                CopyMutableState(current, item);
                _logger.Warn("Pending import {0} attempt {1} waiting until {2}: {3}", current.Id, current.AttemptCount, current.NextAttemptAt, error);
                return;
            }
        }

        private static string MergeRequestOrigin(string existing, string incoming) =>
            string.IsNullOrWhiteSpace(existing) || MonitoringConfig.IsImportListRequestOrigin(existing) &&
            !string.IsNullOrWhiteSpace(incoming) && !MonitoringConfig.IsImportListRequestOrigin(incoming) ? incoming : existing;

        private static void CopyProcessedRequestProgress(PendingAuthorImport source, PendingAuthorImport target)
        {
            if (!string.IsNullOrWhiteSpace(source.AuthorName)) target.AuthorName = source.AuthorName;
            target.AudiobookStatus = source.AudiobookStatus;
            target.EbookStatus = source.EbookStatus;
            target.AudiobookBooksToAdd = source.AudiobookBooksToAdd;
            target.AudiobookBooksToMonitor = source.AudiobookBooksToMonitor;
            target.EbookBooksToAdd = source.EbookBooksToAdd;
            target.EbookBooksToMonitor = source.EbookBooksToMonitor;
            target.AudiobookBooksToSearch = source.AudiobookBooksToSearch;
            target.EbookBooksToSearch = source.EbookBooksToSearch;
            target.AudiobookNarrations = source.AudiobookNarrations;
            target.ExplicitBookSearches = source.ExplicitBookSearches;
            target.SearchForMissingBooks = source.SearchForMissingBooks;
        }

        private static void CopyMutableState(PendingAuthorImport source, PendingAuthorImport target)
        {
            if (ReferenceEquals(source, target))
            {
                return;
            }

            target.AudiobookStatus = source.AudiobookStatus;
            target.EbookStatus = source.EbookStatus;
            target.OverallStatus = source.OverallStatus;
            target.AttemptCount = source.AttemptCount;
            target.MaxAttempts = source.MaxAttempts;
            target.LastAttemptAt = source.LastAttemptAt;
            target.LastError = source.LastError;
            target.NextAttemptAt = source.NextAttemptAt;
            target.UpdatedAt = source.UpdatedAt;
            target.Version = source.Version;
            target.RequestedBy = source.RequestedBy;
            target.AudiobookRequestedBy = source.AudiobookRequestedBy;
            target.EbookRequestedBy = source.EbookRequestedBy;
            target.AuthorName = source.AuthorName;
        }

        public List<PendingAuthorImport> GetAll()
        {
            return _repository.GetAll();
        }

        public PendingAuthorImport Get(int id) => _repository.Find(id);

        public PagingSpec<PendingAuthorImport> GetPaged(PagingSpec<PendingAuthorImport> pagingSpec) => _repository.GetPaged(pagingSpec);

        public List<PendingAuthorImport> GetDueForProcessing(int limit = 10)
        {
            var effectiveLimit = Math.Max(1, limit);
            return _repository.GetDueForProcessing(DateTime.UtcNow, effectiveLimit);
        }

        public PendingAuthorImport GetByProviderId(string providerId)
        {
            providerId = NormalizeProviderId(providerId);
            return _repository.GetByProviderId(providerId);
        }

        public bool IsActiveRequest(int id, long version)
        {
            var item = _repository.Find(id);
            return item?.IsActive() == true && item.Version == version;
        }

        public void Handle(AuthorSetupMetadataRefusedEvent message)
        {
            var author = message.Author;
            var options = author.AddOptions;
            if (options?.AwaitingMetadata != true) return;

            // Only transfer the side whose persisted initial setup is waiting.
            // Other configured sides and exact-book demands keep their own lifecycle.
            var legacyCheckpoint = !options.AudiobookAwaitingMetadata && !options.EbookAwaitingMetadata;
            var audio = options.AudiobookAwaitingMetadata || legacyCheckpoint && options.AudiobookMonitor.HasValue;
            var ebook = options.EbookAwaitingMetadata || legacyCheckpoint && options.EbookMonitor.HasValue;
            if (!audio && !ebook)
            {
                _logger.Warn("Author {0} setup was refused but has no media-side ownership; retaining its checkpoint for diagnosis.", author.Id);
                return;
            }

            var config = new MonitoringConfig
            {
                CreateAudiobook = audio,
                CreateEbook = ebook,
                IsAuthorSettingsRequest = true,
                RequestedBy = "AuthorSetup",
                AuthorName = author.Name,
                AudiobookMonitorExistingMode = audio ? options.AudiobookMonitor : null,
                EbookMonitorExistingMode = ebook ? options.EbookMonitor : null,
                AudiobookMonitored = audio ? author.AudiobookMonitored : null,
                EbookMonitored = ebook ? author.EbookMonitored : null,
                AudiobookMonitorNewItems = audio ? author.AudiobookMonitorNewItems : null,
                EbookMonitorNewItems = ebook ? author.EbookMonitorNewItems : null,
                AudiobookQualityProfileId = audio ? author.AudiobookQualityProfileId : null,
                EbookQualityProfileId = ebook ? author.EbookQualityProfileId : null,
                AudiobookMetadataProfileId = audio ? author.AudiobookMetadataProfileId : null,
                EbookMetadataProfileId = ebook ? author.EbookMetadataProfileId : null,
                AudiobookRootFolderPath = audio ? author.AudiobookRootFolderPath : null,
                EbookRootFolderPath = ebook ? author.EbookRootFolderPath : null,
                SearchForMissingBooks = audio && options.AudiobookSetupSearchForMissingBooks || ebook && options.EbookSetupSearchForMissingBooks,
                SyncMonitoredAcrossFormats = author.SyncMonitoredAcrossFormats
            };
            var id = EnqueueAsync(message.ProviderId, config, config.RequestedBy).GetAwaiter().GetResult();
            if (id <= 0) throw new InvalidOperationException("Unable to retain refused author media-side setup.");
            if (audio) options.AudiobookSetupPendingImportId = id;
            if (ebook) options.EbookSetupPendingImportId = id;
            options.MetadataUnavailable = true;
            _authorService.SetAddOptions(author);

            var pending = _repository.Find(id);
            if (pending == null)
            {
                SetOwnedSetupState(new PendingAuthorImport { Id = id, ProviderId = message.ProviderId },
                    cancelledMediaType: null, cancelled: true);
                return;
            }
            if (audio && !pending.HasAudiobook())
                SetOwnedSetupState(pending, BookMediaType.Audiobook, cancelled: true);
            if (ebook && !pending.HasEbook())
                SetOwnedSetupState(pending, BookMediaType.Ebook, cancelled: true);
            if (!pending.IsActive()) return;
            var hasIndependentDemand = pending.HasAudiobook() && !audio || pending.HasEbook() && !ebook ||
                ParseRequestedProviderIds(pending.AudiobookBooksToAdd).Count > 0 ||
                ParseRequestedProviderIds(pending.AudiobookBooksToMonitor).Count > 0 ||
                ParseRequestedProviderIds(pending.EbookBooksToAdd).Count > 0 ||
                ParseRequestedProviderIds(pending.EbookBooksToMonitor).Count > 0 ||
                ParseRequestedProviderIds(pending.AudiobookBooksToSearch).Count > 0 ||
                ParseRequestedProviderIds(pending.EbookBooksToSearch).Count > 0 ||
                ParseRequestedNarrations(pending.AudiobookNarrations, activeRequest: true).Count > 0;
            if (hasIndependentDemand)
            {
                // The existing worker can route and fulfill exact-book demand before
                // acknowledging the original author's terminal result.
                ScheduleRetry(pending, message.Refusal.Message);
            }
            else if (UpdateStatus(pending, PendingImportStatus.Failed, message.Refusal.Message, pending.Version))
            {
                _eventAggregator.PublishEvent(new PendingAuthorImportFailedEvent(pending));
            }
        }

        public void Handle(PendingAuthorImportFailedEvent message) =>
            SetOwnedSetupState(message.PendingImport, cancelledMediaType: null, cancelled: false);

        public void Handle(PendingAuthorImportCancelledEvent message) =>
            SetOwnedSetupState(message.PendingImport, cancelledMediaType: null, cancelled: true);

        private void SetOwnedSetupState(PendingAuthorImport item, BookMediaType? cancelledMediaType, bool cancelled)
        {
            var author = _authorService.FindByProviderId(ExtractPrefix(item.ProviderId), ExtractIdWithoutPrefix(item.ProviderId));
            if (author == null) return;
            lock (BookService.GetWantedEditionLock(author.Id))
            {
                author = _authorService.GetAuthor(author.Id);
                var options = author?.AddOptions;
                if (options == null) return;
                var audio = options.AudiobookSetupPendingImportId == item.Id &&
                    (!cancelledMediaType.HasValue || cancelledMediaType == BookMediaType.Audiobook);
                var ebook = options.EbookSetupPendingImportId == item.Id &&
                    (!cancelledMediaType.HasValue || cancelledMediaType == BookMediaType.Ebook);
                if (!audio && !ebook) return;
                var currentRequest = _repository.Find(item.Id);
                if (currentRequest != null) item = currentRequest;

                if (cancelled)
                {
                    audio &= currentRequest == null || !currentRequest.HasAudiobook();
                    ebook &= currentRequest == null || !currentRequest.HasEbook();
                    options.CancelInitialSearch(audio, ebook);
                    if (audio)
                    {
                        options.AudiobookMonitor = null;
                        options.AudiobookSetupPending = false;
                        options.AudiobookAwaitingMetadata = false;
                        options.AudiobookSetupPendingImportId = null;
                    }
                    if (ebook)
                    {
                        options.EbookMonitor = null;
                        options.EbookSetupPending = false;
                        options.EbookAwaitingMetadata = false;
                        options.EbookSetupPendingImportId = null;
                    }

                    if (!options.AudiobookSetupPending && !options.EbookSetupPending &&
                        !options.AudiobookMonitor.HasValue && !options.EbookMonitor.HasValue && !options.SearchForMissingBooks)
                    {
                        author.AddOptions = null;
                    }
                    else
                    {
                        options.AwaitingMetadata = options.AudiobookAwaitingMetadata || options.EbookAwaitingMetadata;
                    }
                }
                if (author.AddOptions != null)
                {
                    // This operation already loaded one owner. With only two media
                    // sides, at most one distinct remaining owner needs a lookup.
                    var owners = new Dictionary<int, PendingAuthorImport> { [item.Id] = item };
                    var otherOwnerId = new[] { options.AudiobookSetupPendingImportId, options.EbookSetupPendingImportId }
                        .FirstOrDefault(id => id.HasValue && id.Value != item.Id);
                    if (otherOwnerId.HasValue) owners[otherOwnerId.Value] = _repository.Find(otherOwnerId.Value);

                    bool Refused(int? ownerId, BookMediaType mediaType) => ownerId.HasValue &&
                        owners.TryGetValue(ownerId.Value, out var owner) && owner != null && !owner.IsActive() &&
                        (mediaType == BookMediaType.Audiobook ? owner.AudiobookStatus : owner.EbookStatus) == PendingImportStatus.Failed;
                    options.MetadataUnavailable = Refused(options.AudiobookSetupPendingImportId, BookMediaType.Audiobook) ||
                        Refused(options.EbookSetupPendingImportId, BookMediaType.Ebook);
                }

                _authorService.SetAddOptions(author);
            }
        }

        public void Handle(AuthorDeletedEvent message)
        {
            if (message.PreserveRetainedFileHistory) return;
            CancelForAuthor(message.Author, null);
        }

        public void Handle(AuthorEditedEvent message)
        {
            if (!string.IsNullOrWhiteSpace(message.OldAuthor?.AudiobookRootFolderPath) && string.IsNullOrWhiteSpace(message.Author.AudiobookRootFolderPath) && !message.Author.AudiobookQualityProfileId.HasValue)
                CancelForAuthor(message.Author, BookMediaType.Audiobook);
            if (!string.IsNullOrWhiteSpace(message.OldAuthor?.EbookRootFolderPath) && string.IsNullOrWhiteSpace(message.Author.EbookRootFolderPath) && !message.Author.EbookQualityProfileId.HasValue)
                CancelForAuthor(message.Author, BookMediaType.Ebook);
        }

        private void CancelForAuthor(Author author, BookMediaType? mediaType)
        {
            var identities = AuthorIdentity.GetProviderIdentityTokens(author);
            foreach (var alias in _providerAliases?.GetAliases("Author", author.Id, "author") ?? new List<ProviderAlias>())
            {
                identities.Add($"{alias.Provider}:{alias.NormalizedProviderId}");
            }
            foreach (var item in _repository.GetActiveByProviderIds(identities))
            {
                Cancel(item.Id, mediaType);
            }
        }

        public void Cancel(int id) => Cancel(id, null);

        private PendingAuthorImport Cancel(int id, BookMediaType? mediaType)
        {
            while (true)
            {
                var item = _repository.Find(id);
                if (item == null || !item.IsActive())
                {
                    return item;
                }

                if (!mediaType.HasValue || mediaType == BookMediaType.Audiobook)
                {
                    item.AudiobookStatus = PendingImportStatus.NotRequested;
                    item.AudiobookBooksToAdd = null;
                    item.AudiobookBooksToMonitor = null;
                    item.AudiobookBooksToSearch = null;
                }
                if (!mediaType.HasValue || mediaType == BookMediaType.Ebook)
                {
                    item.EbookStatus = PendingImportStatus.NotRequested;
                    item.EbookBooksToAdd = null;
                    item.EbookBooksToMonitor = null;
                    item.EbookBooksToSearch = null;
                }
                if (!mediaType.HasValue)
                {
                    item.AudiobookNarrations = null;
                }
                else if (!string.IsNullOrEmpty(item.AudiobookNarrations))
                {
                    var requests = JsonConvert.DeserializeObject<List<RequestedNarration>>(item.AudiobookNarrations);
                    requests.RemoveAll(request => request.MediaType == mediaType);
                    item.AudiobookNarrations = requests.Count == 0 ? null : JsonConvert.SerializeObject(requests);
                }
                item.ExplicitBookSearches = RequestedBookSearch.Write(RequestedBookSearch.Read(item.ExplicitBookSearches)
                    .Where(request => mediaType.HasValue && request.MediaType != mediaType));
                if (!item.HasAudiobook() && !item.HasEbook()) item.OverallStatus = PendingImportStatus.Failed;
                else item.UpdateOverallStatus();
                item.LastError = mediaType.HasValue ? $"Cancelled {mediaType} request after author media side removal" : "Cancelled by user";
                item.UpdatedAt = DateTime.UtcNow;

                var expectedVersion = item.Version;
                if (!_repository.TryUpdateRequest(item, expectedVersion))
                {
                    continue;
                }

                _logger.Info("Cancelled pending import {0}", id);
                SetOwnedSetupState(item, mediaType, cancelled: true);
                if (!item.IsActive()) _eventAggregator.PublishEvent(new PendingAuthorImportCancelledEvent(item));
                return item;
            }
        }

        public void RetryNow(int id)
        {
            while (true)
            {
                var item = _repository.Find(id);
                if (item == null)
                {
                    return;
                }

                item.NextAttemptAt = DateTime.UtcNow;
                item.OverallStatus = PendingImportStatus.Retrying;
                item.MaxAttempts = 0;
                item.UpdatedAt = DateTime.UtcNow;

                var expectedVersion = item.Version;
                try
                {
                    if (!_repository.TryUpdateRequest(item, expectedVersion))
                    {
                        continue;
                    }
                }
                catch (SqliteException ex) when (IsActiveProviderUniqueViolation(ex))
                {
                    throw new ModelConflictException(typeof(PendingAuthorImport), id);
                }
                catch (PostgresException ex) when (IsActiveProviderUniqueViolation(ex))
                {
                    throw new ModelConflictException(typeof(PendingAuthorImport), id);
                }

                _logger.Info("Scheduled immediate retry for pending import {0}", id);
                SetOwnedSetupState(item, cancelledMediaType: null, cancelled: false);
                _eventAggregator.PublishEvent(new PendingAuthorImportRetryingEvent(item));
                return;
            }
        }

        public void CleanupOldCompleted()
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            _repository.DeleteOldCompleted(cutoff);
            _logger.Debug("Cleaned up completed pending imports older than {0}", cutoff);
        }

        public void Delete(int id)
        {
            var item = Cancel(id, null);
            _repository.Delete(id);
            if (item != null) SetOwnedSetupState(item, cancelledMediaType: null, cancelled: true);
            _logger.Info("Deleted pending author import {0}", id);
        }

        public bool TryDeleteIfUnchanged(PendingAuthorImport item)
        {
            if (!_repository.TryDelete(item.Id, item.Version))
            {
                return false;
            }

            _logger.Info("Deleted completed pending author import {0}", item.Id);
            return true;
        }

        private string NormalizeProviderId(string providerId)
        {
            try
            {
                return ProviderIdHelper.Normalize(providerId, null) ?? throw new InvalidOperationException("A canonical provider ID is required.");
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidProviderIdException(providerId, ex.Message);
            }
        }

        private string ExtractPrefix(string providerId)
        {
            var colonIndex = providerId?.IndexOf(':') ?? -1;
            return colonIndex > 0 ? providerId.Substring(0, colonIndex).ToLowerInvariant() : null;
        }

        private string ExtractIdWithoutPrefix(string providerId)
        {
            var colonIndex = providerId?.IndexOf(':') ?? -1;
            return colonIndex > 0 ? providerId.Substring(colonIndex + 1) : providerId;
        }


    }
}
