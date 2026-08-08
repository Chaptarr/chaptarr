using System;
using System.Linq;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(57)]
    public class repair_legacy_work_keys_in_provider_id_fields : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Check table existence here where Schema context is available.
            var hasSyncQueue = Schema.Table("AuthorSyncQueue").Exists();
            var hasSyncMetadata = Schema.Table("AuthorSyncMetadata").Exists();
            var hasPendingImport = Schema.Table("PendingAuthorImport").Exists();

            if (!hasSyncQueue && !hasSyncMetadata && !hasPendingImport)
            {
                return;
            }

            Execute.WithConnection((connection, transaction) =>
            {
                var removedSyncQueue = hasSyncQueue
                    ? DeleteLegacyWorkKeys(connection, transaction, table: "AuthorSyncQueue", idColumn: "Id", keyColumn: "PrefixedAuthorId")
                    : 0;

                var removedSyncMetadata = hasSyncMetadata
                    ? DeleteLegacyWorkKeys(connection, transaction, table: "AuthorSyncMetadata", idColumn: "Id", keyColumn: "ExternalAuthorId")
                    : 0;

                var markedPendingFailed = hasPendingImport
                    ? MarkPendingImportsFailed(connection, transaction)
                    : 0;

                var total = removedSyncQueue + removedSyncMetadata + markedPendingFailed;
                if (total > 0)
                {
                    _logger.Warn("[MIGRATION-57] Repaired legacy V5 work-key provider IDs: deleted {0} sync queue item(s), deleted {1} sync metadata row(s), marked {2} pending import(s) as Failed",
                        removedSyncQueue, removedSyncMetadata, markedPendingFailed);
                }
            });
        }

        private static int DeleteLegacyWorkKeys(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, string table, string idColumn, string keyColumn)
        {
            var rows = connection.Query<(long Id, string Key)>(
                $@"SELECT ""{idColumn}"" AS Id, ""{keyColumn}"" AS Key
                   FROM ""{table}""
                   WHERE ""{keyColumn}"" IS NOT NULL;",
                transaction: transaction).ToList();

            var badIds = rows
                .Where(r => LooksLikeLegacyWorkKey(r.Key))
                .Select(r => r.Id)
                .Distinct()
                .ToList();

            if (badIds.Count == 0)
            {
                return 0;
            }

            var deleted = 0;
            foreach (var id in badIds)
            {
                deleted += connection.Execute(
                    $@"DELETE FROM ""{table}"" WHERE ""{idColumn}"" = @Id;",
                    new { Id = id },
                    transaction: transaction);
            }

            return deleted;
        }

        private static int MarkPendingImportsFailed(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
        {
            // PendingImportStatus enum: Pending = 1, Retrying = 3
            var rows = connection.Query<(long Id, string ProviderId)>(
                @"SELECT ""Id"" AS Id, ""ProviderId"" AS ProviderId
                  FROM ""PendingAuthorImport""
                  WHERE ""ProviderId"" IS NOT NULL
                    AND ""OverallStatus"" IN (1, 3);",
                transaction: transaction).ToList();

            var bad = rows.Where(r => LooksLikeLegacyWorkKey(r.ProviderId)).ToList();
            if (bad.Count == 0)
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            var updated = 0;
            foreach (var row in bad)
            {
                // PendingImportStatus.Failed = 6
                updated += connection.Execute(
                    @"UPDATE ""PendingAuthorImport""
                      SET ""OverallStatus"" = 6,
                          ""LastError"" = @LastError,
                          ""UpdatedAt"" = @UpdatedAt
                      WHERE ""Id"" = @Id;",
                    new
                    {
                        Id = row.Id,
                        LastError = $"Invalid provider ID '{row.ProviderId}' (looks like a legacy V5 work key).",
                        UpdatedAt = now
                    },
                    transaction: transaction);
            }

            return updated;
        }

        private static bool LooksLikeLegacyWorkKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            key = key.Trim();
            if (key.Length < 2)
            {
                return false;
            }

            if (key[0] != 'W' && key[0] != 'w')
            {
                return false;
            }

            for (var i = 1; i < key.Length; i++)
            {
                if (!char.IsDigit(key[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
