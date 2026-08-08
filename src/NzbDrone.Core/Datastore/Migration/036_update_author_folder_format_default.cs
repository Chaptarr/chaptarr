using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(36)]
    public class update_author_folder_format_default : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Update legacy/default author folder naming to drop middle names/initials by default.
            // Only touch values that are clearly default/backfilled so we don't override user customizations.
            Execute.Sql(@"
UPDATE ""NamingConfig""
SET ""AuthorFolderFormat"" = '{Author NameFirstLast}'
WHERE ""AuthorFolderFormat"" IS NULL
   OR ""AuthorFolderFormat"" = ''
   OR ""AuthorFolderFormat"" = '{Author Name}';
");

            Execute.Sql(@"
UPDATE ""NamingConfig""
SET ""EbookAuthorFolderFormat"" = '{Author NameFirstLast}'
WHERE ""EbookAuthorFolderFormat"" IS NULL
   OR ""EbookAuthorFolderFormat"" = ''
   OR ""EbookAuthorFolderFormat"" = '{Author Name}';
");
        }
    }
}

