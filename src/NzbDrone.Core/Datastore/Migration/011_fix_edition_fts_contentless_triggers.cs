using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(011)]
    public class FixEditionFtsContentlessTriggers : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Drop all broken triggers that use DELETE on contentless FTS5 table
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_book_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_author_au;");

            // For contentless FTS5, must use INSERT ... VALUES('delete', ...) with OLD values
            // instead of DELETE FROM. The OLD values are needed so FTS knows which tokens to remove.

            // edition_fts_au: AFTER UPDATE ON Editions
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_au AFTER UPDATE ON Editions BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                    SELECT 'delete', old.Id, COALESCE(old.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(old.Subtitle, ''), COALESCE(old.Narrator, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = old.BookId;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                    SELECT new.Id, COALESCE(new.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(new.Subtitle, ''), COALESCE(new.Narrator, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = new.BookId;
                END;
            ");

            // edition_fts_ad: AFTER DELETE ON Editions
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_ad AFTER DELETE ON Editions BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                    SELECT 'delete', old.Id, COALESCE(old.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(old.Subtitle, ''), COALESCE(old.Narrator, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = old.BookId;
                END;
            ");

            // edition_fts_book_au: AFTER UPDATE OF SeriesName ON Books
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_book_au AFTER UPDATE OF SeriesName ON Books BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                    SELECT 'delete', e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(old.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(e.Subtitle, ''), COALESCE(e.Narrator, '')
                    FROM Editions e JOIN Authors a ON old.AuthorId = a.Id WHERE e.BookId = old.Id;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                    SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(new.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(e.Subtitle, ''), COALESCE(e.Narrator, '')
                    FROM Editions e JOIN Authors a ON new.AuthorId = a.Id WHERE e.BookId = new.Id;
                END;
            ");

            // edition_fts_author_au: AFTER UPDATE OF Name ON Authors
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_author_au AFTER UPDATE OF Name ON Authors BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                    SELECT 'delete', e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(old.Name, ''), COALESCE(e.Subtitle, ''), COALESCE(e.Narrator, '')
                    FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = old.Id;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                    SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(new.Name, ''), COALESCE(e.Subtitle, ''), COALESCE(e.Narrator, '')
                    FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = new.Id;
                END;
            ");
        }
    }
}
