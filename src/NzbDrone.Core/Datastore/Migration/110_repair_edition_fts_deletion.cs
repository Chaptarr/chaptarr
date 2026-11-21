using System.Diagnostics;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(110)]
    public class repair_edition_fts_deletion : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfDatabase("sqlite").Execute.WithConnection((connection, transaction) =>
            {
                var stopwatch = Stopwatch.StartNew();
                // Reuse one projection for rebuilding and every insert/update trigger.
                // Deletes must need only the row ID, even after a parent has disappeared.
                const string insert = @"
                    INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                    SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SeriesName, ''),
                           COALESCE(a.Name, ''), COALESCE(e.Subtitle, ''),
                           CASE
                               WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames)
                                                  WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
                               ELSE COALESCE(e.Narrator, '')
                           END,
                           COALESCE(e.Publisher, '')
                    FROM Editions e JOIN Books b ON e.BookId = b.Id JOIN Authors a ON b.AuthorId = a.Id";

                // Parent-derived search values should only be refreshed when their source values change.
                var triggers = new[]
                {
                    (Name: "ai", Event: "AFTER INSERT ON Editions", Delete: "", Where: "e.Id = new.Id", When: (string)null),
                    (Name: "ad", Event: "AFTER DELETE ON Editions",
                        Delete: "DELETE FROM edition_fts WHERE rowid = old.Id;", Where: (string)null, When: (string)null),
                    (Name: "au", Event: "AFTER UPDATE ON Editions",
                        Delete: "DELETE FROM edition_fts WHERE rowid = old.Id;", Where: "e.Id = new.Id", When: (string)null),
                    (Name: "book_au", Event: "AFTER UPDATE OF SeriesName, AuthorId ON Books",
                        Delete: "DELETE FROM edition_fts WHERE rowid IN (SELECT Id FROM Editions WHERE BookId = old.Id);",
                        Where: "e.BookId = new.Id", When: "old.SeriesName IS NOT new.SeriesName OR old.AuthorId IS NOT new.AuthorId"),
                    (Name: "author_au", Event: "AFTER UPDATE OF Name ON Authors",
                        Delete: "DELETE FROM edition_fts WHERE rowid IN (SELECT e.Id FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = old.Id);",
                        Where: "b.AuthorId = new.Id", When: "old.Name IS NOT new.Name")
                };

                foreach (var trigger in triggers)
                {
                    connection.Execute($"DROP TRIGGER IF EXISTS edition_fts_{trigger.Name};", transaction: transaction);
                }

                connection.Execute(@"
                    DROP TABLE IF EXISTS edition_fts;
                    CREATE VIRTUAL TABLE edition_fts USING fts5(
                        MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher,
                        content='', content_rowid='rowid', contentless_delete=1,
                        tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');", transaction: transaction);
                connection.Execute(insert, transaction: transaction);

                foreach (var trigger in triggers)
                {
                    var populate = trigger.Where == null ? string.Empty : $"{insert} WHERE {trigger.Where};";
                    var guard = trigger.When == null ? string.Empty : $" WHEN {trigger.When}";
                    connection.Execute($"CREATE TRIGGER edition_fts_{trigger.Name} {trigger.Event}{guard} BEGIN {trigger.Delete} {populate} END;", transaction: transaction);
                }

                var indexed = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM edition_fts;", transaction: transaction);
                _logger.Info("Rebuilt edition search index with {0} entries and row-ID deletion in {1} ms", indexed, stopwatch.ElapsedMilliseconds);
            });
        }
    }
}
