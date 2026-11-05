using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(126)]
    public class index_bookshelf_legacy_identity_lookups : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Match the service's case-insensitive entity predicate. Index both
            // directions so large migrated libraries do not scan every mapping
            // for each resource. These expressions work on SQLite and PostgreSQL.
            Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_BookshelfLegacyIdentityMap_SourceCaseLookup""
ON ""BookshelfLegacyIdentityMap"" (""Source"", LOWER(""EntityType""), ""SourceId"");");
            Execute.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_BookshelfLegacyIdentityMap_TargetCaseLookup""
ON ""BookshelfLegacyIdentityMap"" (""Source"", LOWER(""EntityType""), ""TargetId"");");

            // The unique index already covers the old case-sensitive prefix.
            if (Schema.Table("BookshelfLegacyIdentityMap").Index("IX_BookshelfLegacyIdentityMap_Lookup").Exists())
            {
                Delete.Index("IX_BookshelfLegacyIdentityMap_Lookup").OnTable("BookshelfLegacyIdentityMap");
            }
        }
    }
}
