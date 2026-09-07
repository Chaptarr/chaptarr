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
    [Migration(108)]
    public class normalize_comparison_keys : NzbDroneMigrationBase
    {
        private sealed class KeyRow
        {
            public int Id { get; set; }
            public string SourceValue { get; set; }
            public string ExistingValue { get; set; }
        }

        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                var stopwatch = Stopwatch.StartNew();
                var rules = new (string Name, string SelectSql, string UpdateSql, Func<string, string> Normalize)[]
                {
                    ("Books.CleanTitle",
                        "SELECT \"Id\", \"Title\" AS \"SourceValue\", \"CleanTitle\" AS \"ExistingValue\" FROM \"Books\";",
                        "UPDATE \"Books\" SET \"CleanTitle\" = @Value WHERE \"Id\" = @Id;",
                        value => (value ?? string.Empty).CleanBookTitle().CleanAuthorName()),
                    ("Authors.CleanName",
                        "SELECT \"Id\", \"Name\" AS \"SourceValue\", \"CleanName\" AS \"ExistingValue\" FROM \"Authors\";",
                        "UPDATE \"Authors\" SET \"CleanName\" = @Value WHERE \"Id\" = @Id;",
                        value => (value ?? string.Empty).CleanAuthorName()),
                    ("Narrators.CleanName",
                        "SELECT n.\"Id\", m.\"Name\" AS \"SourceValue\", n.\"CleanName\" AS \"ExistingValue\" FROM \"Narrators\" n JOIN \"NarratorMetadata\" m ON m.\"Id\" = n.\"NarratorMetadataId\";",
                        "UPDATE \"Narrators\" SET \"CleanName\" = @Value WHERE \"Id\" = @Id;",
                        value => (value ?? string.Empty).CleanNarratorName()),
                    ("Editions.MatchingTitle",
                        "SELECT \"Id\", \"Title\" AS \"SourceValue\", \"MatchingTitle\" AS \"ExistingValue\" FROM \"Editions\";",
                        "UPDATE \"Editions\" SET \"MatchingTitle\" = @Value WHERE \"Id\" = @Id;",
                        value => StringSuperNormalizer.ComputeMatchingTitle(value))
                };

                var scanned = 0;
                var changed = 0;
                foreach (var (name, selectSql, updateSql, normalize) in rules)
                {
                    var rows = connection.Query<KeyRow>(selectSql, transaction: transaction).ToList();
                    var updates = new List<object>();
                    foreach (var row in rows)
                    {
                        var value = normalize(row.SourceValue);
                        if (!string.Equals(value, row.ExistingValue, StringComparison.Ordinal))
                        {
                            updates.Add(new { row.Id, Value = value });
                        }
                    }

                    if (updates.Count > 0)
                    {
                        connection.Execute(updateSql, updates, transaction);
                    }

                    scanned += rows.Count;
                    changed += updates.Count;
                    _logger.Info("Comparison-key migration {0}: scanned {1}, updated {2}", name, rows.Count, updates.Count);
                }

                stopwatch.Stop();
                _logger.Info("Comparison-key migration completed: scanned {0}, updated {1} in {2} ms", scanned, changed, stopwatch.ElapsedMilliseconds);
            });
        }
    }
}
