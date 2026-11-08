using FluentMigrator;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(118)]
    public class add_library_title_search_index : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfDatabase("sqlite").Execute.WithConnection((connection, transaction) =>
                EditionFtsRepository.RebuildSqliteHeaderIndex(connection, transaction, _logger));
        }
    }
}
