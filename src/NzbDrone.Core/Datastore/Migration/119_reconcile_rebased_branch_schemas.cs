using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(119)]
    public class reconcile_rebased_branch_schemas : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            EnsureRanobeDbProviderIds();
            EnsureRootFolderCalibreGates();
            EnsureImportListSyncHistory();
            EnsureMetadataProfilePreferences();
        }

        private void EnsureRanobeDbProviderIds()
        {
            if (Schema.Table("Books").Exists() && !Schema.Table("Books").Column("RanobeDbBookId").Exists())
            {
                Alter.Table("Books").AddColumn("RanobeDbBookId").AsString(128).Nullable();
            }

            if (Schema.Table("Authors").Exists() && !Schema.Table("Authors").Column("RanobeDbAuthorId").Exists())
            {
                Alter.Table("Authors").AddColumn("RanobeDbAuthorId").AsString(128).Nullable();
            }

            if (Schema.Table("Books").Exists())
            {
                Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"UX_Books_RanobeDbBookId_MediaType\" ON \"Books\" (\"RanobeDbBookId\", \"MediaType\");");
            }

            if (Schema.Table("Authors").Exists())
            {
                Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"UX_Authors_RanobeDbAuthorId\" ON \"Authors\" (\"RanobeDbAuthorId\");");
            }
        }

        private void EnsureRootFolderCalibreGates()
        {
            if (!Schema.Table("RootFolders").Exists())
            {
                return;
            }

            if (!Schema.Table("RootFolders").Column("ReapCalibreDuplicates").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("ReapCalibreDuplicates").AsBoolean().NotNullable().WithDefaultValue(false);
            }

            if (!Schema.Table("RootFolders").Column("AutoPushCalibreMetadata").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("AutoPushCalibreMetadata").AsBoolean().NotNullable().WithDefaultValue(false);
            }
        }

        private void EnsureImportListSyncHistory()
        {
            const string tableName = "ImportListSyncHistory";

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
            }

            Execute.Sql("CREATE INDEX IF NOT EXISTS \"IX_ImportListSyncHistory_ImportListId_StartedAt\" ON \"ImportListSyncHistory\" (\"ImportListId\" ASC, \"StartedAt\" DESC);");
            Execute.Sql("CREATE INDEX IF NOT EXISTS \"IX_ImportListSyncHistory_StartedAt\" ON \"ImportListSyncHistory\" (\"StartedAt\" DESC);");
        }

        private void EnsureMetadataProfilePreferences()
        {
            const string tableName = "MetadataProfiles";
            if (!Schema.Table(tableName).Exists())
            {
                return;
            }

            if (!Schema.Table(tableName).Column("PreferredEditionTerms").Exists())
            {
                Alter.Table(tableName).AddColumn("PreferredEditionTerms").AsString().Nullable();
            }

            if (!Schema.Table(tableName).Column("PreferredSeries").Exists())
            {
                Alter.Table(tableName).AddColumn("PreferredSeries").AsString().Nullable();
            }
        }
    }
}
