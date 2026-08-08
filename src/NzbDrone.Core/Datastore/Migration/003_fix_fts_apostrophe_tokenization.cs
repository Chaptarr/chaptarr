using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(3)]
    public class FixFtsApostropheTokenization : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // SQLite only - PostgreSQL has separate FTS implementation
            // Split into individual Execute.Sql calls with string concatenation to avoid FluentMigrator parsing issues

            // Drop existing triggers to avoid duplicates
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS author_fts_ai;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS author_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS author_fts_au;");

            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ai;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_au;");

            // Drop and recreate FTS tables to ensure consistent tokenization configuration.
            // Note: our SQLite FTS config keeps hyphens and periods as token characters (tokenchars '-.').
            // Apostrophes are treated as separators (e.g., O'Brien -> tokens "o" and "brien").
            IfDatabase("sqlite").Execute.Sql("DROP TABLE IF EXISTS author_fts;");
            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE author_fts USING fts5(" +
                "Name, SortName, TitleSlug, " +
                "content='Authors', content_rowid='Id', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            // Repopulate author_fts
            IfDatabase("sqlite").Execute.Sql(
                "INSERT INTO author_fts(rowid, Name, SortName, TitleSlug) " +
                "SELECT Id, COALESCE(Name, ''), COALESCE(SortName, ''), COALESCE(TitleSlug, '') " +
                "FROM Authors;"
            );

            IfDatabase("sqlite").Execute.Sql("DROP TABLE IF EXISTS edition_fts;");
            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE edition_fts USING fts5(" +
                "Title, TitleSlug, " +
                "content='Editions', content_rowid='Id', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            // Repopulate edition_fts
            IfDatabase("sqlite").Execute.Sql(
                "INSERT INTO edition_fts(rowid, Title, TitleSlug) " +
                "SELECT Id, COALESCE(Title, ''), COALESCE(TitleSlug, '') " +
                "FROM Editions;"
            );

            // Recreate external-content triggers (same pattern as migration 001)
            // Authors
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER author_fts_ai AFTER INSERT ON Authors BEGIN " +
                "INSERT INTO author_fts(rowid, Name, SortName, TitleSlug) " +
                "VALUES (new.Id, COALESCE(new.Name, ''), COALESCE(new.SortName, ''), COALESCE(new.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER author_fts_ad AFTER DELETE ON Authors BEGIN " +
                "INSERT INTO author_fts(author_fts, rowid, Name, SortName, TitleSlug) " +
                "VALUES('delete', old.Id, COALESCE(old.Name, ''), COALESCE(old.SortName, ''), COALESCE(old.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER author_fts_au AFTER UPDATE ON Authors BEGIN " +
                "INSERT INTO author_fts(author_fts, rowid, Name, SortName, TitleSlug) " +
                "VALUES('delete', old.Id, COALESCE(old.Name, ''), COALESCE(old.SortName, ''), COALESCE(old.TitleSlug, '')); " +
                "INSERT INTO author_fts(rowid, Name, SortName, TitleSlug) " +
                "VALUES (new.Id, COALESCE(new.Name, ''), COALESCE(new.SortName, ''), COALESCE(new.TitleSlug, '')); " +
                "END;"
            );

            // Editions
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ai AFTER INSERT ON Editions BEGIN " +
                "INSERT INTO edition_fts(rowid, Title, TitleSlug) " +
                "VALUES (new.Id, COALESCE(new.Title, ''), COALESCE(new.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ad AFTER DELETE ON Editions BEGIN " +
                "INSERT INTO edition_fts(edition_fts, rowid, Title, TitleSlug) " +
                "VALUES('delete', old.Id, COALESCE(old.Title, ''), COALESCE(old.TitleSlug, '')); " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_au AFTER UPDATE ON Editions BEGIN " +
                "INSERT INTO edition_fts(edition_fts, rowid, Title, TitleSlug) " +
                "VALUES('delete', old.Id, COALESCE(old.Title, ''), COALESCE(old.TitleSlug, '')); " +
                "INSERT INTO edition_fts(rowid, Title, TitleSlug) " +
                "VALUES (new.Id, COALESCE(new.Title, ''), COALESCE(new.TitleSlug, '')); " +
                "END;"
            );
        }
    }
}
