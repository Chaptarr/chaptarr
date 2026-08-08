using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(16)]
    public class add_publisher_to_edition_fts : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Add Publisher column to edition_fts for ebook edition matching.
            // For ebooks, Publisher helps distinguish editions (like Narrator does for audiobooks).
            // Publisher is only used in Step 2 (edition selection), NOT Step 1 (book selection),
            // to avoid wrong-book pollution from common publisher strings.

            // =========================================================================
            // SQLite: Recreate edition_fts with Publisher column, update contentless triggers
            // =========================================================================

            // Drop all existing triggers
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ai;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_book_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_author_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_publisher_au;");

            // Drop and recreate FTS table with Publisher column
            IfDatabase("sqlite").Execute.Sql("DROP TABLE IF EXISTS edition_fts;");
            IfDatabase("sqlite").Execute.Sql(
                "CREATE VIRTUAL TABLE edition_fts USING fts5(" +
                "MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher, " +
                "content='', content_rowid='rowid', " +
                "tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');"
            );

            // Repopulate FTS index with Publisher data
            IfDatabase("sqlite").Execute.Sql(@"
                INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                SELECT e.Id,
                       COALESCE(e.MatchingTitle, ''),
                       COALESCE(b.SeriesName, ''),
                       COALESCE(a.Name, ''),
                       COALESCE(e.Subtitle, ''),
                       COALESCE(e.Narrator, ''),
                       COALESCE(e.Publisher, '')
                FROM Editions e
                JOIN Books b ON e.BookId = b.Id
                JOIN Authors a ON b.AuthorId = a.Id;
            ");

            // =========================================================================
            // Contentless FTS5 triggers: must use INSERT ... VALUES('delete', ...) with OLD values
            // =========================================================================

            // edition_fts_ai: AFTER INSERT ON Editions
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_ai AFTER INSERT ON Editions BEGIN
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT new.Id, COALESCE(new.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(new.Subtitle, ''), COALESCE(new.Narrator, ''),
                           COALESCE(new.Publisher, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = new.BookId;
                END;
            ");

            // edition_fts_ad: AFTER DELETE ON Editions
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_ad AFTER DELETE ON Editions BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT 'delete', old.Id, COALESCE(old.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(old.Subtitle, ''), COALESCE(old.Narrator, ''),
                           COALESCE(old.Publisher, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = old.BookId;
                END;
            ");

            // edition_fts_au: AFTER UPDATE ON Editions
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_au AFTER UPDATE ON Editions BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT 'delete', old.Id, COALESCE(old.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(old.Subtitle, ''), COALESCE(old.Narrator, ''),
                           COALESCE(old.Publisher, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = old.BookId;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT new.Id, COALESCE(new.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(new.Subtitle, ''), COALESCE(new.Narrator, ''),
                           COALESCE(new.Publisher, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = new.BookId;
                END;
            ");

            // edition_fts_book_au: AFTER UPDATE OF SeriesName ON Books
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_book_au AFTER UPDATE OF SeriesName ON Books BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT 'delete', e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(old.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(e.Subtitle, ''), COALESCE(e.Narrator, ''),
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Authors a ON old.AuthorId = a.Id WHERE e.BookId = old.Id;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(new.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(e.Subtitle, ''), COALESCE(e.Narrator, ''),
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Authors a ON new.AuthorId = a.Id WHERE e.BookId = new.Id;
                END;
            ");

            // edition_fts_author_au: AFTER UPDATE OF Name ON Authors
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_author_au AFTER UPDATE OF Name ON Authors BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT 'delete', e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(old.Name, ''), COALESCE(e.Subtitle, ''), COALESCE(e.Narrator, ''),
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = old.Id;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(new.Name, ''), COALESCE(e.Subtitle, ''), COALESCE(e.Narrator, ''),
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = new.Id;
                END;
            ");

            // NOTE: edition_fts_publisher_au NOT NEEDED - edition_fts_au AFTER UPDATE ON Editions
            // already handles Publisher updates. Creating a separate trigger would cause double
            // delete/insert cycles for the same rowid, skewing BM25 and adding wasted work.

            // =========================================================================
            // PostgreSQL: Add new GIN index for ebook matching with Publisher
            // Existing idx_editions_matching_fts is for audiobooks (MatchingTitle + Subtitle + Narrator)
            // New idx_editions_ebook_fts is for ebooks (MatchingTitle + Publisher)
            // NOTE: Subtitle excluded from Step 2 for accuracy, so index excludes it too
            // (Subtitle should only affect edition selection WITHIN a book, not ranking)
            // =========================================================================
            IfPostgres().Execute.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_editions_ebook_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple',
                        COALESCE(""MatchingTitle"", '') || ' ' ||
                        COALESCE(""Publisher"", '')
                    )
                );
            ");
        }
    }
}
