using System.Data;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(75)]
    public class cleanup_duplicate_import_incomplete_history : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            var hasHistory = Schema.Table("History").Exists();
            var hasDownloadHistory = Schema.Table("DownloadHistory").Exists();

            if (!hasHistory && !hasDownloadHistory)
            {
                return;
            }

            Execute.WithConnection((connection, transaction) =>
            {
                var historyEventType = (int)EntityHistoryEventType.BookImportIncomplete;
                var downloadHistoryEventType = (int)DownloadHistoryEventType.DownloadImportIncomplete;

                var historyBefore = hasHistory
                    ? CountDuplicateHistoryRows(connection, transaction, historyEventType)
                    : 0;
                var downloadHistoryBefore = hasDownloadHistory
                    ? CountDuplicateDownloadHistoryRows(connection, transaction, downloadHistoryEventType)
                    : 0;

                if (historyBefore > 0)
                {
                    CleanupHistory(connection, transaction, historyEventType);
                }

                if (downloadHistoryBefore > 0)
                {
                    CleanupDownloadHistory(connection, transaction, downloadHistoryEventType);
                }

                var historyAfter = hasHistory
                    ? CountDuplicateHistoryRows(connection, transaction, historyEventType)
                    : 0;
                var downloadHistoryAfter = hasDownloadHistory
                    ? CountDuplicateDownloadHistoryRows(connection, transaction, downloadHistoryEventType)
                    : 0;

                _logger.Info("[MIGRATION-75] Import-incomplete duplicate cleanup: History before={0}, removed={1}, remaining={2}; DownloadHistory before={3}, removed={4}, remaining={5}",
                    historyBefore,
                    historyBefore - historyAfter,
                    historyAfter,
                    downloadHistoryBefore,
                    downloadHistoryBefore - downloadHistoryAfter,
                    downloadHistoryAfter);
            });
        }

        private static void CleanupHistory(IDbConnection connection, IDbTransaction transaction, int eventType)
        {
            // One-time backfill of import-incomplete dedupe: keep the newest exact stuck-state row.
            connection.Execute(@"
                WITH ranked AS (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (
                               PARTITION BY ""EventType"", ""DownloadId"", ""BookId"", ""EditionId"", ""SourceTitle"", ""Data""
                               ORDER BY ""Date"" DESC, ""Id"" DESC
                           ) AS rn
                    FROM ""History""
                    WHERE ""EventType"" = @EventType
                      AND ""DownloadId"" IS NOT NULL
                      AND ""SourceTitle"" IS NOT NULL
                      AND ""Data"" IS NOT NULL
                )
                DELETE FROM ""History""
                WHERE ""Id"" IN (SELECT ""Id"" FROM ranked WHERE rn > 1);",
                new { EventType = eventType },
                transaction: transaction);
        }

        private static void CleanupDownloadHistory(IDbConnection connection, IDbTransaction transaction, int eventType)
        {
            // One-time backfill of import-incomplete dedupe: keep the newest exact stuck-state row.
            connection.Execute(@"
                WITH ranked AS (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (
                               PARTITION BY ""EventType"", ""DownloadId"", ""BookId"", ""SourceTitle"", ""Data""
                               ORDER BY ""Date"" DESC, ""Id"" DESC
                           ) AS rn
                    FROM ""DownloadHistory""
                    WHERE ""EventType"" = @EventType
                      AND ""DownloadId"" IS NOT NULL
                      AND ""SourceTitle"" IS NOT NULL
                      AND ""Data"" IS NOT NULL
                )
                DELETE FROM ""DownloadHistory""
                WHERE ""Id"" IN (SELECT ""Id"" FROM ranked WHERE rn > 1);",
                new { EventType = eventType },
                transaction: transaction);
        }

        private static int CountDuplicateHistoryRows(IDbConnection connection, IDbTransaction transaction, int eventType)
        {
            return connection.ExecuteScalar<int>(@"
                WITH ranked AS (
                    SELECT ROW_NUMBER() OVER (
                               PARTITION BY ""EventType"", ""DownloadId"", ""BookId"", ""EditionId"", ""SourceTitle"", ""Data""
                               ORDER BY ""Date"" DESC, ""Id"" DESC
                           ) AS rn
                    FROM ""History""
                    WHERE ""EventType"" = @EventType
                      AND ""DownloadId"" IS NOT NULL
                      AND ""SourceTitle"" IS NOT NULL
                      AND ""Data"" IS NOT NULL
                )
                SELECT COUNT(*)
                FROM ranked
                WHERE rn > 1;",
                new { EventType = eventType },
                transaction: transaction);
        }

        private static int CountDuplicateDownloadHistoryRows(IDbConnection connection, IDbTransaction transaction, int eventType)
        {
            return connection.ExecuteScalar<int>(@"
                WITH ranked AS (
                    SELECT ROW_NUMBER() OVER (
                               PARTITION BY ""EventType"", ""DownloadId"", ""BookId"", ""SourceTitle"", ""Data""
                               ORDER BY ""Date"" DESC, ""Id"" DESC
                           ) AS rn
                    FROM ""DownloadHistory""
                    WHERE ""EventType"" = @EventType
                      AND ""DownloadId"" IS NOT NULL
                      AND ""SourceTitle"" IS NOT NULL
                      AND ""Data"" IS NOT NULL
                )
                SELECT COUNT(*)
                FROM ranked
                WHERE rn > 1;",
                new { EventType = eventType },
                transaction: transaction);
        }
    }
}
