using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(59)]
    public class fix_postgres_audiobook_fts_index : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // The audiobook GIN index included Subtitle, but the query in EditionFtsRepository
            // excludes Subtitle (intentionally — Subtitle tokens like "The Complete Collection"
            // polluted book-level ranking). PostgreSQL GIN expression indexes require exact
            // syntactic match, so the old index was never used (seq scan fallback on every query).
            //
            // Recreate without Subtitle to match the actual query expression.
            IfPostgres().Execute.Sql(@"
                DROP INDEX IF EXISTS idx_editions_matching_fts;
                CREATE INDEX IF NOT EXISTS idx_editions_matching_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple',
                        COALESCE(""MatchingTitle"", '') || ' ' ||
                        COALESCE(""NarratorNames"", '') || ' ' ||
                        COALESCE(""Narrator"", '')
                    )
                );
            ");
        }
    }
}
