using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(22)]
    public class add_author_id_to_pending_releases : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("PendingReleases").Column("AuthorId").Exists())
            {
                Alter.Table("PendingReleases")
                    .AddColumn("AuthorId").AsInt32().NotNullable().WithDefaultValue(0);
            }

            // Old schema stored BookId on PendingReleases; backfill AuthorId where possible.
            if (!Schema.Table("PendingReleases").Column("BookId").Exists())
            {
                return;
            }

            Execute.WithConnection((connection, transaction) =>
            {
                connection.Execute(@"
UPDATE ""PendingReleases""
SET ""AuthorId"" = (
    SELECT ""Books"".""AuthorId""
    FROM ""Books""
    WHERE ""Books"".""Id"" = ""PendingReleases"".""BookId""
)
WHERE ""AuthorId"" = 0
  AND ""BookId"" IS NOT NULL
  AND ""BookId"" > 0
  AND EXISTS (
    SELECT 1
    FROM ""Books""
    WHERE ""Books"".""Id"" = ""PendingReleases"".""BookId""
  );
", transaction: transaction);
            });
        }
    }
}
