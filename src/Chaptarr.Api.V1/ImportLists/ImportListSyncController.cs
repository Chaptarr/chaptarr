using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.ImportLists;

namespace Chaptarr.Api.V1.ImportLists
{
    [V1ApiController("importlist")]
    public class ImportListSyncController : Controller
    {
        private readonly ImportListSyncService _syncService;
        private readonly IImportListSyncHistoryService _historyService;

        public ImportListSyncController(ImportListSyncService syncService, IImportListSyncHistoryService historyService)
        {
            _syncService = syncService;
            _historyService = historyService;
        }

        [HttpPost("{id:int}/preview")]
        public ImportListSyncHistoryResource Preview(int id)
        {
            return ImportListSyncHistoryResource.FromModel(_syncService.PreviewImportListSync(id));
        }

        [HttpGet("{id:int}/history")]
        public List<ImportListSyncHistoryResource> GetHistory(int id)
        {
            return _historyService.GetForImportList(id)
                .Select(ImportListSyncHistoryResource.FromModel)
                .ToList();
        }
    }

    public class ImportListSyncHistoryResource
    {
        public int Id { get; set; }
        public int ImportListId { get; set; }
        public string ImportListName { get; set; }
        public global::System.DateTime StartedAt { get; set; }
        public global::System.DateTime? EndedAt { get; set; }
        public long DurationMilliseconds { get; set; }
        public string Result { get; set; }
        public string FailureStage { get; set; }
        public bool DryRun { get; set; }
        public int ProcessedCount { get; set; }
        public int NewCount { get; set; }
        public int ExistingCount { get; set; }
        public int ChangedCount { get; set; }
        public int ExcludedCount { get; set; }
        public int UnresolvedCount { get; set; }
        public int SearchEligibleCount { get; set; }
        public bool ProviderStateCommitted { get; set; }

        public static ImportListSyncHistoryResource FromModel(ImportListSyncHistory model)
        {
            if (model == null)
            {
                return null;
            }

            return new ImportListSyncHistoryResource
            {
                Id = model.Id,
                ImportListId = model.ImportListId,
                ImportListName = model.ImportListName,
                StartedAt = model.StartedAt,
                EndedAt = model.EndedAt,
                DurationMilliseconds = model.DurationMilliseconds,
                Result = model.Result,
                FailureStage = model.FailureStage,
                DryRun = model.DryRun,
                ProcessedCount = model.ProcessedCount,
                NewCount = model.NewCount,
                ExistingCount = model.ExistingCount,
                ChangedCount = model.ChangedCount,
                ExcludedCount = model.ExcludedCount,
                UnresolvedCount = model.UnresolvedCount,
                SearchEligibleCount = model.SearchEligibleCount,
                ProviderStateCommitted = model.ProviderStateCommitted
            };
        }
    }
}
