using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IAuthorSyncMetadataRepository : IBasicRepository<AuthorSyncMetadata>
    {
        AuthorSyncMetadata FindByAuthorId(int authorId);
        AuthorSyncMetadata FindByExternalAuthorId(string externalAuthorId);
        List<AuthorSyncMetadata> FindByAuthorIds(List<int> authorIds);
        List<AuthorSyncMetadata> GetDueForSync(int limit = 100);
        void BulkUpsert(List<AuthorSyncMetadata> syncMetadata);
    }

    public class AuthorSyncMetadataRepository : BasicRepository<AuthorSyncMetadata>, IAuthorSyncMetadataRepository
    {
        public AuthorSyncMetadataRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public AuthorSyncMetadata FindByAuthorId(int authorId)
        {
            return Query(s => s.AuthorId == authorId).SingleOrDefault();
        }

        public AuthorSyncMetadata FindByExternalAuthorId(string externalAuthorId)
        {
            return Query(s => s.ExternalAuthorId == externalAuthorId).SingleOrDefault();
        }

        public List<AuthorSyncMetadata> FindByAuthorIds(List<int> authorIds)
        {
            if (authorIds == null || authorIds.Count == 0)
            {
                return new List<AuthorSyncMetadata>();
            }

            var ids = authorIds.Distinct().ToArray();
            if (ids.Length == 0)
            {
                return new List<AuthorSyncMetadata>();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && ids.Length > SqliteVariableLimit.MaxParameters)
            {
                var metadata = new List<AuthorSyncMetadata>();
                foreach (var batch in ids.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchIds = batch.ToArray();
                    metadata.AddRange(Query(s => Enumerable.Contains(batchIds, s.AuthorId)));
                }

                return metadata.DistinctBy(m => m.Id).ToList();
            }

            return Query(s => Enumerable.Contains(ids, s.AuthorId));
        }

        public List<AuthorSyncMetadata> GetDueForSync(int limit = 100)
        {
            using (var conn = _database.OpenConnection())
            {
                var now = System.DateTime.UtcNow;

                // Query for sync metadata that is due for sync (NextSyncNotBefore is NULL or past)
                var sql = @"
                    SELECT * FROM ""AuthorSyncMetadata""
                    WHERE ""NextSyncNotBefore"" IS NULL OR ""NextSyncNotBefore"" <= @now
                    ORDER BY (""NextSyncNotBefore"" IS NOT NULL), ""NextSyncNotBefore"", ""Id""
                    LIMIT @limit";

                return conn.Query<AuthorSyncMetadata>(sql, new { limit, now }).ToList();
            }
        }

        public void BulkUpsert(List<AuthorSyncMetadata> syncMetadata)
        {
            using (var conn = _database.OpenConnection())
            using (var tran = conn.BeginTransaction())
            {
                try
                {
                    foreach (var metadata in syncMetadata)
                    {
                        if (metadata.Id == 0)
                        {
                            // Insert new record
                            var sql = @"
                                INSERT INTO ""AuthorSyncMetadata""
                                (""AuthorId"", ""ExternalAuthorId"", ""ETag"", ""ServerVersion"", ""LastSyncAttempt"",
                                 ""LastSuccessfulSync"", ""LastSyncStatus"", ""LastHttpStatus"", ""SyncFailureCount"",
                                 ""LastError"", ""LastSyncDurationMs"", ""NextSyncNotBefore"")
                                VALUES 
                                (@AuthorId, @ExternalAuthorId, @ETag, @ServerVersion, @LastSyncAttempt,
                                 @LastSuccessfulSync, @LastSyncStatus, @LastHttpStatus, @SyncFailureCount,
                                 @LastError, @LastSyncDurationMs, @NextSyncNotBefore)";
                            
                            conn.Execute(sql, metadata, tran);
                        }
                        else
                        {
                            // Update existing record
                            var sql = @"
                                UPDATE ""AuthorSyncMetadata"" SET
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
                                WHERE ""Id"" = @Id";
                            
                            conn.Execute(sql, metadata, tran);
                        }
                    }
                    
                    tran.Commit();
                }
                catch
                {
                    tran.Rollback();
                    throw;
                }
            }
        }
    }
}
