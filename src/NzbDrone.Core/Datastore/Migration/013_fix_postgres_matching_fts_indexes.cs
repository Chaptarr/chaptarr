using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(13)]
    public class fix_postgres_matching_fts_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // PostgreSQL matching queries use per-table tsvectors (Editions + Books + Authors),
            // since edition_fts is a SQLite-only virtual table.
            //
            // This migration aligns the GIN indexes with the expressions used by the Postgres matching queries.
            IfPostgres().Execute.Sql(@"
                DROP INDEX IF EXISTS idx_books_matching_fts;
                DROP INDEX IF EXISTS idx_editions_matching_fts;

                CREATE INDEX IF NOT EXISTS idx_books_series_fts
                ON ""Books""
                USING GIN (
                    to_tsvector('simple', COALESCE(""SeriesName"", ''))
                );

                CREATE INDEX IF NOT EXISTS idx_editions_matching_title_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple', COALESCE(""MatchingTitle"", ''))
                );

                CREATE INDEX IF NOT EXISTS idx_editions_matching_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple',
                        COALESCE(""MatchingTitle"", '') || ' ' ||
                        COALESCE(""Subtitle"", '') || ' ' ||
                        COALESCE(""Narrator"", '')
                    )
                );
            ");
        }
    }
}
