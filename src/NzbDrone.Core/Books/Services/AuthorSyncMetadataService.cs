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
                             int httpStatus = 0, TimeSpan? duration = null);
        List<AuthorSyncMetadata> GetDueForSync(int limit = 100);
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

            if (existingByAuthor != null)
            {
                // If another row already owns this external ID, we have duplicate author rows (or stale metadata rows).
                // Keep the row for the author being refreshed and remove the conflicting external mapping.
                if (existingByExternal != null && existingByExternal.Id != existingByAuthor.Id)
                {
                    _logger.Warn("[SYNC-METADATA] ExternalAuthorId '{0}' is already mapped to AuthorId={1}. " +
                                 "AuthorId={2} is attempting to claim the same external id; deleting the conflicting sync-metadata row (Id={3}).",
                        externalAuthorId,
                        existingByExternal.AuthorId,
                        authorId,
                        existingByExternal.Id);

                    try
                    {
                        _syncMetadataRepository.Delete(existingByExternal.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[SYNC-METADATA] Failed deleting conflicting sync-metadata row for ExternalAuthorId '{0}' (Id={1})",
                            externalAuthorId, existingByExternal.Id);
                        throw;
                    }
                }

                existingByAuthor.ExternalAuthorId = externalAuthorId;
                if (etag != null)
                {
                    existingByAuthor.ETag = etag;
                }

                _syncMetadataRepository.Update(existingByAuthor);
                return existingByAuthor;
            }

            // No row for this AuthorId yet. If there is already a row for this ExternalAuthorId, reassign it
            // instead of inserting and crashing on the UNIQUE constraint.
            if (existingByExternal != null)
            {
                if (existingByExternal.AuthorId != authorId)
                {
                    _logger.Warn("[SYNC-METADATA] ExternalAuthorId '{0}' is already mapped to AuthorId={1}. " +
                                 "Reassigning sync-metadata row (Id={2}) to AuthorId={3}.",
                        externalAuthorId,
                        existingByExternal.AuthorId,
                        existingByExternal.Id,
                        authorId);

                    existingByExternal.AuthorId = authorId;
                }

                if (etag != null)
                {
                    existingByExternal.ETag = etag;
                }

                _syncMetadataRepository.Update(existingByExternal);
                return existingByExternal;
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
                                   int httpStatus = 0, TimeSpan? duration = null)
        {
            var syncMetadata = _syncMetadataRepository.FindByAuthorId(authorId);
            if (syncMetadata == null)
            {
                _logger.Warn("No sync metadata found for author {0}, cannot update sync result", authorId);
                return;
            }

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
                syncMetadata.NextSyncNotBefore = null; // Available for next sync immediately
                
                if (!string.IsNullOrEmpty(etag))
                {
                    syncMetadata.ETag = etag;
                }
            }
            else
            {
                syncMetadata.LastSyncStatus = httpStatus == 429 ? SyncStatus.RateLimited : SyncStatus.Failed;
                syncMetadata.SyncFailureCount++;

                // Implement exponential backoff for failed syncs
                var backoffMinutes = Math.Min(60 * Math.Pow(2, syncMetadata.SyncFailureCount - 1), 1440); // Max 24 hours
                syncMetadata.NextSyncNotBefore = now.AddMinutes(backoffMinutes);
                
                _logger.Debug("Author {0} sync failed {1} time(s), next sync not before {2}", 
                    authorId, syncMetadata.SyncFailureCount, syncMetadata.NextSyncNotBefore);
            }

            _syncMetadataRepository.Update(syncMetadata);
        }

        public List<AuthorSyncMetadata> GetDueForSync(int limit = 100)
        {
            return _syncMetadataRepository.GetDueForSync(limit);
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
