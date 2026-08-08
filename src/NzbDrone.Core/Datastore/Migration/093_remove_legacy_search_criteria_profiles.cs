using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(93)]
    public class remove_legacy_search_criteria_profiles : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("QualityProfiles").Exists() &&
                Schema.Table("QualityProfiles").Column("SearchCriteriaProfileId").Exists())
            {
                Delete.Column("SearchCriteriaProfileId").FromTable("QualityProfiles");
            }

            if (Schema.Table("RootFolders").Exists() &&
                Schema.Table("RootFolders").Column("DefaultSearchCriteriaProfileId").Exists())
            {
                Delete.Column("DefaultSearchCriteriaProfileId").FromTable("RootFolders");
            }

            if (Schema.Table("SearchCriteriaProfiles").Exists())
            {
                Delete.Table("SearchCriteriaProfiles");
            }

            if (Schema.Table("Config").Exists())
            {
                Delete.FromTable("Config").Row(new { Key = "DefaultSearchCriteriaProfileId" });
            }
        }
    }
}
