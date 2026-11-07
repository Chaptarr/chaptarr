using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(117)]
    public class add_import_list_sync_history : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("ImportListSyncHistory").Exists())
            {
                Create.Table("ImportListSyncHistory")
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

            if (!Schema.Table("ImportListSyncHistory").Index("IX_ImportListSyncHistory_ImportListId_StartedAt").Exists())
            {
                Create.Index("IX_ImportListSyncHistory_ImportListId_StartedAt")
                    .OnTable("ImportListSyncHistory")
                    .OnColumn("ImportListId").Ascending()
                    .OnColumn("StartedAt").Descending();
            }

            if (!Schema.Table("ImportListSyncHistory").Index("IX_ImportListSyncHistory_StartedAt").Exists())
            {
                Create.Index("IX_ImportListSyncHistory_StartedAt")
                    .OnTable("ImportListSyncHistory")
                    .OnColumn("StartedAt").Descending();
            }
        }
    }
}
