using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Chaptarr.Core.Test.Common;
using Dapper;
using FluentMigrator;
using FluentMigrator.Expressions;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Generators;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class MigrationRebuildBudgetFixture
    {
        private sealed record CommandReceipt(int Version, string Sql, int Timeout, int Parameters, string Operation, long ElapsedMilliseconds);

        private sealed class Capture
        {
            public int Version;
            public readonly List<CommandReceipt> Commands = new();

            public IDbConnection Wrap(IDbConnection connection) => InterfaceProxy.Proxy<IDbConnection>((method, args) =>
            {
                var result = Invoke(method, connection, args);
                return result is IDbCommand command ? InterfaceProxy.Proxy<IDbCommand>((commandMethod, commandArgs) =>
                {
                    var timer = Stopwatch.StartNew();
                    var value = Invoke(commandMethod, command, commandArgs);
                    if (commandMethod.Name.StartsWith("Execute", StringComparison.Ordinal))
                        Commands.Add(new CommandReceipt(Version, command.CommandText, command.CommandTimeout, command.Parameters.Count, commandMethod.Name, timer.ElapsedMilliseconds));
                    return value;
                }) : result;
            });
        }

        private static object Invoke(MethodInfo method, object target, object[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static void Upgrade(IMainDatabase database, bool postgres, long version, Capture capture, bool observeCommands = true)
        {
            capture.Version = 0;
            using var connection = database.OpenConnection();
            var connectionString = connection.ConnectionString;
            using var provider = new ServiceCollection().AddFluentMigratorCore()
                .ConfigureRunner(builder => builder.AddPostgres().AddSQLite().WithGlobalConnectionString(connectionString)
                    .ScanIn(typeof(rebuild_shared_comparison_keys).Assembly).For.All())
                .Configure<TypeFilterOptions>(options => options.Namespace = "NzbDrone.Core.Datastore.Migration")
                .Configure<ProcessorOptions>(options => options.Timeout = TimeSpan.FromSeconds(300))
                .Configure<SelectingProcessorAccessorOptions>(options => options.ProcessorId = postgres ? "PostgreSQL" : "SQLite")
                .Configure<SelectingGeneratorAccessorOptions>(options => options.GeneratorId = postgres ? "PostgreSQL" : "SQLite")
                .AddScoped<IProcessorAccessor>(services =>
                {
                    var processor = ActivatorUtilities.CreateInstance<SelectingProcessorAccessor>(services).Processor;
                    var observed = InterfaceProxy.Proxy<IMigrationProcessor>((method, args) =>
                    {
                        if (observeCommands && capture.Version >= 108 && args?.FirstOrDefault() is PerformDBOperationExpression expression)
                        {
                            var operation = expression.Operation;
                            expression.Operation = (native, transaction) => operation(capture.Wrap(native), transaction);
                        }

                        return Invoke(method, processor, args);
                    });
                    return InterfaceProxy.Proxy<IProcessorAccessor>((method, args) => observed);
                }).BuildServiceProvider();
            try
            {
                MigrationContext.Current = new MigrationContext(MigrationType.Main, version) { BeforeMigration = migration => capture.Version = migration.Version };
                provider.GetRequiredService<IMigrationRunner>().MigrateUp(version);
            }
            finally { MigrationContext.Current = null; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void released_107_upgrade_should_skip_superseded_108_work_and_preserve_final_search_semantics(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                Seed(connection);
                var capture = new Capture();
                Upgrade(database, postgres, 108, capture);
                Assert.That(capture.Commands, Is.Empty, "The unreleased duplicate rebuild must defer to the bounded shared rebuild.");
                Assert.That(connection.QuerySingle<string>("SELECT \"CleanTitle\" FROM \"Books\" WHERE \"Id\"=1;"), Is.EqualTo("stale"));
                Upgrade(database, postgres, 115, capture);
                Assert.That(connection.QuerySingle<long>("SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(115));
                Assert.That(connection.Query<string>("SELECT \"CleanTitle\" FROM \"Books\" ORDER BY \"Id\";"), Is.EqualTo(new[] { "cafe", "cafe" }));
                Assert.That(connection.Query<int>("SELECT \"MediaType\" FROM \"Books\" ORDER BY \"Id\";"), Is.EqualTo(new[] { 0, 1 }));
                Assert.That(connection.Query<string>("SELECT \"HardcoverBookId\" FROM \"Books\" ORDER BY \"Id\";"), Is.EqualTo(new[] { "hc:shared", "hc:shared" }));
                Assert.That(connection.QuerySingle<string>("SELECT \"Name\" FROM \"Authors\" WHERE \"Id\"=1;"), Is.EqualTo("Straße Voice"));
                Assert.That(connection.QuerySingle<string>("SELECT \"SearchName\" FROM \"Authors\" WHERE \"Id\"=1;"), Is.EqualTo("strasse voice"));
                Assert.That(connection.QuerySingle<string>("SELECT \"SearchNarratorNames\" FROM \"Editions\" WHERE \"Id\"=1;"), Is.EqualTo("soren voice королев reader"));
                Assert.That(connection.QuerySingle<string>("SELECT \"NarratorNames\" FROM \"Editions\" WHERE \"Id\"=1;"), Is.EqualTo("[\"Søren Voice\",\"Королёв Reader\"]"));
                Assert.That(connection.QuerySingle<string>("SELECT \"SearchSubtitle\" FROM \"Editions\" WHERE \"Id\"=1;"), Is.EqualTo("aelfric notes"));
                Assert.That(connection.QuerySingle<string>("SELECT \"SearchPublisher\" FROM \"Editions\" WHERE \"Id\"=1;"), Is.EqualTo("aegir press"));
                if (postgres)
                    Assert.That(connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""Editions"" WHERE to_tsvector('simple', COALESCE(NULLIF(""SearchNarratorNames"", ''), ""SearchNarrator"", '')) @@ plainto_tsquery('simple', 'soren');"), Is.EqualTo(1));
                else
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'Narrator:soren';"), Is.EqualTo(1));
                Assert.That(capture.Commands.Where(command => command.Version == 111 || command.Version >= 113).Select(command => command.Timeout), Has.All.EqualTo(300));
                Assert.That(capture.Commands.Select(command => command.Parameters), Has.All.LessThanOrEqualTo(900));
                foreach (var group in capture.Commands.GroupBy(command => command.Version))
                    TestContext.Progress.WriteLine($"Upgrade {group.Key} ({(postgres ? "PG" : "SQLite")}): direct commands {group.Count()}, readers {group.Count(command => command.Operation == "ExecuteReader")}, writes {group.Count(command => command.Operation == "ExecuteNonQuery")}, elapsed command {group.Sum(command => command.ElapsedMilliseconds)} ms");
            }, desiredVersion: 107);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void totality_rebuild_should_use_runner_budget_for_every_direct_command_and_drop_old_pg_indexes_first(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                Seed(connection);
                var capture = new Capture();
                Upgrade(database, postgres, 115, capture);
                Assert.That(capture.Commands, Is.Not.Empty);
                Assert.That(capture.Commands.Select(command => command.Timeout), Has.All.EqualTo(300));
                Assert.That(capture.Commands.Any(command => command.Operation == "ExecuteReader"), Is.True);
                Assert.That(capture.Commands.Any(command => command.Sql.StartsWith("UPDATE", StringComparison.Ordinal)), Is.True);
                if (postgres)
                {
                    Assert.That(capture.Commands.First().Sql, Does.Contain("DROP INDEX IF EXISTS idx_authors_fts"));
                    var definitions = connection.Query<string>("SELECT indexdef FROM pg_indexes WHERE indexname IN ('idx_authors_fts','idx_books_series_fts','idx_editions_narrator_fts','idx_editions_publisher_fts') ORDER BY indexname;").ToList();
                    Assert.That(definitions, Has.Count.EqualTo(4));
                    Assert.That(definitions, Has.Some.Contains("SearchNarratorNames"));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM pg_indexes WHERE indexname IN ('idx_editions_matching_fts','idx_editions_ebook_fts');"), Is.Zero);
                }
                TestContext.Progress.WriteLine($"115 {(postgres ? "PG" : "SQLite")}: {capture.Commands.Count} actual direct commands, all timeout300s, maxparameters {capture.Commands.Max(command => command.Parameters)}");
            }, desiredVersion: 114);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void command_capture_should_detect_an_omitted_dapper_timeout(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                var capture = new Capture();
                capture.Wrap(connection).Execute("SELECT 1;");
                Assert.That(capture.Commands.Single().Timeout, Is.EqualTo(30));
                Assert.That(capture.Commands.Single().Timeout, Is.Not.EqualTo(300));
            }, desiredVersion: 107);
        }


        [TestCase(false)]
        [TestCase(true)]
        public void import_list_upgrade_should_bound_alias_pages_preserve_sources_and_share_runner_budget(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                SeedAliasPages(connection, postgres);
                var sources = connection.Query<string>(@"SELECT CAST(""Id"" AS TEXT) || ':' || COALESCE(""Asins"", '') FROM ""Editions"" ORDER BY ""Id"";").ToArray();
                var savedAliases = connection.Query<string>(@"SELECT CAST(""Id"" AS TEXT) || ':' || CAST(""CreatedAt"" AS TEXT) || ':' || CAST(""UpdatedAt"" AS TEXT) FROM ""ProviderAliasIndex"" ORDER BY ""Id"";").ToArray();
                var previousLogging = LogManager.Configuration;
                var warnings = new MemoryTarget { Layout = "${level}|${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Warn, LogLevel.Fatal, warnings);
                var capture = new Capture();
                try
                {
                    LogManager.Configuration = logging;
                    Upgrade(database, postgres, 111, capture);
                }
                finally
                {
                    LogManager.Configuration = previousLogging;
                }

                Assert.That(warnings.Logs, Has.Some.Contains("Cannot parse stored edition ASIN collection for local edition 900"));
                var commands = capture.Commands.Where(command => command.Version == 111).ToList();
                var reads = commands.Where(command => command.Operation == "ExecuteReader" && command.Sql.Contains("FROM \"Editions\"", StringComparison.Ordinal)).ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(commands, Is.Not.Empty);
                    Assert.That(commands.Select(command => command.Timeout), Has.All.EqualTo(MigrationController.CommandTimeoutSeconds));
                    Assert.That(commands.Select(command => command.Parameters), Has.All.LessThanOrEqualTo(SqliteVariableLimit.MaxParameters));
                    Assert.That(reads, Has.Count.EqualTo(3));
                    Assert.That(reads.All(command => command.Sql.Contains("WHERE \"Id\" > @AfterId ORDER BY \"Id\" LIMIT @BatchSize", StringComparison.Ordinal)), Is.True);
                    Assert.That(reads.Select(command => command.Parameters), Has.All.EqualTo(2));
                    Assert.That(connection.Query<string>(@"SELECT CAST(""Id"" AS TEXT) || ':' || COALESCE(""Asins"", '') FROM ""Editions"" ORDER BY ""Id"";"), Is.EqualTo(sources));
                    Assert.That(ReadAliasKeys(connection), Is.EquivalentTo(ExpectedAliasKeys()));
                    Assert.That(connection.Query<string>(@"SELECT CAST(""Id"" AS TEXT) || ':' || CAST(""CreatedAt"" AS TEXT) || ':' || CAST(""UpdatedAt"" AS TEXT) FROM ""ProviderAliasIndex"" WHERE ""Id"" IN (1,2) ORDER BY ""Id"";"), Is.EqualTo(savedAliases));
                });
                var pageReadIndexes = commands.Select((command, index) => (command, index))
                    .Where(row => row.command.Operation == "ExecuteReader" && row.command.Sql.Contains("FROM \"Editions\"", StringComparison.Ordinal))
                    .Select(row => row.index).ToArray();
                for (var index = 1; index < pageReadIndexes.Length; index++)
                {
                    var betweenPages = commands.Skip(pageReadIndexes[index - 1] + 1).Take(pageReadIndexes[index] - pageReadIndexes[index - 1] - 1);
                    Assert.That(betweenPages.Any(command => command.Operation == "ExecuteNonQuery" && command.Sql.StartsWith("INSERT INTO \"ProviderAliasIndex\"", StringComparison.Ordinal)), Is.True,
                        "Each populated page must flush its aliases before the next edition read.");
                }
                foreach (var table in new[] { "RootFolders", "ImportLists", "QualityProfiles", "MetadataProfiles" })
                {
                    Assert.That(commands.Count(command => command.Operation == "ExecuteReader" && command.Sql.Contains("FROM \"" + table + "\"", StringComparison.Ordinal)), Is.EqualTo(1), table);
                }
                Assert.That(commands.Any(command => command.Operation == "ExecuteNonQuery" && command.Sql.StartsWith("UPDATE \"ImportLists\"", StringComparison.Ordinal)), Is.True);
                Assert.That(commands.Any(command => command.Operation == "ExecuteNonQuery" && command.Sql.StartsWith("INSERT INTO \"ProviderAliasIndex\"", StringComparison.Ordinal)), Is.True);
                var firstKeys = ReadAliasKeys(connection);
                using (var transaction = connection.BeginTransaction())
                {
                    typeof(add_import_list_monitoring_options).GetMethod("BackfillEditionAliases", BindingFlags.NonPublic | BindingFlags.Static)
                        .Invoke(null, new object[] { capture.Wrap(connection), transaction, LogManager.GetCurrentClassLogger() });
                    transaction.Commit();
                }
                Assert.That(ReadAliasKeys(connection), Is.EqualTo(firstKeys));
                Assert.That(connection.QuerySingle<long>("SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(111));
                TestContext.Progress.WriteLine($"111 {(postgres ? "PG" : "SQLite")}: 901 nonconsecutive editions, 3 keyset pages, 1802 retained aliases; all direct commands timeout300s, maxparameters {commands.Max(command => command.Parameters)}; repeat unchanged.");
            }, desiredVersion: 110);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_alias_insert_should_roll_back_prior_pages_settings_schema_and_migration_version(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                SeedAliasPages(connection, postgres);
                var aliases = ReadAliasKeys(connection);
                var settings = connection.QuerySingle<string>("SELECT \"Settings\" FROM \"ImportLists\";");
                if (postgres)
                {
                    connection.Execute(@"CREATE FUNCTION fixture_alias_reject() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                        IF NEW.""EntityId"" >= 900 THEN RAISE EXCEPTION 'fixture alias rollback'; END IF; RETURN NEW; END; $$;
                        CREATE TRIGGER fixture_alias_reject BEFORE INSERT ON ""ProviderAliasIndex"" FOR EACH ROW EXECUTE FUNCTION fixture_alias_reject();");
                }
                else
                {
                    connection.Execute(@"CREATE TRIGGER fixture_alias_reject BEFORE INSERT ON ""ProviderAliasIndex"" WHEN NEW.""EntityId"" >= 900 BEGIN SELECT RAISE(ABORT, 'fixture alias rollback'); END;");
                }
                var capture = new Capture();
                Assert.That(() => Upgrade(database, postgres, 111, capture), Throws.Exception.With.Message.Contains("fixture alias rollback"));
                Assert.That(capture.Commands.Any(command => command.Sql.StartsWith("INSERT INTO \"ProviderAliasIndex\"", StringComparison.Ordinal)), Is.True, "An earlier successful write must be rolled back with the failed page.");
                var monitoringColumns = postgres
                    ? connection.QuerySingle<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_name='ImportLists' AND column_name='MonitoringOptions';")
                    : connection.QuerySingle<int>("SELECT COUNT(*) FROM pragma_table_info('ImportLists') WHERE name='MonitoringOptions';");
                Assert.Multiple(() =>
                {
                    Assert.That(ReadAliasKeys(connection), Is.EqualTo(aliases));
                    Assert.That(connection.QuerySingle<string>("SELECT \"Settings\" FROM \"ImportLists\";"), Is.EqualTo(settings));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"Editions\";"), Is.EqualTo(901));
                    Assert.That(connection.QuerySingle<long>("SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(110));
                    Assert.That(monitoringColumns, Is.Zero);
                });
                connection.Execute(postgres
                    ? "DROP TRIGGER fixture_alias_reject ON \"ProviderAliasIndex\"; DROP FUNCTION fixture_alias_reject();"
                    : "DROP TRIGGER fixture_alias_reject;");
                Upgrade(database, postgres, 111, new Capture());
                Assert.That(ReadAliasKeys(connection), Is.EquivalentTo(ExpectedAliasKeys()));
            }, desiredVersion: 110);
        }

        private static void SeedAliasPages(IDbConnection connection, bool postgres)
        {
            Seed(connection);
            connection.Execute(@"UPDATE ""Editions"" SET ""ForeignEditionId""='gr:456', ""GoodreadsEditionId""=456, ""Asins""='[""B00QALIAS1"",""B00QALIAS1""]' WHERE ""Id""=1;
                INSERT INTO ""ImportLists"" (""Name"",""Implementation"",""ConfigContract"",""RootFolderPath"",""QualityProfileId"",""MetadataProfileId"",""Tags"",""Settings"",""ShouldMonitor"",""ShouldMonitorExisting"",""MonitorNewItems"",""EnableAutomaticAdd"",""ShouldSearch"")
                VALUES ('Paged alias control','ChaptarrImport','ChaptarrSettings','/fixture',0,0,'[]','{}',0,@Yes,0,@No,@No);
                INSERT INTO ""ProviderAliasIndex"" (""EntityType"",""EntityId"",""Scope"",""Provider"",""NormalizedProviderId"",""CreatedAt"",""UpdatedAt"")
                VALUES ('Edition',1,'edition','gr','456',@Old,@Old),('Edition',1,'edition','gr','99999',@Old,@Old);",
                new { Yes = true, No = false, Old = new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc) });
            var identity = postgres ? "OVERRIDING SYSTEM VALUE" : string.Empty;
            connection.Execute($@"WITH RECURSIVE ids(id) AS (SELECT 1 UNION ALL SELECT id+1 FROM ids WHERE id<900)
                INSERT INTO ""Editions"" (""Id"",""BookId"",""Title"",""Monitored"",""GoodreadsEditionId"",""Asins"") {identity}
                SELECT id*2, 1, 'Paged alias edition', @Yes, 10000+id, CASE WHEN id=450 THEN '{{broken' ELSE '[""B00QALIAS1"",""B00QALIAS1""]' END FROM ids;", new { Yes = true });
        }

        private static string[] ReadAliasKeys(IDbConnection connection) => connection.Query<string>(@"SELECT CAST(""EntityId"" AS TEXT) || ':' || ""Provider"" || ':' || ""NormalizedProviderId"" FROM ""ProviderAliasIndex"" ORDER BY ""EntityId"",""Provider"",""NormalizedProviderId"";").ToArray();

        private static string[] ExpectedAliasKeys() => new[] { "1:gr:456", "1:gr:99999", "1:az:B00QALIAS1" }
            .Concat(Enumerable.Range(1,900).Select(id => $"{id*2}:gr:{10000+id}"))
            .Concat(Enumerable.Range(1,900).Where(id => id != 450).Select(id => $"{id*2}:az:B00QALIAS1")).ToArray();

        [TestCase(false)]
        [TestCase(true)]
        public void complete_released_upgrade_preserves_recordings_history_and_extra_files(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                Seed(connection);
                var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                connection.Execute(@"UPDATE ""Books"" SET ""HardcoverBookId""='hc:12345', ""TitleSlug""='cafe';
                    UPDATE ""Authors"" SET ""GoodreadsAuthorId""='gr:58', ""AudiobookTags""='[1]', ""EbookTags""='[2]';
                    INSERT INTO ""Books"" (""AuthorId"",""Title"",""CleanTitle"",""AnyEditionOk"",""Added"",""MediaType"",""HardcoverBookId"",""TitleSlug"")
                        VALUES (1,'Café','stale',@Yes,@Now,0,'hc:12345','cafe');
                    INSERT INTO ""Editions"" (""BookId"",""Title"",""Monitored"",""NarratorNames"",""ReadingFormatId"",""Asin"")
                        VALUES (2,'Café',@Yes,'[]',3,'B00CFTEST01'),(3,'Café',@Yes,'[""Another Reader""]',2,'B00CFTEST02');
                    UPDATE ""Editions"" SET ""ReadingFormatId""=2, ""Asin""='B00CFTEST03' WHERE ""Id""=1;
                    INSERT INTO ""BookFiles"" (""Path"",""Size"",""Modified"",""DateAdded"",""Quality"",""MediaInfo"",""EditionId"",""CalibreId"",""Part"",""MediaType"")
                        VALUES ('/audio/Café/Original.m4b',123456,@Now,@Now,'{}','{}',1,0,1,'audiobook'),
                               ('/ebooks/Café/Book.epub',4321,@Now,@Now,'{}','{}',2,0,1,'ebook'),
                               ('/audio/Café/Other recording.m4b',456789,@Now,@Now,'{}','{}',3,0,1,'audiobook');
                    INSERT INTO ""History"" (""BookId"",""AuthorId"",""EditionId"",""Quality"",""Date"",""EventType"",""Data"",""SourceTitle"",""DownloadId"")
                        VALUES (1,1,1,'{}',@Now,3,'{}','Café original','download-a'),(3,1,3,'{}',@Now,3,'{}','Café other recording','download-b');
                    INSERT INTO ""DownloadHistory"" (""BookId"",""AuthorId"",""Date"",""EventType"",""SourceTitle"",""DownloadId"")
                        VALUES (1,1,@Now,1,'Café original','download-a'),(3,1,@Now,1,'Café other recording','download-b');
                    INSERT INTO ""ExtraFiles"" (""AuthorId"",""BookId"",""BookFileId"",""RelativePath"",""Extension"",""Added"",""LastUpdated"")
                        VALUES (1,1,1,'Café/cover.jpg','.jpg',@Now,@Now);", new { Yes = true, Now = now });
                string Snapshot(string table, string columns) => Newtonsoft.Json.JsonConvert.SerializeObject(connection.Query($"SELECT {columns} FROM \"{table}\" ORDER BY \"Id\""));
                var checks = new Dictionary<string, string>
                {
                    ["BookFiles"] = "\"Id\",\"Path\",\"EditionId\",\"Size\",\"MediaType\"",
                    ["Editions"] = "\"Id\",\"BookId\",\"NarratorNames\",\"ReadingFormatId\"",
                    ["Books"] = "\"Id\",\"AuthorId\",\"MediaType\"",
                    ["History"] = "\"Id\",\"BookId\",\"AuthorId\",\"EditionId\",\"DownloadId\"",
                    ["DownloadHistory"] = "\"Id\",\"BookId\",\"AuthorId\",\"DownloadId\"",
                    ["ExtraFiles"] = "\"Id\",\"AuthorId\",\"BookId\",\"BookFileId\",\"RelativePath\""
                };
                var before = checks.ToDictionary(pair => pair.Key, pair => Snapshot(pair.Key, pair.Value));
                var capture = new Capture();
                for (var version = 108; version <= 128; version++)
                {
                    var watch = Stopwatch.StartNew();
                    Upgrade(database, postgres, version, capture, observeCommands: false);
                    TestContext.Progress.WriteLine($"Complete upgrade {(postgres ? "PG" : "SQLite")} through {version}: {watch.ElapsedMilliseconds} ms");
                }
                foreach (var check in checks)
                    Assert.That(Snapshot(check.Key, check.Value), Is.EqualTo(before[check.Key]), check.Key);
                Assert.That(connection.ExecuteScalar<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\""), Is.EqualTo(128));
                Assert.That(connection.Query<string>("SELECT \"TitleSlug\" FROM \"Books\" ORDER BY \"Id\""), Is.EqualTo(new[] { "cafe", "cafe", "cafe_2" }));
                Assert.That(connection.ExecuteScalar<string>("SELECT \"Title\" FROM \"QualityDefinitions\" WHERE \"Quality\"=0"), Is.EqualTo("Unknown"));
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition' AND \"Provider\"='az'"), Is.EqualTo(3));
            }, desiredVersion: 107);
        }

        private static void Seed(IDbConnection connection)
        {
            connection.Execute(@"INSERT INTO ""Authors"" (""Name"",""CleanName"",""Path"",""Monitored"",""Added"") VALUES ('Straße Voice','stale','/fixture',@Yes,@Now);
                INSERT INTO ""Books"" (""AuthorId"",""Title"",""CleanTitle"",""AnyEditionOk"",""Added"",""MediaType"",""HardcoverBookId"",""SeriesName"") VALUES (1,'Café','stale',@Yes,@Now,0,'hc:shared','Łukasz Archive'),(1,'Café','stale',@Yes,@Now,1,'hc:shared','Łukasz Archive');
                INSERT INTO ""Editions"" (""BookId"",""Title"",""MatchingTitle"",""Monitored"",""Subtitle"",""Narrator"",""NarratorNames"",""Publisher"") VALUES (1,'Café','stale',@Yes,'Ælfric Notes','Søren Voice','[""Søren Voice"",""Королёв Reader""]','Ægir Press');", new { Yes = true, Now = DateTime.UtcNow });
        }
    }
}
