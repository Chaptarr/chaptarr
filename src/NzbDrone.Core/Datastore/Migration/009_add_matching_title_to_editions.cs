using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(9)]
    public class add_matching_title_to_editions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Add MatchingTitle column to Editions table
            // This stores pre-normalized title for FTS matching:
            // - Lowercase
            // - Possessives merged ('s → s): Philosopher's → Philosophers
            // - Diacritics stripped: café → cafe
            Alter.Table("Editions").AddColumn("MatchingTitle").AsString().Nullable();

            // NOTE: Backfill will be done via C# command (not SQL) to ensure
            // consistent normalization using StringSuperNormalizer.ComputeMatchingTitle()
            // See: BackfillMatchingTitlesCommand (runs automatically on startup)

            // Drop existing FTS triggers first
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ai;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_book_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_author_au;");

            // Drop and recreate FTS table with MatchingTitle column
            IfDatabase("sqlite").Execute.Sql("DROP TABLE IF EXISTS edition_fts;");
            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE edition_fts USING fts5(" +
                "Title, MatchingTitle, SeriesName, AuthorName, Narrator, " +
                "content='', content_rowid='rowid', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            // Populate edition_fts - MatchingTitle will be empty until C# backfill runs
            // But FTS will still work on Title, SeriesName, AuthorName, Narrator
            IfDatabase("sqlite").Execute.Sql(
                "INSERT INTO edition_fts(rowid, Title, MatchingTitle, SeriesName, AuthorName, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.Title, ''), " +
                "COALESCE(e.MatchingTitle, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(e.Narrator, '') " +
                "FROM Editions e " +
                "JOIN Books b ON e.BookId = b.Id " +
                "JOIN Authors a ON b.AuthorId = a.Id;"
            );

            // Trigger for INSERT on Editions
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ai AFTER INSERT ON Editions BEGIN " +
                "INSERT INTO edition_fts(rowid, Title, MatchingTitle, SeriesName, AuthorName, Narrator) " +
                "SELECT new.Id, " +
                "COALESCE(new.Title, ''), " +
                "COALESCE(new.MatchingTitle, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(new.Narrator, '') " +
                "FROM Books b " +
                "JOIN Authors a ON b.AuthorId = a.Id " +
                "WHERE b.Id = new.BookId; " +
                "END;"
            );

            // Trigger for DELETE on Editions
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ad AFTER DELETE ON Editions BEGIN " +
                "DELETE FROM edition_fts WHERE rowid = old.Id; " +
                "END;"
            );

            // Trigger for UPDATE on Editions
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_au AFTER UPDATE ON Editions BEGIN " +
                "DELETE FROM edition_fts WHERE rowid = old.Id; " +
                "INSERT INTO edition_fts(rowid, Title, MatchingTitle, SeriesName, AuthorName, Narrator) " +
                "SELECT new.Id, " +
                "COALESCE(new.Title, ''), " +
                "COALESCE(new.MatchingTitle, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(new.Narrator, '') " +
                "FROM Books b " +
                "JOIN Authors a ON b.AuthorId = a.Id " +
                "WHERE b.Id = new.BookId; " +
                "END;"
            );

            // Trigger on Books for when SeriesName changes
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_book_au AFTER UPDATE OF SeriesName ON Books BEGIN " +
                "DELETE FROM edition_fts WHERE rowid IN (SELECT Id FROM Editions WHERE BookId = new.Id); " +
                "INSERT INTO edition_fts(rowid, Title, MatchingTitle, SeriesName, AuthorName, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.Title, ''), " +
                "COALESCE(e.MatchingTitle, ''), " +
                "COALESCE(new.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(e.Narrator, '') " +
                "FROM Editions e " +
                "JOIN Authors a ON new.AuthorId = a.Id " +
                "WHERE e.BookId = new.Id; " +
                "END;"
            );

            // Trigger on Authors for when Name changes
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_author_au AFTER UPDATE OF Name ON Authors BEGIN " +
                "DELETE FROM edition_fts WHERE rowid IN (" +
                "SELECT e.Id FROM Editions e " +
                "JOIN Books b ON e.BookId = b.Id " +
                "WHERE b.AuthorId = new.Id); " +
                "INSERT INTO edition_fts(rowid, Title, MatchingTitle, SeriesName, AuthorName, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.Title, ''), " +
                "COALESCE(e.MatchingTitle, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(new.Name, ''), " +
                "COALESCE(e.Narrator, '') " +
                "FROM Editions e " +
                "JOIN Books b ON e.BookId = b.Id " +
                "WHERE b.AuthorId = new.Id; " +
                "END;"
            );
        }
    }
}
