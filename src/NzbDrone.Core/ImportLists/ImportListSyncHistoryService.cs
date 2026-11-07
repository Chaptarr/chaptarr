using System;
using System.Collections.Generic;
using NLog;

namespace NzbDrone.Core.ImportLists
{
    public interface IImportListSyncHistoryService
    {
        ImportListSyncHistory Start(int importListId, string importListName, DateTime? startedAt = null, bool dryRun = false);
        ImportListSyncHistory Complete(ImportListSyncHistory history, ImportListSyncCounts counts, string result, string failureStage, bool providerStateCommitted);
        List<ImportListSyncHistory> GetForImportList(int importListId, int limit = 100);
    }

    public class ImportListSyncHistoryService : IImportListSyncHistoryService
    {
        private readonly IImportListSyncHistoryRepository _repository;
        private readonly Logger _logger;

        public ImportListSyncHistoryService(IImportListSyncHistoryRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public ImportListSyncHistory Start(int importListId, string importListName, DateTime? startedAt = null, bool dryRun = false)
        {
            return _repository.Insert(new ImportListSyncHistory
            {
                ImportListId = importListId,
                ImportListName = importListName ?? string.Empty,
                StartedAt = startedAt ?? DateTime.UtcNow,
                Result = "running",
                DryRun = dryRun
            });
        }

        public ImportListSyncHistory Complete(ImportListSyncHistory history, ImportListSyncCounts counts, string result, string failureStage, bool providerStateCommitted)
        {
            if (history == null)
            {
                return null;
            }

            var endedAt = DateTime.UtcNow;
            counts ??= new ImportListSyncCounts();

            history.EndedAt = endedAt;
            history.DurationMilliseconds = Math.Max(0, (long)(endedAt - history.StartedAt).TotalMilliseconds);
            history.Result = result;
            history.FailureStage = failureStage;
            history.ProcessedCount = counts.ProcessedCount;
            history.NewCount = counts.NewCount;
            history.ExistingCount = counts.ExistingCount;
            history.ChangedCount = counts.ChangedCount;
            history.ExcludedCount = counts.ExcludedCount;
            history.UnresolvedCount = counts.UnresolvedCount;
            history.SearchEligibleCount = counts.SearchEligibleCount;
            history.ProviderStateCommitted = providerStateCommitted;

            try
            {
                _repository.Update(history);
                _repository.TrimRetention(history.ImportListId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save import-list sync history for list {0}", history.ImportListId);
            }

            return history;
        }

        public List<ImportListSyncHistory> GetForImportList(int importListId, int limit = 100)
        {
            return _repository.GetForImportList(importListId, Math.Clamp(limit, 1, 100));
        }
    }
}
