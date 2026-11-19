using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Data;
using System.Linq;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(127)]
    public class unique_book_slugs_by_format : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                var timer = Stopwatch.StartNew();
                var rows = connection.Query<SlugRow>(@"SELECT ""Id"", ""MediaType"", ""TitleSlug"",
                    CASE WHEN ""MediaType"" = 0 THEN ""AudiobookMonitored"" ELSE ""EbookMonitored"" END AS ""Monitored""
                    FROM ""Books"" WHERE ""TitleSlug"" IS NOT NULL", transaction: transaction,
                    commandTimeout: MigrationController.CommandTimeoutSeconds).ToList();
                // One canonical Unicode casing on both engines (SQLite lower is ASCII-only).
                var reserved = rows.Select(row => (row.MediaType, row.TitleSlug.ToLowerInvariant())).ToHashSet();
                var changed = new List<SlugRow>();
                var duplicates = 0;
                foreach (var group in rows.GroupBy(row => (row.MediaType, Slug: row.TitleSlug.ToLowerInvariant())))
                {
                    var first = true;
                    var suffix = 2;
                    foreach (var row in group.OrderByDescending(row => row.Monitored).ThenBy(row => row.Id))
                    {
                        var slug = group.Key.Slug;
                        if (!first)
                        {
                            while (!reserved.Add((row.MediaType, slug = $"{group.Key.Slug}_{suffix++}"))) { }
                            duplicates++;
                        }
                        first = false;
                        if (slug == row.TitleSlug) continue;
                        _logger.Debug("[BOOK-SLUG] Migration book {0} (media {1}): '{2}' -> '{3}'", row.Id, row.MediaType, row.TitleSlug, slug);
                        row.TitleSlug = slug;
                        changed.Add(row);
                    }
                }

                foreach (var chunk in changed.Chunk(SqliteVariableLimit.MaxParameters / 2))
                {
                    var parameters = new DynamicParameters();
                    var cases = new List<string>();
                    var ids = new List<string>();
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        parameters.Add($"id{i}", chunk[i].Id);
                        parameters.Add($"slug{i}", chunk[i].TitleSlug);
                        cases.Add($"WHEN @id{i} THEN @slug{i}");
                        ids.Add($"@id{i}");
                    }
                    connection.Execute($"UPDATE \"Books\" SET \"TitleSlug\" = CASE \"Id\" {string.Join(" ", cases)} END WHERE \"Id\" IN ({string.Join(",", ids)})",
                        parameters, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                }
                _logger.Info("[BOOK-SLUG] Checked {0} rows, disambiguated {1}, updated {2} in {3} ms", rows.Count, duplicates, changed.Count, timer.ElapsedMilliseconds);
            });
            IfDatabase("SQLite").Execute.WithConnection((connection, transaction) => CreateIndex(connection, transaction, false));
            IfPostgres().Execute.WithConnection((connection, transaction) => CreateIndex(connection, transaction, true));
        }

        private void CreateIndex(IDbConnection connection, IDbTransaction transaction, bool postgres)
        {
            var timer = Stopwatch.StartNew();
            var collation = postgres ? " COLLATE \"C\"" : string.Empty;
            connection.Execute($"CREATE UNIQUE INDEX \"IX_Books_MediaType_TitleSlug\" ON \"Books\" (\"MediaType\", lower(\"TitleSlug\"){collation});",
                transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
            _logger.Info("[BOOK-SLUG] Built unique link index in {0} ms", timer.ElapsedMilliseconds);
        }

        private sealed class SlugRow
        {
            public int Id { get; set; }
            public int MediaType { get; set; }
            public string TitleSlug { get; set; }
            public bool Monitored { get; set; }
        }
    }
}
