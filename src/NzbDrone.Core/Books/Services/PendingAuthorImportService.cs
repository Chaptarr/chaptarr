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
        List<PendingAuthorImport> GetDueForProcessing(int limit = 10);
        PendingAuthorImport GetByProviderId(string providerId);
        void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error);
        void ScheduleRetry(PendingAuthorImport item, string error);
        void Cancel(int id);
        void RetryNow(int id);
        void CleanupOldCompleted();
        void Delete(int id);
        bool TryDeleteIfUnchanged(PendingAuthorImport item)
        {
            Delete(item.Id);
            return true;
        }
    }

    public class PendingAuthorImportService : IPendingAuthorImportService
    {
        private readonly IPendingAuthorImportRepository _repository;
        private readonly IAuthorService _authorService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public PendingAuthorImportService(
            IPendingAuthorImportRepository repository,
            IAuthorService authorService,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _repository = repository;
            _authorService = authorService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
        {
            // Normalize and validate provider ID
            providerId = NormalizeProviderId(providerId);
            if (!IsValidProviderId(providerId))
            {
                throw new ArgumentException($"Invalid provider ID format: {providerId}");
            }

            // A book request may need the metadata-server rescue lifecycle even when its author
            // already exists locally. Author-only requests can still stop here.
            if (!HasRequestedBooks(config))
            {
                try
                {
                    var prefix = ExtractPrefix(providerId);
                    var rawId = ExtractIdWithoutPrefix(providerId);
                    var existingAuthor = _authorService.FindByProviderId(prefix, rawId);
                    if (existingAuthor != null)
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

                    // Merge compatible settings (widen scope if needed)
                    bool updated = false;

                    // Enable audiobook if requested and not already
                    if (config.CreateAudiobook && !existing.HasAudiobook())
                    {
                        existing.AudiobookStatus = PendingImportStatus.Pending;
                        // Convert bool to int?: true=1 (All), false=0 (None)
                        existing.AudiobookMonitorExisting = config.AudiobookMonitorExisting ?? (config.MonitorExisting ? 1 : 0);
                        existing.AudiobookMonitorFuture = config.AudiobookMonitorFuture ?? config.MonitorFuture;
                        existing.AudiobookQualityProfileId = config.AudiobookQualityProfileId;
                        existing.AudiobookMetadataProfileId = config.AudiobookMetadataProfileId;
                        existing.AudiobookRootFolderPath = config.AudiobookRootFolderPath;

                        if (config.AudiobookBooksToMonitor?.Any() == true)
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
                            out var audiobookBooksToMonitor))
                    {
                        existing.AudiobookBooksToMonitor = audiobookBooksToMonitor;
                        updated = true;
                    }

                    // Enable ebook if requested and not already
                    if (config.CreateEbook && !existing.HasEbook())
                    {
                        existing.EbookStatus = PendingImportStatus.Pending;
                        // Convert bool to int?: true=1 (All), false=0 (None)
                        existing.EbookMonitorExisting = config.EbookMonitorExisting ?? (config.MonitorExisting ? 1 : 0);
                        existing.EbookMonitorFuture = config.EbookMonitorFuture ?? config.MonitorFuture;
                        existing.EbookQualityProfileId = config.EbookQualityProfileId;
                        existing.EbookMetadataProfileId = config.EbookMetadataProfileId;
                        existing.EbookRootFolderPath = config.EbookRootFolderPath;

                        if (config.EbookBooksToMonitor?.Any() == true)
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
                            out var ebookBooksToMonitor))
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

                    if (config.SearchForMissingBooks == true && !existing.SearchForMissingBooks)
                    {
                        existing.SearchForMissingBooks = true;
                        updated = true;
                    }

                    // Preserve discovered author folder path if provided and not already set
                    if (string.IsNullOrWhiteSpace(existing.DiscoveredAuthorFolderPath) && !string.IsNullOrWhiteSpace(config.DiscoveredAuthorFolderPath))
                    {
                        existing.DiscoveredAuthorFolderPath = config.DiscoveredAuthorFolderPath;
                        updated = true;
                    }

                    if (updated)
                    {
                        existing.UpdatedAt = DateTime.UtcNow;
                        // Trigger immediate processing rather than waiting for scheduled delay
                        existing.NextAttemptAt = DateTime.UtcNow;
                        existing.UpdateOverallStatus();
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
                    AuthorName = config.AuthorName,
                    DiscoveredAuthorFolderPath = config.DiscoveredAuthorFolderPath,
                    CreatedAt = DateTime.UtcNow,
                    NextAttemptAt = DateTime.UtcNow,
                    // Zero is the persisted/API representation for an unbounded retry lifecycle.
                    // Declared terminal outcomes are stopped by ProcessPendingImportsCommandHandler;
                    // transient "not ready" states must not age into a synthetic terminal.
                    MaxAttempts = 0,
                    SourceApplication = sourceApplication,
                    RequestedBy = config.RequestedBy,
                    SearchForMissingBooks = config.SearchForMissingBooks ?? false,
                    Version = 1
                };

                // Set audiobook configuration if requested
                if (config.CreateAudiobook)
                {
                    pending.AudiobookStatus = PendingImportStatus.Pending;
                    // Convert bool to int?: true=1 (All), false=0 (None)
                    pending.AudiobookMonitorExisting = config.AudiobookMonitorExisting ?? (config.MonitorExisting ? 1 : 0);
                    pending.AudiobookMonitorFuture = config.AudiobookMonitorFuture ?? config.MonitorFuture;
                    pending.AudiobookQualityProfileId = config.AudiobookQualityProfileId;
                    pending.AudiobookMetadataProfileId = config.AudiobookMetadataProfileId;
                    pending.AudiobookRootFolderPath = config.AudiobookRootFolderPath;

                    if (config.AudiobookBooksToMonitor?.Any() == true)
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
                    // Convert bool to int?: true=1 (All), false=0 (None)
                    pending.EbookMonitorExisting = config.EbookMonitorExisting ?? (config.MonitorExisting ? 1 : 0);
                    pending.EbookMonitorFuture = config.EbookMonitorFuture ?? config.MonitorFuture;
                    pending.EbookQualityProfileId = config.EbookQualityProfileId;
                    pending.EbookMetadataProfileId = config.EbookMetadataProfileId;
                    pending.EbookRootFolderPath = config.EbookRootFolderPath;

                    if (config.EbookBooksToMonitor?.Any() == true)
                    {
                        pending.EbookBooksToMonitor = JsonConvert.SerializeObject(config.EbookBooksToMonitor);
                    }

                    if (config.EbookBooksToSearch?.Any() == true)
                    {
                        pending.EbookBooksToSearch = JsonConvert.SerializeObject(config.EbookBooksToSearch);
                    }
                }

                // Set common fields
                if (config.Tags?.Any() == true)
                {
                    pending.Tags = JsonConvert.SerializeObject(config.Tags);
                }

                pending.UpdateOverallStatus();

                try
                {
                    _repository.Insert(pending);
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

        private static bool HasRequestedBooks(MonitoringConfig config)
        {
            return config?.AudiobookBooksToMonitor?.Any() == true ||
                   config?.AudiobookBooksToSearch?.Any() == true ||
                   config?.EbookBooksToMonitor?.Any() == true ||
                   config?.EbookBooksToSearch?.Any() == true;
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
            out string mergedJson)
        {
            mergedJson = existingJson;
            var incomingIds = incoming?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (incomingIds?.Any() != true)
            {
                return false;
            }

            try
            {
                var existingIds = string.IsNullOrWhiteSpace(existingJson)
                    ? new List<string>()
                    : JsonConvert.DeserializeObject<List<string>>(existingJson) ?? new List<string>();
                var merged = existingIds
                    .Concat(incomingIds)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (merged.Count == existingIds.Count)
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

        public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error)
        {
            while (true)
            {
                var current = _repository.GetActiveByProviderId(item.ProviderId) ?? item;
                current.LastError = error;
                current.LastAttemptAt = DateTime.UtcNow;

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

                current.UpdateOverallStatus();
                current.UpdatedAt = DateTime.UtcNow;
                var expectedVersion = current.Version;
                if (!_repository.TryUpdateRequest(current, expectedVersion))
                {
                    if (_repository.GetActiveByProviderId(item.ProviderId) == null)
                    {
                        _logger.Debug("Pending import {0} became inactive while its status was being updated", item.Id);
                        return;
                    }

                    continue;
                }

                CopyMutableState(current, item);
                _logger.Info("Updated pending import {0} status to {1}", current.Id, status);
                return;
            }
        }

        public void ScheduleRetry(PendingAuthorImport item, string error)
        {
            while (true)
            {
                var current = _repository.GetActiveByProviderId(item.ProviderId) ?? item;
                current.AttemptCount++;
                current.MaxAttempts = 0;
                current.LastAttemptAt = DateTime.UtcNow;
                current.LastError = error;
                current.NextAttemptAt = CalculateNextAttempt(current.AttemptCount);
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
                _logger.Debug("Scheduled retry for pending import {0} at {1}", current.Id, current.NextAttemptAt);
                return;
            }
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
        }

        public List<PendingAuthorImport> GetAll()
        {
            return _repository.GetAll();
        }

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

        public void Cancel(int id)
        {
            while (true)
            {
                var item = _repository.Find(id);
                if (item == null || !item.IsActive())
                {
                    return;
                }

                item.AudiobookStatus = item.HasAudiobook() ? PendingImportStatus.Failed : PendingImportStatus.NotRequested;
                item.EbookStatus = item.HasEbook() ? PendingImportStatus.Failed : PendingImportStatus.NotRequested;
                item.OverallStatus = PendingImportStatus.Failed;
                item.LastError = "Cancelled by user";
                item.UpdatedAt = DateTime.UtcNow;

                var expectedVersion = item.Version;
                if (!_repository.TryUpdateRequest(item, expectedVersion))
                {
                    continue;
                }

                _logger.Info("Cancelled pending import {0}", id);
                _eventAggregator.PublishEvent(new PendingAuthorImportCancelledEvent(item));
                return;
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
                if (!_repository.TryUpdateRequest(item, expectedVersion))
                {
                    continue;
                }

                _logger.Info("Scheduled immediate retry for pending import {0}", id);
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
            _repository.Delete(id);
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
            if (string.IsNullOrWhiteSpace(providerId))
                return providerId;

            // Lowercase the prefix part only
            var colonIndex = providerId.IndexOf(':');
            if (colonIndex > 0)
            {
                var prefix = providerId.Substring(0, colonIndex).ToLowerInvariant();
                var id = providerId.Substring(colonIndex + 1).Trim();
                return $"{prefix}:{id}";
            }

            return providerId.Trim();
        }

        private bool IsValidProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return false;

            var colonIndex = providerId.IndexOf(':');
            if (colonIndex <= 0)
                return false;

            var prefix = providerId.Substring(0, colonIndex);
            return ProviderIdHelper.IsCanonicalPrefix(prefix);
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

        private DateTime CalculateNextAttempt(int attemptCount)
        {
            // Simple retry schedule: 60 seconds for first 3 attempts, then 5 minutes forever
            var baseDelay = attemptCount switch
            {
                <= 3 => TimeSpan.FromSeconds(60),   // First 3 attempts: every 60 seconds
                _ => TimeSpan.FromMinutes(5)        // All subsequent attempts: every 5 minutes
            };

            // Add ±20% jitter to prevent thundering herd
            var jitter = (Random.Shared.NextDouble() * 0.4) - 0.2;
            var delayWithJitter = baseDelay * (1 + jitter);

            return DateTime.UtcNow.Add(delayWithJitter);
        }
    }
}
