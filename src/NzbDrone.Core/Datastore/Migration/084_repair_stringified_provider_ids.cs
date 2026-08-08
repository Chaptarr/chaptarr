using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(84)]
    public class repair_stringified_provider_ids : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Books").Exists() ||
                !Schema.Table("Editions").Exists() ||
                !Schema.Table("ProviderAliasIndex").Exists())
            {
                return;
            }

            IfDatabase("sqlite").Execute.WithConnection((connection, transaction) =>
            {
                StringifiedProviderIdRepair.Apply(connection, transaction, isPostgres: false);
            });

            IfPostgres().Execute.WithConnection((connection, transaction) =>
            {
                StringifiedProviderIdRepair.Apply(connection, transaction, isPostgres: true);
            });
        }
    }
}
