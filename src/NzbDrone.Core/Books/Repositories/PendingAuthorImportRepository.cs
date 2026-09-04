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
            return Query(x => x.ProviderId == providerId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefault();
        }

        public PendingAuthorImport GetActiveByProviderId(string providerId)
        {
            return Query(x => x.ProviderId == providerId &&
                            (x.OverallStatus == PendingImportStatus.Pending ||
                             x.OverallStatus == PendingImportStatus.InProgress ||
                             x.OverallStatus == PendingImportStatus.Retrying))
                .FirstOrDefault();
        }

        public List<PendingAuthorImport> GetDueForProcessing(DateTime cutoff, int limit)
        {
            return Query(x => x.NextAttemptAt <= cutoff &&
                            (x.OverallStatus == PendingImportStatus.Pending ||
                             x.OverallStatus == PendingImportStatus.Retrying))
                .OrderBy(x => x.NextAttemptAt)
                .Take(limit)
                .ToList();
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
                   SET ""DiscoveredAuthorFolderPath"" = @DiscoveredAuthorFolderPath,
                       ""AudiobookStatus"" = @AudiobookStatus,
                       ""EbookStatus"" = @EbookStatus,
                       ""OverallStatus"" = @OverallStatus,
                       ""AudiobookMonitored"" = @AudiobookMonitored,
                       ""AudiobookMonitorNewItems"" = @AudiobookMonitorNewItems,
                       ""AudiobookMonitorExistingMode"" = @AudiobookMonitorExistingMode,
                       ""AudiobookQualityProfileId"" = @AudiobookQualityProfileId,
                       ""AudiobookMetadataProfileId"" = @AudiobookMetadataProfileId,
                       ""AudiobookRootFolderPath"" = @AudiobookRootFolderPath,
                       ""AudiobookBooksToMonitor"" = @AudiobookBooksToMonitor,
                       ""AudiobookBooksToSearch"" = @AudiobookBooksToSearch,
                       ""AudiobookTags"" = @AudiobookTags,
                       ""EbookMonitored"" = @EbookMonitored,
                       ""EbookMonitorNewItems"" = @EbookMonitorNewItems,
                       ""EbookMonitorExistingMode"" = @EbookMonitorExistingMode,
                       ""EbookQualityProfileId"" = @EbookQualityProfileId,
                       ""EbookMetadataProfileId"" = @EbookMetadataProfileId,
                       ""EbookRootFolderPath"" = @EbookRootFolderPath,
                       ""EbookBooksToMonitor"" = @EbookBooksToMonitor,
                       ""EbookBooksToSearch"" = @EbookBooksToSearch,
                       ""EbookTags"" = @EbookTags,
                       ""Tags"" = @Tags,
                       ""SearchForMissingBooks"" = @SearchForMissingBooks,
                       ""LastSelectedMediaType"" = @LastSelectedMediaType,
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
                item.AudiobookBooksToMonitor,
                item.AudiobookBooksToSearch,
                item.AudiobookTags,
                item.EbookMonitored,
                item.EbookMonitorNewItems,
                item.EbookMonitorExistingMode,
                item.EbookQualityProfileId,
                item.EbookMetadataProfileId,
                item.EbookRootFolderPath,
                item.EbookBooksToMonitor,
                item.EbookBooksToSearch,
                item.EbookTags,
                item.Tags,
                item.SearchForMissingBooks,
                item.LastSelectedMediaType,
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
            Delete(x => x.UpdatedAt < cutoff &&
                       (x.OverallStatus == PendingImportStatus.Succeeded ||
                        x.OverallStatus == PendingImportStatus.Failed));
        }
    }
}
