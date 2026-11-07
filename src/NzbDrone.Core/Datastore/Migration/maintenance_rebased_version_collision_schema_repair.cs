using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using FluentMigrator;
using FluentMigrator.Builders.IfDatabase;
using Microsoft.Data.Sqlite;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Datastore.Migration.Framework;
using Npgsql;

namespace NzbDrone.Core.Datastore.Migration
{
    [Maintenance(MigrationStage.BeforeAll)]
    public class RebasedVersionCollisionSchemaRepair : FluentMigrator.Migration
    {
        private readonly Logger _logger;

        public RebasedVersionCollisionSchemaRepair()
        {
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public override void Up()
        {
            if (MigrationContext.Current?.MigrationType != MigrationType.Main)
            {
                return;
            }

            if (!VersionInfoTableExists())
            {
                // Fresh installs haven't created VersionInfo yet.
                return;
            }

            HashSet<long> applied;
            try
            {
                applied = ReadAppliedVersions();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[MIGRATION-REPAIR] Unable to read VersionInfo; skipping schema repair preflight");
                return;
            }

            var repairs = 0;

            // These versions were historically reused during a migrations rebase. When an older database already has
            // VersionInfo entries for these numbers, FluentMigrator skips the current migrations, which can leave the
            // schema missing required columns/tables. We only repair the schema when the corresponding version number
            // is already recorded as applied (meaning the normal migration will be skipped).

            if (applied.Contains(4))
            {
                repairs += EnsureBooksIsOmnibus();
            }

            if (applied.Contains(5))
            {
                repairs += EnsureMetadataProfilesSkipOmnibus();
            }

            if (applied.Contains(6))
            {
                repairs += EnsureMetadataProfilesSkipMissingAsin();
            }

            if (applied.Contains(9))
            {
                repairs += EnsureEditionsMatchingTitle();
            }

            if (applied.Contains(10))
            {
                repairs += EnsureEditionsSubtitle();
            }

            if (applied.Contains(15))
            {
                repairs += EnsureImportListsSchema();
            }

            if (applied.Contains(22))
            {
                repairs += EnsurePendingReleasesAuthorId();
            }

            if (applied.Contains(23))
            {
                repairs += EnsureEbookNamingConfigColumns();
            }

            if (applied.Contains(24))
            {
                repairs += EnsureEditionsAsins();
            }

            if (applied.Contains(25))
            {
                repairs += EnsureDownloadClientMediaTags();
            }

            if (applied.Contains(114))
            {
                // The active backlog branch used 114 for RanobeDB identities;
                // the consolidation branch used it for root-folder Calibre gates.
                repairs += EnsureRanobeDbProviderIds();
                repairs += EnsureRootFolderCalibreGates();
            }

            if (applied.Contains(115))
            {
                // The active backlog branch used 115 for import-list history;
                // the consolidation branch used it for metadata preferences.
                repairs += EnsureImportListSyncHistory();
                repairs += EnsureMetadataProfilePreferences();
            }

            if (repairs > 0)
            {
                _logger.Info("[MIGRATION-REPAIR] Applied {0} schema repair(s) for rebased migration version collisions", repairs);
            }
        }

        public override void Down()
        {
        }

        private HashSet<long> ReadAppliedVersions()
        {
            var applied = new HashSet<long>();

            Execute.WithConnection((connection, transaction) =>
            {
                foreach (var version in QueryVersions(connection, transaction))
                {
                    applied.Add(version);
                }
            });

            return applied;
        }

        private bool VersionInfoTableExists()
        {
            var exists = false;

            Execute.WithConnection((connection, transaction) =>
            {
                if (connection is NpgsqlConnection)
                {
                    // FluentMigrator's schema probe can resolve a table outside the
                    // selected search_path schema. A staged Bookshelf conversion
                    // must only inspect the VersionInfo table in its target schema.
                    exists = connection.QuerySingle<bool>(@"
                        SELECT to_regclass(format('%I.%I', current_schema(), @quotedName)) IS NOT NULL
                            OR to_regclass(format('%I.%I', current_schema(), @unquotedName)) IS NOT NULL;",
                        new { quotedName = "VersionInfo", unquotedName = "versioninfo" },
                        transaction);
                    return;
                }

                if (connection is SqliteConnection)
                {
                    exists = connection.QuerySingle<bool>(@"
                        SELECT EXISTS (
                            SELECT 1
                              FROM sqlite_master
                             WHERE type = 'table'
                               AND name = @tableName COLLATE NOCASE);",
                        new { tableName = "VersionInfo" },
                        transaction);
                    return;
                }

                throw new InvalidOperationException($"Unsupported migration database provider '{connection.GetType().Name}'.");
            });

            return exists;
        }

        private static IEnumerable<long> QueryVersions(IDbConnection connection, IDbTransaction transaction)
        {
            Exception last = null;

            // Try both quoted and unquoted forms; older runners/processors may differ in how they created identifiers.
            var candidates = new[]
            {
                "SELECT \"Version\" FROM \"VersionInfo\";",
                "SELECT Version FROM VersionInfo;",
                "SELECT version FROM versioninfo;"
            };

            foreach (var sql in candidates)
            {
                try
                {
                    return connection.Query<long>(sql, transaction: transaction);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException("Unable to query VersionInfo.Version", last);
        }

        private IIfDatabaseExpressionRoot IfPostgres()
        {
            return IfDatabase(dbType =>
                !string.IsNullOrWhiteSpace(dbType) &&
                dbType.IndexOf("postgres", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private int EnsureRanobeDbProviderIds()
        {
            var repairs = 0;

            if (Schema.Table("Books").Exists() && !Schema.Table("Books").Column("RanobeDbBookId").Exists())
            {
                Alter.Table("Books").AddColumn("RanobeDbBookId").AsString(128).Nullable();
                repairs++;
            }

            if (Schema.Table("Authors").Exists() && !Schema.Table("Authors").Column("RanobeDbAuthorId").Exists())
            {
                Alter.Table("Authors").AddColumn("RanobeDbAuthorId").AsString(128).Nullable();
                repairs++;
            }

            if (Schema.Table("Books").Exists() &&
                Schema.Table("Books").Column("RanobeDbBookId").Exists() &&
                Schema.Table("Books").Column("MediaType").Exists() &&
                !Schema.Table("Books").Index("UX_Books_RanobeDbBookId_MediaType").Exists())
            {
                Create.Index("UX_Books_RanobeDbBookId_MediaType")
                    .OnTable("Books")
                    .OnColumn("RanobeDbBookId").Ascending()
                    .OnColumn("MediaType").Ascending()
                    .WithOptions().Unique();
                repairs++;
            }

            if (Schema.Table("Authors").Exists() &&
                Schema.Table("Authors").Column("RanobeDbAuthorId").Exists() &&
                !Schema.Table("Authors").Index("UX_Authors_RanobeDbAuthorId").Exists())
            {
                Create.Index("UX_Authors_RanobeDbAuthorId")
                    .OnTable("Authors")
                    .OnColumn("RanobeDbAuthorId").Ascending()
                    .WithOptions().Unique();
                repairs++;
            }

            return repairs;
        }

        private int EnsureRootFolderCalibreGates()
        {
            if (!Schema.Table("RootFolders").Exists())
            {
                return 0;
            }

            var repairs = 0;
            if (!Schema.Table("RootFolders").Column("ReapCalibreDuplicates").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("ReapCalibreDuplicates").AsBoolean().NotNullable().WithDefaultValue(false);
                repairs++;
            }

            if (!Schema.Table("RootFolders").Column("AutoPushCalibreMetadata").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("AutoPushCalibreMetadata").AsBoolean().NotNullable().WithDefaultValue(false);
                repairs++;
            }

            return repairs;
        }

        private int EnsureMetadataProfilePreferences()
        {
            const string tableName = "MetadataProfiles";
            if (!Schema.Table(tableName).Exists())
            {
                return 0;
            }

            var repairs = 0;
            if (!Schema.Table(tableName).Column("PreferredEditionTerms").Exists())
            {
                Alter.Table(tableName).AddColumn("PreferredEditionTerms").AsString().Nullable();
                repairs++;
            }

            if (!Schema.Table(tableName).Column("PreferredSeries").Exists())
            {
                Alter.Table(tableName).AddColumn("PreferredSeries").AsString().Nullable();
                repairs++;
            }

            return repairs;
        }

        private int EnsureImportListSyncHistory()
        {
            const string tableName = "ImportListSyncHistory";
            var repairs = 0;

            if (!Schema.Table(tableName).Exists())
            {
                Create.Table(tableName)
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("ImportListId").AsInt32().NotNullable()
                    .WithColumn("ImportListName").AsString(512).NotNullable()
                    .WithColumn("StartedAt").AsDateTime().NotNullable()
                    .WithColumn("EndedAt").AsDateTime().Nullable()
                    .WithColumn("DurationMilliseconds").AsInt64().NotNullable().WithDefaultValue(0)
                    .WithColumn("Result").AsString(32).NotNullable()
                    .WithColumn("FailureStage").AsString(32).Nullable()
                    .WithColumn("DryRun").AsBoolean().NotNullable().WithDefaultValue(false)
                    .WithColumn("ProcessedCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("NewCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("ExistingCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("ChangedCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("ExcludedCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("UnresolvedCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("SearchEligibleCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("ProviderStateCommitted").AsBoolean().NotNullable().WithDefaultValue(false);
                repairs++;
            }

            if (Schema.Table(tableName).Exists() &&
                Schema.Table(tableName).Column("ImportListId").Exists() &&
                Schema.Table(tableName).Column("StartedAt").Exists() &&
                !Schema.Table(tableName).Index("IX_ImportListSyncHistory_ImportListId_StartedAt").Exists())
            {
                Create.Index("IX_ImportListSyncHistory_ImportListId_StartedAt")
                    .OnTable(tableName)
                    .OnColumn("ImportListId").Ascending()
                    .OnColumn("StartedAt").Descending();
                repairs++;
            }

            if (Schema.Table(tableName).Exists() &&
                Schema.Table(tableName).Column("StartedAt").Exists() &&
                !Schema.Table(tableName).Index("IX_ImportListSyncHistory_StartedAt").Exists())
            {
                Create.Index("IX_ImportListSyncHistory_StartedAt")
                    .OnTable(tableName)
                    .OnColumn("StartedAt").Descending();
                repairs++;
            }

            return repairs;
        }

        private int EnsureBooksIsOmnibus()
        {
            if (!Schema.Table("Books").Exists())
            {
                return 0;
            }

            if (Schema.Table("Books").Column("IsOmnibus").Exists())
            {
                return 0;
            }

            Alter.Table("Books")
                .AddColumn("IsOmnibus").AsBoolean().NotNullable().WithDefaultValue(false);

            return 1;
        }

        private int EnsureMetadataProfilesSkipOmnibus()
        {
            if (!Schema.Table("MetadataProfiles").Exists())
            {
                return 0;
            }

            if (Schema.Table("MetadataProfiles").Column("SkipOmnibus").Exists())
            {
                return 0;
            }

            Alter.Table("MetadataProfiles")
                .AddColumn("SkipOmnibus").AsBoolean().NotNullable().WithDefaultValue(false);

            return 1;
        }

        private int EnsureMetadataProfilesSkipMissingAsin()
        {
            if (!Schema.Table("MetadataProfiles").Exists())
            {
                return 0;
            }

            if (Schema.Table("MetadataProfiles").Column("SkipMissingAsin").Exists())
            {
                return 0;
            }

            Alter.Table("MetadataProfiles")
                .AddColumn("SkipMissingAsin").AsBoolean().NotNullable().WithDefaultValue(false);

            return 1;
        }

        private int EnsureEditionsMatchingTitle()
        {
            if (!Schema.Table("Editions").Exists())
            {
                return 0;
            }

            if (Schema.Table("Editions").Column("MatchingTitle").Exists())
            {
                return 0;
            }

            Alter.Table("Editions")
                .AddColumn("MatchingTitle").AsString().Nullable();

            return 1;
        }

        private int EnsureEditionsSubtitle()
        {
            if (!Schema.Table("Editions").Exists())
            {
                return 0;
            }

            if (Schema.Table("Editions").Column("Subtitle").Exists())
            {
                return 0;
            }

            Alter.Table("Editions")
                .AddColumn("Subtitle").AsString().Nullable();

            return 1;
        }

        private int EnsurePendingReleasesAuthorId()
        {
            if (!Schema.Table("PendingReleases").Exists())
            {
                return 0;
            }

            if (Schema.Table("PendingReleases").Column("AuthorId").Exists())
            {
                return 0;
            }

            Alter.Table("PendingReleases")
                .AddColumn("AuthorId").AsInt32().NotNullable().WithDefaultValue(0);

            return 1;
        }

        private int EnsureImportListsSchema()
        {
            if (!Schema.Table("ImportLists").Exists())
            {
                return 0;
            }

            var repairs = 0;

            // Be non-destructive: only add the missing column(s) needed by current code.
            // Do not rebuild tables in SQLite, since that can drop future columns/migrations.
            if (!Schema.Table("ImportLists").Column("RootFolderPath").Exists())
            {
                Alter.Table("ImportLists")
                    .AddColumn("RootFolderPath").AsString().Nullable();
                repairs++;
            }

            // PostgreSQL: fix ShouldMonitor to be an integer when it was incorrectly created as a boolean.
            IfPostgres().Execute.Sql(@"

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'importlists'
                          AND column_name ILIKE 'shouldmonitor'
                          AND data_type = 'boolean'
                    ) THEN
                        EXECUTE 'ALTER TABLE ""ImportLists"" ALTER COLUMN ""ShouldMonitor"" TYPE integer USING (CASE WHEN ""ShouldMonitor"" THEN 2 ELSE 0 END)';
                    END IF;
                END $$;
            ");

            return repairs;
        }

        private int EnsureEbookNamingConfigColumns()
        {
            if (!Schema.Table("NamingConfig").Exists())
            {
                return 0;
            }

            var repairs = 0;
            var assignments = new List<string>();

            if (!Schema.Table("NamingConfig").Column("EbookRenameBooks").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookRenameBooks").AsBoolean().NotNullable().WithDefaultValue(false);
                repairs++;
                assignments.Add("\"EbookRenameBooks\" = \"RenameBooks\"");
            }

            if (!Schema.Table("NamingConfig").Column("EbookReplaceIllegalCharacters").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookReplaceIllegalCharacters").AsBoolean().NotNullable().WithDefaultValue(true);
                repairs++;
                assignments.Add("\"EbookReplaceIllegalCharacters\" = \"ReplaceIllegalCharacters\"");
            }

            if (!Schema.Table("NamingConfig").Column("EbookStandardBookFormat").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookStandardBookFormat").AsString().Nullable();
                repairs++;
                assignments.Add("\"EbookStandardBookFormat\" = \"StandardBookFormat\"");
            }

            if (!Schema.Table("NamingConfig").Column("EbookAuthorFolderFormat").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookAuthorFolderFormat").AsString().Nullable();
                repairs++;
                assignments.Add("\"EbookAuthorFolderFormat\" = \"AuthorFolderFormat\"");
            }

            if (!Schema.Table("NamingConfig").Column("EbookColonReplacementFormat").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookColonReplacementFormat").AsInt32().NotNullable().WithDefaultValue(0);
                repairs++;
                assignments.Add("\"EbookColonReplacementFormat\" = \"ColonReplacementFormat\"");
            }

            if (assignments.Count > 0)
            {
                Execute.Sql($@"UPDATE ""NamingConfig""
SET {string.Join(",\n    ", assignments)};");
            }

            return repairs;
        }

        private int EnsureEditionsAsins()
        {
            if (!Schema.Table("Editions").Exists())
            {
                return 0;
            }

            if (Schema.Table("Editions").Column("Asins").Exists())
            {
                return 0;
            }

            Alter.Table("Editions")
                .AddColumn("Asins").AsString(int.MaxValue).NotNullable().WithDefaultValue("[]");

            // Backfill existing Asin values to JSON array format (idempotent).
            Execute.Sql("UPDATE \"Editions\" SET \"Asins\" = '[\"' || UPPER(TRIM(\"Asin\")) || '\"]' WHERE \"Asin\" IS NOT NULL AND TRIM(\"Asin\") != '' AND (\"Asins\" IS NULL OR TRIM(\"Asins\") = '' OR TRIM(\"Asins\") = '[]')");

            return 1;
        }

        private int EnsureDownloadClientMediaTags()
        {
            if (!Schema.Table("DownloadClients").Exists())
            {
                return 0;
            }

            var repairs = 0;

            if (!Schema.Table("DownloadClients").Column("AudiobookTags").Exists())
            {
                Alter.Table("DownloadClients").AddColumn("AudiobookTags").AsString().Nullable();
                repairs++;
            }

            if (!Schema.Table("DownloadClients").Column("EbookTags").Exists())
            {
                Alter.Table("DownloadClients").AddColumn("EbookTags").AsString().Nullable();
                repairs++;
            }

            if (repairs > 0)
            {
                // Backfill from the legacy "Tags" column so existing configurations continue to work.
                Execute.Sql("UPDATE \"DownloadClients\" SET \"AudiobookTags\" = \"Tags\" WHERE \"AudiobookTags\" IS NULL AND \"Tags\" IS NOT NULL");
                Execute.Sql("UPDATE \"DownloadClients\" SET \"EbookTags\" = \"Tags\" WHERE \"EbookTags\" IS NULL AND \"Tags\" IS NOT NULL");
            }

            return repairs;
        }
    }
}
