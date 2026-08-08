using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(47)]
    public class normalize_download_ids_to_upper : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                connection.Execute(
                    @"UPDATE ""History""
                      SET ""DownloadId"" = UPPER(""DownloadId"")
                      WHERE ""DownloadId"" IS NOT NULL;",
                    transaction: transaction);

                connection.Execute(
                    @"UPDATE ""DownloadHistory""
                      SET ""DownloadId"" = UPPER(""DownloadId"")
                      WHERE ""DownloadId"" IS NOT NULL;",
                    transaction: transaction);
            });
        }
    }
}

