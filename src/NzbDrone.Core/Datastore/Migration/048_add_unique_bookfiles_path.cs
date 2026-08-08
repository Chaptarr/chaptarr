using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(48)]
    public class add_unique_bookfiles_path : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Ensure BookFiles.Path is unique so path-based lookups (GetFileWithPath -> SingleOrDefault)
            // can't throw and so staging can safely create EditionId=0 rows concurrently with imports.
            //
            // Deduplicate first (keep best candidate per path): prefer mapped rows (higher EditionId), then lowest Id.
            // IMPORTANT: Merge references (MetadataFiles/ExtraFiles) to the kept row before deleting duplicates.
            Execute.WithConnection((connection, transaction) =>
            {
                var duplicateGroupCount = connection.ExecuteScalar<int>(
                    @"SELECT COUNT(*) FROM (
                        SELECT 1
                        FROM ""BookFiles""
                        GROUP BY ""Path""
                        HAVING COUNT(*) > 1
                      ) x;",
                    transaction: transaction);

                if (duplicateGroupCount > 0)
                {
                    _logger.Warn("Found {0} duplicate BookFiles.Path groups; merging references before enforcing uniqueness", duplicateGroupCount);
                }

                connection.Execute(
                    @"DROP TABLE IF EXISTS bookfiles_dedupe_map;

                      CREATE TEMP TABLE bookfiles_dedupe_map AS
                      WITH ranked AS (
                        SELECT ""Id"",
                               FIRST_VALUE(""Id"") OVER (PARTITION BY ""Path"" ORDER BY ""EditionId"" DESC, ""Id"" ASC) AS canonical_id,
                               ROW_NUMBER() OVER (PARTITION BY ""Path"" ORDER BY ""EditionId"" DESC, ""Id"" ASC) AS rn
                        FROM ""BookFiles""
                      )
                      SELECT ""Id"" AS dup_id, canonical_id
                      FROM ranked
                      WHERE rn > 1;

                      UPDATE ""MetadataFiles""
                      SET ""BookFileId"" = (SELECT canonical_id FROM bookfiles_dedupe_map WHERE dup_id = ""BookFileId"")
                      WHERE ""BookFileId"" IN (SELECT dup_id FROM bookfiles_dedupe_map);

                      UPDATE ""ExtraFiles""
                      SET ""BookFileId"" = (SELECT canonical_id FROM bookfiles_dedupe_map WHERE dup_id = ""BookFileId"")
                      WHERE ""BookFileId"" IN (SELECT dup_id FROM bookfiles_dedupe_map);

                      DELETE FROM ""BookFiles""
                      WHERE ""Id"" IN (SELECT dup_id FROM bookfiles_dedupe_map);

                      DROP TABLE IF EXISTS bookfiles_dedupe_map;",
                    transaction: transaction);
            });

            // Add unique index on Path. Use a SQLite BINARY-collation index so existing LIKE optimizations remain.
            IfDatabase("sqlite").Execute.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_BookFiles_Path_Unique"" ON ""BookFiles"" (""Path"" COLLATE BINARY);");
            IfPostgres().Execute.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_BookFiles_Path_Unique"" ON ""BookFiles"" (""Path"");");

            // Drop legacy non-unique SQLite indexes on Path to avoid redundant write overhead.
            IfDatabase("sqlite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_BookFiles_Path_Binary"";");
            IfDatabase("sqlite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_BookFiles_Path_Partial"";");
        }
    }
}
