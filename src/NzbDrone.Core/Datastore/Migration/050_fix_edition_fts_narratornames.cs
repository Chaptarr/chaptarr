using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(50)]
    public class fix_edition_fts_narratornames : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // SQLite: edition_fts is a contentless FTS5 table. The existing triggers index Editions.Narrator (legacy single string),
            // which hides additional narrators stored in Editions.NarratorNames (JSON array). Rebuild the index and update triggers
            // to index the full narrator list.

            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ai;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_ad;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_book_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_author_au;");
            IfDatabase("sqlite").Execute.Sql("DROP TRIGGER IF EXISTS edition_fts_publisher_au;");

            // Clear and repopulate FTS index content with NarratorNames joined.
            IfDatabase("sqlite").Execute.Sql("INSERT INTO edition_fts(edition_fts) VALUES('delete-all');");
            IfDatabase("sqlite").Execute.Sql(@"
                INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                SELECT e.Id,
                       COALESCE(e.MatchingTitle, ''),
                       COALESCE(b.SeriesName, ''),
                       COALESCE(a.Name, ''),
                       COALESCE(e.Subtitle, ''),
                       CASE
                           WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
                               THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
                           ELSE COALESCE(e.Narrator, '')
                       END,
                       COALESCE(e.Publisher, '')
                FROM Editions e
                JOIN Books b ON e.BookId = b.Id
                JOIN Authors a ON b.AuthorId = a.Id;
            ");

            // edition_fts_ai: AFTER INSERT ON Editions
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_ai AFTER INSERT ON Editions BEGIN
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT new.Id, COALESCE(new.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(new.Subtitle, ''),
                           CASE
                               WHEN json_valid(new.NarratorNames) AND json_type(new.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(new.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(new.Narrator, ''))
                               ELSE COALESCE(new.Narrator, '')
                           END,
                           COALESCE(new.Publisher, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = new.BookId;
                END;
            ");

            // edition_fts_ad: AFTER DELETE ON Editions
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_ad AFTER DELETE ON Editions BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT 'delete', old.Id, COALESCE(old.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(old.Subtitle, ''),
                           CASE
                               WHEN json_valid(old.NarratorNames) AND json_type(old.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(old.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(old.Narrator, ''))
                               ELSE COALESCE(old.Narrator, '')
                           END,
                           COALESCE(old.Publisher, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = old.BookId;
                END;
            ");

            // edition_fts_au: AFTER UPDATE ON Editions
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_au AFTER UPDATE ON Editions BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT 'delete', old.Id, COALESCE(old.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(old.Subtitle, ''),
                           CASE
                               WHEN json_valid(old.NarratorNames) AND json_type(old.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(old.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(old.Narrator, ''))
                               ELSE COALESCE(old.Narrator, '')
                           END,
                           COALESCE(old.Publisher, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = old.BookId;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT new.Id, COALESCE(new.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(new.Subtitle, ''),
                           CASE
                               WHEN json_valid(new.NarratorNames) AND json_type(new.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(new.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(new.Narrator, ''))
                               ELSE COALESCE(new.Narrator, '')
                           END,
                           COALESCE(new.Publisher, '')
                    FROM Books b JOIN Authors a ON b.AuthorId = a.Id WHERE b.Id = new.BookId;
                END;
            ");

            // edition_fts_book_au: AFTER UPDATE OF SeriesName ON Books
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_book_au AFTER UPDATE OF SeriesName ON Books BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT 'delete', e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(old.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(e.Subtitle, ''),
                           CASE
                               WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
                               ELSE COALESCE(e.Narrator, '')
                           END,
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Authors a ON old.AuthorId = a.Id WHERE e.BookId = old.Id;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(new.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(e.Subtitle, ''),
                           CASE
                               WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
                               ELSE COALESCE(e.Narrator, '')
                           END,
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Authors a ON new.AuthorId = a.Id WHERE e.BookId = new.Id;
                END;
            ");

            // edition_fts_author_au: AFTER UPDATE OF Name ON Authors
            IfDatabase("sqlite").Execute.Sql(@"
                CREATE TRIGGER edition_fts_author_au AFTER UPDATE OF Name ON Authors BEGIN
                  INSERT INTO edition_fts(edition_fts, rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT 'delete', e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(old.Name, ''), COALESCE(e.Subtitle, ''),
                           CASE
                               WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
                               ELSE COALESCE(e.Narrator, '')
                           END,
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = old.Id;
                  INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(new.Name, ''), COALESCE(e.Subtitle, ''),
                           CASE
                               WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
                               ELSE COALESCE(e.Narrator, '')
                           END,
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = new.Id;
                END;
            ");

            // PostgreSQL: keep GIN index aligned with matching query (include NarratorNames for multi-narrator audiobooks).
            IfPostgres().Execute.Sql(@"
                DROP INDEX IF EXISTS idx_editions_matching_fts;
                CREATE INDEX IF NOT EXISTS idx_editions_matching_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple',
                        COALESCE(""MatchingTitle"", '') || ' ' ||
                        COALESCE(""Subtitle"", '') || ' ' ||
                        COALESCE(""NarratorNames"", '') || ' ' ||
                        COALESCE(""Narrator"", '')
                    )
                );
            ");
        }
    }
}
