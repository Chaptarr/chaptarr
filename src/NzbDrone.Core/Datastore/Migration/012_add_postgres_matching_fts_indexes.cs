using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(12)]
    public class add_postgres_matching_fts_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // PostgreSQL full-text search indexes used by file/edition matching.
            // SQLite uses FTS5 virtual tables and triggers instead.

            IfPostgres().Execute.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_books_matching_fts
                ON ""Books""
                USING GIN (
                    to_tsvector('simple',
                        COALESCE(""CleanTitle"", '') || ' ' ||
                        COALESCE(""Title"", '') || ' ' ||
                        COALESCE(""SeriesName"", '') || ' ' ||
                        COALESCE(""TitleSlug"", '')
                    )
                );

                CREATE INDEX IF NOT EXISTS idx_editions_matching_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple',
                        COALESCE(""MatchingTitle"", '') || ' ' ||
                        COALESCE(""Title"", '') || ' ' ||
                        COALESCE(""Subtitle"", '') || ' ' ||
                        COALESCE(""Narrator"", '')
                    )
                );
            ");
        }
    }
}
