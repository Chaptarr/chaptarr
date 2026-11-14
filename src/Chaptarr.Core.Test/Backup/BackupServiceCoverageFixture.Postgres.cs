using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using Npgsql;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Backup
{
    public partial class BackupServiceCoverageFixture
    {
        private sealed class PostgresRole : IDisposable
        {
            private readonly string _admin;
            public string Name { get; } = "backup_role_" + Guid.NewGuid().ToString("N");
            public string Password { get; } = "synthetic:" + Guid.NewGuid().ToString("N") + "\\password'Å";

            public PostgresRole(bool canCreate = false)
            {
                _admin = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
                if (string.IsNullOrEmpty(_admin))
                {
                    Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL role permissions");
                }
                using var admin = PostgresBackup.Open(_admin, "postgres");
                admin.Execute("CREATE ROLE " + PostgresBackup.Identifier(Name) + " LOGIN NOSUPERUSER NOCREATEROLE " +
                    (canCreate ? "CREATEDB" : "NOCREATEDB") + " PASSWORD '" + Password.Replace("'", "''") + "'");
            }

            public void Dispose()
            {
                using var admin = PostgresBackup.Open(_admin, "postgres");
                admin.Execute("DROP ROLE " + PostgresBackup.Identifier(Name));
            }
        }

        private sealed class PostgresStores : IDisposable
        {
            private readonly string _admin;
            public readonly Dictionary<string, string> Connections = new();

            public PostgresStores(string marker, PostgresRole role = null, bool alternateLocale = false)
            {
                _admin = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
                if (string.IsNullOrEmpty(_admin))
                {
                    Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise native PostgreSQL backup and restore");
                }
                using var admin = PostgresBackup.Open(_admin, "postgres");
                var locale = alternateLocale ? admin.QueryFirstOrDefault<string>("SELECT collcollate FROM pg_collation WHERE collprovider = 'c' AND collencoding IN (-1, pg_char_to_encoding('UTF8')) AND collcollate NOT IN ('C', 'POSIX') AND collcollate <> (SELECT datcollate FROM pg_database WHERE datname = current_database()) ORDER BY collname LIMIT 1") : null;
                if (alternateLocale && locale == null) Assert.Ignore("The PostgreSQL test server needs two distinct libc locales for locale preservation coverage");
                foreach (var component in new[] { "chaptarr", "logs", "cache" })
                {
                    var name = "backup_test_" + Guid.NewGuid().ToString("N") + "_" + component;
                    admin.Execute("CREATE DATABASE " + PostgresBackup.Identifier(name) + " TEMPLATE template0" + (alternateLocale ? " ENCODING 'UTF8' LC_COLLATE '" + locale.Replace("'", "''") + "' LC_CTYPE '" + locale.Replace("'", "''") + "'" : "") + (role == null ? "" : " OWNER " + PostgresBackup.Identifier(role.Name)));
                    var builder = new NpgsqlConnectionStringBuilder(_admin) { Database = name, Pooling = false };
                    if (role != null)
                    {
                        builder.Username = role.Name;
                        builder.Password = role.Password;
                    }
                    var connectionString = builder.ConnectionString;
                    Connections[component + ".dump"] = connectionString;
                    var type = component == "chaptarr" ? MigrationType.Main : component == "logs" ? MigrationType.Log : MigrationType.Cache;
                    new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connectionString, new MigrationContext(type), DatabaseType.PostgreSQL);
                    using var connection = PostgresBackup.Open(connectionString);
                    connection.Execute("CREATE TABLE \"BackupProbe\" (\"Id\" serial PRIMARY KEY, \"Text\" text, \"Binary\" bytea, \"Optional\" text, \"Number\" numeric, \"Date\" timestamp)");
                    connection.Execute("INSERT INTO \"BackupProbe\" (\"Text\", \"Binary\", \"Number\", \"Date\") VALUES (@Text, @Blob, 1.25, '2020-01-01'), ('', NULL, -2.5, NULL)",
                        new { Text = marker + " Å & \"", Blob = new byte[] { 0, 1, 255 } });
                }
            }

            public IConnectionStringFactory Factory => InterfaceProxy.Proxy<IConnectionStringFactory>((method, _) => new DatabaseConnectionInfo(DatabaseType.PostgreSQL,
                Connections[method.Name == "get_MainDbConnection" ? "chaptarr.dump" : method.Name == "get_LogDbConnection" ? "logs.dump" : "cache.dump"]));

            public IMainDatabase Main => new MainDatabase(new Database("backup-pg", () => PostgresBackup.Open(Connections["chaptarr.dump"])));
            public ILogDatabase Logs => new LogDatabase(new Database("backup-pg-logs", () => PostgresBackup.Open(Connections["logs.dump"])));
            public ICacheDatabase Cache => new CacheDatabase(new Database("backup-pg-cache", () => PostgresBackup.Open(Connections["cache.dump"])));

            public Dictionary<string, Dictionary<string, string[]>> Snapshot() => Connections.ToDictionary(entry => entry.Key, entry => PostgresSnapshot(entry.Value));

            public void Dispose()
            {
                using var admin = PostgresBackup.Open(_admin, "postgres");
                foreach (var value in Connections.Values)
                {
                    var name = new NpgsqlConnectionStringBuilder(value).Database;
                    admin.Execute("DROP DATABASE IF EXISTS " + PostgresBackup.Identifier(name));
                }
            }
        }

        private static Dictionary<string, string[]> PostgresSnapshot(string connectionString)
        {
            using var connection = PostgresBackup.Open(connectionString);
            var result = new Dictionary<string, string[]>();
            var tables = connection.Query<string>("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name").ToList();
            foreach (var table in tables)
            {
                result["columns:" + table] = connection.Query<string>("SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @Table ORDER BY ordinal_position", new { Table = table }).ToArray();
                using var reader = connection.ExecuteReader("SELECT * FROM " + PostgresBackup.Identifier(table));
                var rows = new List<string>();
                while (reader.Read())
                {
                    rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null :
                        reader.GetValue(i) is byte[] bytes ? "blob:" + Convert.ToBase64String(bytes) :
                        reader.GetFieldType(i).Name + ":" + Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray()));
                }
                result["table:" + table] = rows.OrderBy(row => row, StringComparer.Ordinal).ToArray();
            }
            foreach (var sequence in connection.Query<string>("SELECT sequence_name FROM information_schema.sequences WHERE sequence_schema = 'public'").ToList())
            {
                result["sequence:" + sequence] = new[] { connection.ExecuteScalar<string>("SELECT last_value::text || ':' || is_called::text FROM " + PostgresBackup.Identifier(sequence)) };
            }
            result["indexes"] = connection.Query<string>("SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY indexname").ToArray();
            return result;
        }

        private static void AssertSnapshot(Dictionary<string, Dictionary<string, string[]>> expected, PostgresStores actual)
        {
            var snapshots = actual.Snapshot();
            Assert.Multiple(() =>
            {
                foreach (var component in expected)
                {
                    Assert.That(snapshots[component.Key].Keys, Is.EquivalentTo(component.Value.Keys), component.Key);
                    foreach (var entry in component.Value)
                    {
                        Assert.That(snapshots[component.Key].GetValueOrDefault(entry.Key), Is.EqualTo(entry.Value), component.Key + ":" + entry.Key);
                    }
                }
            });
        }

        private string PostgresArchive(PostgresStores stores, AppFolders folders)
        {
            SeedConfig(folders);
            SeedStaging(folders);
            var service = CreateService(stores.Main, folders, logs: stores.Logs, cache: stores.Cache, connectionStrings: stores.Factory);
            service.Backup(BackupType.Manual);
            var path = Directory.GetFiles(service.GetBackupFolder(BackupType.Manual)).Single();
            using var archive = ZipFile.OpenRead(path);
            Assert.That(archive.Entries.Select(entry => entry.FullName), Is.EquivalentTo(new[] { "chaptarr.dump", "logs.dump", "cache.dump", "config.xml", "INFO", BackupManifest.FileName }));
            return path;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void postgres_backup_space_estimate_must_exclude_indexes_but_still_require_table_space(bool insufficient)
        {
            using var stores = new PostgresStores("space");
            var folders = new AppFolders(Path.Combine(_root, "source"));
            SeedConfig(folders);
            using var connection = PostgresBackup.Open(stores.Connections["chaptarr.dump"]);
            connection.Execute("CREATE TABLE backup_size_probe AS SELECT i, repeat(md5(i::text), 16) AS value FROM generate_series(1, 5000) i");
            var tables = connection.Query<string>("SELECT quote_ident(schemaname) || '.' || quote_ident(tablename) FROM pg_tables WHERE schemaname = 'public'").ToList();
            var tableBytes = tables.Sum(table => connection.ExecuteScalar<long>("SELECT pg_table_size(@Table::regclass)", new { Table = table }));
            tableBytes += connection.ExecuteScalar<long>("SELECT COALESCE(SUM(pg_relation_size((quote_ident(schemaname) || '.' || quote_ident(sequencename))::regclass)), 0)::bigint FROM pg_sequences WHERE schemaname = 'public'");
            tableBytes += connection.ExecuteScalar<long>("SELECT pg_table_size('pg_catalog.pg_largeobject')");
            connection.Execute("CREATE INDEX backup_size_probe_large ON backup_size_probe(value)");
            var indexBytes = connection.ExecuteScalar<long>("SELECT pg_relation_size('backup_size_probe_large')");
            var databaseBytes = connection.ExecuteScalar<long>("SELECT pg_database_size(current_database())");
            Assert.That(indexBytes, Is.GreaterThan(0));
            var available = insufficient ? tableBytes - 1 : tableBytes + indexBytes / 2;
            Assert.That(available, Is.LessThan(databaseBytes));
            var service = CreateService(stores.Main, folders, connectionStrings: stores.Factory, includeExtras: false);
            var destination = service.GetBackupFolder(BackupType.Manual);
            _disk.SpaceForPath = path => path == destination ? long.MaxValue : available;
            if (insufficient)
            {
                var error = Assert.Throws<IOException>(() => service.Backup(BackupType.Manual));
                Assert.That(error.Message, Does.Contain("Insufficient space for backup creation"));
                Assert.That(service.GetBackups(), Is.Empty);
            }
            else
            {
                service.Backup(BackupType.Manual);
                var backup = service.GetBackups().Single();
                Assert.That(backup.IncludedComponents, Does.Contain("chaptarr.dump"));
                Assert.That(backup.Size, Is.LessThan(available));
                TestContext.WriteLine($"PG backup preflight: physical={databaseBytes}, tables={tableBytes}, index={indexBytes}, available={available}, archive={backup.Size} bytes");
            }
        }

        [Test]
        public void postgres_backup_must_work_when_password_authentication_redacts_the_open_connection_string()
        {
            using var role = new PostgresRole();
            using var stores = new PostgresStores("authenticated", role);
            var configured = stores.Connections["chaptarr.dump"];
            using var connection = PostgresBackup.Open(configured);
            Assert.That(string.IsNullOrEmpty(new NpgsqlConnectionStringBuilder(connection.ConnectionString).Password), Is.True,
                "An opened connection must not expose its password");

            var wrongPassword = new NpgsqlConnectionStringBuilder(configured) { Password = Guid.NewGuid().ToString("N") }.ConnectionString;
            var rejection = Assert.Throws<PostgresException>(() =>
            {
                using var rejected = PostgresBackup.Open(wrongPassword);
            }, "Use a password-authenticated PostgreSQL test server; trust authentication cannot verify credential preservation");
            Assert.That(rejection.SqlState, Is.EqualTo(PostgresErrorCodes.InvalidPassword));

            var password = new NpgsqlConnectionStringBuilder(configured).Password;
            Assert.That(string.IsNullOrEmpty(password), Is.False, "Configure a password for PostgreSQL credential coverage");
            var originalLogging = LogManager.Configuration;
            var messages = new NLog.Targets.MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
            var logging = new NLog.Config.LoggingConfiguration();
            logging.AddRule(LogLevel.Trace, LogLevel.Fatal, messages);
            LogManager.Configuration = logging;
            try
            {
                PostgresArchive(stores, new AppFolders(Path.Combine(_root, "authenticated")));
                Assert.That(Directory.GetDirectories(_root, "chaptarr_pgpass_*", SearchOption.AllDirectories), Is.Empty);

                var failure = Assert.Throws<InvalidOperationException>(() => PostgresBackup.Run(
                    PostgresBackup.Tool("pg_dump", PostgresBackup.ServerMajor(configured)),
                    new[] { "--schema=missing_" + Guid.NewGuid().ToString("N"), "--schema-only" }, configured, CancellationToken.None, _root));
                Assert.That(failure.ToString().Contains(password, StringComparison.Ordinal), Is.False, "Client failures must not expose credentials");
                Assert.That(Directory.GetDirectories(_root, "chaptarr_pgpass_*", SearchOption.AllDirectories), Is.Empty);
                Assert.That(messages.Logs.Any(message => message.Contains(password, StringComparison.Ordinal)), Is.False, "Backup logs must not expose credentials");
            }
            finally
            {
                LogManager.Configuration = originalLogging;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void omitted_postgres_auxiliary_stores_should_survive_restore_and_scratch_work_should_be_cleared(bool unavailableOptionalLog)
        {
            using var source = new PostgresStores("source");
            using var target = new PostgresStores("target");
            var sourceFolders = new AppFolders(Path.Combine(_root, "source"));
            var targetFolders = new AppFolders(Path.Combine(_root, "target"));
            SeedConfig(sourceFolders);
            SeedConfig(targetFolders);
            SeedStaging(targetFolders);
            var logs = PostgresSnapshot(target.Connections["logs.dump"]);
            var cache = PostgresSnapshot(target.Connections["cache.dump"]);
            var failedLog = InterfaceProxy.Proxy<ILogDatabase>((_, _) => throw new IOException("Optional log database is unavailable"));
            var backup = CreateService(source.Main, sourceFolders, logs: unavailableOptionalLog ? failedLog : source.Logs, cache: source.Cache,
                connectionStrings: source.Factory, includeExtras: unavailableOptionalLog);
            backup.Backup(BackupType.Manual);
            var item = backup.GetBackups().Single();
            Assert.That(item.IncludedComponents, Does.Not.Contain("logs.dump").And.Not.Contain("staging.db"));
            Assert.That(item.OmittedComponents, Is.EquivalentTo(unavailableOptionalLog ? new[] { "logs.dump" } : Array.Empty<string>()));
            var restoration = new DatabaseRestorationService(_disk, targetFolders, target.Factory);
            CreateService(target.Main, targetFolders, restoration, logs: target.Logs, cache: target.Cache).Restore(Path.Combine(backup.GetBackupFolder(BackupType.Manual), item.Name));
            DatabaseRestorationService.PrepareConfiguration(targetFolders);
            Assert.That(restoration.Restore(DatabaseType.PostgreSQL), Is.True);
            restoration.Commit();
            Assert.That(PostgresSnapshot(target.Connections["chaptarr.dump"]), Is.EquivalentTo(PostgresSnapshot(source.Connections["chaptarr.dump"])));
            Assert.That(PostgresSnapshot(target.Connections["logs.dump"]), Is.EquivalentTo(logs));
            Assert.That(PostgresSnapshot(target.Connections["cache.dump"]), Is.EquivalentTo(unavailableOptionalLog ? PostgresSnapshot(source.Connections["cache.dump"]) : cache));
            Assert.That(File.Exists(targetFolders.GetStagingDatabase()), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void postgres_restore_must_preserve_the_source_locale_instead_of_template_defaults(bool olderCollationVersion)
        {
            using var source = new PostgresStores("source", alternateLocale: true);
            using var target = new PostgresStores("target");
            using (var original = PostgresBackup.Open(source.Connections["chaptarr.dump"]))
            using (var destination = PostgresBackup.Open(target.Connections["chaptarr.dump"]))
            {
                Assert.That(PostgresBackup.DatabaseSettings(original).Collation, Is.Not.EqualTo(PostgresBackup.DatabaseSettings(destination).Collation), "The fixture must exercise a different locale");
            }
            var archive = PostgresArchive(source, new AppFolders(Path.Combine(_root, "source")));
            if (olderCollationVersion)
            {
                var folder = Path.Combine(_root, "older-locale-version");
                ZipFile.ExtractToDirectory(archive, folder);
                var manifest = BackupManifest.Read(folder);
                foreach (var settings in manifest.PostgresDatabases.Values) settings.CollationVersion = "older-provider-version";
                manifest.Write(folder);
                File.Delete(archive);
                ZipFile.CreateFromDirectory(folder, archive);
            }
            var targetFolders = new AppFolders(Path.Combine(_root, "target"));
            SeedConfig(targetFolders);
            SeedStaging(targetFolders);
            var restoration = new DatabaseRestorationService(_disk, targetFolders, target.Factory);
            CreateService(target.Main, targetFolders, restoration, logs: target.Logs, cache: target.Cache).Restore(archive);
            DatabaseRestorationService.PrepareConfiguration(targetFolders);
            Assert.That(restoration.Restore(DatabaseType.PostgreSQL), Is.True);
            foreach (var entry in source.Connections)
            {
                using var original = PostgresBackup.Open(entry.Value);
                using var restored = PostgresBackup.Open(target.Connections[entry.Key]);
                Assert.That(JsonSerializer.Serialize(PostgresBackup.DatabaseSettings(restored)),
                    Is.EqualTo(JsonSerializer.Serialize(PostgresBackup.DatabaseSettings(original))));
            }
            restoration.Commit();
        }

        [TestCase("commit")]
        [TestCase("rollback")]
        [TestCase("interrupted")]
        public void postgres_full_restore_should_preserve_every_store_and_recover_the_original_cohort(string outcome)
        {
            using var source = new PostgresStores("source");
            using var target = new PostgresStores("target");
            var sourceFolders = new AppFolders(Path.Combine(_root, "pg-source"));
            var targetFolders = new AppFolders(Path.Combine(_root, "pg-target"));
            var archive = PostgresArchive(source, sourceFolders);
            var artifactFolder = Environment.GetEnvironmentVariable("CHAPTARR_BACKUP_TEST_ARTIFACTS");
            if (outcome == "commit" && !string.IsNullOrWhiteSpace(artifactFolder))
            {
                Directory.CreateDirectory(artifactFolder);
                File.Copy(archive, Path.Combine(artifactFolder, "postgres-round-trip.zip"), true);
            }
            var expected = source.Snapshot();
            var original = target.Snapshot();
            SeedConfig(targetFolders);
            File.WriteAllText(targetFolders.GetConfigPath(), "<Config><ApiKey>target-value</ApiKey></Config>");
            SeedStaging(targetFolders);
            using (var queue = Open(ConnectionString(targetFolders.GetStagingDatabase())))
            {
                queue.Execute("UPDATE ingest_queue SET path = 'original-queue'");
            }
            var originalConfig = File.ReadAllBytes(targetFolders.GetConfigPath());
            var originalQueue = Snapshot(ConnectionString(targetFolders.GetStagingDatabase()));
            var restoration = new DatabaseRestorationService(_disk, targetFolders, target.Factory);
            CreateService(target.Main, targetFolders, restoration, logs: target.Logs, cache: target.Cache).Restore(archive);
            AssertSnapshot(original, target);
            Assert.That(File.ReadAllBytes(targetFolders.GetConfigPath()), Is.EqualTo(originalConfig));
            DatabaseRestorationService.PrepareConfiguration(targetFolders);
            Assert.That(restoration.Restore(DatabaseType.PostgreSQL), Is.True);
            AssertSnapshot(expected, target);
            var config = XDocument.Load(targetFolders.GetConfigPath());
            Assert.That(config.Root.Element("PostgresMainDb"), Is.Null, "Environment-only target settings must not be written to config.xml");
            Assert.That(config.Root.Element("BackupCoverageFutureKey").Value, Is.EqualTo("unknown value & Unicode Å"));
            if (outcome == "commit")
            {
                restoration.Commit();
                Assert.That(Directory.Exists(targetFolders.GetDatabaseRestoreBundle()), Is.False);
            }
            else
            {
                if (outcome == "interrupted")
                {
                    DatabaseRestorationService.PrepareConfiguration(targetFolders);
                    Assert.That(new DatabaseRestorationService(_disk, targetFolders, target.Factory).Restore(DatabaseType.PostgreSQL), Is.False);
                }
                else
                {
                    restoration.Rollback();
                }
                AssertSnapshot(original, target);
                Assert.That(File.ReadAllBytes(targetFolders.GetConfigPath()), Is.EqualTo(originalConfig));
                Assert.That(Snapshot(ConnectionString(targetFolders.GetStagingDatabase())), Is.EquivalentTo(originalQueue));
            }
            TestContext.Progress.WriteLine($"PostgreSQL {outcome}: main/log/cache schemas, rows, sequences, config and queue checked");
        }

        [TestCase("checksum")]
        [TestCase("truncated-cache")]
        [TestCase("future-schema")]
        [TestCase("future-server")]
        [TestCase("busy-cache")]
        [TestCase("unavailable-locale")]
        public void postgres_restore_failure_should_leave_all_active_data_and_configuration_unchanged(string failure)
        {
            using var source = new PostgresStores("source");
            using var target = new PostgresStores("target");
            var sourceFolders = new AppFolders(Path.Combine(_root, "source"));
            var targetFolders = new AppFolders(Path.Combine(_root, "target"));
            if (failure == "future-schema")
            {
                using var cache = PostgresBackup.Open(source.Connections["cache.dump"]);
                cache.Execute("INSERT INTO \"VersionInfo\" (\"Version\") VALUES (99999999)");
            }
            var archive = PostgresArchive(source, sourceFolders);
            if (failure is "checksum" or "truncated-cache" or "future-server" or "unavailable-locale")
            {
                var extracted = Path.Combine(_root, "altered");
                ZipFile.ExtractToDirectory(archive, extracted);
                if (failure is "checksum" or "truncated-cache")
                {
                    var path = Path.Combine(extracted, "cache.dump");
                    using var file = File.OpenWrite(path);
                    file.SetLength(file.Length - 32);
                }
                var manifest = failure == "checksum" ? BackupManifest.Read(extracted) : BackupManifest.Create(extracted, DatabaseType.PostgreSQL);
                manifest.PostgresDatabases = BackupManifest.Read(extracted).PostgresDatabases;
                manifest.PostgresMajorVersion = failure == "future-server" ? 999 : PostgresBackup.ServerMajor(target.Connections["chaptarr.dump"]);
                if (failure == "unavailable-locale")
                {
                    manifest.PostgresDatabases["chaptarr.dump"].Collation = "unavailable-backup-test-locale";
                }
                manifest.Write(extracted);
                File.Delete(archive);
                ZipFile.CreateFromDirectory(extracted, archive);
            }
            SeedConfig(targetFolders);
            SeedStaging(targetFolders);
            var expected = target.Snapshot();
            var config = File.ReadAllBytes(targetFolders.GetConfigPath());
            var queue = Snapshot(ConnectionString(targetFolders.GetStagingDatabase()));
            var restoration = new DatabaseRestorationService(_disk, targetFolders, target.Factory);
            var service = CreateService(target.Main, targetFolders, restoration, logs: target.Logs, cache: target.Cache);
            if (failure is "checksum" or "future-server")
            {
                Assert.Throws<RestoreBackupFailedException>(() => service.Restore(archive));
            }
            else
            {
                service.Restore(archive);
                DatabaseRestorationService.PrepareConfiguration(targetFolders);
                using var blocker = failure == "busy-cache" ? PostgresBackup.Open(target.Connections["cache.dump"]) : null;
                Assert.That(() => restoration.Restore(DatabaseType.PostgreSQL), Throws.Exception);
            }
            AssertSnapshot(expected, target);
            Assert.That(File.ReadAllBytes(targetFolders.GetConfigPath()), Is.EqualTo(config));
            Assert.That(Snapshot(ConnectionString(targetFolders.GetStagingDatabase())), Is.EquivalentTo(queue));
        }

        [Test]
        public void postgres_restore_should_recover_a_stage_created_before_its_oid_was_saved()
        {
            using var source = new PostgresStores("source");
            using var target = new PostgresStores("target");
            var sourceFolders = new AppFolders(Path.Combine(_root, "source"));
            var targetFolders = new AppFolders(Path.Combine(_root, "target"));
            var archive = PostgresArchive(source, sourceFolders);
            var restoration = new DatabaseRestorationService(_disk, targetFolders, target.Factory);
            CreateService(target.Main, targetFolders, restoration, logs: target.Logs, cache: target.Cache).Restore(archive);
            DatabaseRestorationService.PrepareConfiguration(targetFolders);
            var path = Path.Combine(targetFolders.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            var stages = new System.Text.Json.Nodes.JsonObject();
            using var admin = PostgresBackup.Open(target.Connections["chaptarr.dump"], "postgres");
            var id = Guid.NewGuid().ToString("N");
            foreach (var entry in target.Connections)
            {
                var name = new NpgsqlConnectionStringBuilder(entry.Value).Database;
                stages[entry.Key] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Target"] = name,
                    ["Staged"] = "chaptarr_restore_" + id + "_" + Path.GetFileNameWithoutExtension(entry.Key),
                    ["Previous"] = "chaptarr_previous_" + id + "_" + Path.GetFileNameWithoutExtension(entry.Key),
                    ["OriginalOid"] = admin.ExecuteScalar<long>("SELECT oid::bigint FROM pg_database WHERE datname = @Name", new { Name = name }),
                    ["StagedOid"] = 0
                };
            }
            receipt["Postgres"] = stages;
            File.WriteAllText(path, receipt.ToJsonString());
            admin.Execute("CREATE DATABASE " + PostgresBackup.Identifier(stages["chaptarr.dump"]["Staged"].GetValue<string>()));
            Assert.That(restoration.Restore(DatabaseType.PostgreSQL), Is.True);
            AssertSnapshot(source.Snapshot(), target);
            restoration.Commit();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cancelling_a_blocked_postgres_dump_should_stop_the_process_and_publish_nothing(bool blockPreflight)
        {
            using var role = new PostgresRole();
            using var source = new PostgresStores("cancel", role);
            var password = new NpgsqlConnectionStringBuilder(source.Connections["chaptarr.dump"]).Password;
            Assert.That(string.IsNullOrEmpty(password), Is.False, "Configure a password for PostgreSQL backup cancellation coverage");
            var folders = new AppFolders(Path.Combine(_root, "cancel"));
            SeedConfig(folders);
            SeedStaging(folders);
            var service = CreateService(source.Main, folders, logs: source.Logs, cache: source.Cache, connectionStrings: source.Factory);
            using var blocker = PostgresBackup.Open(source.Connections["chaptarr.dump"]);
            using var transaction = blocker.BeginTransaction();
            void Block() => blocker.Execute("LOCK TABLE \"BackupProbe\" IN ACCESS EXCLUSIVE MODE", transaction: transaction);
            if (blockPreflight) Block();
            else
            {
                var locked = false;
                _disk.SpaceForPath = _ =>
                {
                    if (!locked) { Block(); locked = true; }
                    return long.MaxValue;
                };
            }
            using var cancellation = new CancellationTokenSource();
            var existingPasswordFolders = Directory.GetDirectories(_root, "chaptarr_pgpass_*", SearchOption.AllDirectories);
            var task = Task.Run(() => service.Execute(new BackupCommand(), cancellation.Token));
            using var admin = PostgresBackup.Open(source.Connections["chaptarr.dump"], "postgres");
            var name = new NpgsqlConnectionStringBuilder(source.Connections["chaptarr.dump"]).Database;
            Assert.That(SpinWait.SpinUntil(() => admin.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @Name AND application_name LIKE 'chaptarr_backup_%' AND wait_event_type = 'Lock'", new { Name = name }) == 1, TimeSpan.FromSeconds(15)), Is.True);
            var passwordFolders = Directory.GetDirectories(_root, "chaptarr_pgpass_*", SearchOption.AllDirectories).Except(existingPasswordFolders).ToArray();
            Assert.That(passwordFolders, Has.Length.EqualTo(blockPreflight ? 0 : 1));
            if (!blockPreflight) AssertPrivateFolder(passwordFolders[0]);
            if (!blockPreflight && OperatingSystem.IsLinux())
            {
                Assert.That(File.GetUnixFileMode(Path.Combine(passwordFolders[0], "pgpass")), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
                foreach (var process in Process.GetProcessesByName("pg_dump"))
                {
                    using (process)
                    {
                        Assert.That(File.ReadAllText($"/proc/{process.Id}/cmdline").Contains(password, StringComparison.Ordinal), Is.False,
                            "The client process arguments must not contain its password");
                    }
                }
            }
            cancellation.Cancel();
            Assert.That(() => task.GetAwaiter().GetResult(), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(SpinWait.SpinUntil(() => admin.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @Name AND application_name LIKE 'chaptarr_backup_%'", new { Name = name }) == 0, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(Directory.GetFileSystemEntries(service.GetBackupFolder(BackupType.Manual)), Is.Empty);
            Assert.That(Directory.GetFileSystemEntries(folders.TempFolder), Is.Empty);
            Assert.That(Directory.GetDirectories(_root, "chaptarr_pgpass_*", SearchOption.AllDirectories).Except(existingPasswordFolders), Is.Empty);
        }

        [Test]
        public void missing_postgres_client_tools_should_report_the_required_version_without_a_partial_backup()
        {
            Assert.That(() => PostgresBackup.Tool("pg_dump", 999), Throws.InvalidOperationException.With.Message.Contains("PostgreSQL 999"));
        }

        [Test]
        public void native_client_errors_should_not_surface_untrusted_diagnostic_content()
        {
            var sensitive = "synthetic-sensitive-" + Guid.NewGuid().ToString("N");
            var executable = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            var exception = Assert.Throws<InvalidOperationException>(() => PostgresBackup.Run(
                executable, new[] { "--" + sensitive }, null, CancellationToken.None));
            Assert.That(exception.Message.Contains(sensitive, StringComparison.Ordinal), Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void postgres_staged_restore_should_need_createdb_but_not_superuser(bool canCreate)
        {
            using var role = new PostgresRole(canCreate);
            using var source = new PostgresStores("source", role);
            using var target = new PostgresStores("target", role);
            var folders = new AppFolders(Path.Combine(_root, "target"));
            SeedConfig(folders);
            var config = File.ReadAllBytes(folders.GetConfigPath());
            var expected = target.Snapshot();
            var restoration = new DatabaseRestorationService(_disk, folders, target.Factory);
            var archive = PostgresArchive(source, new AppFolders(Path.Combine(_root, "source")));
            var service = CreateService(target.Main, folders, restoration, logs: target.Logs, cache: target.Cache);
            if (canCreate)
            {
                service.Restore(archive);
                DatabaseRestorationService.PrepareConfiguration(folders);
                Assert.That(restoration.Restore(DatabaseType.PostgreSQL), Is.True);
                AssertSnapshot(source.Snapshot(), target);
                restoration.Commit();
            }
            else
            {
                Assert.That(() => service.Restore(archive), Throws.TypeOf<RestoreBackupFailedException>().With.Message.Contains("CREATEDB"));
                Assert.That(Directory.Exists(folders.GetDatabaseRestoreBundle()), Is.False);
                AssertSnapshot(expected, target);
                Assert.That(File.ReadAllBytes(folders.GetConfigPath()), Is.EqualTo(config));
            }
        }

        [Test]
        public void postgres_rollback_should_release_its_own_idle_pooled_connections()
        {
            using var source = new PostgresStores("source");
            using var target = new PostgresStores("target");
            var archive = PostgresArchive(source, new AppFolders(Path.Combine(_root, "source")));
            var folders = new AppFolders(Path.Combine(_root, "target"));
            var expected = target.Snapshot();
            foreach (var key in target.Connections.Keys.ToList())
            {
                target.Connections[key] = new NpgsqlConnectionStringBuilder(target.Connections[key]) { Pooling = true }.ConnectionString;
            }
            var restoration = new DatabaseRestorationService(_disk, folders, target.Factory);
            CreateService(target.Main, folders, restoration).Restore(archive);
            DatabaseRestorationService.PrepareConfiguration(folders);
            Assert.That(restoration.Restore(DatabaseType.PostgreSQL), Is.True);
            foreach (var value in target.Connections.Values)
            {
                using var connection = new NpgsqlConnection(value);
                connection.Open();
                connection.Execute("SELECT 1");
            }
            using var unrelated = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(target.Connections["chaptarr.dump"]) { Database = "postgres", Pooling = false }.ConnectionString);
            unrelated.Open();
            var unrelatedPid = unrelated.ProcessID;
            restoration.Rollback();
            AssertSnapshot(expected, target);
            Assert.That(unrelated.ProcessID, Is.EqualTo(unrelatedPid));
            Assert.That(unrelated.ExecuteScalar<int>("SELECT 1"), Is.EqualTo(1));
        }

        [Test]
        public void postgres_staging_should_preserve_destination_connections_and_other_xml_and_source_version()
        {
            using var source = new PostgresStores("source");
            using var target = new PostgresStores("target");
            var archive = PostgresArchive(source, new AppFolders(Path.Combine(_root, "source")));
            var extracted = Path.Combine(_root, "extracted");
            ZipFile.ExtractToDirectory(archive, extracted);
            var config = XDocument.Load(Path.Combine(extracted, "config.xml"));
            config.Root.Add(new XElement("PostgresHost", "source.invalid"), new XElement("PostgresMainDb", "foreign-main"),
                new XElement("PostgresFutureKey", "preserve-future-value"), new XElement("Chaptarr", new XElement("Postgres",
                    new XElement("Host", "nested-source.invalid"), new XElement("MainDb", "nested-foreign-main"), new XElement("FutureKey", "preserve-nested-value"))));
            config.Save(Path.Combine(extracted, "config.xml"));
            var manifest = BackupManifest.Create(extracted, DatabaseType.PostgreSQL);
            manifest.PostgresMajorVersion = PostgresBackup.ServerMajor(source.Connections["chaptarr.dump"]);
            manifest.PostgresDatabases = source.Connections.ToDictionary(entry => entry.Key, entry =>
            {
                using var connection = PostgresBackup.Open(entry.Value);
                return PostgresBackup.DatabaseSettings(connection);
            });
            manifest.ChaptarrVersion = "0.9.0.0";
            manifest.Write(extracted);
            var folders = new AppFolders(Path.Combine(_root, "target"));
            var targetConnection = new NpgsqlConnectionStringBuilder(target.Connections["chaptarr.dump"]);
            File.WriteAllText(folders.GetConfigPath(), new XDocument(new XElement("Config",
                new XElement("PostgresHost", targetConnection.Host), new XElement("PostgresMainDb", targetConnection.Database))).ToString());
            new DatabaseRestorationService(_disk, folders, target.Factory).Stage(extracted);
            var prepared = XDocument.Load(Path.Combine(folders.GetDatabaseRestoreBundle(), "config.xml"));
            Assert.Multiple(() =>
            {
                Assert.That(prepared.Root.Element("PostgresHost").Value, Is.EqualTo(targetConnection.Host));
                Assert.That(prepared.Root.Element("PostgresMainDb").Value, Is.EqualTo(targetConnection.Database));
                Assert.That(prepared.Root.Element("PostgresFutureKey").Value, Is.EqualTo("preserve-future-value"));
                var nested = prepared.Root.Element("Chaptarr").Element("Postgres");
                Assert.That(nested.Element("Host"), Is.Null);
                Assert.That(nested.Element("MainDb"), Is.Null);
                Assert.That(nested.Element("FutureKey").Value, Is.EqualTo("preserve-nested-value"));
            });
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(folders.GetDatabaseRestoreBundle(), "restore.json")));
            Assert.That(receipt.RootElement.GetProperty("Manifest").GetProperty("ChaptarrVersion").GetString(), Is.EqualTo("0.9.0.0"));
        }
    }
}
