using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(77)]
    public class clear_legacy_author_sync_etags : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("AuthorSyncMetadata").Exists())
            {
                return;
            }

            Execute.WithConnection((connection, transaction) =>
            {
                var rows = connection.Execute(@"
                    UPDATE ""AuthorSyncMetadata""
                    SET ""ETag"" = NULL
                    WHERE ""ETag"" IS NOT NULL
                      AND ""ETag"" NOT LIKE 'W/""v%""';",
                    transaction: transaction);

                _logger.Info("[MIGRATION-77] Cleared {0} legacy AuthorSyncMetadata ETag values.", rows);
            });
        }
    }
}
