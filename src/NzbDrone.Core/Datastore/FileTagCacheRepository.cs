using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace NzbDrone.Core.Datastore
{
    public interface IFileTagCacheRepository
    {
        bool TryGet(string path, long mtimeNs, long sizeBytes, out string tagsJson, out int? durationSeconds, out string extractionStatus);
        void Upsert(string path, long mtimeNs, long sizeBytes, string tagsJson, int? durationSeconds, string extractionStatus);
        void PurgeOld(int daysToKeep = 30);
        void MoveFromStaging(IStagingDbContext staging);
    }

    public class FileTagCacheRepository : IFileTagCacheRepository
    {
        private const string InsertSql = "INSERT INTO file_tag_cache (path, mtime_ns, size_bytes, tags_json, duration_seconds, extraction_status, updated_at) VALUES ";
        private const string ConflictSql = @"
            ON CONFLICT(path) DO UPDATE SET
                mtime_ns = excluded.mtime_ns,
                size_bytes = excluded.size_bytes,
                tags_json = excluded.tags_json,
                duration_seconds = excluded.duration_seconds,
                extraction_status = excluded.extraction_status,
                updated_at = excluded.updated_at";

        private readonly ICacheDatabase _dbContext;
        private readonly Logger _logger;

        public FileTagCacheRepository(ICacheDatabase dbContext, Logger logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public bool TryGet(string path, long mtimeNs, long sizeBytes, out string tagsJson, out int? durationSeconds, out string extractionStatus)
        {
            tagsJson = null;
            durationSeconds = null;
            extractionStatus = null;

            if (path.IsNullOrWhiteSpace())
            {
                return false;
            }

            try
            {
                using var conn = _dbContext.OpenConnection();
                var row = conn.QuerySingleOrDefault<FileTagCacheRow>(@"
                    SELECT tags_json AS TagsJson,
                           duration_seconds AS DurationSeconds,
                           extraction_status AS ExtractionStatus
                    FROM file_tag_cache
                    WHERE path = @path
                      AND mtime_ns = @mtimeNs
                      AND size_bytes = @sizeBytes;
                ", new { path, mtimeNs, sizeBytes });

                if (row == null)
                {
                    return false;
                }

                tagsJson = row.TagsJson;
                durationSeconds = row.DurationSeconds;
                extractionStatus = row.ExtractionStatus;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "[TAG-CACHE] Failed to read file tag cache for '{0}'", path);
                return false;
            }
        }

        public void Upsert(string path, long mtimeNs, long sizeBytes, string tagsJson, int? durationSeconds, string extractionStatus)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return;
            }

            if (!TagExtractionResult.IsCacheableStorageValue(extractionStatus))
            {
                throw new ArgumentOutOfRangeException(nameof(extractionStatus), extractionStatus, "Only successful tag-extraction dispositions may be cached.");
            }

            try
            {
                using var conn = _dbContext.OpenConnection();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                conn.Execute(InsertSql + "(@path, @mtimeNs, @sizeBytes, @tagsJson, @durationSeconds, @extractionStatus, @now)" + ConflictSql, new
                {
                    path,
                    mtimeNs,
                    sizeBytes,
                    tagsJson = tagsJson ?? "{}",
                    durationSeconds,
                    extractionStatus,
                    now
                });
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "[TAG-CACHE] Failed to write file tag cache for '{0}'", path);
            }
        }

        public void MoveFromStaging(IStagingDbContext staging)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                using var source = staging.OpenConnection();
                var columns = source.Query<string>("SELECT name FROM pragma_table_info('file_tag_cache')").ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (columns.Count == 0)
                {
                    return;
                }

                using var destination = _dbContext.OpenConnection();
                using var transaction = destination.BeginTransaction();
                var status = columns.Contains("extraction_status") ? "extraction_status" : "NULL";
                var rows = source.Query<FileTagCacheRow>($@"
                    SELECT path AS Path, mtime_ns AS MtimeNs, size_bytes AS SizeBytes,
                           tags_json AS TagsJson, duration_seconds AS DurationSeconds,
                           {status} AS ExtractionStatus, updated_at AS UpdatedAt
                    FROM file_tag_cache", buffered: false);
                var count = 0;
                var batches = 0;
                foreach (var batch in rows.Chunk(SqliteVariableLimit.MaxParameters / 7))
                {
                    var parameters = new DynamicParameters();
                    var values = new List<string>();
                    for (var i = 0; i < batch.Length; i++)
                    {
                        var row = batch[i];
                        parameters.Add("path" + i, row.Path);
                        parameters.Add("mtime" + i, row.MtimeNs);
                        parameters.Add("size" + i, row.SizeBytes);
                        parameters.Add("tags" + i, row.TagsJson);
                        parameters.Add("duration" + i, row.DurationSeconds);
                        parameters.Add("status" + i, row.ExtractionStatus);
                        parameters.Add("updated" + i, row.UpdatedAt);
                        values.Add($"(@path{i}, @mtime{i}, @size{i}, @tags{i}, @duration{i}, @status{i}, @updated{i})");
                    }

                    // A restarted transfer must not overwrite a newer extraction in the destination.
                    destination.Execute(InsertSql + string.Join(",", values) + ConflictSql +
                        " WHERE excluded.updated_at > file_tag_cache.updated_at", parameters, transaction);
                    count += batch.Length;
                    batches++;
                }

                transaction.Commit();
                // The source survives any failed copy; a crash before removal can safely replay it.
                source.Execute("DROP TABLE file_tag_cache");
                _logger.Info("[TAG-CACHE] Moved {0} cached tag reads out of staging in {1} batches, {2} ms", count, batches, elapsed.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[TAG-CACHE] Could not move the old tag cache after {0} ms; retained staging data for the next startup", elapsed.ElapsedMilliseconds);
            }
        }

        public void PurgeOld(int daysToKeep = 30)
        {
            try
            {
                using var conn = _dbContext.OpenConnection();
                var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, daysToKeep)).ToUnixTimeSeconds();
                var deleted = conn.Execute("DELETE FROM file_tag_cache WHERE updated_at < @cutoff;", new { cutoff });
                if (deleted > 0)
                {
                    _logger.Debug("[TAG-CACHE] Purged {0} old file tag cache rows", deleted);
                }
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "[TAG-CACHE] Failed to purge old file tag cache rows");
            }
        }

        private sealed class FileTagCacheRow
        {
            public string Path { get; set; }
            public long MtimeNs { get; set; }
            public long SizeBytes { get; set; }
            public string TagsJson { get; set; }
            public int? DurationSeconds { get; set; }
            public string ExtractionStatus { get; set; }
            public long UpdatedAt { get; set; }
        }
    }
}
