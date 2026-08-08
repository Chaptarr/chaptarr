using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.IO;
using Dapper;
using NLog;
using NzbDrone.Common;
using NzbDrone.Core.MediaFiles.BookImport;

namespace NzbDrone.Core.Datastore
{
    public interface IIngestQueueRepository
    {
        /// <summary>
        /// Begin a command-scoped ingest session for result reporting.
        /// This does not clear history; it records a "start" timestamp so GetImportResults(commandId)
        /// can return only results created during the current command execution.
        /// </summary>
        void BeginSession(int commandId);

        void InsertBatch(List<IngestQueueItem> items);
        List<IngestQueueItem> GetQueuedItems(int limit = 100);
        List<IngestQueueItem> GetQueuedItemsUnderPath(string pathPrefix, int limit = 100, int afterId = 0);
        int GetActiveCountUnderPath(string pathPrefix);
        List<IngestQueueStatusCount> GetActiveStatusCountsUnderPath(string pathPrefix);
        List<IngestQueueItem> GetActiveItemsUnderPath(string pathPrefix, int limit = 20);
        List<IngestQueueItem> GetActiveItems(int limit = 1000, int afterId = 0);
        List<IngestQueueItem> GetActiveItemsForSweepUnderPath(string pathPrefix, int limit = 1000, int afterId = 0);
        /// <summary>
        /// Recover orphaned in-progress claims that have exceeded a lease timeout.
        /// Items can be left in status='in_progress' if the app crashes/restarts or a handler dies after claiming.
        /// This method re-queues stale in-progress items under the provided path prefix so they can be processed normally.
        /// </summary>
        int RecoverStaleInProgress(string pathPrefix, int staleMinutes = 10);
        int RecoverInProgressUpdatedBefore(string pathPrefix, long updatedBefore, string error = null);
        bool TryClaimItem(int id, out IngestQueueItem item);
        /// <summary>
        /// Atomically claims all queued items under a folder path (the book unit directory).
        /// Returns the claimed items (now marked in_progress) so the caller can process them as a unit.
        /// </summary>
        List<IngestQueueItem> TryClaimUnit(string folderPath);
        void UpdateStatus(int id, string status, string error = null);
        void UpdateBatchTagsJson(IEnumerable<(int Id, string TagsJson)> items);
        void UpdateBatchTagsAndDuration(IEnumerable<(int Id, string TagsJson, int? DurationSeconds)> items);
        void UpdateBatchStatus(List<int> ids, string status);
        void RequeueInProgress(List<int> ids, string error = null);
        int GetQueueCount();
        /// <summary>
        /// Re-queue previously completed items that were marked as unmapped/failed so they can be retried.
        /// Intended for manual rescans after config changes (e.g., metadata server URL fixed).
        /// </summary>
        int RequeueFailedOrUnmappedUnderPath(string pathPrefix);
        /// <summary>
        /// Re-queue completed Failed items only when their paths were observed during the current root scan.
        /// Unmapped items remain manual-only; observed-path scoping avoids retrying files that no longer exist.
        /// </summary>
        int RequeueFailedPaths(IEnumerable<string> paths);
        int PurgeUnderPath(string pathPrefix);
        int PurgePaths(IEnumerable<string> paths);
        void PurgeOldCompleted(int daysToKeep = 14);
        
        // Result tracking methods
        void RecordImportResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null);
        void CompleteItemWithResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null, string statusError = null);
        List<ImportResult> GetImportResults(int? commandId = null);
    }

        public class IngestQueueRepository : IIngestQueueRepository
        {
            private readonly IStagingDbContext _dbContext;
            private readonly Logger _logger;

            public IngestQueueRepository(IStagingDbContext dbContext, Logger logger)
            {
                _dbContext = dbContext;
                _logger = logger;
            }

            public void BeginSession(int commandId)
            {
                if (commandId <= 0)
                {
                    return;
                }

                using (var conn = _dbContext.OpenConnection())
                {
                    try
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var key = $"command:{commandId}:started_at";
                        var resultsKey = $"command:{commandId}:results_start_id";
                        var maxResultId = conn.ExecuteScalar<long?>("SELECT MAX(id) FROM import_results") ?? 0;

                        // Keep the original start time for this command if called multiple times.
                        conn.Execute(@"
                            INSERT INTO staging_metadata (key, value, updated_at)
                            VALUES (@key, @value, @updatedAt)
                            ON CONFLICT(key) DO NOTHING;
                        ", new
                        {
                            key,
                            value = now.ToString(),
                            updatedAt = now
                        });

                        // Also track the import_results high-water mark to avoid same-second cross-command bleed.
                        conn.Execute(@"
                            INSERT INTO staging_metadata (key, value, updated_at)
                            VALUES (@key, @value, @updatedAt)
                            ON CONFLICT(key) DO NOTHING;
                        ", new
                        {
                            key = resultsKey,
                            value = maxResultId.ToString(),
                            updatedAt = now
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[STAGING-DB] Failed to BeginSession for commandId={0}", commandId);
                    }
                }
            }

        public void InsertBatch(List<IngestQueueItem> items)
        {
            if (!items.Any()) return;

            using (var conn = _dbContext.OpenConnection())
            using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        var sql = @"
                            INSERT INTO ingest_queue (path, mtime_ns, size_bytes, tags_json, duration_seconds, status, created_at, updated_at)
                            VALUES (@Path, @MtimeNs, @SizeBytes, @TagsJson, @DurationSeconds, @Status, @CreatedAt, @UpdatedAt)
                            ON CONFLICT(path) DO UPDATE SET
                                mtime_ns = excluded.mtime_ns,
                                size_bytes = excluded.size_bytes,
                                tags_json = CASE
                                    WHEN (@ForceRequeue = 1 AND ingest_queue.status IN ('done', 'error'))
                                      OR ingest_queue.mtime_ns != excluded.mtime_ns OR ingest_queue.size_bytes != excluded.size_bytes
                                    THEN excluded.tags_json
                                    ELSE ingest_queue.tags_json
                                END,
                                duration_seconds = CASE
                                    WHEN (@ForceRequeue = 1 AND ingest_queue.status IN ('done', 'error'))
                                      OR ingest_queue.mtime_ns != excluded.mtime_ns OR ingest_queue.size_bytes != excluded.size_bytes
                                    THEN excluded.duration_seconds
                                    ELSE ingest_queue.duration_seconds
                                END,
                                status = CASE
                                    WHEN ingest_queue.status IN ('done', 'error')
                                     AND (@ForceRequeue = 1 OR ingest_queue.mtime_ns != excluded.mtime_ns OR ingest_queue.size_bytes != excluded.size_bytes)
                                    THEN 'queued'
                                    ELSE ingest_queue.status
                                END,
                                attempts = CASE
                                    WHEN ingest_queue.status IN ('done', 'error')
                                     AND (@ForceRequeue = 1 OR ingest_queue.mtime_ns != excluded.mtime_ns OR ingest_queue.size_bytes != excluded.size_bytes)
                                    THEN 0
                                    ELSE ingest_queue.attempts
                                END,
                                err = CASE
                                    WHEN ingest_queue.status IN ('done', 'error')
                                     AND (@ForceRequeue = 1 OR ingest_queue.mtime_ns != excluded.mtime_ns OR ingest_queue.size_bytes != excluded.size_bytes)
                                    THEN NULL
                                    ELSE ingest_queue.err
                                END,
                                updated_at = excluded.updated_at;
                        ";

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    foreach (var item in items)
                    {
                        item.CreatedAt = now;
                        item.UpdatedAt = now;
                        item.Status = "queued";
                    }

                    conn.Execute(sql, items, transaction);
                    transaction.Commit();
                    
                    _logger.Debug("[STAGING-DB] Inserted/updated {0} items in ingest_queue", items.Count);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.Error(ex, "Failed to insert batch into staging queue");
                    throw;
                }
            }
        }

        public List<IngestQueueItem> GetQueuedItems(int limit = 100)
        {
            using (var conn = _dbContext.OpenConnection())
            {
                    var sql = @"
                        SELECT id, path, mtime_ns as MtimeNs, size_bytes as SizeBytes,
                               tags_json as TagsJson, duration_seconds as DurationSeconds, status, attempts, err,
                               created_at as CreatedAt, updated_at as UpdatedAt
                        FROM ingest_queue
                        WHERE status = 'queued'
                        ORDER BY id
                    LIMIT @limit;
                ";

                return conn.Query<IngestQueueItem>(sql, new { limit }).ToList();
            }
        }

        public List<IngestQueueItem> GetQueuedItemsUnderPath(string pathPrefix, int limit = 100, int afterId = 0)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix)) return new List<IngestQueueItem>();

            using (var conn = _dbContext.OpenConnection())
            {
                // Normalize prefix to avoid trailing separator issues
                var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var prefixForward = normalizedPrefix + "/";
                var prefixBack = normalizedPrefix + "\\";

                    var sql = @"
                        SELECT id, path, mtime_ns as MtimeNs, size_bytes as SizeBytes,
                               tags_json as TagsJson, duration_seconds as DurationSeconds, status, attempts, err,
                               created_at as CreatedAt, updated_at as UpdatedAt
                        FROM ingest_queue
                        WHERE status = 'queued'
                          AND id > @afterId
                      AND (
                        path = @prefix
                        OR substr(path, 1, length(@prefixForward)) = @prefixForward
                        OR substr(path, 1, length(@prefixBack)) = @prefixBack
                      )
                    ORDER BY id
                    LIMIT @limit;
                ";

                var rows = conn.Query<IngestQueueItem>(sql, new { prefix = normalizedPrefix, prefixForward, prefixBack, limit, afterId }).ToList();
                _logger.Debug("[STAGING-DB] GetQueuedItemsUnderPath prefix='{0}' afterId={1} -> {2} items", normalizedPrefix, afterId, rows.Count);
                return rows;
            }
        }

        public int GetActiveCountUnderPath(string pathPrefix)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                return 0;
            }

            using (var conn = _dbContext.OpenConnection())
            {
                try
                {
                    var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var prefixForward = normalizedPrefix + "/";
                    var prefixBack = normalizedPrefix + "\\";

                    var sql = @"
                        SELECT COUNT(*)
                        FROM ingest_queue
                        WHERE status IN ('queued', 'in_progress')
                          AND (
                            path = @prefix
                            OR substr(path, 1, length(@prefixForward)) = @prefixForward
                            OR substr(path, 1, length(@prefixBack)) = @prefixBack
                          );
                    ";

                    return conn.ExecuteScalar<int>(sql, new { prefix = normalizedPrefix, prefixForward, prefixBack });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to GetActiveCountUnderPath prefix='{0}'", pathPrefix);
                    return 0;
                }
            }
        }

        public List<IngestQueueStatusCount> GetActiveStatusCountsUnderPath(string pathPrefix)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                return new List<IngestQueueStatusCount>();
            }

            using (var conn = _dbContext.OpenConnection())
            {
                try
                {
                    var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var prefixForward = normalizedPrefix + "/";
                    var prefixBack = normalizedPrefix + "\\";

                    var sql = @"
                        SELECT status as Status, COUNT(*) as Count
                        FROM ingest_queue
                        WHERE status IN ('queued', 'in_progress')
                          AND (
                            path = @prefix
                            OR substr(path, 1, length(@prefixForward)) = @prefixForward
                            OR substr(path, 1, length(@prefixBack)) = @prefixBack
                          )
                        GROUP BY status
                        ORDER BY status;
                    ";

                    return conn.Query<IngestQueueStatusCount>(sql, new { prefix = normalizedPrefix, prefixForward, prefixBack }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to GetActiveStatusCountsUnderPath prefix='{0}'", pathPrefix);
                    return new List<IngestQueueStatusCount>();
                }
            }
        }

        public List<IngestQueueItem> GetActiveItemsUnderPath(string pathPrefix, int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                return new List<IngestQueueItem>();
            }

            if (limit <= 0)
            {
                limit = 20;
            }

            using (var conn = _dbContext.OpenConnection())
            {
                try
                {
                    var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var prefixForward = normalizedPrefix + "/";
                    var prefixBack = normalizedPrefix + "\\";

                    var sql = @"
                        SELECT id, path, mtime_ns as MtimeNs, size_bytes as SizeBytes,
                               tags_json as TagsJson, duration_seconds as DurationSeconds, status, attempts, err,
                               created_at as CreatedAt, updated_at as UpdatedAt
                        FROM ingest_queue
                        WHERE status IN ('queued', 'in_progress')
                          AND (
                            path = @prefix
                            OR substr(path, 1, length(@prefixForward)) = @prefixForward
                            OR substr(path, 1, length(@prefixBack)) = @prefixBack
                          )
                        ORDER BY updated_at DESC, id
                        LIMIT @limit;
                    ";

                    return conn.Query<IngestQueueItem>(sql, new { prefix = normalizedPrefix, prefixForward, prefixBack, limit }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to GetActiveItemsUnderPath prefix='{0}'", pathPrefix);
                    return new List<IngestQueueItem>();
                }
            }
        }


        public List<IngestQueueItem> GetActiveItems(int limit = 1000, int afterId = 0)
        {
            if (limit <= 0)
            {
                limit = 1000;
            }

            using (var conn = _dbContext.OpenConnection())
            {
                try
                {
                    var sql = @"
                        SELECT id, path, mtime_ns as MtimeNs, size_bytes as SizeBytes,
                               tags_json as TagsJson, duration_seconds as DurationSeconds, status, attempts, err,
                               created_at as CreatedAt, updated_at as UpdatedAt
                        FROM ingest_queue
                        WHERE status IN ('queued', 'in_progress')
                          AND id > @afterId
                        ORDER BY id
                        LIMIT @limit;
                    ";

                    return conn.Query<IngestQueueItem>(sql, new { limit, afterId }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to GetActiveItems afterId={0}", afterId);
                    return new List<IngestQueueItem>();
                }
            }
        }

        public List<IngestQueueItem> GetActiveItemsForSweepUnderPath(string pathPrefix, int limit = 1000, int afterId = 0)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                return new List<IngestQueueItem>();
            }

            if (limit <= 0)
            {
                limit = 1000;
            }

            using (var conn = _dbContext.OpenConnection())
            {
                try
                {
                    var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var prefixForward = normalizedPrefix + "/";
                    var prefixBack = normalizedPrefix + "\\";

                    var sql = @"
                        SELECT id, path, mtime_ns as MtimeNs, size_bytes as SizeBytes,
                               tags_json as TagsJson, duration_seconds as DurationSeconds, status, attempts, err,
                               created_at as CreatedAt, updated_at as UpdatedAt
                        FROM ingest_queue
                        WHERE status IN ('queued', 'in_progress')
                          AND id > @afterId
                          AND (
                            path = @prefix
                            OR substr(path, 1, length(@prefixForward)) = @prefixForward
                            OR substr(path, 1, length(@prefixBack)) = @prefixBack
                          )
                        ORDER BY id
                        LIMIT @limit;
                    ";

                    return conn.Query<IngestQueueItem>(sql, new { prefix = normalizedPrefix, prefixForward, prefixBack, limit, afterId }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to GetActiveItemsForSweepUnderPath prefix='{0}' afterId={1}", pathPrefix, afterId);
                    return new List<IngestQueueItem>();
                }
            }
        }

        public int RecoverStaleInProgress(string pathPrefix, int staleMinutes = 10)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                return 0;
            }

            if (staleMinutes <= 0)
            {
                staleMinutes = 10;
            }

            using (var conn = _dbContext.OpenConnection())
            {
                try
                {
                    var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var prefixForward = normalizedPrefix + "/";
                    var prefixBack = normalizedPrefix + "\\";
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var cutoff = now - (staleMinutes * 60);

                    var sql = @"
                        UPDATE ingest_queue
                        SET status = 'queued',
                            updated_at = @now,
                            err = NULL
                        WHERE status = 'in_progress'
                          AND updated_at < @cutoff
                          AND (
                            path = @prefix
                            OR substr(path, 1, length(@prefixForward)) = @prefixForward
                            OR substr(path, 1, length(@prefixBack)) = @prefixBack
                          );
                    ";

                    return conn.Execute(sql, new { prefix = normalizedPrefix, prefixForward, prefixBack, now, cutoff });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to recover stale in_progress items under prefix='{0}'", pathPrefix);
                    return 0;
                }
            }
        }

        public int RecoverInProgressUpdatedBefore(string pathPrefix, long updatedBefore, string error = null)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix) || updatedBefore <= 0)
            {
                return 0;
            }

            using (var conn = _dbContext.OpenConnection())
            {
                try
                {
                    var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var prefixForward = normalizedPrefix + "/";
                    var prefixBack = normalizedPrefix + "\\";
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    var sql = @"
                        UPDATE ingest_queue
                        SET status = 'queued',
                            updated_at = @now,
                            err = @error
                        WHERE status = 'in_progress'
                          AND updated_at < @updatedBefore
                          AND (
                            path = @prefix
                            OR substr(path, 1, length(@prefixForward)) = @prefixForward
                            OR substr(path, 1, length(@prefixBack)) = @prefixBack
                          );
                    ";

                    return conn.Execute(sql, new
                    {
                        prefix = normalizedPrefix,
                        prefixForward,
                        prefixBack,
                        updatedBefore,
                        error = error ?? "RECOVERED_PREVIOUS_COMMAND",
                        now
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to recover abandoned in_progress items under prefix='{0}'", pathPrefix);
                    return 0;
                }
            }
        }

        public bool TryClaimItem(int id, out IngestQueueItem item)
        {
            using (var conn = _dbContext.OpenConnection())
            {
                    var sql = @"
                        UPDATE ingest_queue
                        SET status = 'in_progress',
                            updated_at = @now,
                            attempts = attempts + 1
                        WHERE id = @id AND status = 'queued'
                        RETURNING id, path, mtime_ns as MtimeNs, size_bytes as SizeBytes,
                                  tags_json as TagsJson, duration_seconds as DurationSeconds, status, attempts, err,
                                  created_at as CreatedAt, updated_at as UpdatedAt;
                    ";

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                item = conn.QuerySingleOrDefault<IngestQueueItem>(sql, new { id, now });
                _logger.Debug("[STAGING-DB] TryClaimItem id={0} claimed={1}", id, item != null);
                
                return item != null;
            }
        }

        public List<IngestQueueItem> TryClaimUnit(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return new List<IngestQueueItem>();

            using (var conn = _dbContext.OpenConnection())
            {
                // Normalize folder path (no trailing separator)
                var normalizedFolder = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var folderForward = normalizedFolder + "/";
                var folderBack = normalizedFolder + "\\";

                var sql = @"
                    UPDATE ingest_queue
                    SET status = 'in_progress',
                        updated_at = @now,
                        attempts = attempts + 1
                    WHERE status = 'queued'
                      AND (
                        path = @folder
                        OR substr(path, 1, length(@folderForward)) = @folderForward
                        OR substr(path, 1, length(@folderBack)) = @folderBack
                      )
                    RETURNING id, path, mtime_ns as MtimeNs, size_bytes as SizeBytes,
                              tags_json as TagsJson, duration_seconds as DurationSeconds, status, attempts, err,
                              created_at as CreatedAt, updated_at as UpdatedAt;
                ";

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var claimed = conn.Query<IngestQueueItem>(sql, new { now, folder = normalizedFolder, folderForward, folderBack }).ToList();
                _logger.Debug("[STAGING-DB] TryClaimUnit folder='{0}' claimed={1}", normalizedFolder, claimed.Count);
                return claimed;
            }
        }

        public void UpdateStatus(int id, string status, string error = null)
        {
            using (var conn = _dbContext.OpenConnection())
            {
                var sql = @"
                    UPDATE ingest_queue
                    SET status = @status,
                        err = @error,
                        updated_at = @now
                    WHERE id = @id;
                ";

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                conn.Execute(sql, new { id, status, error, now });
                _logger.Debug("[STAGING-DB] UpdateStatus id={0} -> '{1}' err='{2}'", id, status, error);
            }
        }

        public void UpdateBatchTagsJson(IEnumerable<(int Id, string TagsJson)> items)
        {
            var list = items?.ToList();
            if (list == null || list.Count == 0) return;

            using (var conn = _dbContext.OpenConnection())
            using (var tran = conn.BeginTransaction())
            {
                try
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var sql = "UPDATE ingest_queue SET tags_json = @tagsJson, updated_at = @now WHERE id = @id;";
                    foreach (var item in list)
                    {
                        conn.Execute(sql, new { id = item.Id, tagsJson = item.TagsJson ?? "{}", now }, tran);
                    }
                    tran.Commit();
                    _logger.Debug("[STAGING-DB] UpdateBatchTagsJson updated {0} items", list.Count);
                }
                catch (Exception ex)
                {
                    tran.Rollback();
                    _logger.Error(ex, "[STAGING-DB] Failed to batch update tags_json for {0} items", list.Count);
                }
            }
        }

        public void UpdateBatchTagsAndDuration(IEnumerable<(int Id, string TagsJson, int? DurationSeconds)> items)
        {
            var list = items?.ToList();
            if (list == null || list.Count == 0)
            {
                return;
            }

            using (var conn = _dbContext.OpenConnection())
            using (var tran = conn.BeginTransaction())
            {
                try
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var sql = "UPDATE ingest_queue SET tags_json = @tagsJson, duration_seconds = @durationSeconds, updated_at = @now WHERE id = @id;";
                    foreach (var item in list)
                    {
                        conn.Execute(sql, new { id = item.Id, tagsJson = item.TagsJson ?? "{}", durationSeconds = item.DurationSeconds, now }, tran);
                    }

                    tran.Commit();
                    _logger.Debug("[STAGING-DB] UpdateBatchTagsAndDuration updated {0} items", list.Count);
                }
                catch (Exception ex)
                {
                    tran.Rollback();
                    _logger.Error(ex, "[STAGING-DB] Failed to batch update tags_json+duration_seconds for {0} items", list.Count);
                }
            }
        }

            public void UpdateBatchStatus(List<int> ids, string status)
            {
                if (!ids.Any()) return;

                using (var conn = _dbContext.OpenConnection())
                {
                    var sql = @"
                        UPDATE ingest_queue
                        SET status = @status,
                            updated_at = @now
                        WHERE id IN @ids;
                    ";

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var uniqueIds = ids.Distinct().ToList();
                    if (uniqueIds.Count > SqliteVariableLimit.MaxParameters)
                    {
                        using (var tran = conn.BeginTransaction())
                        {
                            foreach (var batch in uniqueIds.Chunk(SqliteVariableLimit.MaxParameters))
                            {
                                conn.Execute(sql, new { ids = batch.ToArray(), status, now }, tran);
                            }

                            tran.Commit();
                        }
                    }
                    else
                    {
                        conn.Execute(sql, new { ids = uniqueIds, status, now });
                    }
                }
            }

        public void RequeueInProgress(List<int> ids, string error = null)
        {
            if (ids == null || ids.Count == 0)
            {
                return;
            }

            using (var conn = _dbContext.OpenConnection())
            {
                try
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var uniqueIds = ids.Distinct().ToList();

                    const string sql = @"
                        UPDATE ingest_queue
                        SET status = 'queued',
                            err = @error,
                            updated_at = @now
                        WHERE status = 'in_progress'
                          AND id IN @ids;
                    ";

                    if (uniqueIds.Count > SqliteVariableLimit.MaxParameters)
                    {
                        using (var tran = conn.BeginTransaction())
                        {
                            foreach (var batch in uniqueIds.Chunk(SqliteVariableLimit.MaxParameters))
                            {
                                conn.Execute(sql, new { ids = batch.ToArray(), error, now }, tran);
                            }

                            tran.Commit();
                        }
                    }
                    else
                    {
                        conn.Execute(sql, new { ids = uniqueIds, error, now });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to requeue in_progress ids");
                }
            }
        }

        public int GetQueueCount()
        {
            using (var conn = _dbContext.OpenConnection())
            {
                return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM ingest_queue WHERE status IN ('queued', 'in_progress')");
            }
        }

            public int RequeueFailedOrUnmappedUnderPath(string pathPrefix)
            {
                if (string.IsNullOrWhiteSpace(pathPrefix))
                {
                    return 0;
                }

                using (var conn = _dbContext.OpenConnection())
                {
                    try
                    {
                        var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var prefixForward = normalizedPrefix + "/";
                        var prefixBack = normalizedPrefix + "\\";
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                        var updated = conn.Execute(@"
                            UPDATE ingest_queue
                            SET status = 'queued',
                                attempts = 0,
                                err = NULL,
                                updated_at = @now
                            WHERE status IN ('done', 'error')
                              AND (
                                path = @prefix
                                OR substr(path, 1, length(@prefixForward)) = @prefixForward
                                OR substr(path, 1, length(@prefixBack)) = @prefixBack
                              )
                              AND (
                                (
                                  SELECT r.outcome
                                  FROM import_results r
                                  WHERE r.queue_item_id = ingest_queue.id
                                  ORDER BY r.id DESC
                                  LIMIT 1
                                ) IN ('unmapped', 'failed')
                                OR NOT EXISTS (
                                  SELECT 1
                                  FROM import_results r
                                  WHERE r.queue_item_id = ingest_queue.id
                                )
                              );
                        ", new { prefix = normalizedPrefix, prefixForward, prefixBack, now });

                        if (updated > 0)
                        {
                            _logger.Debug("[STAGING-DB] Re-queued {0} previously failed/unmapped items under '{1}'", updated, normalizedPrefix);
                        }

                        return updated;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[STAGING-DB] Failed to requeue items under prefix='{0}'", pathPrefix);
                        return 0;
                    }
                }
            }

            public int RequeueFailedPaths(IEnumerable<string> paths)
            {
                var uniquePaths = (paths ?? Enumerable.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(PathEqualityComparer.Instance)
                    .ToList();

                if (uniquePaths.Count == 0)
                {
                    return 0;
                }

                using (var conn = _dbContext.OpenConnection())
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var updated = 0;

                        foreach (var pathBatch in uniquePaths.Chunk(500))
                        {
                            updated += conn.Execute(@"
                                UPDATE ingest_queue
                                SET status = 'queued',
                                    err = NULL,
                                    updated_at = @now
                                WHERE status IN ('done', 'error')
                                  AND path IN @paths
                                  AND (
                                    SELECT r.outcome
                                    FROM import_results r
                                    WHERE r.queue_item_id = ingest_queue.id
                                    ORDER BY r.id DESC
                                    LIMIT 1
                                  ) = 'failed';
                            ", new { paths = pathBatch.ToArray(), now }, transaction);
                        }

                        transaction.Commit();

                        if (updated > 0)
                        {
                            _logger.Debug("[STAGING-DB] Re-queued {0} failed items observed in the current root scan", updated);
                        }

                        return updated;
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            transaction.Rollback();
                        }
                        catch
                        {
                        }

                        _logger.Debug(ex, "[STAGING-DB] Failed to requeue failed items observed in the current root scan");
                        return 0;
                    }
                }
            }

        public int PurgeUnderPath(string pathPrefix)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                return 0;
            }

            using (var conn = _dbContext.OpenConnection())
            using (var transaction = conn.BeginTransaction())
            {
                try
                {
                    var normalizedPrefix = pathPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var prefixForward = normalizedPrefix + "/";
                    var prefixBack = normalizedPrefix + "\\";

                    // Delete results first (FK references ingest_queue)
                    var deletedResults = conn.Execute(@"
                        DELETE FROM import_results
                        WHERE queue_item_id IN (
                            SELECT id
                            FROM ingest_queue
                            WHERE path = @prefix
                               OR substr(path, 1, length(@prefixForward)) = @prefixForward
                               OR substr(path, 1, length(@prefixBack)) = @prefixBack
                        );
                    ", new { prefix = normalizedPrefix, prefixForward, prefixBack }, transaction);

                    var deletedQueue = conn.Execute(@"
                        DELETE FROM ingest_queue
                        WHERE path = @prefix
                           OR substr(path, 1, length(@prefixForward)) = @prefixForward
                           OR substr(path, 1, length(@prefixBack)) = @prefixBack;
                    ", new { prefix = normalizedPrefix, prefixForward, prefixBack }, transaction);

                    transaction.Commit();

                    if (deletedQueue > 0 || deletedResults > 0)
                    {
                        _logger.Debug("[STAGING-DB] Purged {0} queue items and {1} results under '{2}'", deletedQueue, deletedResults, normalizedPrefix);
                    }

                    return deletedQueue;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.Debug(ex, "[STAGING-DB] Failed to purge items under prefix='{0}'", pathPrefix);
                    return 0;
                }
            }
        }

        public int PurgePaths(IEnumerable<string> paths)
        {
            var uniquePaths = paths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(PathEqualityComparer.Instance)
                .ToList() ?? new List<string>();

            if (uniquePaths.Count == 0)
            {
                return 0;
            }

            var deletedQueueTotal = 0;
            var deletedResultsTotal = 0;

            foreach (var batch in uniquePaths.Chunk(900))
            {
                using (var conn = _dbContext.OpenConnection())
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        var batchPaths = batch.ToArray();

                        var deletedResults = conn.Execute(@"
                            DELETE FROM import_results
                            WHERE queue_item_id IN (
                                SELECT id
                                FROM ingest_queue
                                WHERE path IN @paths
                            );
                        ", new { paths = batchPaths }, transaction);

                        var deletedQueue = conn.Execute(@"
                            DELETE FROM ingest_queue
                            WHERE path IN @paths;
                        ", new { paths = batchPaths }, transaction);

                        transaction.Commit();

                        deletedQueueTotal += deletedQueue;
                        deletedResultsTotal += deletedResults;
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _logger.Debug(ex, "[STAGING-DB] Failed to purge batch of {0} exact paths", batch.Count());
                    }
                }
            }

            if (deletedQueueTotal > 0 || deletedResultsTotal > 0)
            {
                _logger.Debug("[STAGING-DB] Purged {0} queue items and {1} results for {2} exact paths",
                    deletedQueueTotal,
                    deletedResultsTotal,
                    uniquePaths.Count);
            }

            return deletedQueueTotal;
        }

        public void PurgeOldCompleted(int daysToKeep = 14)
        {
            using (var conn = _dbContext.OpenConnection())
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-daysToKeep).ToUnixTimeSeconds();
                
                int deletedResults;
                int deletedQueue;

                using (var transaction = conn.BeginTransaction())
                {
                    // Must delete child rows first; import_results.queue_item_id references ingest_queue.id.
                    deletedResults = conn.Execute(@"
                        DELETE FROM import_results
                        WHERE queue_item_id IN (
                            SELECT id
                            FROM ingest_queue
                            WHERE status = 'done' AND updated_at < @cutoff
                        );
                    ", new { cutoff }, transaction);

                    deletedQueue = conn.Execute(@"
                        DELETE FROM ingest_queue
                        WHERE status = 'done' AND updated_at < @cutoff;
                    ", new { cutoff }, transaction);

                    transaction.Commit();
                }

                if (deletedQueue > 0 || deletedResults > 0)
                {
                    _logger.Info("Purged {0} completed items and {1} results older than {2} days", deletedQueue, deletedResults, daysToKeep);
                    
                    // VACUUM periodically to reclaim space
                    conn.Execute("VACUUM;");
                }
            }
        }
        
        public void RecordImportResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null)
        {
            using (var conn = _dbContext.OpenConnection())
            {
                InsertImportResult(conn, null, queueItemId, path, outcome, bookId, authorId, quality, errorMessage);
                _logger.Debug("Recorded {0} result for {1}", outcome, path);
            }
        }

        public void CompleteItemWithResult(int queueItemId, string path, ImportOutcome outcome, int? bookId = null, int? authorId = null, string quality = null, string errorMessage = null, string statusError = null)
        {
            using (var conn = _dbContext.OpenConnection())
            using (var transaction = conn.BeginTransaction())
            {
                try
                {
                    InsertImportResult(conn, transaction, queueItemId, path, outcome, bookId, authorId, quality, errorMessage);

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    conn.Execute(@"
                        UPDATE ingest_queue
                        SET status = 'done',
                            err = @error,
                            updated_at = @now
                        WHERE id = @id;
                    ", new { id = queueItemId, error = statusError, now }, transaction);

                    transaction.Commit();
                    _logger.Debug("Completed queue item {0} with {1} result for {2}", queueItemId, outcome, path);
                }
                catch (Exception ex)
                {
                    try
                    {
                        transaction.Rollback();
                    }
                    catch
                    {
                    }

                    _logger.Warn(ex, "[STAGING-DB] Failed to record {0} result for queue item {1}; marking item done without result history", outcome, queueItemId);

                    try
                    {
                        UpdateStatus(queueItemId, "done", statusError ?? errorMessage ?? ex.Message);
                    }
                    catch (Exception statusEx)
                    {
                        _logger.Error(statusEx, "[STAGING-DB] Failed to mark queue item {0} done after result recording failed", queueItemId);
                        throw;
                    }
                }
            }
        }

        private static void InsertImportResult(IDbConnection conn, IDbTransaction transaction, int queueItemId, string path, ImportOutcome outcome, int? bookId, int? authorId, string quality, string errorMessage)
        {
            var sql = @"
                INSERT INTO import_results (queue_item_id, path, outcome, book_id, author_id, quality, error_message, created_at)
                VALUES (@queueItemId, @path, @outcome, @bookId, @authorId, @quality, @errorMessage, @createdAt);
            ";

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(sql, new
            {
                queueItemId,
                path,
                outcome = outcome.ToString().ToLowerInvariant(),
                bookId,
                authorId,
                quality,
                errorMessage,
                createdAt = now
            }, transaction);
        }
        
            public List<ImportResult> GetImportResults(int? commandId = null)
            {
                using (var conn = _dbContext.OpenConnection())
                {
                    // Filter by command session marker (if available) so a scan doesn't reuse stale import_results rows.
                    long? resultsStartId = null;
                    long? startedAt = null;
                    if (commandId.HasValue && commandId.Value > 0)
                    {
                        try
                        {
                            var resultsKey = $"command:{commandId.Value}:results_start_id";
                            var resultsRaw = conn.ExecuteScalar<string>("SELECT value FROM staging_metadata WHERE key = @key", new { key = resultsKey });
                            if (long.TryParse(resultsRaw, out var parsedResultsId))
                            {
                                resultsStartId = parsedResultsId;
                            }

                            var key = $"command:{commandId.Value}:started_at";
                            var raw = conn.ExecuteScalar<string>("SELECT value FROM staging_metadata WHERE key = @key", new { key });
                            if (long.TryParse(raw, out var parsed))
                            {
                                startedAt = parsed;
                        }
                    }
                    catch
                    {
                        startedAt = null;
                    }
                }

                    var sql = @"
                        SELECT id, queue_item_id as QueueItemId, path, 
                               outcome, book_id as BookId, author_id as AuthorId, 
                               quality, error_message as ErrorMessage, created_at as CreatedAt
                        FROM import_results
                        /**where**/
                        ORDER BY created_at DESC
                        LIMIT 1000;
                    ";

                    var p = new DynamicParameters();
                    if (resultsStartId.HasValue)
                    {
                        sql = sql.Replace("/**where**/", "WHERE id > @resultsStartId");
                        p.Add("resultsStartId", resultsStartId.Value);
                    }
                    else if (startedAt.HasValue)
                    {
                        sql = sql.Replace("/**where**/", "WHERE created_at >= @startedAt");
                        p.Add("startedAt", startedAt.Value);
                    }
                    else
                {
                    sql = sql.Replace("/**where**/", string.Empty);
                }

                var results = conn.Query<ImportResultRaw>(sql, p).ToList();
                
                // Convert outcome string to enum
                return results.Select(r => new ImportResult
                {
                    Id = r.Id,
                    QueueItemId = r.QueueItemId,
                    Path = r.Path,
                    Outcome = Enum.Parse<ImportOutcome>(r.Outcome, true),
                    BookId = r.BookId,
                    AuthorId = r.AuthorId,
                    Quality = r.Quality,
                    ErrorMessage = r.ErrorMessage,
                    CreatedAt = r.CreatedAt
                }).ToList();
            }
        }
        
        private class ImportResultRaw
        {
            public int Id { get; set; }
            public int QueueItemId { get; set; }
            public string Path { get; set; }
            public string Outcome { get; set; }
            public int? BookId { get; set; }
            public int? AuthorId { get; set; }
            public string Quality { get; set; }
            public string ErrorMessage { get; set; }
            public long CreatedAt { get; set; }
        }
    }

    public class IngestQueueStatusCount
    {
        public string Status { get; set; }
        public int Count { get; set; }
    }

    public class IngestQueueItem
    {
        public int Id { get; set; }
        public string Path { get; set; }
        public long MtimeNs { get; set; }
        public long SizeBytes { get; set; }
        public string TagsJson { get; set; }
        public int? DurationSeconds { get; set; }
        public string Status { get; set; }
        public int Attempts { get; set; }
        public string Err { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public bool ForceRequeue { get; set; }
    }
    
    public enum ImportOutcome
    {
        Imported,
        Unmapped,
        Failed,
        Ignored,
        AlreadyLinked
    }
    
    public class ImportResult
    {
        public int Id { get; set; }
        public int QueueItemId { get; set; }
        public string Path { get; set; }
        public ImportOutcome Outcome { get; set; }
        public int? BookId { get; set; }
        public int? AuthorId { get; set; }
        public string Quality { get; set; }
        public string ErrorMessage { get; set; }
        public long CreatedAt { get; set; }
    }
}
