using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(106)]
    public class add_pending_author_media_tags : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            const string tableName = "PendingAuthorImport";
            if (!Schema.Table(tableName).Exists())
            {
                return;
            }

            if (!Schema.Table(tableName).Column("AudiobookTags").Exists())
            {
                Alter.Table(tableName).AddColumn("AudiobookTags").AsString().Nullable();
            }

            if (!Schema.Table(tableName).Column("EbookTags").Exists())
            {
                Alter.Table(tableName).AddColumn("EbookTags").AsString().Nullable();
            }

            // Legacy Tags applied to every requested side. Keep that intent while
            // leaving a side that was never requested genuinely unset.
            Execute.Sql(@"
                UPDATE ""PendingAuthorImport""
                   SET ""AudiobookTags"" = ""Tags""
                 WHERE ""AudiobookTags"" IS NULL
                   AND ""Tags"" IS NOT NULL
                   AND ""AudiobookStatus"" <> 0;

                UPDATE ""PendingAuthorImport""
                   SET ""EbookTags"" = ""Tags""
                 WHERE ""EbookTags"" IS NULL
                   AND ""Tags"" IS NOT NULL
                   AND ""EbookStatus"" <> 0;");
        }
    }
}
