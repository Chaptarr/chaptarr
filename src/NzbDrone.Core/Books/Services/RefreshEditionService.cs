using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Books
{
    public interface IRefreshEditionService
    {
        bool RefreshEditionInfo(List<Edition> add, List<Edition> update, List<Tuple<Edition, Edition>> merge, List<Edition> delete, List<Edition> upToDate, List<Edition> remoteEditions, bool forceUpdateFileTags);
    }

    public class RefreshEditionService : IRefreshEditionService
    {
        private readonly IEditionService _editionService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly INarratorLinkService _narratorLinkService;
        private readonly IMainDatabase _mainDatabase;
        private readonly Logger _logger;

        public RefreshEditionService(IEditionService editionService,
            IMetadataTagService metadataTagService,
            INarratorLinkService narratorLinkService,
            IMainDatabase mainDatabase,
            Logger logger)
        {
            _editionService = editionService;
            _metadataTagService = metadataTagService;
            _narratorLinkService = narratorLinkService;
            _mainDatabase = mainDatabase;
            _logger = logger;
        }

        public bool RefreshEditionInfo(List<Edition> add, List<Edition> update, List<Tuple<Edition, Edition>> merge, List<Edition> delete, List<Edition> upToDate, List<Edition> remoteEditions, bool forceUpdateFileTags)
        {
            var updateList = new List<Edition>();

	            // Merge duplicate local edition rows first so downstream operations (tag sync, deletes) operate on a converged DB.
	            // This is a repair path for legacy/buggy DBs where ForeignEditionId isn't unique within a book.
	            if (merge != null && merge.Count > 0)
	            {
	                try
	                {
	                    ApplyEditionMerges(merge);
	                }
	                catch (Exception ex)
	                {
	                    // Do not abort refresh; worst case is duplicates persist until the next refresh.
	                    _logger.Error(ex, "Failed to merge duplicate editions during refresh");
	                }
	            }

	            if (remoteEditions != null && remoteEditions.Count > 1)
	            {
	                var duplicates = remoteEditions
	                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.ForeignEditionId))
	                    .GroupBy(e => e.ForeignEditionId, StringComparer.OrdinalIgnoreCase)
	                    .Where(g => g.Count() > 1)
	                    .Select(g => new { ForeignEditionId = g.Key, Count = g.Count() })
	                    .ToList();

	                if (duplicates.Count > 0)
	                {
	                    var sample = string.Join(", ", duplicates.Take(5).Select(d => $"{d.ForeignEditionId}({d.Count})"));
	                    if (duplicates.Count > 5)
	                    {
	                        sample += ", ...";
	                    }

	                    _logger.Warn("Remote metadata returned {0} duplicate editions by ForeignEditionId. Using first match for updates. Duplicates: {1}",
	                        duplicates.Count, sample);
	                }
	            }

		            // for editions that need updating, just grab the remote edition and set db ids
		            foreach (var edition in update)
		            {
		                // ForeignEditionId should be unique, but tolerate duplicates to avoid aborting refreshes.
		                var remoteEdition = remoteEditions.FirstOrDefault(e => e.ForeignEditionId == edition.ForeignEditionId);
		                if (remoteEdition != null)
		                {
		                    var remoteForUpdate = RefreshEntityCopy.CloneEdition(remoteEdition);
		                    remoteForUpdate.UseDbFieldsFrom(edition);
		                    edition.UseMetadataFrom(remoteForUpdate);
		                }
		                else
		                {
		                    // Protected local editions can be preserved even when remote metadata no longer includes them.
		                    // Still persist local-only fields (Monitored/ManualAdd/etc) so user intent isn't lost.
		                    _logger.Warn("Remote edition not found for ForeignEditionId: {0}. Preserving local edition and updating local fields only.", edition.ForeignEditionId);
		                }

		                // make sure title is not null
		                edition.Title = edition.Title ?? "Unknown";
		                updateList.Add(edition);
	            }

            // Only delete editions that are explicitly marked for deletion, not merges
            // Merges should be handled by updating the target edition
            if (delete.Any())
            {
                _logger.Debug("Deleting {0} editions marked for deletion", delete.Count);
                _editionService.DeleteMany(delete);
            }
            
            _editionService.UpdateMany(updateList);

            // Persist narrator identity + edition/book links from refreshed edition metadata.
            // This runs after edition insert/update so Edition IDs are stable.
            try
            {
                var editionsWithNarrators = new List<Edition>();
                if (add?.Any() == true)
                {
                    editionsWithNarrators.AddRange(add);
                }

                if (updateList.Any())
                {
                    editionsWithNarrators.AddRange(updateList);
                }

                if (editionsWithNarrators.Any())
                {
                    _narratorLinkService?.UpsertEditionNarratorLinks(editionsWithNarrators);
                    _narratorLinkService?.RebuildBookNarratorLinks(editionsWithNarrators.Select(e => e.BookId));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to update narrator links during edition refresh");
            }

            var tagsToUpdate = updateList;
            if (forceUpdateFileTags)
            {
                _logger.Debug("Forcing tag update due to Author/Book/Edition updates");
                tagsToUpdate = updateList.Concat(upToDate).ToList();
            }

            _metadataTagService.SyncTags(tagsToUpdate);

            return add.Any() || delete.Any() || updateList.Any() || merge.Any();
        }

	        private void ApplyEditionMerges(List<Tuple<Edition, Edition>> merge)
	        {
	            if (merge == null || merge.Count == 0)
	            {
	                return;
	            }

	            // De-dupe and sanity-check merge pairs.
	            var pairs = merge
	                .Where(m => m?.Item1 != null && m.Item1.Id > 0 && m.Item2 != null && m.Item2.Id > 0 && m.Item1.Id != m.Item2.Id)
	                .Select(m => new { SourceId = m.Item1.Id, TargetId = m.Item2.Id })
	                .Distinct()
	                .ToList();

	            if (pairs.Count == 0)
	            {
	                return;
	            }

	            using (var conn = _mainDatabase.OpenConnection())
	            using (var tran = conn.BeginTransaction(IsolationLevel.ReadCommitted))
	            {
	                foreach (var pair in pairs)
	                {
	                    MergeEdition(conn, tran, pair.SourceId, pair.TargetId);
	                }

	                tran.Commit();
	            }
	        }

	        private void MergeEdition(IDbConnection conn, IDbTransaction tran, int sourceEditionId, int targetEditionId)
	        {
	            if (sourceEditionId <= 0 || targetEditionId <= 0 || sourceEditionId == targetEditionId)
	            {
	                return;
	            }

	            // Re-parent file attachments.
	            conn.Execute(
	                "UPDATE \"BookFiles\" SET \"EditionId\" = @TargetEditionId WHERE \"EditionId\" = @SourceEditionId",
	                new { SourceEditionId = sourceEditionId, TargetEditionId = targetEditionId },
	                tran);

	            // Re-parent history rows (nullable EditionId).
	            conn.Execute(
	                "UPDATE \"History\" SET \"EditionId\" = @TargetEditionId WHERE \"EditionId\" = @SourceEditionId",
	                new { SourceEditionId = sourceEditionId, TargetEditionId = targetEditionId },
	                tran);

	            // Re-parent any legacy other-file rows that reference EditionId.
	            conn.Execute(
	                "UPDATE \"OtherFiles\" SET \"EditionId\" = @TargetEditionId WHERE \"EditionId\" = @SourceEditionId",
	                new { SourceEditionId = sourceEditionId, TargetEditionId = targetEditionId },
	                tran);

	            // Re-parent narrator normalized rows (used for matching).
	            conn.Execute(
	                "UPDATE \"narrator_normalized\" SET \"EditionId\" = @TargetEditionId WHERE \"EditionId\" = @SourceEditionId",
	                new { SourceEditionId = sourceEditionId, TargetEditionId = targetEditionId },
	                tran);

	            // Merge narrator links safely under the unique (EditionId, NarratorId) constraint.
	            conn.Execute(
	                "DELETE FROM \"EditionNarratorLink\" " +
	                "WHERE \"EditionId\" = @SourceEditionId " +
	                "  AND \"NarratorId\" IN (SELECT \"NarratorId\" FROM \"EditionNarratorLink\" WHERE \"EditionId\" = @TargetEditionId)",
	                new { SourceEditionId = sourceEditionId, TargetEditionId = targetEditionId },
	                tran);

	            conn.Execute(
	                "UPDATE \"EditionNarratorLink\" SET \"EditionId\" = @TargetEditionId WHERE \"EditionId\" = @SourceEditionId",
	                new { SourceEditionId = sourceEditionId, TargetEditionId = targetEditionId },
	                tran);

	            // Finally delete the duplicate edition row.
	            conn.Execute(
	                "DELETE FROM \"Editions\" WHERE \"Id\" = @SourceEditionId",
	                new { SourceEditionId = sourceEditionId },
	                tran);
	        }
    }
}
