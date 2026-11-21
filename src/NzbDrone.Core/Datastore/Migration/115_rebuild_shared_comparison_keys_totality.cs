using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(115)]
    public class rebuild_shared_comparison_keys_totality : rebuild_shared_comparison_keys
    {
        // Historical migrations retain their DDL so later maintenance cannot change an upgrade.
        private const string SqliteProjection = @"
            INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
            SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SearchSeriesName, ''),
                   COALESCE(a.SearchName, ''), COALESCE(e.SearchSubtitle, ''),
                   COALESCE(NULLIF(e.SearchNarratorNames, ''), e.SearchNarrator, ''), COALESCE(e.SearchPublisher, '')
            FROM Editions e JOIN Books b ON e.BookId = b.Id JOIN Authors a ON b.AuthorId = a.Id";

        private static readonly (string Name, string Event, string Delete, string Where, string When)[] SqliteTriggers =
        {
            ("ai", "AFTER INSERT ON Editions", "", "e.Id = new.Id", null),
            ("ad", "AFTER DELETE ON Editions", "DELETE FROM edition_fts WHERE rowid = old.Id;", null, null),
            ("au", "AFTER UPDATE OF Id, BookId, MatchingTitle, SearchSubtitle, SearchNarratorNames, SearchNarrator, SearchPublisher ON Editions",
                "DELETE FROM edition_fts WHERE rowid = old.Id;", "e.Id = new.Id",
                "old.Id IS NOT new.Id OR old.BookId IS NOT new.BookId OR COALESCE(old.MatchingTitle, '') IS NOT COALESCE(new.MatchingTitle, '') OR COALESCE(old.SearchSubtitle, '') IS NOT COALESCE(new.SearchSubtitle, '') OR COALESCE(old.SearchNarratorNames, '') IS NOT COALESCE(new.SearchNarratorNames, '') OR COALESCE(old.SearchNarrator, '') IS NOT COALESCE(new.SearchNarrator, '') OR COALESCE(old.SearchPublisher, '') IS NOT COALESCE(new.SearchPublisher, '')"),
            ("book_au", "AFTER UPDATE OF SearchSeriesName, AuthorId ON Books",
                "DELETE FROM edition_fts WHERE rowid IN (SELECT Id FROM Editions WHERE BookId = old.Id);", "e.BookId = new.Id",
                "old.SearchSeriesName IS NOT new.SearchSeriesName OR old.AuthorId IS NOT new.AuthorId"),
            ("author_au", "AFTER UPDATE OF SearchName ON Authors",
                "DELETE FROM edition_fts WHERE rowid IN (SELECT e.Id FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = old.Id);", "b.AuthorId = new.Id",
                "old.SearchName IS NOT new.SearchName")
        };

        protected override void MainDbUpgrade()
        {
            Alter.Table("Authors").AddColumn("SearchName").AsString(int.MaxValue).Nullable();
            Alter.Table("Books").AddColumn("SearchSeriesName").AsString(int.MaxValue).Nullable();
            Alter.Table("Editions").AddColumn("SearchSubtitle").AsString(int.MaxValue).Nullable();
            Alter.Table("Editions").AddColumn("SearchNarratorNames").AsString(int.MaxValue).Nullable();
            Alter.Table("Editions").AddColumn("SearchNarrator").AsString(int.MaxValue).Nullable();
            Alter.Table("Editions").AddColumn("SearchPublisher").AsString(int.MaxValue).Nullable();
            IfDatabase("sqlite").Execute.WithConnection((connection, transaction) =>
            {
                foreach (var trigger in SqliteTriggers)
                {
                    connection.Execute($"DROP TRIGGER IF EXISTS edition_fts_{trigger.Name};", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                }

                connection.Execute("DROP TRIGGER IF EXISTS author_fts_ai; DROP TRIGGER IF EXISTS author_fts_ad; DROP TRIGGER IF EXISTS author_fts_au;", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
            });
            IfPostgres().Execute.WithConnection((connection, transaction) =>
            {
                connection.Execute(@"
                    DROP INDEX IF EXISTS idx_authors_fts;
                    DROP INDEX IF EXISTS idx_books_series_fts;
                    DROP INDEX IF EXISTS idx_editions_matching_fts;
                    DROP INDEX IF EXISTS idx_editions_ebook_fts;", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
            });
            base.MainDbUpgrade();
            Execute.WithConnection((connection, transaction) =>
            {
                (string Table, (string Source, string Key)[] Fields)[] rules =
                {
                    ("Authors", new[] { ("Name", "SearchName") }),
                    ("Books", new[] { ("SeriesName", "SearchSeriesName") }),
                    ("Editions", new[] { ("Subtitle", "SearchSubtitle"), ("Narrator", "SearchNarrator"),
                        ("NarratorNames", "SearchNarratorNames"), ("Publisher", "SearchPublisher") })
                };
                foreach (var rule in rules)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var batchSize = SqliteVariableLimit.MaxParameters / (rule.Fields.Length + 1);
                    var columns = rule.Fields.SelectMany((field, index) => new[]
                    {
                        $"\"{field.Source}\" AS \"Source{index}\"", $"\"{field.Key}\" AS \"Existing{index}\""
                    });
                    var afterId = 0;
                    long scanned = 0;
                    long changed = 0;
                    var reads = 0;
                    var writes = 0;
                    while (true)
                    {
                        var pageTimer = Stopwatch.StartNew();
                        var rows = connection.Query($"SELECT \"Id\", {string.Join(", ", columns)} FROM \"{rule.Table}\" WHERE \"Id\" > @AfterId ORDER BY \"Id\" LIMIT @BatchSize;",
                            new { AfterId = afterId, BatchSize = batchSize }, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds).Cast<IDictionary<string, object>>().ToList();
                        reads++;
                        var parameters = new DynamicParameters();
                        var cases = rule.Fields.Select(_ => new List<string>()).ToArray();
                        var ids = new List<string>();
                        foreach (var row in rows)
                        {
                            var rowIndex = ids.Count;
                            var updated = false;
                            for (var fieldIndex = 0; fieldIndex < rule.Fields.Length; fieldIndex++)
                            {
                                var source = row[$"Source{fieldIndex}"] as string;
                                var value = rule.Fields[fieldIndex].Key == "SearchNarratorNames"
                                    ? StringSuperNormalizer.NormalizeNarratorsForFts(ReadCast(source))
                                    : StringSuperNormalizer.NormalizeForFts(source);
                                // Empty nullable projections and strings have the same indexed/query value.
                                if (string.Equals(value, row[$"Existing{fieldIndex}"] as string ?? string.Empty, StringComparison.Ordinal)) continue;
                                parameters.Add($"Value{fieldIndex}_{rowIndex}", value);
                                cases[fieldIndex].Add($"WHEN @Id{rowIndex} THEN @Value{fieldIndex}_{rowIndex}");
                                updated = true;
                            }

                            if (updated)
                            {
                                parameters.Add($"Id{rowIndex}", row["Id"]);
                                ids.Add($"@Id{rowIndex}");
                            }
                        }

                        var pageChanged = 0;
                        if (ids.Count > 0)
                        {
                            var assignments = rule.Fields.Select((field, index) => cases[index].Count == 0 ? null
                                : $"\"{field.Key}\" = CASE \"Id\" {string.Join(" ", cases[index])} ELSE \"{field.Key}\" END").Where(value => value != null);
                            pageChanged = connection.Execute($"UPDATE \"{rule.Table}\" SET {string.Join(", ", assignments)} WHERE \"Id\" IN ({string.Join(",", ids)});", parameters, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                            changed += pageChanged;
                            writes++;
                        }

                        scanned += rows.Count;
                        _logger.Debug("Folded search rebuild {0} page {1}: scanned {2}, updated rows {3}, elapsed {4} ms",
                            rule.Table, reads, rows.Count, pageChanged, pageTimer.ElapsedMilliseconds);
                        if (reads % 100 == 0)
                        {
                            _logger.Info("Folded search rebuild {0} progress: scanned {1}, updated rows {2}, pages {3}, elapsed {4} ms",
                                rule.Table, scanned, changed, reads, stopwatch.ElapsedMilliseconds);
                        }

                        if (rows.Count < batchSize) break;
                        afterId = Convert.ToInt32(rows[^1]["Id"]);
                    }

                    _logger.Info("Folded search rebuild {0}: scanned {1}, updated rows {2}, read queries {3}, update batches {4}, elapsed {5} ms",
                        rule.Table, scanned, changed, reads, writes, stopwatch.ElapsedMilliseconds);
                }
            });
            IfDatabase("sqlite").Execute.WithConnection((connection, transaction) =>
            {
                var stopwatch = Stopwatch.StartNew();
                connection.Execute(@"
                    DROP TABLE IF EXISTS edition_fts;
                    CREATE VIRTUAL TABLE edition_fts USING fts5(MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher,
                        content='', content_rowid='rowid', contentless_delete=1, tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                connection.Execute(SqliteProjection, transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                foreach (var trigger in SqliteTriggers)
                {
                    var populate = trigger.Where == null ? string.Empty : $"{SqliteProjection} WHERE {trigger.Where};";
                    var guard = trigger.When == null ? string.Empty : $" WHEN {trigger.When}";
                    connection.Execute($"CREATE TRIGGER edition_fts_{trigger.Name} {trigger.Event}{guard} BEGIN {trigger.Delete} {populate} END;", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                }

                connection.Execute(@"
                    DROP TRIGGER IF EXISTS author_fts_ai;
                    DROP TRIGGER IF EXISTS author_fts_ad;
                    DROP TRIGGER IF EXISTS author_fts_au;
                    DROP TABLE IF EXISTS author_fts;
                    CREATE VIRTUAL TABLE author_fts USING fts5(Name, SortName, TitleSlug, SearchName, content='Authors', content_rowid='Id',
                        tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');
                    INSERT INTO author_fts(rowid, Name, SortName, TitleSlug, SearchName) SELECT Id, COALESCE(Name, ''), COALESCE(SortName, ''), COALESCE(TitleSlug, ''), COALESCE(SearchName, '') FROM Authors;
                    CREATE TRIGGER author_fts_ai AFTER INSERT ON Authors BEGIN
                        INSERT INTO author_fts(rowid, Name, SortName, TitleSlug, SearchName) VALUES(new.Id, COALESCE(new.Name, ''), COALESCE(new.SortName, ''), COALESCE(new.TitleSlug, ''), COALESCE(new.SearchName, '')); END;
                    CREATE TRIGGER author_fts_ad AFTER DELETE ON Authors BEGIN
                        INSERT INTO author_fts(author_fts, rowid, Name, SortName, TitleSlug, SearchName) VALUES('delete', old.Id, COALESCE(old.Name, ''), COALESCE(old.SortName, ''), COALESCE(old.TitleSlug, ''), COALESCE(old.SearchName, '')); END;
                    CREATE TRIGGER author_fts_au AFTER UPDATE ON Authors BEGIN
                        INSERT INTO author_fts(author_fts, rowid, Name, SortName, TitleSlug, SearchName) VALUES('delete', old.Id, COALESCE(old.Name, ''), COALESCE(old.SortName, ''), COALESCE(old.TitleSlug, ''), COALESCE(old.SearchName, ''));
                        INSERT INTO author_fts(rowid, Name, SortName, TitleSlug, SearchName) VALUES(new.Id, COALESCE(new.Name, ''), COALESCE(new.SortName, ''), COALESCE(new.TitleSlug, ''), COALESCE(new.SearchName, '')); END;", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                _logger.Info("Rebuilt folded SQLite search indexes in {0} ms", stopwatch.ElapsedMilliseconds);
            });
            IfPostgres().Execute.WithConnection((connection, transaction) =>
            {
                var indexes = new[]
                {
                    (Name: "idx_authors_fts", Sql: @"CREATE INDEX idx_authors_fts ON ""Authors"" USING GIN (to_tsvector('simple', COALESCE(""SearchName"", '') || ' ' || COALESCE(""CleanName"", '') || ' ' || COALESCE(""TitleSlug"", '')));"),
                    (Name: "idx_books_series_fts", Sql: @"CREATE INDEX idx_books_series_fts ON ""Books"" USING GIN (to_tsvector('simple', COALESCE(""SearchSeriesName"", '')));"),
                    (Name: "idx_editions_narrator_fts", Sql: @"CREATE INDEX idx_editions_narrator_fts ON ""Editions"" USING GIN (to_tsvector('simple', COALESCE(NULLIF(""SearchNarratorNames"", ''), ""SearchNarrator"", '')));"),
                    (Name: "idx_editions_publisher_fts", Sql: @"CREATE INDEX idx_editions_publisher_fts ON ""Editions"" USING GIN (to_tsvector('simple', COALESCE(""SearchPublisher"", '')));")
                };
                foreach (var index in indexes)
                {
                    var stopwatch = Stopwatch.StartNew();
                    connection.Execute(index.Sql, transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                    _logger.Info("Built PostgreSQL search index {0} in {1} ms", index.Name, stopwatch.ElapsedMilliseconds);
                }
            });
        }

        private static string[] ReadCast(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                using var document = JsonDocument.Parse(value);
                return document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().Where(name => name.ValueKind != JsonValueKind.Null).Select(name => name.ToString()).ToArray()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
