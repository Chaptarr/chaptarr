using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(10)]
    public class add_subtitle_to_editions_and_fts : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Edition-level subtitle support (distinct from Books.Subtitle)
            if (!Schema.Table("Editions").Column("Subtitle").Exists())
            {
                Alter.Table("Editions").AddColumn("Subtitle").AsString().Nullable();
            }

            // SQLite FTS: add Subtitle to edition_fts so matching can use it without baking it into Title/MatchingTitle.
            // This table is contentless and is maintained via triggers (see migrations 008/009).
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ai;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_book_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_author_au;");

            IfDatabase("sqlite").Execute.Sql("DROP TABLE IF EXISTS edition_fts;");
            // FTS schema: 5 columns (Title REMOVED to prevent double-counting with MatchingTitle)
            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE edition_fts USING fts5(" +
                "MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, " +
                "content='', content_rowid='rowid', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            IfDatabase("sqlite").Execute.Sql(
                "INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.MatchingTitle, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(e.Subtitle, ''), " +
                "COALESCE(e.Narrator, '') " +
                "FROM Editions e " +
                "JOIN Books b ON e.BookId = b.Id " +
                "JOIN Authors a ON b.AuthorId = a.Id;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ai AFTER INSERT ON Editions BEGIN " +
                "INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator) " +
                "SELECT new.Id, " +
                "COALESCE(new.MatchingTitle, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(new.Subtitle, ''), " +
                "COALESCE(new.Narrator, '') " +
                "FROM Books b " +
                "JOIN Authors a ON b.AuthorId = a.Id " +
                "WHERE b.Id = new.BookId; " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_ad AFTER DELETE ON Editions BEGIN " +
                "DELETE FROM edition_fts WHERE rowid = old.Id; " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_au AFTER UPDATE ON Editions BEGIN " +
                "DELETE FROM edition_fts WHERE rowid = old.Id; " +
                "INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator) " +
                "SELECT new.Id, " +
                "COALESCE(new.MatchingTitle, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(new.Subtitle, ''), " +
                "COALESCE(new.Narrator, '') " +
                "FROM Books b " +
                "JOIN Authors a ON b.AuthorId = a.Id " +
                "WHERE b.Id = new.BookId; " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_book_au AFTER UPDATE OF SeriesName ON Books BEGIN " +
                "DELETE FROM edition_fts WHERE rowid IN (SELECT Id FROM Editions WHERE BookId = new.Id); " +
                "INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.MatchingTitle, ''), " +
                "COALESCE(new.SeriesName, ''), " +
                "COALESCE(a.Name, ''), " +
                "COALESCE(e.Subtitle, ''), " +
                "COALESCE(e.Narrator, '') " +
                "FROM Editions e " +
                "JOIN Authors a ON new.AuthorId = a.Id " +
                "WHERE e.BookId = new.Id; " +
                "END;"
            );

            IfDatabase("sqlite").Execute.Sql(
                "CREATE TRIGGER edition_fts_author_au AFTER UPDATE OF Name ON Authors BEGIN " +
                "DELETE FROM edition_fts WHERE rowid IN (" +
                "SELECT e.Id FROM Editions e " +
                "JOIN Books b ON e.BookId = b.Id " +
                "WHERE b.AuthorId = new.Id); " +
                "INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator) " +
                "SELECT e.Id, " +
                "COALESCE(e.MatchingTitle, ''), " +
                "COALESCE(b.SeriesName, ''), " +
                "COALESCE(new.Name, ''), " +
                "COALESCE(e.Subtitle, ''), " +
                "COALESCE(e.Narrator, '') " +
                "FROM Editions e " +
                "JOIN Books b ON e.BookId = b.Id " +
                "WHERE b.AuthorId = new.Id; " +
                "END;"
            );
        }
    }
}

