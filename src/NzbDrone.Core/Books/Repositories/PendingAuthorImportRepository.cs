using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IPendingAuthorImportRepository : IBasicRepository<PendingAuthorImport>
    {
        PendingAuthorImport GetByProviderId(string providerId);
        PendingAuthorImport GetActiveByProviderId(string providerId);
        List<PendingAuthorImport> GetFailedByProviderId(string providerId);
        List<PendingAuthorImport> GetActiveByProviderIds(IEnumerable<string> providerIds);
        List<PendingAuthorImport> GetDueForProcessing(DateTime cutoff, int limit);
        List<PendingAuthorImport> GetByStatus(PendingImportStatus status);
        List<PendingAuthorImport> GetAll();
        bool TryUpdateRequest(PendingAuthorImport item, long expectedVersion);
        bool TryDelete(int id, long expectedVersion);
        void DeleteOldCompleted(DateTime cutoff);
    }

    public class PendingAuthorImportRepository : BasicRepository<PendingAuthorImport>, IPendingAuthorImportRepository
    {
        public PendingAuthorImportRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public PendingAuthorImport GetByProviderId(string providerId)
        {
            using var connection = _database.OpenConnection();
            return connection.QueryFirstOrDefault<PendingAuthorImport>(@"
                SELECT * FROM ""PendingAuthorImport""
                 WHERE ""ProviderId"" = @providerId
                 ORDER BY COALESCE(""UpdatedAt"", ""CreatedAt"") DESC, ""Id"" DESC
                 LIMIT 1", new { providerId });
        }

        public PendingAuthorImport GetActiveByProviderId(string providerId)
        {
            return Query(x => x.ProviderId == providerId &&
                            (x.OverallStatus == PendingImportStatus.Pending ||
                             x.OverallStatus == PendingImportStatus.InProgress ||
                             x.OverallStatus == PendingImportStatus.Retrying))
                .FirstOrDefault();
        }

        public List<PendingAuthorImport> GetFailedByProviderId(string providerId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<PendingAuthorImport>(@"
                SELECT * FROM ""PendingAuthorImport""
                 WHERE ""ProviderId"" = @providerId AND ""OverallStatus"" = @failed
                 ORDER BY COALESCE(""UpdatedAt"", ""CreatedAt"") DESC, ""Id"" DESC",
                new { providerId, failed = (int)PendingImportStatus.Failed }).ToList();
        }

        public List<PendingAuthorImport> GetDueForProcessing(DateTime cutoff, int limit)
        {
            if (limit <= 0) return new List<PendingAuthorImport>();
            using var connection = _database.OpenConnection();
            return connection.Query<PendingAuthorImport>(@"
                SELECT * FROM ""PendingAuthorImport""
                 WHERE ""NextAttemptAt"" <= @cutoff
                   AND ""OverallStatus"" IN (@pending, @retrying)
                 ORDER BY ""NextAttemptAt"", ""Id""
                 LIMIT @limit", new { cutoff, limit, pending = (int)PendingImportStatus.Pending, retrying = (int)PendingImportStatus.Retrying }).ToList();
        }

        public List<PendingAuthorImport> GetActiveByProviderIds(IEnumerable<string> providerIds)
        {
            var result = new List<PendingAuthorImport>();
            using var connection = _database.OpenConnection();
            foreach (var ids in providerIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters - 3 : ushort.MaxValue - 3))
            {
                var clause = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                result.AddRange(connection.Query<PendingAuthorImport>($@"
                    SELECT * FROM ""PendingAuthorImport""
                     WHERE ""ProviderId"" {clause}
                       AND ""OverallStatus"" IN (@pending, @retrying, @processing)", new
                {
                    ids, pending = (int)PendingImportStatus.Pending, retrying = (int)PendingImportStatus.Retrying,
                    processing = (int)PendingImportStatus.InProgress
                }));
            }
            return result;
        }

        public List<PendingAuthorImport> GetByStatus(PendingImportStatus status)
        {
            return Query(x => x.OverallStatus == status).ToList();
        }

        public List<PendingAuthorImport> GetAll()
        {
            return All().OrderByDescending(x => x.CreatedAt).ToList();
        }

        public bool TryUpdateRequest(PendingAuthorImport item, long expectedVersion)
        {
            item.Version = expectedVersion + 1;

            const string sql = @"
                UPDATE ""PendingAuthorImport""
                   SET ""AuthorName"" = @AuthorName,
                       ""DiscoveredAuthorFolderPath"" = @DiscoveredAuthorFolderPath,
                       ""AudiobookStatus"" = @AudiobookStatus,
                       ""EbookStatus"" = @EbookStatus,
                       ""OverallStatus"" = @OverallStatus,
                       ""AudiobookMonitored"" = @AudiobookMonitored,
                       ""AudiobookMonitorNewItems"" = @AudiobookMonitorNewItems,
                       ""AudiobookMonitorExistingMode"" = @AudiobookMonitorExistingMode,
                       ""AudiobookQualityProfileId"" = @AudiobookQualityProfileId,
                       ""AudiobookMetadataProfileId"" = @AudiobookMetadataProfileId,
                       ""AudiobookRootFolderPath"" = @AudiobookRootFolderPath,
                       ""AudiobookBooksToAdd"" = @AudiobookBooksToAdd,
                       ""AudiobookBooksToMonitor"" = @AudiobookBooksToMonitor,
                       ""AudiobookBooksToSearch"" = @AudiobookBooksToSearch,
                       ""AudiobookNarrations"" = @AudiobookNarrations,
                       ""AudiobookTags"" = @AudiobookTags,
                       ""EbookMonitored"" = @EbookMonitored,
                       ""EbookMonitorNewItems"" = @EbookMonitorNewItems,
                       ""EbookMonitorExistingMode"" = @EbookMonitorExistingMode,
                       ""EbookQualityProfileId"" = @EbookQualityProfileId,
                       ""EbookMetadataProfileId"" = @EbookMetadataProfileId,
                       ""EbookRootFolderPath"" = @EbookRootFolderPath,
                       ""EbookBooksToAdd"" = @EbookBooksToAdd,
                       ""EbookBooksToMonitor"" = @EbookBooksToMonitor,
                       ""EbookBooksToSearch"" = @EbookBooksToSearch,
                       ""EbookTags"" = @EbookTags,
                       ""Tags"" = @Tags,
                       ""SearchForMissingBooks"" = @SearchForMissingBooks,
                       ""SyncMonitoredAcrossFormats"" = @SyncMonitoredAcrossFormats,
                       ""IsAuthorSettingsRequest"" = @IsAuthorSettingsRequest,
                       ""LastSelectedMediaType"" = @LastSelectedMediaType,
                       ""ExplicitBookSearches"" = @ExplicitBookSearches,
                       ""RequestedBy"" = @RequestedBy,
                       ""AudiobookRequestedBy"" = @AudiobookRequestedBy,
                       ""EbookRequestedBy"" = @EbookRequestedBy,
                       ""AttemptCount"" = @AttemptCount,
                       ""MaxAttempts"" = @MaxAttempts,
                       ""LastAttemptAt"" = @LastAttemptAt,
                       ""LastError"" = @LastError,
                       ""UpdatedAt"" = @UpdatedAt,
                       ""NextAttemptAt"" = @NextAttemptAt,
                       ""Version"" = @Version
                 WHERE ""Id"" = @Id
                   AND ""Version"" = @ExpectedVersion";

            using var connection = _database.OpenConnection();
            return connection.Execute(sql, new
            {
                item.AuthorName,
                item.DiscoveredAuthorFolderPath,
                item.AudiobookStatus,
                item.EbookStatus,
                item.OverallStatus,
                item.AudiobookMonitored,
                item.AudiobookMonitorNewItems,
                item.AudiobookMonitorExistingMode,
                item.AudiobookQualityProfileId,
                item.AudiobookMetadataProfileId,
                item.AudiobookRootFolderPath,
                item.AudiobookBooksToAdd,
                item.AudiobookBooksToMonitor,
                item.AudiobookBooksToSearch,
                item.AudiobookNarrations,
                item.AudiobookTags,
                item.EbookMonitored,
                item.EbookMonitorNewItems,
                item.EbookMonitorExistingMode,
                item.EbookQualityProfileId,
                item.EbookMetadataProfileId,
                item.EbookRootFolderPath,
                item.EbookBooksToAdd,
                item.EbookBooksToMonitor,
                item.EbookBooksToSearch,
                item.EbookTags,
                item.Tags,
                item.SearchForMissingBooks,
                item.SyncMonitoredAcrossFormats,
                item.IsAuthorSettingsRequest,
                item.LastSelectedMediaType,
                item.ExplicitBookSearches,
                item.RequestedBy,
                item.AudiobookRequestedBy,
                item.EbookRequestedBy,
                item.AttemptCount,
                item.MaxAttempts,
                item.LastAttemptAt,
                item.LastError,
                item.UpdatedAt,
                item.NextAttemptAt,
                item.Version,
                item.Id,
                ExpectedVersion = expectedVersion
            }) == 1;
        }

        public bool TryDelete(int id, long expectedVersion)
        {
            const string sql = @"
                DELETE FROM ""PendingAuthorImport""
                 WHERE ""Id"" = @Id
                   AND ""Version"" = @Version";

            using var connection = _database.OpenConnection();
            return connection.Execute(sql, new { Id = id, Version = expectedVersion }) == 1;
        }

        public void DeleteOldCompleted(DateTime cutoff)
        {
            Delete(x => x.UpdatedAt < cutoff && x.OverallStatus == PendingImportStatus.Succeeded);
        }
    }
}
