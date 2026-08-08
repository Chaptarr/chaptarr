using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(8)]
    public class fix_edition_fts_columns : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Fix edition_fts to use correct columns: Title, SeriesName, AuthorName, Narrator
            // TitleSlug is useless for matching - replaced with Series and Author info
            // This requires JOINing Editions -> Books -> Authors

            // Drop existing triggers first
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ai;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_au;");

            // Drop and recreate FTS table with correct columns
            // Note: No content='Editions' because we need data from multiple tables
            IfDatabase("sqlite").Execute.Sql("DROP TABLE IF EXISTS edition_fts;");
            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE edition_fts USING fts5(" +
                "Title, SeriesName, AuthorName, Narrator, " +
                "content='', content_rowid='rowid', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            // Populate edition_fts by joining Editions -> Books -> Authors
            IfDatabase("sqlite").Execute.Sql(
                "INSERT INTO edition_fts(rowid, Title, SeriesName, AuthorName, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.Title, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(e.Narrator, '') " +
                "FROM Editions e " +
                "JOIN Books b ON e.BookId = b.Id " +
                "JOIN Authors a ON b.AuthorId = a.Id;"
            );

            // Triggers for INSERT on Editions - need to lookup Book/Author
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ai AFTER INSERT ON Editions BEGIN " +
                "INSERT INTO edition_fts(rowid, Title, SeriesName, AuthorName, Narrator) " +
                "SELECT new.Id, " +
                "COALESCE(new.Title, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(new.Narrator, '') " +
                "FROM Books b " +
                "JOIN Authors a ON b.AuthorId = a.Id " +
                "WHERE b.Id = new.BookId; " +
                "END;"
            );

            // Triggers for DELETE on Editions
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ad AFTER DELETE ON Editions BEGIN " +
                "DELETE FROM edition_fts WHERE rowid = old.Id; " +
                "END;"
            );

            // Triggers for UPDATE on Editions
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_au AFTER UPDATE ON Editions BEGIN " +
                "DELETE FROM edition_fts WHERE rowid = old.Id; " +
                "INSERT INTO edition_fts(rowid, Title, SeriesName, AuthorName, Narrator) " +
                "SELECT new.Id, " +
                "COALESCE(new.Title, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(new.Narrator, '') " +
                "FROM Books b " +
                "JOIN Authors a ON b.AuthorId = a.Id " +
                "WHERE b.Id = new.BookId; " +
                "END;"
            );

            // Also need triggers on Books for when SeriesName changes
            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_book_au AFTER UPDATE OF SeriesName ON Books BEGIN " +
                "DELETE FROM edition_fts WHERE rowid IN (SELECT Id FROM Editions WHERE BookId = new.Id); " +
                "INSERT INTO edition_fts(rowid, Title, SeriesName, AuthorName, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.Title, ''), " +
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
                "INSERT INTO edition_fts(rowid, Title, SeriesName, AuthorName, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.Title, ''), " +
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
