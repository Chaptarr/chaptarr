using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(113)]
    public class rebuild_shared_comparison_keys : NzbDroneMigrationBase
    {
        // Reuse the shared provider bound; every changed row contributes an ID and a key.
        private const int BatchSize = SqliteVariableLimit.MaxParameters / 2;

        private sealed class KeyRow
        {
            public int Id { get; set; }
            public string SourceValue { get; set; }
            public string ExistingValue { get; set; }
        }

        private sealed class CollisionCounts
        {
            public long Groups { get; set; }
            public long Rows { get; set; }
            public long LargestGroup { get; set; }
        }

        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                var stopwatch = Stopwatch.StartNew();
                var rules = new (string Table, string Key, string SelectSql, string GroupBy, Func<string, string> Normalize)[]
                {
                    ("Books", "CleanTitle",
                        "SELECT \"Id\", \"Title\" AS \"SourceValue\", \"CleanTitle\" AS \"ExistingValue\" FROM \"Books\" WHERE \"Id\" > @AfterId ORDER BY \"Id\" LIMIT @BatchSize;",
                        "\"AuthorId\", \"MediaType\", \"CleanTitle\"",
                        value => (value ?? string.Empty).CleanBookTitle().CleanAuthorName()),
                    ("Authors", "CleanName",
                        "SELECT \"Id\", \"Name\" AS \"SourceValue\", \"CleanName\" AS \"ExistingValue\" FROM \"Authors\" WHERE \"Id\" > @AfterId ORDER BY \"Id\" LIMIT @BatchSize;",
                        "\"CleanName\"",
                        value => (value ?? string.Empty).CleanAuthorName()),
                    ("Narrators", "CleanName",
                        "SELECT n.\"Id\", m.\"Name\" AS \"SourceValue\", n.\"CleanName\" AS \"ExistingValue\" FROM \"Narrators\" n JOIN \"NarratorMetadata\" m ON m.\"Id\" = n.\"NarratorMetadataId\" WHERE n.\"Id\" > @AfterId ORDER BY n.\"Id\" LIMIT @BatchSize;",
                        "\"CleanName\"",
                        value => (value ?? string.Empty).CleanNarratorName()),
                    ("Editions", "MatchingTitle",
                        "SELECT \"Id\", \"Title\" AS \"SourceValue\", \"MatchingTitle\" AS \"ExistingValue\" FROM \"Editions\" WHERE \"Id\" > @AfterId ORDER BY \"Id\" LIMIT @BatchSize;",
                        "\"BookId\", \"MatchingTitle\"",
                        StringSuperNormalizer.ComputeMatchingTitle)
                };

                long totalScanned = 0;
                long totalChanged = 0;
                foreach (var (table, key, selectSql, groupBy, normalize) in rules)
                {
                    var tableStopwatch = Stopwatch.StartNew();
                    var afterId = 0;
                    long scanned = 0;
                    long changed = 0;
                    var reads = 0;
                    var writes = 0;
                    _logger.Info("Comparison-key rebuild started: {0}.{1}", table, key);
                    while (true)
                    {
                        var batchStopwatch = Stopwatch.StartNew();
                        var rows = connection.Query<KeyRow>(selectSql, new { AfterId = afterId, BatchSize }, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds).ToList();
                        reads++;
                        var updates = new List<(int Id, string Value)>();
                        foreach (var row in rows)
                        {
                            var value = normalize(row.SourceValue);
                            if (!string.Equals(value, row.ExistingValue, StringComparison.Ordinal))
                            {
                                updates.Add((row.Id, value));
                            }
                        }

                        var batchChanged = 0;
                        if (updates.Count > 0)
                        {
                            var parameters = new DynamicParameters();
                            var cases = new List<string>(updates.Count);
                            var ids = new List<string>(updates.Count);
                            for (var index = 0; index < updates.Count; index++)
                            {
                                parameters.Add($"Id{index}", updates[index].Id);
                                parameters.Add($"Value{index}", updates[index].Value);
                                cases.Add($"WHEN @Id{index} THEN @Value{index}");
                                ids.Add($"@Id{index}");
                            }

                            batchChanged = connection.Execute($"UPDATE \"{table}\" SET \"{key}\" = CASE \"Id\" {string.Join(" ", cases)} END WHERE \"Id\" IN ({string.Join(",", ids)});", parameters, transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                            changed += batchChanged;
                            writes++;
                        }

                        scanned += rows.Count;
                        _logger.Debug("Comparison-key rebuild {0}.{1} page {2}: scanned {3}, updated {4}, elapsed {5} ms",
                            table, key, reads, rows.Count, batchChanged, batchStopwatch.ElapsedMilliseconds);
                        if (reads % 100 == 0)
                        {
                            _logger.Info("Comparison-key rebuild {0}.{1} progress: scanned {2}, updated {3}, pages {4}, elapsed {5} ms",
                                table, key, scanned, changed, reads, tableStopwatch.ElapsedMilliseconds);
                        }

                        if (rows.Count < BatchSize)
                        {
                            break;
                        }

                        afterId = rows[^1].Id;
                    }

                    // Equal retrieval keys do not establish identity; retain every row and report only aggregate ambiguity.
                    var collisions = connection.QuerySingle<CollisionCounts>($@"
                        SELECT COUNT(*) AS ""Groups"", COALESCE(SUM(c.""Count""), 0) AS ""Rows"", COALESCE(MAX(c.""Count""), 0) AS ""LargestGroup""
                        FROM (SELECT COUNT(*) AS ""Count"" FROM ""{table}""
                              WHERE ""{key}"" IS NOT NULL AND ""{key}"" <> ''
                              GROUP BY {groupBy} HAVING COUNT(*) > 1) c;", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                    totalScanned += scanned;
                    totalChanged += changed;
                    _logger.Info("Comparison-key rebuild {0}.{1}: scanned {2}, updated {3}, read queries {4}, update batches {5}, collision groups {6}, colliding rows {7}, largest group {8}, elapsed {9} ms",
                        table, key, scanned, changed, reads + 1, writes, collisions.Groups, collisions.Rows, collisions.LargestGroup, tableStopwatch.ElapsedMilliseconds);
                }

                _logger.Info("Comparison-key rebuild processed: scanned {0}, updated {1} in {2} ms", totalScanned, totalChanged, stopwatch.ElapsedMilliseconds);
            });
        }
    }
}
