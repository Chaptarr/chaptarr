using System.Diagnostics;
using System.Collections.Generic;
using System.Data;
using NzbDrone.Core.Notifications;
using System.Linq;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(128)]
    public class retain_book_level_extra_files : NzbDroneMigrationBase
    {
        private sealed class ConversionIdentity
        {
            public int Id { get; set; }
            public string DownloadId { get; set; }
            public int DownloadClientId { get; set; }
            public string RequestJson { get; set; }
        }

        private void IndexConversionOwnership(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType)
        {
            var timer = Stopwatch.StartNew();
            var afterId = 0;
            var count = 0;
            while (true)
            {
                var batch = connection.Query<ConversionIdentity>(@"SELECT ""Id"", ""DownloadId"", ""DownloadClientId"", ""RequestJson"" FROM ""ConversionJobs""
                    WHERE ""Id"" > @afterId ORDER BY ""Id"" LIMIT 150", new { afterId }, transaction).ToList();
                if (batch.Count == 0) break;
                var keys = batch.Select(job => Download.DownloadIdentity.Key(job.DownloadId)).Distinct().ToArray();
                var predicate = databaseType == DatabaseType.PostgreSQL ? "= ANY(@keys)" : "IN @keys";
                var clients = connection.Query<ConversionIdentity>($@"SELECT ""DownloadId"", MIN(""DownloadClientId"") AS ""DownloadClientId""
                    FROM ""DownloadHistory"" WHERE ""DownloadId"" {predicate}
                    GROUP BY ""DownloadId"" HAVING COUNT(DISTINCT ""DownloadClientId"") = 1", new { keys }, transaction)
                    .ToDictionary(row => row.DownloadId, row => row.DownloadClientId);
                var parameters = new DynamicParameters();
                var keyCases = new List<string>();
                var clientCases = new List<string>();
                var pendingCases = new List<string>();
                var attentionCases = new List<string>();
                var ids = new List<string>();
                for (var i = 0; i < batch.Count; i++)
                {
                    var key = Download.DownloadIdentity.Key(batch[i].DownloadId);
                    var state = new MediaFiles.ConversionJob { RequestJson = batch[i].RequestJson };
                    state.PrepareForPersistence(null);
                    parameters.Add($"pending{i}", state.CleanupPending);
                    parameters.Add($"attention{i}", state.CleanupAttention);
                    pendingCases.Add($"WHEN @id{i} THEN @pending{i}");
                    attentionCases.Add($"WHEN @id{i} THEN @attention{i}");
                    parameters.Add($"id{i}", batch[i].Id);
                    parameters.Add($"key{i}", key);
                    parameters.Add($"client{i}", batch[i].DownloadClientId > 0 ? batch[i].DownloadClientId : clients.GetValueOrDefault(key ?? string.Empty));
                    ids.Add($"@id{i}"); keyCases.Add($"WHEN @id{i} THEN @key{i}"); clientCases.Add($"WHEN @id{i} THEN @client{i}");
                }
                connection.Execute($"UPDATE \"ConversionJobs\" SET \"DownloadIdKey\" = CASE \"Id\" {string.Join(" ", keyCases)} END, \"DownloadClientId\" = CASE \"Id\" {string.Join(" ", clientCases)} END, \"CleanupPending\" = CASE \"Id\" {string.Join(" ", pendingCases)} END, \"CleanupAttention\" = CASE \"Id\" {string.Join(" ", attentionCases)} END WHERE \"Id\" IN ({string.Join(",", ids)})",
                    parameters, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                afterId = batch[^1].Id; count += batch.Count;
            }
            connection.Execute(@"DROP INDEX IF EXISTS ""IX_ConversionJobs_DownloadId_Upper"";
                CREATE INDEX IF NOT EXISTS ""IX_ConversionJobs_DownloadIdKey_Client"" ON ""ConversionJobs"" (""DownloadIdKey"", ""DownloadClientId"");
                CREATE INDEX IF NOT EXISTS ""IX_ConversionJobs_CleanupAttention"" ON ""ConversionJobs"" (""CleanupAttention"", ""Id"");
                CREATE INDEX IF NOT EXISTS ""IX_ConversionJobs_CleanupPending"" ON ""ConversionJobs"" (""CleanupPending"", ""Status"", ""UpdatedAt"", ""Id"");
                CREATE INDEX IF NOT EXISTS ""IX_BookFiles_ConversionWorkspace"" ON ""BookFiles"" (""Id"")
                    WHERE LOWER(""Path"") LIKE '%chaptarr-conversions%' OR LOWER(""ReplicaPaths"") LIKE '%chaptarr-conversions%';
                CREATE INDEX IF NOT EXISTS ""IX_BookFiles_Replicas_Present"" ON ""BookFiles"" (""Id"")
                    WHERE ""ReplicaPaths"" IS NOT NULL AND ""ReplicaPaths"" <> '' AND ""ReplicaPaths"" <> '[]';",
                transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
            if (databaseType == DatabaseType.PostgreSQL)
                connection.Execute(@"CREATE INDEX IF NOT EXISTS ""IX_BookFiles_Path_C"" ON ""BookFiles"" (""Path"" COLLATE ""C"");", transaction: transaction,
                    commandTimeout: MigrationController.CommandTimeoutSeconds);
            _logger.Info("Normalized {0} conversion identities and built conversion/ownership indexes in {1} ms", count, timer.ElapsedMilliseconds);
        }

        protected override void MainDbUpgrade()
        {
            Execute.Sql("DROP TABLE IF EXISTS book_trigrams; DROP TABLE IF EXISTS author_trigrams;");

            if (!Schema.Table("PendingNotificationItems").Exists())
            {
                Create.TableForModel("PendingNotificationItems")
                    .WithColumn("NotificationId").AsInt32().NotNullable()
                    .WithColumn("Path").AsString(int.MaxValue).NotNullable()
                    .WithColumn("PathKey").AsString(64).NotNullable()
                    .WithColumn("IsFolder").AsBoolean().NotNullable();
            }
            else
            {
                Execute.WithConnection((connection, transaction) =>
                {
                    var timer = Stopwatch.StartNew();
                    var afterId = 0;
                    var count = 0;
                    while (true)
                    {
                        var batch = connection.Query<NotificationKey>(@"SELECT ""Id"", ""Path"" FROM ""PendingNotificationItems""
                            WHERE ""Id"" > @afterId ORDER BY ""Id"" LIMIT 450", new { afterId }, transaction,
                            commandTimeout: MigrationController.CommandTimeoutSeconds).ToList();
                        if (batch.Count == 0) break;
                        var parameters = new DynamicParameters();
                        var cases = new List<string>();
                        var ids = new List<string>();
                        for (var i = 0; i < batch.Count; i++)
                        {
                            parameters.Add($"id{i}", batch[i].Id);
                            parameters.Add($"key{i}", PendingNotificationItem.GetPathKey(batch[i].Path));
                            cases.Add($"WHEN @id{i} THEN @key{i}");
                            ids.Add($"@id{i}");
                        }
                        connection.Execute($"UPDATE \"PendingNotificationItems\" SET \"PathKey\" = CASE \"Id\" {string.Join(" ", cases)} END WHERE \"Id\" IN ({string.Join(",", ids)})",
                            parameters, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                        afterId = batch[^1].Id;
                        count += batch.Count;
                    }
                    _logger.Info("Hashed {0} pending notification keys in {1} ms", count, timer.ElapsedMilliseconds);
                });
            }

            if (!Schema.Table("PendingNotificationItems").Index("IX_PendingNotificationItems_Identity").Exists())
            {
                Create.Index("IX_PendingNotificationItems_Identity").OnTable("PendingNotificationItems")
                    .OnColumn("NotificationId").Ascending().OnColumn("PathKey").Ascending()
                    .OnColumn("IsFolder").Ascending().WithOptions().Unique();
            }

            Execute.WithConnection((connection, transaction) =>
            {
                var timer = Stopwatch.StartNew();
                var removed = connection.Execute(@"DELETE FROM ""ImportListExclusions"" WHERE ""Id"" IN (
                    SELECT ""Id"" FROM (
                        SELECT ""Id"", ""MediaType"",
                            MAX(CASE WHEN ""MediaType"" IS NULL THEN 1 ELSE 0 END)
                                OVER (PARTITION BY LOWER(TRIM(""ForeignId""))) AS has_both,
                            ROW_NUMBER() OVER (PARTITION BY LOWER(TRIM(""ForeignId"")), COALESCE(""MediaType"", -1) ORDER BY ""Id"") AS duplicate_number
                        FROM ""ImportListExclusions""
                    ) duplicates WHERE duplicate_number > 1 OR (has_both = 1 AND ""MediaType"" IS NOT NULL))",
                    transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                connection.Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ImportListExclusions_ForeignId_MediaType""
                    ON ""ImportListExclusions"" (LOWER(TRIM(""ForeignId"")), COALESCE(""MediaType"", -1))",
                    transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                _logger.Info("Removed {0} duplicate or redundant import-list exclusions and indexed remaining identities in {1} ms", removed, timer.ElapsedMilliseconds);
            });

            Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Authors_PendingSetup""
                ON ""Authors"" (""Id"") WHERE ""AddOptions"" IS NOT NULL");

            if (!Schema.Table("ConversionJobs").Column("DownloadIdKey").Exists())
                Alter.Table("ConversionJobs").AddColumn("DownloadIdKey").AsString(int.MaxValue).Nullable();
            if (!Schema.Table("ConversionJobs").Column("DownloadClientId").Exists())
                Alter.Table("ConversionJobs").AddColumn("DownloadClientId").AsInt32().NotNullable().WithDefaultValue(0);
            if (!Schema.Table("ConversionJobs").Column("CleanupPending").Exists())
                Alter.Table("ConversionJobs").AddColumn("CleanupPending").AsBoolean().NotNullable().WithDefaultValue(false);
            if (!Schema.Table("ConversionJobs").Column("CleanupAttention").Exists())
                Alter.Table("ConversionJobs").AddColumn("CleanupAttention").AsBoolean().NotNullable().WithDefaultValue(false);
            IfPostgres().Execute.WithConnection((connection, transaction) => IndexConversionOwnership(connection, transaction, DatabaseType.PostgreSQL));
            IfDatabase("SQLite").Execute.WithConnection((connection, transaction) => IndexConversionOwnership(connection, transaction, DatabaseType.SQLite));

            IfPostgres().Execute.WithConnection((connection, transaction) =>
                RemoveLegacyInitialMonitor(connection, transaction, DatabaseType.PostgreSQL));
            IfDatabase("SQLite").Execute.WithConnection((connection, transaction) =>
                RemoveLegacyInitialMonitor(connection, transaction, DatabaseType.SQLite));

            Execute.WithConnection((connection, transaction) => ClearLegacyHeaderSeriesIds(connection, transaction));

            Update.Table("QualityDefinitions").Set(new { Title = "Unknown" }).Where(new { Quality = 0, Title = "Unknown Text" });

            IfPostgres().Execute.Sql(@"ALTER TABLE ""ExtraFiles"" ALTER COLUMN ""BookFileId"" DROP NOT NULL;");
            IfDatabase("SQLite").Execute.WithConnection((connection, transaction) =>
            {
                var timer = Stopwatch.StartNew();
                var timeout = MigrationController.CommandTimeoutSeconds;
                var objects = connection.Query<string>(@"SELECT sql FROM sqlite_master
                    WHERE tbl_name = 'ExtraFiles' AND type IN ('index', 'trigger') AND sql IS NOT NULL",
                    transaction: transaction, commandTimeout: timeout).ToList();
                var sequence = connection.ExecuteScalar<long>("SELECT COALESCE(MAX(seq), 0) FROM sqlite_sequence WHERE name = 'ExtraFiles'",
                    transaction: transaction, commandTimeout: timeout);
                // SQLite cannot relax a column constraint in place. Copy every stored
                // value, preserving IDs, indexes and the identity high-water mark.
                connection.Execute(@"CREATE TABLE ""ExtraFiles_retained"" (
                    ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ""AuthorId"" INTEGER NOT NULL, ""BookId"" INTEGER NOT NULL, ""BookFileId"" INTEGER,
                    ""RelativePath"" TEXT NOT NULL, ""Extension"" TEXT NOT NULL,
                    ""Added"" DATETIME NOT NULL, ""LastUpdated"" DATETIME NOT NULL);
                    INSERT INTO ""ExtraFiles_retained"" (""Id"", ""AuthorId"", ""BookId"", ""BookFileId"", ""RelativePath"", ""Extension"", ""Added"", ""LastUpdated"")
                    SELECT ""Id"", ""AuthorId"", ""BookId"", ""BookFileId"", ""RelativePath"", ""Extension"", ""Added"", ""LastUpdated"" FROM ""ExtraFiles"";
                    DROP TABLE ""ExtraFiles"";
                    ALTER TABLE ""ExtraFiles_retained"" RENAME TO ""ExtraFiles"";
                    UPDATE sqlite_sequence SET seq = MAX(seq, @sequence) WHERE name = 'ExtraFiles';
                    INSERT INTO sqlite_sequence (name, seq) SELECT 'ExtraFiles', @sequence
                    WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'ExtraFiles');",
                    new { sequence }, transaction, commandTimeout: timeout);
                foreach (var sql in objects) connection.Execute(sql, transaction: transaction, commandTimeout: timeout);
                var count = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM \"ExtraFiles\"", transaction: transaction, commandTimeout: timeout);
                _logger.Info("Retained {0} extra-file records and {1} indexes/triggers while allowing book-level ownership in {2} ms",
                    count, objects.Count, timer.ElapsedMilliseconds);
            });
        }
        internal int ClearLegacyHeaderSeriesIds(IDbConnection connection, IDbTransaction transaction)
        {
            var timer = Stopwatch.StartNew();
            // This column previously mixed server-internal and local series IDs.
            // Fresh metadata supplies the Goodreads header identity after upgrade.
            var count = connection.Execute("UPDATE \"Books\" SET \"SeriesId\" = NULL WHERE \"SeriesId\" IS NOT NULL",
                transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
            _logger.Info("Cleared {0} legacy book header series identifiers in {1} ms", count, timer.ElapsedMilliseconds);
            return count;
        }

        internal int RemoveLegacyInitialMonitor(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType)
        {
            var timer = Stopwatch.StartNew();
            // Older specific-book adds persisted a default generic policy that would
            // widen monitoring on the first scan. Preserve explicit per-format policies.
            var sql = databaseType switch
            {
                DatabaseType.PostgreSQL => @"UPDATE ""Authors""
                    SET ""AddOptions"" = (""AddOptions""::jsonb - 'monitor')::text
                    WHERE ""AddOptions"" IS NOT NULL
                        AND jsonb_typeof(""AddOptions""::jsonb) = 'object'
                        AND ""AddOptions""::jsonb ? 'monitor'
                        AND ""AddOptions""::jsonb ->> 'audiobookMonitor' IS NULL
                        AND ""AddOptions""::jsonb ->> 'ebookMonitor' IS NULL",
                DatabaseType.SQLite => @"UPDATE ""Authors""
                    SET ""AddOptions"" = json_remove(""AddOptions"", '$.monitor')
                    WHERE ""AddOptions"" IS NOT NULL
                        AND json_type(""AddOptions"") = 'object'
                        AND json_type(""AddOptions"", '$.monitor') IS NOT NULL
                        AND json_extract(""AddOptions"", '$.audiobookMonitor') IS NULL
                        AND json_extract(""AddOptions"", '$.ebookMonitor') IS NULL",
                _ => throw new System.ArgumentOutOfRangeException(nameof(databaseType), databaseType, null)
            };
            var count = connection.Execute(sql, transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
            _logger.Info("Removed legacy initial monitoring fallback from {0} authors in {1} ms", count, timer.ElapsedMilliseconds);
            return count;
        }

        private sealed class NotificationKey
        {
            public int Id { get; set; }
            public string Path { get; set; }
        }

    }
}
