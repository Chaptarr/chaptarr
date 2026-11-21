using System;
using System.Collections.Generic;
using System.Linq;
using NLog;

namespace NzbDrone.Core.Books
{
    public interface IAuthorSyncMetadataService
    {
        AuthorSyncMetadata GetSyncMetadata(int authorId);
        AuthorSyncMetadata GetSyncMetadataByExternalId(string externalAuthorId);
        List<AuthorSyncMetadata> GetSyncMetadataForAuthors(List<int> authorIds);
        AuthorSyncMetadata CreateOrUpdateSyncMetadata(int authorId, string externalAuthorId, string etag = null);
        void UpdateSyncMetadata(AuthorSyncMetadata syncMetadata);
        void UpdateSyncResult(int authorId, bool success, string etag = null, string error = null, 
                             int httpStatus = 0, TimeSpan? duration = null, bool retryAllowed = true, bool localFailure = false);
        List<AuthorSyncMetadata> GetDueForSync(int limit = 100);
        void DeferRetry(AuthorSyncMetadata metadata);
        void BulkUpdateSyncMetadata(List<AuthorSyncMetadata> syncMetadata);
    }

    public class AuthorSyncMetadataService : IAuthorSyncMetadataService
    {
        private readonly IAuthorSyncMetadataRepository _syncMetadataRepository;
        private readonly Logger _logger;

        public AuthorSyncMetadataService(IAuthorSyncMetadataRepository syncMetadataRepository, Logger logger)
        {
            _syncMetadataRepository = syncMetadataRepository;
            _logger = logger;
        }

        public AuthorSyncMetadata GetSyncMetadata(int authorId)
        {
            return _syncMetadataRepository.FindByAuthorId(authorId);
        }

        public AuthorSyncMetadata GetSyncMetadataByExternalId(string externalAuthorId)
        {
            return _syncMetadataRepository.FindByExternalAuthorId(externalAuthorId);
        }

        public List<AuthorSyncMetadata> GetSyncMetadataForAuthors(List<int> authorIds)
        {
            return _syncMetadataRepository.FindByAuthorIds(authorIds);
        }

        public AuthorSyncMetadata CreateOrUpdateSyncMetadata(int authorId, string externalAuthorId, string etag = null)
        {
            externalAuthorId = externalAuthorId?.Trim();
            if (string.IsNullOrWhiteSpace(externalAuthorId))
            {
                throw new ArgumentException("External author id must be provided", nameof(externalAuthorId));
            }

            var existingByAuthor = _syncMetadataRepository.FindByAuthorId(authorId);
            var existingByExternal = _syncMetadataRepository.FindByExternalAuthorId(externalAuthorId);

            // A sync token is not ownership proof. Reconcile the published identity at the
            // refresh boundary before moving children; never steal another author's mapping.
            if (existingByExternal != null && existingByExternal.AuthorId != authorId)
            {
                throw new InvalidOperationException($"Author {authorId} cannot claim {externalAuthorId}: it belongs to author {existingByExternal.AuthorId}. Reconcile author identity first.");
            }

            if (existingByAuthor != null)
            {
                existingByAuthor.ExternalAuthorId = externalAuthorId;
                if (etag != null) existingByAuthor.ETag = etag;
                _syncMetadataRepository.Update(existingByAuthor);
                return existingByAuthor;
            }

            // Create new record
            var syncMetadata = new AuthorSyncMetadata
            {
                AuthorId = authorId,
                ExternalAuthorId = externalAuthorId,
                ETag = etag,
                LastSyncStatus = SyncStatus.Unknown,
                SyncFailureCount = 0,
                LastSyncDurationMs = 0
            };

            return _syncMetadataRepository.Insert(syncMetadata);
        }

        public void UpdateSyncMetadata(AuthorSyncMetadata syncMetadata)
        {
            _syncMetadataRepository.Update(syncMetadata);
        }

        public void UpdateSyncResult(int authorId, bool success, string etag = null, string error = null,
                                   int httpStatus = 0, TimeSpan? duration = null, bool retryAllowed = true, bool localFailure = false)
        {
            var syncMetadata = _syncMetadataRepository.FindByAuthorId(authorId);
            if (syncMetadata == null)
            {
                _logger.Warn("No sync metadata found for author {0}, cannot update sync result", authorId);
                return;
            }

            var previousFailureCount = syncMetadata.SyncFailureCount;
            var previousStatus = syncMetadata.LastSyncStatus;
            var previousHttpStatus = syncMetadata.LastHttpStatus;
            var now = DateTime.UtcNow;
            syncMetadata.LastSyncAttempt = now;
            syncMetadata.LastHttpStatus = httpStatus;
            syncMetadata.LastSyncDurationMs = (int)(duration?.TotalMilliseconds ?? 0);
            syncMetadata.LastError = error?.Truncate(1024);

            if (success)
            {
                syncMetadata.LastSuccessfulSync = now;
                syncMetadata.LastSyncStatus = SyncStatus.Success;
                syncMetadata.SyncFailureCount = 0;
                syncMetadata.NextSyncNotBefore = null; // Successful work leaves the retry queue.
                
                if (!string.IsNullOrEmpty(etag))
                {
                    syncMetadata.ETag = etag;
                }
            }
            else
            {
                syncMetadata.LastSyncStatus = httpStatus == 429 ? SyncStatus.RateLimited : SyncStatus.Failed;
                var providerUnavailable = !localFailure && (httpStatus == 0 || httpStatus == 429 || httpStatus >= 500);
                if (!providerUnavailable) syncMetadata.SyncFailureCount++;
                if (localFailure) syncMetadata.ETag = null;

                // A provider rate limit must not inherit an author's earlier failure count
                // and postpone a recoverable refresh for a day.
                syncMetadata.NextSyncNotBefore = !retryAllowed ? null : localFailure || httpStatus == 404 || httpStatus == 429
                    ? now.AddHours(1)
                    : AuthorRetrySchedule.NextAttempt(providerUnavailable ? 1 : syncMetadata.SyncFailureCount);
            }

            _syncMetadataRepository.Update(syncMetadata);
            if (success && previousStatus is SyncStatus.Failed or SyncStatus.RateLimited)
                _logger.Info("Author {0} refresh recovered; previous failure: {1}", authorId, previousHttpStatus == 0 ? "local or connection failure" : "HTTP " + previousHttpStatus);
            else if (!success)
            {
                if (retryAllowed && (previousStatus != syncMetadata.LastSyncStatus || previousHttpStatus != httpStatus ||
                    localFailure && previousFailureCount == 0))
                    _logger.Warn("Author {0} refresh could not complete: {1}. Next automatic retry is due at {2:u}.",
                        authorId, syncMetadata.LastError, syncMetadata.NextSyncNotBefore);
                _logger.Debug("Author {0} sync failed {1} time(s), next retry {2}",
                    authorId, syncMetadata.SyncFailureCount, syncMetadata.NextSyncNotBefore);
            }
        }

        public List<AuthorSyncMetadata> GetDueForSync(int limit = 100)
        {
            return _syncMetadataRepository.GetDueForSync(limit);
        }

        public void DeferRetry(AuthorSyncMetadata metadata)
        {
            // Reserve a bounded retry before work that can escape without a result.
            // A recorded outcome replaces this date; this is not an author failure.
            metadata.NextSyncNotBefore = AuthorRetrySchedule.NextAttempt(Math.Max(4, metadata.SyncFailureCount));
            _syncMetadataRepository.SetFields(metadata, item => item.NextSyncNotBefore);
        }

        public void BulkUpdateSyncMetadata(List<AuthorSyncMetadata> syncMetadata)
        {
            if (syncMetadata?.Any() == true)
            {
                _syncMetadataRepository.BulkUpsert(syncMetadata);
            }
        }
    }
}
