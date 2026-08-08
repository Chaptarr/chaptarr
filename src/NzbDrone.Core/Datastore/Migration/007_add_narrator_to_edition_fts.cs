using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(7)]
    public class add_narrator_to_edition_fts : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Rebuild edition_fts to include Narrator column
            // This allows narrator tags from files to boost editions with matching narrators
            // Critical for audiobook matching accuracy

            // Drop existing triggers first
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ai;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_au;");

            // Drop and recreate FTS table with Narrator column
            IfDatabase("sqlite").Execute.Sql("DROP TABLE IF EXISTS edition_fts;");
            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE edition_fts USING fts5(" +
                "Title, TitleSlug, Narrator, " +
                "content='Editions', content_rowid='Id', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            // Repopulate edition_fts with all three columns
            IfDatabase("sqlite").Execute.Sql(
                "INSERT INTO edition_fts(rowid, Title, TitleSlug, Narrator) " +
                "SELECT Id, COALESCE(Title, ''), COALESCE(TitleSlug, ''), COALESCE(Narrator, '') " +
                "FROM Editions;"
            );

            // Recreate triggers to maintain FTS sync with Narrator column
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ai AFTER INSERT ON Editions BEGIN " +
                "INSERT INTO edition_fts(rowid, Title, TitleSlug, Narrator) " +
                "VALUES (new.Id, COALESCE(new.Title, ''), COALESCE(new.TitleSlug, ''), COALESCE(new.Narrator, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ad AFTER DELETE ON Editions BEGIN " +
                "INSERT INTO edition_fts(edition_fts, rowid, Title, TitleSlug, Narrator) " +
                "VALUES('delete', old.Id, COALESCE(old.Title, ''), COALESCE(old.TitleSlug, ''), COALESCE(old.Narrator, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_au AFTER UPDATE ON Editions BEGIN " +
                "INSERT INTO edition_fts(edition_fts, rowid, Title, TitleSlug, Narrator) " +
                "VALUES('delete', old.Id, COALESCE(old.Title, ''), COALESCE(old.TitleSlug, ''), COALESCE(old.Narrator, '')); " +
                "INSERT INTO edition_fts(rowid, Title, TitleSlug, Narrator) " +
                "VALUES (new.Id, COALESCE(new.Title, ''), COALESCE(new.TitleSlug, ''), COALESCE(new.Narrator, '')); " +
                "END;"
            );
        }
    }
}
