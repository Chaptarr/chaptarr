using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(29)]
    public class add_bookid_to_metadatafiles : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("MetadataFiles").Exists())
            {
                return;
            }

            if (Schema.Table("MetadataFiles").Column("BookId").Exists())
            {
                return;
            }

            IfDatabase("sqlite").Execute.Sql(@"
                ALTER TABLE ""MetadataFiles"" ADD COLUMN ""BookId"" INTEGER;
            ");

            IfPostgres().Execute.Sql(@"
                ALTER TABLE ""MetadataFiles""
                ADD COLUMN IF NOT EXISTS ""BookId"" INTEGER;
            ");

            var hasEditionId = Schema.Table("MetadataFiles").Column("EditionId").Exists();
            if (hasEditionId)
            {
                IfDatabase("sqlite").Execute.Sql(@"
                    UPDATE ""MetadataFiles""
                    SET ""BookId"" = (
                        SELECT ""Editions"".""BookId""
                        FROM ""Editions""
                        WHERE ""Editions"".""Id"" = ""MetadataFiles"".""EditionId""
                    )
                    WHERE ""BookId"" IS NULL
                      AND ""EditionId"" IS NOT NULL
                      AND ""EditionId"" > 0
                      AND EXISTS (
                        SELECT 1
                        FROM ""Editions""
                        WHERE ""Editions"".""Id"" = ""MetadataFiles"".""EditionId""
                      );
                ");

                IfPostgres().Execute.Sql(@"
                    UPDATE ""MetadataFiles""
                    SET ""BookId"" = ""Editions"".""BookId""
                    FROM ""Editions""
                    WHERE ""MetadataFiles"".""BookId"" IS NULL
                      AND ""MetadataFiles"".""EditionId"" IS NOT NULL
                      AND ""MetadataFiles"".""EditionId"" > 0
                      AND ""Editions"".""Id"" = ""MetadataFiles"".""EditionId"";
                ");
            }

            var hasBookFileId = Schema.Table("MetadataFiles").Column("BookFileId").Exists();
            if (hasBookFileId)
            {
                IfDatabase("sqlite").Execute.Sql(@"
                    UPDATE ""MetadataFiles""
                    SET ""BookId"" = (
                        SELECT ""Editions"".""BookId""
                        FROM ""BookFiles""
                        JOIN ""Editions"" ON ""Editions"".""Id"" = ""BookFiles"".""EditionId""
                        WHERE ""BookFiles"".""Id"" = ""MetadataFiles"".""BookFileId""
                    )
                    WHERE ""BookId"" IS NULL
                      AND ""BookFileId"" IS NOT NULL
                      AND ""BookFileId"" > 0
                      AND EXISTS (
                        SELECT 1
                        FROM ""BookFiles""
                        WHERE ""BookFiles"".""Id"" = ""MetadataFiles"".""BookFileId""
                      );
                ");

                IfPostgres().Execute.Sql(@"
                    UPDATE ""MetadataFiles""
                    SET ""BookId"" = ""Editions"".""BookId""
                    FROM ""BookFiles""
                    JOIN ""Editions"" ON ""Editions"".""Id"" = ""BookFiles"".""EditionId""
                    WHERE ""MetadataFiles"".""BookId"" IS NULL
                      AND ""MetadataFiles"".""BookFileId"" IS NOT NULL
                      AND ""MetadataFiles"".""BookFileId"" > 0
                      AND ""BookFiles"".""Id"" = ""MetadataFiles"".""BookFileId"";
                ");
            }
        }
    }
}
