using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
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

            // Guard: if the author already exists in the library, do not queue again
            try
            {
                var prefix = ExtractPrefix(providerId);
                var rawId = ExtractIdWithoutPrefix(providerId);
                var existingAuthor = _authorService.FindByProviderId(prefix, rawId);
                if (existingAuthor != null)
                {
                    _logger.Debug("[PENDING-IMPORT] Author already exists in database, skipping queue: {0} (ID: {1})", providerId, existingAuthor.Id);
                    return Task.FromResult(0);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[PENDING-IMPORT] Failed to check existing author for {0}; proceeding cautiously", providerId);
            }

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
                    _repository.Update(existing);
                    return Task.FromResult(existing.Id);
                }

                // Nothing changed; return existing pending ID so caller can inform user
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
                NextAttemptAt = DateTime.UtcNow, // Process immediately
                // Zero is the persisted/API representation for an unbounded retry lifecycle.
                // Declared terminal outcomes are stopped by ProcessPendingImportsCommandHandler;
                // transient "not ready" states must not age into a synthetic terminal.
                MaxAttempts = 0,
                SourceApplication = sourceApplication,
                RequestedBy = config.RequestedBy,
                SearchForMissingBooks = config.SearchForMissingBooks ?? false
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

            _repository.Insert(pending);
            _logger.Info("Queued author {0} for pending import (ID: {1}), NextAttemptAt={2:o}", providerId, pending.Id, pending.NextAttemptAt);

            // Fire event for UI updates
            _eventAggregator.PublishEvent(new PendingAuthorImportQueuedEvent(pending));

            return Task.FromResult(pending.Id);
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
            item.LastError = error;
            item.LastAttemptAt = DateTime.UtcNow;

            if (status == PendingImportStatus.Succeeded)
            {
                item.AudiobookStatus = item.HasAudiobook() ? PendingImportStatus.Succeeded : PendingImportStatus.NotRequested;
                item.EbookStatus = item.HasEbook() ? PendingImportStatus.Succeeded : PendingImportStatus.NotRequested;
            }
            else if (status == PendingImportStatus.Failed)
            {
                item.AudiobookStatus = item.HasAudiobook() ? PendingImportStatus.Failed : PendingImportStatus.NotRequested;
                item.EbookStatus = item.HasEbook() ? PendingImportStatus.Failed : PendingImportStatus.NotRequested;
            }

            item.UpdateOverallStatus();
            item.UpdatedAt = DateTime.UtcNow;
            _repository.Update(item);

            _logger.Info("Updated pending import {0} status to {1}", item.Id, status);
        }

        public void ScheduleRetry(PendingAuthorImport item, string error)
        {
            item.AttemptCount++;
            item.MaxAttempts = 0;
            item.LastAttemptAt = DateTime.UtcNow;
            item.LastError = error;
            item.NextAttemptAt = CalculateNextAttempt(item.AttemptCount);

            item.OverallStatus = PendingImportStatus.Retrying;

            item.UpdatedAt = DateTime.UtcNow;
            _repository.Update(item);

            _logger.Debug("Scheduled retry for pending import {0} at {1}", item.Id, item.NextAttemptAt);
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
            var item = _repository.Get(id);
            if (item != null && item.IsActive())
            {
                item.AudiobookStatus = item.HasAudiobook() ? PendingImportStatus.Failed : PendingImportStatus.NotRequested;
                item.EbookStatus = item.HasEbook() ? PendingImportStatus.Failed : PendingImportStatus.NotRequested;
                item.OverallStatus = PendingImportStatus.Failed;
                item.LastError = "Cancelled by user";
                item.UpdatedAt = DateTime.UtcNow;
                _repository.Update(item);

                _logger.Info("Cancelled pending import {0}", id);
                _eventAggregator.PublishEvent(new PendingAuthorImportCancelledEvent(item));
            }
        }

        public void RetryNow(int id)
        {
            var item = _repository.Get(id);
            if (item != null)
            {
                item.NextAttemptAt = DateTime.UtcNow;
                item.OverallStatus = PendingImportStatus.Retrying;
                item.MaxAttempts = 0;
                item.UpdatedAt = DateTime.UtcNow;
                _repository.Update(item);

                _logger.Info("Scheduled immediate retry for pending import {0}", id);
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
