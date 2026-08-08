using System;
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
    }

    public class FileTagCacheRepository : IFileTagCacheRepository
    {
        private readonly IStagingDbContext _dbContext;
        private readonly Logger _logger;

        public FileTagCacheRepository(IStagingDbContext dbContext, Logger logger)
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
                conn.Execute(@"
                    INSERT INTO file_tag_cache (path, mtime_ns, size_bytes, tags_json, duration_seconds, extraction_status, updated_at)
                    VALUES (@path, @mtimeNs, @sizeBytes, @tagsJson, @durationSeconds, @extractionStatus, @now)
                    ON CONFLICT(path) DO UPDATE SET
                        mtime_ns = excluded.mtime_ns,
                        size_bytes = excluded.size_bytes,
                        tags_json = excluded.tags_json,
                        duration_seconds = excluded.duration_seconds,
                        extraction_status = excluded.extraction_status,
                        updated_at = excluded.updated_at;
                ", new
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
            public string TagsJson { get; set; }
            public int? DurationSeconds { get; set; }
            public string ExtractionStatus { get; set; }
        }
    }
}
