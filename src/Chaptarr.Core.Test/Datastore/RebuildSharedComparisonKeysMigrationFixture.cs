using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FluentMigrator;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class RebuildSharedComparisonKeysMigrationFixture
    {
        // Historical output is an immutable review anchor. Append a receipt for a newer
        // shared rebuild when persisted policy changes; changing this anchor hides drift.
        private const string Historical113Fingerprint = "8dff8b68e02176d5a7b14aea79b45378b70b573127c538dba116201dcc473be2";
        private const string Historical114Fingerprint = "867d05833c443dd4acb884e8df84550af49faf1284b4fce407bcbd263ef96e1c";

        private static readonly IReadOnlyDictionary<long, string> PersistedKeyContracts = new Dictionary<long, string>
        {
            [113] = Historical113Fingerprint,
            [114] = Historical114Fingerprint,
            [115] = "ac60238bfb698561d39a590c9db56b44b41d10d747cdcd8652369eddcf9dc9f9"
        };

        private static readonly string[] PersistedKeyCorpus =
        {
            null, "", " ", "...", "John A Smith", "John Smith", "The Art of War", "The Art War",
            "Dreams (Book of Glass)", "Correct Title", "J. K. Rowling", "Søren", "Łódź", "Straße", "Ægir",
            "Café", "Cafe\u0301", "Nguyễn", "Nguye\u0302\u0303n", "Renée Ballard", "Rene\u0301e Ballard",
            "ø\u0301", "ǿ", "가", "가", "각", "각", "Й", "И\u0306", "ї", "і", "Ў", "У", "ѓ", "г",
            "Королёв", "ΟΔΥΣΣΕΑΣ", "οδυσσεας", "οδυσσεασ", "Σ", "σ", "ς", "ά", "α\u0301", "α\u0345",
            "ᾳ", "ᾼ", "A\u0345", "ᲀ", "ᲁ", "ᲂ", "ᲃ", "ᲄ", "ᲅ", "ᲆ", "ᲇ", "ᲈ", "ɩ", "Ɩ", "ϲ", "ẛ", "Ṡ",
            "\U0001D6D3", "\U0001D70D", "\U0001D747", "\U0001D781", "\U0001D7BB",
            "がとう", "か\u3099とう", "かとう", "村上 春樹", "हिन्दी", "हन्द", "हाँ", "हा", "ग़", "بِ", "ب", "أ", "ا",
            "מִ", "מ", "เก้า", "เกา", "𐐀 Reader", "𐐨 Reader", "𠀀\U000E0100", "𠀀", "Aか\u3099𠀀\u0301B",
            "aʼb", "aːb", "ŉ", "É Æ ẞ Ʀ", "é æ ß ʀ", "a©b", "a±b", "🌶️", "ab\uD800cd\uDC00ef",
            "Dune Ⅱ", "Dune II", "I, Robot", "Ready Player Two", "Ready Player 𝟚", "Ready Player ٢", "Ready Player ②",
            "23 ½ Lies", "23 1/2 Lies", "23.5 Lies", "1\u20E3", "٢\u20E3", " 00123 ", " +00123 ", "-00123", "\u2003+00123\u2003"
        };

        private static readonly string[] CurrentPersistedKeyCorpus = PersistedKeyCorpus
            .Concat(new[] { "Philosopher's Stone", "Philosopher’s Stone", "Philosopher‘s Stone", "O'Brien", "Ready-Player", "ready.player", "WAR AND PEACE NOT OR NEAR", "KING ARTHUR’S COURT", "KING ARTHUR\'S COURT", "KING ARTHUR‘S COURT" })
            .Concat(Enumerable.Range(0x24B6, 52).Select(point => new Rune(point).ToString()))
            .Concat(Enumerable.Range(0xFDD0, 32).Concat(Enumerable.Range(0, 17)
                .SelectMany(plane => new[] { (plane << 16) | 0xFFFE, (plane << 16) | 0xFFFF }))
                .Select(point => "a" + new Rune(point) + "b"))
            .ToArray();

        private static string FingerprintPersistedKeys()
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var value in CurrentPersistedKeyCorpus)
                {
                    AppendValue(digest, value);
                    AppendValue(digest, value.CleanAuthorName());
                    AppendValue(digest, value.CleanNarratorName());
                    AppendValue(digest, value.CleanBookTitle().CleanAuthorName());
                    AppendValue(digest, StringSuperNormalizer.ComputeMatchingTitle(value));
                }

                return Convert.ToHexStringLower(digest.GetHashAndReset());
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static void AppendValue(IncrementalHash digest, string value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value?.Length ?? -1);
            digest.AppendData(bytes);
            if (value == null) return;
            foreach (var character in value)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, character);
                digest.AppendData(bytes[..2]);
            }
        }

        private static bool MatchesPersistedKeyContract(string fingerprint,
            IReadOnlyDictionary<long, string> contracts, IEnumerable<Type> migrationTypes)
        {
            var rebuild = typeof(rebuild_shared_comparison_keys);
            var versions = migrationTypes.Where(type => !type.IsAbstract && rebuild.IsAssignableFrom(type))
                .Select(type => type.GetCustomAttribute<MigrationAttribute>(inherit: false)?.Version)
                .Where(version => version.HasValue && version.Value >= 113).Select(version => version.Value).OrderBy(version => version).ToList();
            if (versions.Count == 0 || !contracts.TryGetValue(113, out var historical) || historical != Historical113Fingerprint ||
                (contracts.TryGetValue(114, out var historical114) && historical114 != Historical114Fingerprint) ||
                contracts.Keys.Except(versions).Any() || versions.Any(version => !contracts.ContainsKey(version)))
            {
                return false;
            }

            return contracts[versions.Last()] == fingerprint;
        }

        [Test]
        public void persisted_key_output_should_match_the_latest_shared_rebuild_contract()
        {
            var fingerprint = FingerprintPersistedKeys();
            TestContext.Progress.WriteLine($"Persisted key corpus: {CurrentPersistedKeyCorpus.Length} inputs, fingerprint {fingerprint}");
            Assert.That(MatchesPersistedKeyContract(fingerprint, PersistedKeyContracts,
                typeof(rebuild_shared_comparison_keys).Assembly.GetTypes()), Is.True,
                "Persisted key output changed. Preserve historical receipts and add a newer shared key rebuild with its reviewed fingerprint.");
        }

        [TestCase(null, "")]
        [TestCase("[]", "")]
        [TestCase("[null,\" \",\"Søren Voice\",\"Королёв Reader\"]", "soren voice королев reader")]
        [TestCase("[\"Second Voice\",\"First Voice\",\"Second Voice\"]", "second voice first voice second voice")]
        [TestCase("[\"Ælfric Reader\",\"Straße Voice\"]", "aelfric reader strasse voice")]
        [TestCase("[", "")]
        [TestCase("{\"name\":\"Voice\"}", "")]
        public void serialized_complete_cast_projection_should_preserve_its_reviewed_output(string source, string expected)
        {
            var readCast = typeof(rebuild_shared_comparison_keys_totality).GetMethod("ReadCast", BindingFlags.Static | BindingFlags.NonPublic);
            var names = (string[])readCast.Invoke(null, new object[] { source });
            Assert.That(StringSuperNormalizer.NormalizeNarratorsForFts(names), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> CompleteProjectionRebuilds()
        {
            foreach (var type in typeof(rebuild_shared_comparison_keys).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(rebuild_shared_comparison_keys).IsAssignableFrom(type)))
            {
                var version = type.GetCustomAttribute<MigrationAttribute>(inherit: false)?.Version;
                if (version < 115 || !version.HasValue) continue;
                foreach (var database in new[] { DatabaseType.SQLite, DatabaseType.PostgreSQL })
                    yield return new TestCaseData(database, (int)version.Value);
            }
        }

        [TestCaseSource(nameof(CompleteProjectionRebuilds))]
        public void every_registered_newer_shared_rebuild_should_refresh_all_search_projections(DatabaseType databaseType, int version)
        {
            WithDatabase(databaseType, (connection, connectionString) =>
            {
                ApplyMigration(connectionString, databaseType, version - 1);
                InsertRows(connection);
                connection.Execute(@"UPDATE ""Books"" SET ""SeriesName""='Łukasz Archive';
                    UPDATE ""Editions"" SET ""Subtitle""='Ælfric Notes', ""Narrator""='Straße Voice',
                        ""NarratorNames""='[""Королёв Reader"",""Søren Voice""]', ""Publisher""='Ægir Press';");
                if (version > 115)
                    connection.Execute(@"UPDATE ""Authors"" SET ""SearchName""='stale';
                        UPDATE ""Books"" SET ""SearchSeriesName""='stale';
                        UPDATE ""Editions"" SET ""SearchSubtitle""='stale', ""SearchNarrator""='stale',
                            ""SearchNarratorNames""='stale', ""SearchPublisher""='stale';");
                var source = SnapshotSources(connection);
                ApplyMigration(connectionString, databaseType, version);
                Assert.That(SnapshotSources(connection), Is.EqualTo(source));
                AssertProductionKeys(connection);
                foreach (var row in connection.Query<(string Name, string SearchName)>("SELECT \"Name\", \"SearchName\" FROM \"Authors\";"))
                    Assert.That(row.SearchName, Is.EqualTo(StringSuperNormalizer.NormalizeForFts(row.Name)));
                Assert.That(connection.Query<string>("SELECT \"SearchSeriesName\" FROM \"Books\";"), Has.All.EqualTo("lukasz archive"));
                foreach (var row in connection.Query<(string Subtitle, string Narrator, string Cast, string Publisher)>(
                    "SELECT \"SearchSubtitle\", \"SearchNarrator\", \"SearchNarratorNames\", \"SearchPublisher\" FROM \"Editions\";"))
                    Assert.That(row, Is.EqualTo(("aelfric notes", "strasse voice", "королев reader soren voice", "aegir press")));
            });
        }

        [Test]
        public void migration_sources_should_use_the_registered_postgres_processor_guard()
        {
            var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "NzbDrone.Core", "Datastore", "Migration"))) root = root.Parent;
            Assert.That(root, Is.Not.Null, "Run this source contract test beneath a physical repository copy.");
            var wrongGuards = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "NzbDrone.Core", "Datastore", "Migration"), "*.cs")
                .Where(path => HasWrongPostgresGuard(File.ReadAllText(path)))
                .Select(Path.GetFileName).ToArray();
            Assert.That(wrongGuards, Is.Empty);
        }

        private static bool HasWrongPostgresGuard(string source)
        {
            return Regex.IsMatch(Regex.Replace(source, @"(?m)^\s*//.*$", ""),
                @"IfDatabase\s*\(\s*""postgres""\s*\)", RegexOptions.IgnoreCase);
        }

        [TestCase("IfDatabase(\"postgres\")", true)]
        [TestCase("IfDatabase(\"PostgreS\")", true)]
        [TestCase("// IfDatabase(\"postgres\")", false)]
        [TestCase("IfPostgres()", false)]
        public void wrong_processor_guard_scan_should_ignore_case_and_full_line_comments(string source, bool wrong)
        {
            Assert.That(HasWrongPostgresGuard(source), Is.EqualTo(wrong));
        }

        [Migration(116)]
        private sealed class FutureCombinedRebuild : rebuild_shared_comparison_keys_totality
        {
            protected override void MainDbUpgrade() { }
        }

        [Test]
        public void future_combined_override_should_be_subject_to_the_same_registered_key_contract()
        {
            var fingerprint = FingerprintPersistedKeys();
            var contracts = new Dictionary<long, string>(PersistedKeyContracts) { [116] = fingerprint };
            Assert.That(MatchesPersistedKeyContract(fingerprint, contracts,
                typeof(rebuild_shared_comparison_keys).Assembly.GetTypes().Append(typeof(FutureCombinedRebuild))), Is.True);
            contracts[116] = "stale";
            Assert.That(MatchesPersistedKeyContract(fingerprint, contracts,
                typeof(rebuild_shared_comparison_keys).Assembly.GetTypes().Append(typeof(FutureCombinedRebuild))), Is.False);
        }

        [Migration(112)]
        private sealed class StaleSharedRebuild : rebuild_shared_comparison_keys { }

        [Migration(115)]
        private sealed class LaterSharedRebuild : rebuild_shared_comparison_keys { }

        [Migration(115)]
        private sealed class UnrelatedRebuild : NzbDroneMigrationBase
        {
            protected override void MainDbUpgrade() { }
        }

        [Migration(115)]
        private sealed class ReplacedSharedWriter : rebuild_shared_comparison_keys
        {
            protected override void MainDbUpgrade() { }
        }

        [Test]
        public void changed_output_should_require_a_registered_newer_actual_shared_rebuild()
        {
            var changed = Historical113Fingerprint + "changed";
            var historical = new Dictionary<long, string> { [113] = Historical113Fingerprint, [114] = Historical114Fingerprint };
            var contracts = new Dictionary<long, string>(historical) { [115] = changed };
            var previous = new[] { typeof(rebuild_shared_comparison_keys), typeof(rebuild_shared_comparison_keys_casefold) };
            Assert.Multiple(() =>
            {
                Assert.That(MatchesPersistedKeyContract(changed, historical,
                    previous), Is.False);
                Assert.That(MatchesPersistedKeyContract(changed, contracts,
                    previous), Is.False);
                Assert.That(MatchesPersistedKeyContract(changed, contracts,
                    new[] { typeof(StaleSharedRebuild), typeof(UnrelatedRebuild) }), Is.False);
                Assert.That(MatchesPersistedKeyContract(changed, contracts,
                    new[] { typeof(rebuild_shared_comparison_keys), typeof(UnrelatedRebuild) }), Is.False);
                Assert.That(MatchesPersistedKeyContract(changed, contracts,
                    new[] { typeof(rebuild_shared_comparison_keys), typeof(ReplacedSharedWriter) }), Is.False);
                Assert.That(MatchesPersistedKeyContract(changed, contracts,
                    previous.Append(typeof(LaterSharedRebuild))), Is.True);
            });
        }

        [TestCase(113)]
        [TestCase(114)]
        public void rewriting_a_historical_receipt_without_a_new_rebuild_should_not_hide_drift(int version)
        {
            var changed = Historical113Fingerprint + "changed";
            var rewrittenHistory = new Dictionary<long, string>
            {
                [113] = Historical113Fingerprint, [114] = Historical114Fingerprint, [115] = changed
            };
            rewrittenHistory[version] = changed;
            Assert.That(MatchesPersistedKeyContract(changed, rewrittenHistory,
                new[] { typeof(rebuild_shared_comparison_keys), typeof(rebuild_shared_comparison_keys_casefold), typeof(rebuild_shared_comparison_keys_totality) }), Is.False);
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void should_rebuild_current_keys_without_merging_identity_or_media_and_keep_search_current(DatabaseType databaseType)
        {
            WithDatabase(databaseType, (connection, connectionString) =>
            {
                InsertRows(connection);
                var before = SnapshotSources(connection);
                var logs = CaptureMigration(connectionString, databaseType);

                Assert.That(connection.QuerySingle<long>("SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(113));
                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                AssertProductionKeys(connection);
                Assert.That(connection.QuerySingle<string>("SELECT \"CleanName\" FROM \"Narrators\" WHERE \"Id\"=9;"), Is.EqualTo("orphanpreserved"));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates WHERE TableName='Books' AND RowId=10;"), Is.Zero);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates WHERE TableName='Narrators' AND RowId=7;"), Is.Zero);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"Authors\" WHERE \"Name\"='Linda Kay Silva';"), Is.EqualTo(2));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"Books\" WHERE \"Id\" IN (3,4) AND \"HardcoverBookId\"='hc:3';"), Is.EqualTo(2));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(DISTINCT \"MediaType\") FROM \"Books\" WHERE \"Id\" IN (3,4);"), Is.EqualTo(2));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(DISTINCT \"HardcoverBookId\") FROM \"Books\" WHERE \"Id\" IN (3,5);"), Is.EqualTo(2));
                Assert.That(logs, Has.Some.Contains("Books.CleanTitle").And.Contains("collision groups 2, colliding rows 4, largest group 2"));
                Assert.That(logs, Has.Some.Contains("Authors.CleanName").And.Contains("collision groups 2, colliding rows 4, largest group 2"));
                Assert.That(logs, Has.Some.Contains("Narrators.CleanName").And.Contains("collision groups 2, colliding rows 4, largest group 2"));
                Assert.That(logs, Has.Some.Contains("Editions.MatchingTitle").And.Contains("collision groups 1, colliding rows 2, largest group 2"));
                Assert.That(logs, Has.None.Contains("Linda Kay Silva").And.None.Contains("hc:685041"));

                var expectedBookKeys = new[]
                {
                    "theartofwar", "theartwar", "cafe", "cafe", "cafe", "duneii", "235lies", "235lies",
                    "dreamsbookofglass", "correcttitle", "readyplayertwo", "readyplayer2", "がとう", "かとう", "lodz"
                };
                Assert.That(connection.Query<string>("SELECT \"CleanTitle\" FROM \"Books\" ORDER BY \"Id\";"), Is.EqualTo(expectedBookKeys));
                Assert.That(connection.Query<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\" IN (1,2,7) ORDER BY \"Id\";"), Is.EqualTo(new[] { "johnasmith", "johnsmith", "lodz" }));
                Assert.That(connection.Query<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\" IN (6,7,8,11,12) ORDER BY \"Id\";"), Is.EqualTo(new[] { "dune ii", "23.5 lies", "23.5 lies", "ready player two", "ready player 2" }));

                var searchSql = databaseType == DatabaseType.SQLite
                    ? "SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH @Query AND rowid=@Id;"
                    : "SELECT COUNT(*) FROM \"Editions\" WHERE to_tsvector('simple', COALESCE(\"MatchingTitle\", '')) @@ plainto_tsquery('simple', @Query) AND \"Id\"=@Id;";
                foreach (var (id, query) in new[] { (1, "of"), (6, "dune ii"), (7, "23.5 lies"), (8, "23.5 lies"), (9, "glass"), (11, "two"), (12, "2"), (15, "lodz") })
                {
                    Assert.That(connection.QuerySingle<int>(searchSql, new { Id = id, Query = databaseType == DatabaseType.SQLite ? $"\"{query}\"" : query }), Is.EqualTo(1), query);
                }

                if (databaseType == DatabaseType.PostgreSQL)
                {
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"Authors\" WHERE to_tsvector('simple', COALESCE(\"CleanName\", '')) @@ plainto_tsquery('simple', 'johnasmith');"), Is.EqualTo(1));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"Books\" WHERE to_tsvector('simple', COALESCE(\"CleanTitle\", '')) @@ plainto_tsquery('simple', 'theartofwar');"), Is.EqualTo(1));
                }

                var updates = connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;");
                var keys = SnapshotKeys(connection);
                var restartLogs = CaptureMigration(connectionString, databaseType);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(updates));
                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                Assert.That(SnapshotKeys(connection), Is.EqualTo(keys));
                Assert.That(restartLogs, Has.None.Contains("Comparison-key rebuild"));
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void should_process_multi_page_inputs_with_one_write_per_changed_page(DatabaseType databaseType)
        {
            WithDatabase(databaseType, (connection, connectionString) =>
            {
                InsertBatchRows(connection, 1201);
                var before = SnapshotSources(connection);
                var logs = CaptureMigration(connectionString, databaseType);
                AssertProductionKeys(connection);
                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                foreach (var table in new[] { "Books", "Authors", "Narrators", "Editions" })
                {
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates WHERE TableName=@table;", new { table }), Is.EqualTo(1201));
                    Assert.That(logs, Has.Some.Contains("Comparison-key rebuild " + table + ".").And.Contains("scanned 1201, updated 1201, read queries 4, update batches 3"));
                }

                Assert.That(logs, Has.Some.Contains("scanned 4804, updated 4804"));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\"=113;"), Is.EqualTo(1));
                if (databaseType == DatabaseType.PostgreSQL)
                {
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM StatementUpdates;"), Is.EqualTo(12));
                }
                else
                {
                    var compileOptions = connection.Query<string>("PRAGMA compile_options;");
                    TestContext.WriteLine("SQLite " + connection.QuerySingle<string>("SELECT sqlite_version();") + " " + compileOptions.Single(option => option.StartsWith("MAX_VARIABLE_NUMBER=", StringComparison.Ordinal)));
                }
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void should_roll_back_a_later_batch_failure_and_complete_on_next_startup(DatabaseType databaseType)
        {
            WithDatabase(databaseType, (connection, connectionString) =>
            {
                InsertBatchRows(connection, 451);
                var before = SnapshotSources(connection);
                var keys = SnapshotKeys(connection);
                CreateFailingTrigger(connection, databaseType);

                Assert.That(() => ApplyMigration(connectionString, databaseType, 113), Throws.Exception);

                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\"=113;"), Is.Zero);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.Zero);
                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                Assert.That(SnapshotKeys(connection), Is.EqualTo(keys));
                if (databaseType == DatabaseType.PostgreSQL)
                {
                    connection.Execute("DROP TRIGGER fail_later_batch ON \"Editions\";");
                }
                else
                {
                    connection.Execute("DROP TRIGGER fail_later_batch;");
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'stale';"), Is.EqualTo(451));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'lodz';"), Is.Zero);
                }

                CaptureMigration(connectionString, databaseType);
                AssertProductionKeys(connection);
                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(1804));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\"=113;"), Is.EqualTo(1));
                if (databaseType == DatabaseType.SQLite)
                {
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'stale';"), Is.Zero);
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'lodz';"), Is.EqualTo(451));
                }
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void should_upgrade_existing_113_keys_without_merging_case_collisions_or_media(DatabaseType databaseType)
        {
            WithDatabase(databaseType, (connection, connectionString) =>
            {
                InsertRows(connection);
                ApplyMigration(connectionString, databaseType, 113);
                InsertLegacyCaseRows(connection);
                connection.Execute("DELETE FROM KeyUpdates;");
                var before = SnapshotSources(connection);
                var logs = CaptureMigration(connectionString, databaseType, 114);

                AssertProductionKeys(connection);
                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\" IN (113,114);"), Is.EqualTo(2));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(5));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates WHERE RowId=21;"), Is.Zero);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"Authors\" WHERE \"Id\" IN (20,21);"), Is.EqualTo(2));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(DISTINCT \"MediaType\") FROM \"Books\" WHERE \"Id\" IN (20,21);"), Is.EqualTo(2));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(DISTINCT \"HardcoverBookId\") FROM \"Books\" WHERE \"Id\" IN (20,22);"), Is.EqualTo(2));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"Narrators\" WHERE \"CleanName\"=@key;", new { key = "ΟΔΥΣΣΕΑΣ".CleanNarratorName() }), Is.EqualTo(2));
                Assert.That(logs, Has.Some.Contains("Books.CleanTitle").And.Contains("collision groups 3, colliding rows 6"));
                Assert.That(logs, Has.Some.Contains("Authors.CleanName").And.Contains("collision groups 3, colliding rows 6"));
                Assert.That(logs, Has.Some.Contains("Narrators.CleanName").And.Contains("collision groups 3, colliding rows 6"));
                Assert.That(logs, Has.Some.Contains("Editions.MatchingTitle").And.Contains("collision groups 2, colliding rows 4"));
                var search = databaseType == DatabaseType.SQLite
                    ? "SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH @key AND rowid IN (20,21,22);"
                    : "SELECT COUNT(*) FROM \"Editions\" WHERE to_tsvector('simple', COALESCE(\"MatchingTitle\", '')) @@ plainto_tsquery('simple', @key) AND \"Id\" IN (20,21,22);";
                Assert.That(connection.QuerySingle<int>(search, new { key = StringSuperNormalizer.ComputeMatchingTitle("ΟΔΥΣΣΕΑΣ") }), Is.EqualTo(3));
                var keys = SnapshotKeys(connection);
                Assert.That(CaptureMigration(connectionString, databaseType, 114), Has.None.Contains("Comparison-key rebuild"));
                Assert.That(SnapshotKeys(connection), Is.EqualTo(keys));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(5));
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void public_upgrade_should_scan_twice_and_write_only_the_first_changed_pass(DatabaseType databaseType)
        {
            WithDatabase(databaseType, (connection, connectionString) =>
            {
                InsertBatchRows(connection, 1201);
                var before = SnapshotSources(connection);
                var logs = CaptureMigration(connectionString, databaseType, 114);
                var completions = logs.Where(line => line.StartsWith("Comparison-key rebuild processed:", StringComparison.Ordinal)).ToList();
                AssertProductionKeys(connection);
                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                Assert.That(completions, Has.Count.EqualTo(2));
                Assert.That(completions[0], Does.Contain("scanned 4804, updated 4804"));
                Assert.That(completions[1], Does.Contain("scanned 4804, updated 0"));
                foreach (var table in new[] { "Books", "Authors", "Narrators", "Editions" })
                {
                    Assert.That(logs, Has.Some.Contains("Comparison-key rebuild " + table + ".").And.Contains("scanned 1201, updated 1201, read queries 4, update batches 3"));
                    Assert.That(logs, Has.Some.Contains("Comparison-key rebuild " + table + ".").And.Contains("scanned 1201, updated 0, read queries 4, update batches 0"));
                }

                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(4804));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\" IN (113,114);"), Is.EqualTo(2));
                if (databaseType == DatabaseType.PostgreSQL)
                {
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM StatementUpdates;"), Is.EqualTo(12));
                }

                TestContext.WriteLine(databaseType + " public 112-to-114 upgrade: 9608 scanned keys, 32 read queries, 12 bulk updates; " + string.Join("; ", completions));
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void should_roll_back_114_and_retry_without_rewriting_the_113_receipt(DatabaseType databaseType)
        {
            WithDatabase(databaseType, (connection, connectionString) =>
            {
                InsertLegacyCaseBatch(connection, 451);
                connection.Execute("INSERT INTO \"VersionInfo\" (\"Version\", \"Description\") VALUES (113, 'existing key policy');");
                var before = SnapshotSources(connection);
                var keys = SnapshotKeys(connection);
                CreateFailingTrigger(connection, databaseType);

                Assert.That(() => ApplyMigration(connectionString, databaseType, 114), Throws.Exception);

                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                Assert.That(SnapshotKeys(connection), Is.EqualTo(keys));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.Zero);
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\"=113;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\"=114;"), Is.Zero);
                connection.Execute(databaseType == DatabaseType.PostgreSQL
                    ? "DROP TRIGGER fail_later_batch ON \"Editions\";" : "DROP TRIGGER fail_later_batch;");

                var logs = CaptureMigration(connectionString, databaseType, 114);
                AssertProductionKeys(connection);
                Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(1804));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\" IN (113,114);"), Is.EqualTo(2));
                Assert.That(logs, Has.Some.Contains("scanned 1804, updated 1804"));
            });
        }

        [TestCase(DatabaseType.SQLite, 112)]
        [TestCase(DatabaseType.PostgreSQL, 112)]
        [TestCase(DatabaseType.SQLite, 114)]
        [TestCase(DatabaseType.PostgreSQL, 114)]
        public void latest_rebuild_should_handle_noncharacters_and_restore_circled_casing_without_changing_sources(DatabaseType databaseType, int baseline)
        {
            WithDatabase(databaseType, (connection, connectionString) =>
            {
                if (baseline == 114)
                {
                    ApplyMigration(connectionString, databaseType, 114);
                }

                InsertBatchRows(connection, 27);
                var rows = Enumerable.Range(1, 27).Select(id => new
                {
                    Id = id,
                    Source = id == 1 ? "Poison \uFFFE title" : "Volume " + new Rune(0x24B6 + id - 2),
                    Existing = id == 1 ? "stale" : "volume",
                    ExistingTitle = id == 1 ? "stale" : "volume " + new Rune(0x24B6 + id - 2)
                }).ToList();
                connection.Execute(@"
                    UPDATE ""Authors"" SET ""Name""=@Source, ""CleanName""=@Existing WHERE ""Id""=@Id;
                    UPDATE ""Books"" SET ""Title""=@Source, ""CleanTitle""=@Existing, ""MediaType""=""Id""%2 WHERE ""Id""=@Id;
                    UPDATE ""NarratorMetadata"" SET ""Name""=@Source WHERE ""Id""=@Id;
                    UPDATE ""Narrators"" SET ""CleanName""=@Existing WHERE ""Id""=@Id;
                    UPDATE ""Editions"" SET ""Title""=@Source, ""MatchingTitle""=@ExistingTitle WHERE ""Id""=@Id;", rows);
                connection.Execute("INSERT INTO \"BookFiles\" (\"Id\", \"BookId\", \"EditionId\", \"RelativePath\") VALUES (1,1,1,'retained.m4b'); DELETE FROM KeyUpdates;");
                if (databaseType == DatabaseType.PostgreSQL)
                {
                    connection.Execute("DELETE FROM StatementUpdates;");
                }

                var before = SnapshotSources(connection);
                var logs = CaptureMigration(connectionString, databaseType, 115);
                var completions = logs.Where(line => line.StartsWith("Comparison-key rebuild processed:", StringComparison.Ordinal)).ToList();
                AssertProductionKeys(connection);
                Assert.Multiple(() =>
                {
                    Assert.That(SnapshotSources(connection), Is.EqualTo(before));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\" IN (113,114,115);"), Is.EqualTo(3));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(30));
                    Assert.That(completions, Has.Count.EqualTo(baseline == 112 ? 3 : 1));
                    Assert.That(completions[0], Does.Contain("scanned 108, updated 30"));
                    Assert.That(completions.Skip(1), Has.All.Contains("scanned 108, updated 0"));
                    Assert.That(connection.QuerySingle<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\"=1;"), Is.EqualTo("poison \uFFFE title"));
                });
                foreach (var row in connection.Query<(int Id, string MatchingTitle)>("SELECT \"Id\", \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\">1 ORDER BY \"Id\";"))
                {
                    Assert.That(row.MatchingTitle, Is.EqualTo("volume " + Rune.ToLowerInvariant(new Rune(0x24B6 + row.Id - 2))));
                }
                foreach (var table in new[] { "Books", "Authors", "Narrators", "Editions" })
                {
                    Assert.That(logs, Has.Some.Contains("Comparison-key rebuild " + table + ".").And.Contains("read queries 2, update batches 1"));
                }
                if (databaseType == DatabaseType.PostgreSQL)
                {
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM StatementUpdates;"), Is.EqualTo(4));
                }

                var keys = SnapshotKeys(connection);
                Assert.That(CaptureMigration(connectionString, databaseType, 115), Has.None.Contains("Comparison-key rebuild"));
                Assert.That(SnapshotKeys(connection), Is.EqualTo(keys));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM KeyUpdates;"), Is.EqualTo(30));
                TestContext.WriteLine($"{databaseType} {baseline}-to-115: {completions.Count * 108} scanned keys, {completions.Count * 8} read queries, four bulk writes, 30 changed keys; " + string.Join("; ", completions));
            });
        }

        private static void InsertLegacyCaseRows(IDbConnection connection)
        {
            connection.Execute(@"
                INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""HardcoverAuthorId"") VALUES
                    (20, @Lower, @OldKey, 'test:case20'), (21, @Upper, @CurrentKey, 'test:case21');
                INSERT INTO ""Books"" (""Id"", ""Title"", ""CleanTitle"", ""AuthorId"", ""MediaType"", ""HardcoverBookId"") VALUES
                    (20, @Lower, @OldKey, 20, 0, 'test:case20'), (21, @Upper, @CurrentKey, 20, 1, 'test:case20'), (22, @Lower, @OldKey, 20, 0, 'test:case22');
                INSERT INTO ""NarratorMetadata"" (""Id"", ""Name"") VALUES (20, @Lower), (21, @Upper);
                INSERT INTO ""Narrators"" (""Id"", ""NarratorMetadataId"", ""CleanName"") VALUES (20,20,@OldKey), (21,21,@CurrentKey);
                INSERT INTO ""Editions"" (""Id"", ""Title"", ""MatchingTitle"", ""BookId"", ""ForeignEditionId"") VALUES
                    (20, @Lower, @OldKey, 20, 'test:edition20'), (21, @Upper, @CurrentKey, 21, 'test:edition21'), (22, @Upper, @CurrentKey, 20, 'test:edition22');
                INSERT INTO ""BookFiles"" (""Id"", ""BookId"", ""EditionId"", ""RelativePath"") VALUES
                    (20,20,20,'case.m4b'), (21,21,21,'case.epub');",
                new { Lower = "οδυσσεας", Upper = "ΟΔΥΣΣΕΑΣ", OldKey = "οδυσσεας", CurrentKey = "οδυσσεασ" });
        }

        private static void InsertLegacyCaseBatch(IDbConnection connection, int count)
        {
            connection.Execute(@"
                WITH RECURSIVE ids(""Id"") AS (SELECT 1 UNION ALL SELECT ""Id""+1 FROM ids WHERE ""Id""<@Count)
                INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""HardcoverAuthorId"")
                    SELECT ""Id"", @Source || ' ' || CAST(""Id"" AS TEXT), @OldKey || CAST(""Id"" AS TEXT), 'test:' || CAST(""Id"" AS TEXT) FROM ids;
                INSERT INTO ""Books"" (""Id"", ""Title"", ""CleanTitle"", ""AuthorId"", ""MediaType"", ""HardcoverBookId"")
                    SELECT ""Id"", ""Name"", ""CleanName"", ""Id"", 0, ""HardcoverAuthorId"" FROM ""Authors"";
                INSERT INTO ""NarratorMetadata"" (""Id"", ""Name"") SELECT ""Id"", ""Name"" FROM ""Authors"";
                INSERT INTO ""Narrators"" (""Id"", ""NarratorMetadataId"", ""CleanName"") SELECT ""Id"", ""Id"", ""CleanName"" FROM ""Authors"";
                INSERT INTO ""Editions"" (""Id"", ""Title"", ""MatchingTitle"", ""BookId"", ""ForeignEditionId"")
                    SELECT ""Id"", ""Name"", @OldKey || ' ' || CAST(""Id"" AS TEXT), ""Id"", ""HardcoverAuthorId"" FROM ""Authors"";",
                new { Count = count, Source = "οδυσσεας", OldKey = "οδυσσεας" });
        }

        private static void WithDatabase(DatabaseType databaseType, Action<IDbConnection, string> action)
        {
            if (databaseType == DatabaseType.SQLite)
            {
                var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"shared_comparison_keys_{Guid.NewGuid():N}.db");
                var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
                try
                {
                    using var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    CreateSchema(connection, connectionString, databaseType);
                    action(connection, connectionString);
                }
                finally
                {
                    File.Delete(databasePath);
                }

                return;
            }

            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise the PostgreSQL key rebuild");
            }

            var databaseName = "shared_keys_" + Guid.NewGuid().ToString("N");
            using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                adminConnection.Open();
                adminConnection.Execute($"CREATE DATABASE \"{databaseName}\"");
            }

            try
            {
                var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName, Pooling = false }.ConnectionString;
                using var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                CreateSchema(connection, connectionString, databaseType);
                action(connection, connectionString);
            }
            finally
            {
                using var adminConnection = new NpgsqlConnection(adminConnectionString);
                adminConnection.Open();
                adminConnection.Execute($"DROP DATABASE IF EXISTS \"{databaseName}\"");
            }
        }

        private static void CreateSchema(IDbConnection connection, string connectionString, DatabaseType databaseType)
        {
            connection.Execute(@"
                CREATE TABLE ""VersionInfo"" (""Version"" INTEGER PRIMARY KEY, ""AppliedOn"" TIMESTAMP NULL, ""Description"" TEXT NULL);
                CREATE TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""CleanTitle"" TEXT NOT NULL, ""AuthorId"" INTEGER NOT NULL, ""MediaType"" INTEGER NOT NULL, ""HardcoverBookId"" TEXT, ""SeriesName"" TEXT, ""IsOmnibus"" INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL, ""CleanName"" TEXT NOT NULL, ""HardcoverAuthorId"" TEXT, ""GoodreadsAuthorId"" TEXT, ""TitleSlug"" TEXT, ""SortName"" TEXT);
                CREATE TABLE ""NarratorMetadata"" (""Id"" INTEGER PRIMARY KEY, ""Name"" TEXT NOT NULL);
                CREATE TABLE ""Narrators"" (""Id"" INTEGER PRIMARY KEY, ""NarratorMetadataId"" INTEGER NOT NULL, ""CleanName"" TEXT NULL);
                CREATE TABLE ""Editions"" (""Id"" INTEGER PRIMARY KEY, ""Title"" TEXT NOT NULL, ""MatchingTitle"" TEXT NULL, ""BookId"" INTEGER NOT NULL, ""ForeignEditionId"" TEXT, ""Subtitle"" TEXT, ""NarratorNames"" TEXT, ""Narrator"" TEXT, ""Publisher"" TEXT, ""Asins"" TEXT NOT NULL DEFAULT '[]');
                CREATE TABLE ""BookFiles"" (""Id"" INTEGER PRIMARY KEY, ""BookId"" INTEGER NOT NULL, ""EditionId"" INTEGER NOT NULL, ""RelativePath"" TEXT NOT NULL);");
            connection.Execute("INSERT INTO \"VersionInfo\" (\"Version\", \"Description\") VALUES (@Version, 'test baseline');", Enumerable.Range(1, 109).Select(version => new { Version = version }));
            if (databaseType == DatabaseType.SQLite)
            {
                connection.Execute("CREATE VIRTUAL TABLE edition_fts USING fts5(MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher, content='');");
            }
            else
            {
                connection.Execute(@"
                    CREATE INDEX idx_editions_matching_title_fts ON ""Editions"" USING GIN (to_tsvector('simple', COALESCE(""MatchingTitle"", '')));
                    CREATE INDEX test_author_clean_key ON ""Authors"" USING GIN (to_tsvector('simple', COALESCE(""CleanName"", '')));
                    CREATE INDEX test_book_clean_key ON ""Books"" USING GIN (to_tsvector('simple', COALESCE(""CleanTitle"", '')));");
            }

            ApplyMigration(connectionString, databaseType, 110);
            connection.Execute("INSERT INTO \"VersionInfo\" (\"Version\", \"Description\") VALUES (111, 'test baseline'), (112, 'test baseline');");
            CreateKeyAudit(connection, databaseType);
        }

        private static void CreateKeyAudit(IDbConnection connection, DatabaseType databaseType)
        {
            connection.Execute("CREATE TABLE KeyUpdates (TableName TEXT NOT NULL, RowId INTEGER NOT NULL);");
            if (databaseType == DatabaseType.PostgreSQL)
            {
                connection.Execute(@"
                    CREATE TABLE StatementUpdates (TableName TEXT NOT NULL);
                    CREATE FUNCTION audit_comparison_key_update() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN INSERT INTO KeyUpdates(TableName, RowId) VALUES (TG_TABLE_NAME, NEW.""Id""); RETURN NEW; END; $$;
                    CREATE FUNCTION audit_comparison_key_statement() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN INSERT INTO StatementUpdates(TableName) VALUES (TG_TABLE_NAME); RETURN NULL; END; $$;");
            }

            foreach (var (table, key) in new[] { ("Books", "CleanTitle"), ("Authors", "CleanName"), ("Narrators", "CleanName"), ("Editions", "MatchingTitle") })
            {
                var action = databaseType == DatabaseType.PostgreSQL
                    ? "FOR EACH ROW EXECUTE FUNCTION audit_comparison_key_update();"
                    : $"BEGIN INSERT INTO KeyUpdates(TableName, RowId) VALUES ('{table}', new.Id); END;";
                connection.Execute($"CREATE TRIGGER key_audit_{table} AFTER UPDATE OF \"{key}\" ON \"{table}\" {action}");
                if (databaseType == DatabaseType.PostgreSQL)
                {
                    connection.Execute($"CREATE TRIGGER statement_audit_{table} AFTER UPDATE OF \"{key}\" ON \"{table}\" FOR EACH STATEMENT EXECUTE FUNCTION audit_comparison_key_statement();");
                }
            }
        }

        private static void InsertRows(IDbConnection connection)
        {
            connection.Execute(@"
                INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""HardcoverAuthorId"") VALUES
                    (1, 'John A Smith', 'johnsmith', 'test:one'), (2, 'John Smith', 'stale', 'test:two'),
                    (3, 'José', 'stale', 'test:three'), (4, 'José', 'stale', 'test:four'),
                    (5, 'Linda Kay Silva', 'stale', NULL), (6, 'Linda Kay Silva', 'stale', 'hc:685041'), (7, 'Łódź', 'stale', 'test:seven');
                UPDATE ""Authors"" SET ""GoodreadsAuthorId""='gr:26521' WHERE ""Id""=6;
                INSERT INTO ""Books"" (""Id"", ""Title"", ""CleanTitle"", ""AuthorId"", ""MediaType"", ""HardcoverBookId"") VALUES
                    (1, 'The Art of War', 'theartwar', 1, 0, 'hc:1'), (2, 'The Art War', 'stale', 1, 0, 'hc:2'),
                    (3, 'Café', 'stale', 1, 0, 'hc:3'), (4, 'Café', 'stale', 1, 1, 'hc:3'), (5, 'Cafe', 'stale', 1, 0, 'hc:4'),
                    (6, 'Dune Ⅱ', 'dune2', 1, 0, 'hc:6'), (7, '23 ½ Lies', 'stale', 1, 0, 'hc:880690'),
                    (8, '23 1/2 Lies', 'stale', 1, 0, 'hc:880690'), (9, 'Dreams (Book of Glass)', 'dreams', 1, 0, 'hc:9'),
                    (10, 'Correct Title', 'correcttitle', 1, 0, 'hc:10'), (11, 'Ready Player Two', 'readyplayer2', 1, 0, 'hc:11'),
                    (12, 'Ready Player 𝟚', 'stale', 1, 0, 'hc:12'), (13, 'がとう', 'stale', 1, 0, 'hc:13'),
                    (14, 'かとう', 'stale', 1, 0, 'hc:14'), (15, 'Łódź', 'stale', 1, 0, 'hc:15');
                INSERT INTO ""NarratorMetadata"" (""Id"", ""Name"") SELECT ""Id"", ""Name"" FROM ""Authors"";
                INSERT INTO ""Narrators"" (""Id"", ""NarratorMetadataId"", ""CleanName"") SELECT ""Id"", ""Id"", 'stale' FROM ""Authors"";
                UPDATE ""Narrators"" SET ""CleanName""='lodz' WHERE ""Id""=7;
                INSERT INTO ""Narrators"" (""Id"", ""NarratorMetadataId"", ""CleanName"") VALUES (9, 99, 'orphanpreserved');
                INSERT INTO ""Editions"" (""Id"", ""Title"", ""MatchingTitle"", ""BookId"", ""ForeignEditionId"")
                    SELECT ""Id"", ""Title"", 'stale', CASE WHEN ""Id""=8 THEN 7 ELSE ""Id"" END, 'test:edition' FROM ""Books"";
                INSERT INTO ""BookFiles"" (""Id"", ""BookId"", ""EditionId"", ""RelativePath"") VALUES (1, 3, 3, 'audiobook.m4b'), (2, 4, 4, 'ebook.epub');
                DELETE FROM KeyUpdates;");
        }

        private static void InsertBatchRows(IDbConnection connection, int count)
        {
            connection.Execute(@"
                WITH RECURSIVE ids(""Id"") AS (SELECT 1 UNION ALL SELECT ""Id""+1 FROM ids WHERE ""Id""<@Count)
                INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""HardcoverAuthorId"")
                    SELECT ""Id"", 'Łódź A ' || CAST(""Id"" AS TEXT), 'stale', 'test:' || CAST(""Id"" AS TEXT) FROM ids;
                INSERT INTO ""Books"" (""Id"", ""Title"", ""CleanTitle"", ""AuthorId"", ""MediaType"", ""HardcoverBookId"")
                    SELECT ""Id"", ""Name"", 'stale', ""Id"", 0, ""HardcoverAuthorId"" FROM ""Authors"";
                INSERT INTO ""NarratorMetadata"" (""Id"", ""Name"") SELECT ""Id"", ""Name"" FROM ""Authors"";
                INSERT INTO ""Narrators"" (""Id"", ""NarratorMetadataId"", ""CleanName"") SELECT ""Id"", ""Id"", 'stale' FROM ""Authors"";
                INSERT INTO ""Editions"" (""Id"", ""Title"", ""MatchingTitle"", ""BookId"", ""ForeignEditionId"")
                    SELECT ""Id"", ""Name"", 'stale', ""Id"", ""HardcoverAuthorId"" FROM ""Authors"";", new { Count = count });
        }

        private static void CreateFailingTrigger(IDbConnection connection, DatabaseType databaseType)
        {
            connection.Execute(databaseType == DatabaseType.PostgreSQL
                ? @"CREATE FUNCTION fail_later_key_batch() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN IF NEW.""Id""=451 THEN RAISE EXCEPTION 'forced later-batch failure'; END IF; RETURN NEW; END; $$;
                    CREATE TRIGGER fail_later_batch BEFORE UPDATE OF ""MatchingTitle"" ON ""Editions"" FOR EACH ROW EXECUTE FUNCTION fail_later_key_batch();"
                : @"CREATE TRIGGER fail_later_batch BEFORE UPDATE OF MatchingTitle ON Editions WHEN new.Id=451
                    BEGIN SELECT RAISE(ABORT, 'forced later-batch failure'); END;");
        }

        private static void AssertProductionKeys(IDbConnection connection)
        {
            foreach (var row in connection.Query("SELECT \"Id\", \"Title\", \"CleanTitle\" FROM \"Books\";"))
            {
                Assert.That((string)row.CleanTitle, Is.EqualTo(((string)row.Title).CleanBookTitle().CleanAuthorName()), "Books " + row.Id);
            }

            foreach (var row in connection.Query("SELECT \"Id\", \"Name\", \"CleanName\" FROM \"Authors\";"))
            {
                Assert.That((string)row.CleanName, Is.EqualTo(((string)row.Name).CleanAuthorName()), "Authors " + row.Id);
            }

            foreach (var row in connection.Query("SELECT n.\"Id\", m.\"Name\", n.\"CleanName\" FROM \"Narrators\" n JOIN \"NarratorMetadata\" m ON m.\"Id\"=n.\"NarratorMetadataId\";"))
            {
                Assert.That((string)row.CleanName, Is.EqualTo(((string)row.Name).CleanNarratorName()), "Narrators " + row.Id);
            }

            foreach (var row in connection.Query("SELECT \"Id\", \"Title\", \"MatchingTitle\" FROM \"Editions\";"))
            {
                Assert.That((string)row.MatchingTitle, Is.EqualTo(StringSuperNormalizer.ComputeMatchingTitle((string)row.Title)), "Editions " + row.Id);
            }
        }

        private static string SnapshotSources(IDbConnection connection)
        {
            var queries = new[]
            {
                "SELECT \"Id\", \"Title\", \"AuthorId\", \"MediaType\", \"HardcoverBookId\", \"SeriesName\" FROM \"Books\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"Title\", \"BookId\", \"ForeignEditionId\", \"Subtitle\", \"NarratorNames\", \"Narrator\", \"Publisher\" FROM \"Editions\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"Name\", \"HardcoverAuthorId\", \"GoodreadsAuthorId\" FROM \"Authors\" ORDER BY \"Id\";",
                "SELECT * FROM \"NarratorMetadata\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"NarratorMetadataId\" FROM \"Narrators\" ORDER BY \"Id\";",
                "SELECT * FROM \"BookFiles\" ORDER BY \"Id\";"
            };
            return JsonSerializer.Serialize(queries.Select(sql => connection.Query(sql).ToArray()).ToArray());
        }

        private static string SnapshotKeys(IDbConnection connection)
        {
            var queries = new[]
            {
                "SELECT \"Id\", \"CleanTitle\" FROM \"Books\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"CleanName\" FROM \"Authors\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"CleanName\" FROM \"Narrators\" ORDER BY \"Id\";",
                "SELECT \"Id\", \"MatchingTitle\" FROM \"Editions\" ORDER BY \"Id\";"
            };
            return JsonSerializer.Serialize(queries.Select(sql => connection.Query(sql).ToArray()).ToArray());
        }

        internal static string[] CaptureMigration(string connectionString, DatabaseType databaseType, int version = 113)
        {
            var previousLogging = LogManager.Configuration;
            var target = new MemoryTarget { Layout = "${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Info, LogLevel.Fatal, target);
            try
            {
                LogManager.Configuration = logging;
                ApplyMigration(connectionString, databaseType, version);
                foreach (var line in target.Logs.Where(line => line.StartsWith("Comparison-key rebuild", StringComparison.Ordinal)))
                {
                    TestContext.WriteLine(databaseType + " " + line);
                }

                return target.Logs.ToArray();
            }
            finally
            {
                LogManager.Configuration = previousLogging;
            }
        }

        private static void ApplyMigration(string connectionString, DatabaseType databaseType, int version)
        {
            var controller = new MigrationController(LogManager.GetLogger(nameof(RebuildSharedComparisonKeysMigrationFixture)), null);
            controller.Migrate(connectionString, new MigrationContext(MigrationType.Main, version), databaseType);
        }
    }
}
