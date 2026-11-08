using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IConversionJobRepository : IBasicRepository<ConversionJob>
    {
        ConversionJob FindByDownloadId(string downloadId);
        ConversionJob FindByHistoryDownloadId(string downloadId, int? downloadClientId = null);
        List<ConversionJob> NonCompleted();
        List<ConversionOrphanCandidate> OrphanArtifacts(int afterId, IReadOnlyCollection<int> retryIds);
        List<ConversionJob> ByDownloadIds(IEnumerable<string> downloadIds);
        List<ConversionJob> ByStatus(ConversionJobStatus status);
        List<ConversionJob> PendingCleanup();
        void DeleteExpiredCancelled(IEnumerable<int> ids, DateTime cutoff);
        void DeleteByDownloadId(string downloadId);
        List<ConversionJob> CleanupWarnings();
        int CleanupWarningCount();
        List<ConversionJob> CleanupReviewCandidates(int afterId);
        List<ConversionLifecycleCandidate> LifecycleCandidates(int afterId);
    }

    public class ConversionJobRepository : BasicRepository<ConversionJob>, IConversionJobRepository
    {
        internal const int CleanupBatchSize = 100;

        public ConversionJobRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public ConversionJob FindByDownloadId(string downloadId) => string.IsNullOrWhiteSpace(downloadId)
            ? null : Query(job => job.DownloadId == downloadId).SingleOrDefault();

        public ConversionJob FindByHistoryDownloadId(string downloadId, int? downloadClientId = null)
        {
            if (string.IsNullOrWhiteSpace(downloadId)) return null;

            using var connection = _database.OpenConnection();
            var candidates = connection.Query<ConversionJob>(@"SELECT * FROM ""ConversionJobs""
                WHERE ""DownloadIdKey"" = @key AND (@downloadClientId IS NULL OR ""DownloadClientId"" = @downloadClientId)
                ORDER BY ""Id"" LIMIT 2", new { key = DownloadIdentity.Key(downloadId), downloadClientId }).ToList();
            if (candidates.Count > 1)
                throw new InvalidOperationException($"Ambiguous conversion download identity '{downloadId}' for client {downloadClientId?.ToString() ?? "unknown"}: jobs {string.Join(", ", candidates.Select(job => $"{job.Id} ({job.DownloadId}, client {job.DownloadClientId})"))}. No workspace was removed.");
            return candidates.SingleOrDefault();
        }

        public List<ConversionLifecycleCandidate> LifecycleCandidates(int afterId)
        {
            using var connection = _database.OpenConnection();
            var jobs = connection.Query<ConversionLifecycleCandidate>(@"SELECT j.""Id"", j.""DownloadId"", j.""DownloadClientId"", j.""Status"", j.""UpdatedAt"", j.""WorkRoot"",
                    (SELECT COUNT(*) FROM ""ConversionJobs"" other WHERE other.""DownloadIdKey"" = j.""DownloadIdKey""
                        AND other.""DownloadClientId"" = j.""DownloadClientId"") > 1 AS ""IdentityAmbiguous""
                FROM ""ConversionJobs"" j WHERE j.""Id"" > @afterId ORDER BY j.""Id"" LIMIT @limit",
                new { afterId, limit = CleanupBatchSize }).ToList();
            if (jobs.Count == 0) return jobs;
            var history = connection.Query<DownloadHistory>(DownloadHistoryRepository.BuildCurrentSignificantEventsCte(_database.DatabaseType, scoped: true, perClient: true) +
                @" SELECT * FROM significant WHERE ""RowNumber"" = 1", new
                {
                    downloadIds = jobs.Select(job => DownloadIdentity.Key(job.DownloadId)).Distinct().ToArray(),
                    significantEvents = DownloadHistoryRepository.SignificantEvents()
                }).ToDictionary(row => (row.DownloadId, row.DownloadClientId));
            var clients = connection.Query<int>(@"SELECT ""Id"" FROM ""DownloadClients""").ToHashSet();
            foreach (var job in jobs)
            {
                if (history.TryGetValue((DownloadIdentity.Key(job.DownloadId), job.DownloadClientId), out var row)) job.EventType = row.EventType;
                job.ClientMissing = job.DownloadClientId > 0 && !clients.Contains(job.DownloadClientId);
            }
            return jobs;
        }

        public List<ConversionOrphanCandidate> OrphanArtifacts(int afterId, IReadOnlyCollection<int> retryIds)
        {
            using var connection = _database.OpenConnection();
            // Reserve part of the bounded page for deferred candidates, so an active
            // download cannot prevent discovery of later history.
            var retryPredicate = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@retryIds)" : "IN @retryIds";
            var artifacts = connection.Query<EntityHistory>($@"WITH fresh AS (
                SELECT h.* FROM ""History"" h
                WHERE h.""Id"" > @afterId AND h.""EventType"" IN (@converted, @failed)
                    AND h.""DownloadId"" IS NOT NULL AND h.""DownloadId"" <> ''
                    AND NOT EXISTS (SELECT 1 FROM ""ConversionJobs"" j WHERE j.""DownloadIdKey"" = h.""DownloadId"")
                ORDER BY h.""Id"" LIMIT @limit)
                SELECT * FROM fresh
                UNION ALL SELECT h.* FROM ""History"" h WHERE h.""Id"" {retryPredicate}
                    AND NOT EXISTS (SELECT 1 FROM ""ConversionJobs"" j WHERE j.""DownloadIdKey"" = h.""DownloadId"")
                ORDER BY ""Id""", new { afterId, retryIds, limit = CleanupBatchSize - retryIds.Count,
                    converted = (int)EntityHistoryEventType.BookFileConverted, failed = (int)EntityHistoryEventType.BookFileConversionFailed }).ToList();
            if (artifacts.Count == 0) return new();
            var latest = connection.Query<DownloadHistory>(DownloadHistoryRepository.BuildCurrentSignificantEventsCte(_database.DatabaseType, scoped: true, perClient: true) +
                @" SELECT * FROM significant WHERE ""RowNumber"" = 1", new
                {
                    downloadIds = artifacts.Select(row => row.DownloadId).Distinct().ToArray(),
                    significantEvents = DownloadHistoryRepository.SignificantEvents()
                }).ToLookup(row => row.DownloadId);
            var clients = connection.Query<int>(@"SELECT ""Id"" FROM ""DownloadClients""").ToHashSet();
            return artifacts.Select(row =>
            {
                var terminal = latest[row.DownloadId].ToList();
                return new ConversionOrphanCandidate
                {
                    History = row,
                    Removed = terminal.Count > 0 && terminal.All(item => item.Date >= row.Date &&
                        item.EventType is DownloadHistoryEventType.DownloadImported or DownloadHistoryEventType.DownloadFailed or DownloadHistoryEventType.DownloadIgnored),
                    ClientCount = terminal.Count,
                    DownloadClientId = terminal.Count == 1 ? terminal[0].DownloadClientId : 0,
                    EventType = terminal.Count == 1 ? terminal[0].EventType : null,
                    ClientMissing = terminal.Count == 1 && terminal[0].DownloadClientId > 0 && !clients.Contains(terminal[0].DownloadClientId)
                };
            }).ToList();
        }

        public List<ConversionJob> NonCompleted()
        {
            return Query(job => job.Status != ConversionJobStatus.Completed);
        }

        public List<ConversionJob> ByDownloadIds(IEnumerable<string> downloadIds)
        {
            var ids = downloadIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray() ?? Array.Empty<string>();
            return ids.Length == 0 ? new List<ConversionJob>() : Query(job => ids.Contains(job.DownloadId));
        }

        public List<ConversionJob> ByStatus(ConversionJobStatus status)
        {
            return Query(job => job.Status == status);
        }

        public List<ConversionJob> PendingCleanup()
        {
            return Query(Builder().Where<ConversionJob>(job => (job.Status == ConversionJobStatus.Cancelled || job.Status == ConversionJobStatus.Completed) && job.CleanupPending)
                .OrderBy("\"UpdatedAt\", \"Id\" LIMIT @limit", new { limit = CleanupBatchSize }));
        }

        public void DeleteExpiredCancelled(IEnumerable<int> ids, DateTime cutoff)
        {
            var selected = ids.Distinct().ToArray();
            if (selected.Length == 0) return;
            var batchSize = _database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters - 3 : selected.Length;
            foreach (var batch in selected.Chunk(batchSize))
            {
                Delete(Builder().Where<ConversionJob>(job => batch.Contains(job.Id) && job.Status == ConversionJobStatus.Cancelled && job.CompletedAt < cutoff &&
                    !job.CleanupPending));
            }
        }

        public List<ConversionJob> CleanupReviewCandidates(int afterId)
        {
            return Query(Builder().Where<ConversionJob>(job => job.Id > afterId && job.CleanupAttention)
                .OrderBy("\"Id\" LIMIT @limit", new { limit = CleanupBatchSize }));
        }

        public List<ConversionJob> CleanupWarnings() => CleanupReviewCandidates(0);

        public int CleanupWarningCount()
        {
            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""ConversionJobs"" WHERE ""CleanupAttention"" = @attention", new { attention = true });
        }

        public void DeleteByDownloadId(string downloadId)
        {
            if (!string.IsNullOrWhiteSpace(downloadId))
            {
                Delete(job => job.DownloadId == downloadId);
            }
        }


    }

    public class ConversionOrphanCandidate
    {
        public EntityHistory History { get; set; }
        public bool Removed { get; set; }
        public int ClientCount { get; set; }
        public int DownloadClientId { get; set; }
        public DownloadHistoryEventType? EventType { get; set; }
        public bool ClientMissing { get; set; }
    }

    public class ConversionLifecycleCandidate
    {
        public int Id { get; set; }
        public string DownloadId { get; set; }
        public ConversionJobStatus Status { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DownloadHistoryEventType EventType { get; set; }
        public int DownloadClientId { get; set; }
        public bool ClientMissing { get; set; }
        public bool IdentityAmbiguous { get; set; }
        public string WorkRoot { get; set; }
    }
}
