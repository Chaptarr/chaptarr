using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(104)]
    public class migrate_author_media_monitoring : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            MigrateAuthors();
            MigratePendingAuthorImports();
        }

        private void MigrateAuthors()
        {
            const string tableName = "Authors";
            if (!Schema.Table(tableName).Exists())
            {
                return;
            }

            AddColumnIfMissing(tableName, "AudiobookMonitored", ColumnType.Boolean);
            AddColumnIfMissing(tableName, "AudiobookMonitorNewItems", ColumnType.Integer);
            AddColumnIfMissing(tableName, "EbookMonitored", ColumnType.Boolean);
            AddColumnIfMissing(tableName, "EbookMonitorNewItems", ColumnType.Integer);

            var hasLegacyColumns = Schema.Table(tableName).Column("AudiobookMonitorExisting").Exists() ||
                                   Schema.Table(tableName).Column("AudiobookMonitorFuture").Exists() ||
                                   Schema.Table(tableName).Column("EbookMonitorExisting").Exists() ||
                                   Schema.Table(tableName).Column("EbookMonitorFuture").Exists();
            if (!hasLegacyColumns)
            {
                return;
            }

            Execute.Sql($@"
                UPDATE ""{tableName}""
                   SET ""Monitored"" = CASE
                           WHEN ""AudiobookMonitorExisting"" IN (1, 2) OR ""AudiobookMonitorFuture"" = TRUE
                             OR ""EbookMonitorExisting"" IN (1, 2) OR ""EbookMonitorFuture"" = TRUE THEN TRUE
                           WHEN ""AudiobookMonitorExisting"" IS NOT NULL OR ""AudiobookMonitorFuture"" IS NOT NULL
                             OR ""EbookMonitorExisting"" IS NOT NULL OR ""EbookMonitorFuture"" IS NOT NULL THEN FALSE
                           ELSE ""Monitored""
                       END,
                       ""AudiobookMonitored"" = CASE
                           WHEN ""AudiobookMonitorExisting"" IN (1, 2) OR ""AudiobookMonitorFuture"" = TRUE THEN TRUE
                           WHEN ""AudiobookMonitorExisting"" = 0 THEN FALSE
                           ELSE ""AudiobookMonitored""
                       END,
                       ""AudiobookMonitorNewItems"" = CASE
                           WHEN ""AudiobookMonitorExisting"" = 1 THEN 0
                           WHEN ""AudiobookMonitorExisting"" IN (0, 2) AND ""AudiobookMonitorFuture"" = TRUE THEN 2
                           WHEN ""AudiobookMonitorExisting"" IN (0, 2) THEN 1
                           WHEN ""AudiobookMonitorExisting"" IS NULL AND ""AudiobookMonitorFuture"" = TRUE THEN 2
                           ELSE ""AudiobookMonitorNewItems""
                       END,
                       ""EbookMonitored"" = CASE
                           WHEN ""EbookMonitorExisting"" IN (1, 2) OR ""EbookMonitorFuture"" = TRUE THEN TRUE
                           WHEN ""EbookMonitorExisting"" = 0 THEN FALSE
                           ELSE ""EbookMonitored""
                       END,
                       ""EbookMonitorNewItems"" = CASE
                           WHEN ""EbookMonitorExisting"" = 1 THEN 0
                           WHEN ""EbookMonitorExisting"" IN (0, 2) AND ""EbookMonitorFuture"" = TRUE THEN 2
                           WHEN ""EbookMonitorExisting"" IN (0, 2) THEN 1
                           WHEN ""EbookMonitorExisting"" IS NULL AND ""EbookMonitorFuture"" = TRUE THEN 2
                           ELSE ""EbookMonitorNewItems""
                       END
                 WHERE ""AudiobookMonitorExisting"" IS NOT NULL
                    OR ""AudiobookMonitorFuture"" IS NOT NULL
                    OR ""EbookMonitorExisting"" IS NOT NULL
                    OR ""EbookMonitorFuture"" IS NOT NULL;");

            DropColumnIfPresent(tableName, "AudiobookMonitorExisting");
            DropColumnIfPresent(tableName, "AudiobookMonitorFuture");
            DropColumnIfPresent(tableName, "EbookMonitorExisting");
            DropColumnIfPresent(tableName, "EbookMonitorFuture");
        }

        private void MigratePendingAuthorImports()
        {
            const string tableName = "PendingAuthorImport";
            if (!Schema.Table(tableName).Exists())
            {
                return;
            }

            AddColumnIfMissing(tableName, "AudiobookMonitored", ColumnType.Boolean);
            AddColumnIfMissing(tableName, "AudiobookMonitorNewItems", ColumnType.Integer);
            AddColumnIfMissing(tableName, "AudiobookMonitorExistingMode", ColumnType.Integer);
            AddColumnIfMissing(tableName, "EbookMonitored", ColumnType.Boolean);
            AddColumnIfMissing(tableName, "EbookMonitorNewItems", ColumnType.Integer);
            AddColumnIfMissing(tableName, "EbookMonitorExistingMode", ColumnType.Integer);

            var hasLegacyColumns = Schema.Table(tableName).Column("AudiobookMonitorExisting").Exists() ||
                                   Schema.Table(tableName).Column("AudiobookMonitorFuture").Exists() ||
                                   Schema.Table(tableName).Column("EbookMonitorExisting").Exists() ||
                                   Schema.Table(tableName).Column("EbookMonitorFuture").Exists();
            if (!hasLegacyColumns)
            {
                return;
            }

            // MonitorTypes: All=0, None=6, SpecificBook=7. NewItemMonitorTypes:
            // All=0, None=1, New=2. Exact provider-ID JSON is intentionally left
            // unchanged so legacy Selected requests retain their targets.
            Execute.Sql($@"
                UPDATE ""{tableName}""
                   SET ""AudiobookMonitored"" = CASE
                           WHEN ""AudiobookMonitorExisting"" IN (1, 2) OR ""AudiobookMonitorFuture"" = TRUE THEN TRUE
                           WHEN ""AudiobookMonitorExisting"" = 0 THEN FALSE
                           ELSE ""AudiobookMonitored""
                       END,
                       ""AudiobookMonitorNewItems"" = CASE
                           WHEN ""AudiobookMonitorExisting"" = 1 THEN 0
                           WHEN ""AudiobookMonitorExisting"" IN (0, 2) AND ""AudiobookMonitorFuture"" = TRUE THEN 2
                           WHEN ""AudiobookMonitorExisting"" IN (0, 2) THEN 1
                           WHEN ""AudiobookMonitorExisting"" IS NULL AND ""AudiobookMonitorFuture"" = TRUE THEN 2
                           ELSE ""AudiobookMonitorNewItems""
                       END,
                       ""AudiobookMonitorExistingMode"" = CASE
                           WHEN ""AudiobookMonitorExisting"" = 1 THEN 0
                           WHEN ""AudiobookMonitorExisting"" = 2 THEN 7
                           WHEN ""AudiobookMonitorExisting"" = 0 THEN 6
                           ELSE ""AudiobookMonitorExistingMode""
                       END,
                       ""EbookMonitored"" = CASE
                           WHEN ""EbookMonitorExisting"" IN (1, 2) OR ""EbookMonitorFuture"" = TRUE THEN TRUE
                           WHEN ""EbookMonitorExisting"" = 0 THEN FALSE
                           ELSE ""EbookMonitored""
                       END,
                       ""EbookMonitorNewItems"" = CASE
                           WHEN ""EbookMonitorExisting"" = 1 THEN 0
                           WHEN ""EbookMonitorExisting"" IN (0, 2) AND ""EbookMonitorFuture"" = TRUE THEN 2
                           WHEN ""EbookMonitorExisting"" IN (0, 2) THEN 1
                           WHEN ""EbookMonitorExisting"" IS NULL AND ""EbookMonitorFuture"" = TRUE THEN 2
                           ELSE ""EbookMonitorNewItems""
                       END,
                       ""EbookMonitorExistingMode"" = CASE
                           WHEN ""EbookMonitorExisting"" = 1 THEN 0
                           WHEN ""EbookMonitorExisting"" = 2 THEN 7
                           WHEN ""EbookMonitorExisting"" = 0 THEN 6
                           ELSE ""EbookMonitorExistingMode""
                       END
                 WHERE ""AudiobookMonitorExisting"" IS NOT NULL
                    OR ""AudiobookMonitorFuture"" IS NOT NULL
                    OR ""EbookMonitorExisting"" IS NOT NULL
                    OR ""EbookMonitorFuture"" IS NOT NULL;");

            DropColumnIfPresent(tableName, "AudiobookMonitorExisting");
            DropColumnIfPresent(tableName, "AudiobookMonitorFuture");
            DropColumnIfPresent(tableName, "EbookMonitorExisting");
            DropColumnIfPresent(tableName, "EbookMonitorFuture");
        }

        private void AddColumnIfMissing(string tableName, string columnName, ColumnType columnType)
        {
            if (Schema.Table(tableName).Column(columnName).Exists())
            {
                return;
            }

            var column = Alter.Table(tableName).AddColumn(columnName);
            if (columnType == ColumnType.Boolean)
            {
                column.AsBoolean().Nullable();
            }
            else
            {
                column.AsInt32().Nullable();
            }
        }

        private void DropColumnIfPresent(string tableName, string columnName)
        {
            if (Schema.Table(tableName).Column(columnName).Exists())
            {
                Delete.Column(columnName).FromTable(tableName);
            }
        }

        private enum ColumnType
        {
            Boolean,
            Integer
        }
    }

}
