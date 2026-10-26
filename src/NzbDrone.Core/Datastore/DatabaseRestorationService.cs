using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Dapper;
using FluentMigrator;
using NLog;
using Npgsql;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.Datastore
{
    public interface IRestoreDatabase
    {
        void Validate(string path);
        void Stage(string folder);
        bool Restore(DatabaseType databaseType = DatabaseType.SQLite, CancellationToken cancellationToken = default);
        void Commit();
        void Rollback();
        bool RecoveryCompleted { get; }
        bool CommitFailed { get; }
    }

    public class DatabaseRestorationService : IRestoreDatabase
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IConnectionStringFactory _connectionStrings;
        private bool _restoreApplied;
        public bool RecoveryCompleted { get; private set; }
        public bool CommitFailed { get; private set; }
        private static readonly object RestoreGate = new();
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(DatabaseRestorationService));
        private const string ReceiptName = "restore.json";
        private static readonly Lazy<long> LatestSchemaVersion = new(() => typeof(NzbDroneMigrationBase).Assembly.GetTypes()
            .Where(type => typeof(NzbDroneMigrationBase).IsAssignableFrom(type))
            .Select(type => type.GetCustomAttribute<MigrationAttribute>())
            .Where(attribute => attribute != null).Max(attribute => attribute.Version));

        private sealed class Receipt
        {
            [JsonRequired]
            public BackupManifest Manifest { get; set; }
            [JsonRequired]
            public string State { get; set; } = "Ready";
            [JsonRequired]
            public bool Legacy { get; set; }
            [JsonRequired]
            public Dictionary<string, bool> Originals { get; set; } = new(StringComparer.Ordinal);
            public Dictionary<string, PostgresStore> Postgres { get; set; }
        }

        private sealed class PostgresStore
        {
            public string Target { get; set; }
            public string Staged { get; set; }
            public string Previous { get; set; }
            public long OriginalOid { get; set; }
            public long StagedOid { get; set; }
        }

        public DatabaseRestorationService(IDiskProvider diskProvider, IAppFolderInfo appFolderInfo, IConnectionStringFactory connectionStringFactory = null)
        {
            _diskProvider = diskProvider;
            _appFolderInfo = appFolderInfo;
            _connectionStrings = connectionStringFactory;
        }

        public void Validate(string path)
        {
            ValidateSqlite(path, false);
        }

        internal static void ValidateStaging(string path)
        {
            ValidateSqlite(path, true);
        }

        private static void ValidateSqlite(string path, bool staging, bool auxiliary = false)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using (var integrityCommand = connection.CreateCommand())
            {
                integrityCommand.CommandText = "PRAGMA quick_check(1);";

                if (!string.Equals(integrityCommand.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The restore database failed SQLite's integrity check.");
                }
            }

            // These dual-root columns are present in Chaptarr's earliest schema as well as current
            // databases, but not upstream Readarr. Later monitoring columns are intentionally not
            // required so an old, legitimate Chaptarr/AudioArr backup can still migrate forward.
            if (staging)
            {
                if (!ColumnExists(connection, "ingest_queue", "path") ||
                    !ColumnExists(connection, "ingest_queue", "status") ||
                    !ColumnExists(connection, "import_results", "queue_item_id"))
                {
                    throw new InvalidDataException("The restore database is not a Chaptarr import queue.");
                }

                return;
            }

            if (!auxiliary && (!ColumnExists(connection, "Authors", "AudiobookRootFolderPath") ||
                !ColumnExists(connection, "Authors", "EbookRootFolderPath")))
            {
                throw new InvalidDataException("The restore database is not a Chaptarr database.");
            }

            if (ColumnExists(connection, "VersionInfo", "Version"))
            {
                using var versionCommand = connection.CreateCommand();
                versionCommand.CommandText = "SELECT COALESCE(MAX(\"Version\"), 0) FROM \"VersionInfo\"";
                if (Convert.ToInt64(versionCommand.ExecuteScalar()) > LatestSchemaVersion.Value)
                {
                    throw new InvalidDataException("The restore database schema requires a newer Chaptarr version.");
                }
            }
        }

        public void Stage(string folder)
        {
            lock (RestoreGate)
            {
                Stage(folder, !File.Exists(Path.Combine(folder, BackupManifest.FileName)));
            }
        }

        public static BackupStatus GetStatus(IAppFolderInfo folders)
        {
            var bundle = BundlePath(folders);
            var status = new BackupStatus
            {
                PendingRestore = Directory.Exists(bundle) || File.Exists(folders.GetDatabaseRestore()),
                RestoreFailed = Directory.Exists(folders.AppDataFolder) &&
                    Directory.EnumerateDirectories(folders.AppDataFolder, Path.GetFileName(bundle) + ".failed.*").Any()
            };
            if (Directory.Exists(bundle))
            {
                try
                {
                    var receipt = ReadReceipt(bundle);
                    status.PendingRestore = receipt.State != "Committed";
                    status.CanCancelRestore = receipt.State == "Ready" && receipt.Originals.Count == 0 && receipt.Postgres == null;
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
                {
                    status.RestoreFailed = true;
                }
            }
            else
            {
                status.CanCancelRestore = status.PendingRestore;
            }
            return status;
        }

        public static void CancelPendingRestore(IAppFolderInfo folders, bool forFactoryReset = false)
        {
            lock (RestoreGate)
            {
                var bundle = BundlePath(folders);
                if (Directory.Exists(bundle))
                {
                    var receipt = ReadReceipt(bundle);
                    if (receipt.State != "Committed" && (receipt.State != "Ready" || receipt.Originals.Count != 0 || receipt.Postgres != null))
                    {
                        throw new InvalidOperationException("This restore has started applying; its recovery files must be retained.");
                    }
                    var cancelled = bundle + (receipt.State == "Committed" ? ".committed." : ".cancelled.") + Guid.NewGuid().ToString("N");
                    Directory.Move(bundle, cancelled);
                    if (receipt.State != "Committed") Directory.Delete(cancelled, true);
                }
                File.Delete(folders.GetDatabaseRestore());
                if (forFactoryReset)
                {
                    CleanupFailedRestores(folders);
                    FactoryResetState.MarkResetting();
                }
            }
        }

        private void Stage(string folder, bool legacy, bool convertingRaw = false)
        {
            var timer = Stopwatch.StartNew();
            if (FactoryResetState.IsResetting)
            {
                throw new InvalidOperationException("A factory reset is in progress; a backup cannot be staged.");
            }
            var bundle = BundlePath(_appFolderInfo);
            if (Directory.Exists(bundle) && ReadReceipt(bundle).State == "Committed")
            {
                CleanupCommittedBundle(bundle);
            }
            if (Directory.Exists(bundle) || (!convertingRaw && File.Exists(_appFolderInfo.GetDatabaseRestore())))
            {
                throw new InvalidOperationException("A restore is already pending. Restart Chaptarr before submitting another restore.");
            }

            var manifest = File.Exists(Path.Combine(folder, BackupManifest.FileName))
                ? BackupManifest.Read(folder)
                : BackupManifest.Create(folder, DatabaseType.SQLite);
            manifest.Validate(folder, !legacy);
            if (manifest.DatabaseType == DatabaseType.PostgreSQL)
            {
                var connections = PostgresConnections().Where(entry => manifest.Files.ContainsKey(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value);
                using var admin = PostgresBackup.Open(connections["chaptarr.dump"], "postgres");
                ValidatePostgresDestination(admin, connections);
                var major = admin.PostgreSqlVersion.Major;
                if (manifest.PostgresMajorVersion > major)
                {
                    throw new InvalidDataException("The backup requires a newer PostgreSQL server.");
                }
                foreach (var file in manifest.Files.Keys.Where(name => name.EndsWith(".dump", StringComparison.Ordinal)))
                {
                    PostgresBackup.ValidateArchive(Path.Combine(folder, file), major);
                }
            }
            else
            {
                Validate(Path.Combine(folder, "chaptarr.db"));
                foreach (var file in manifest.Files.Keys.Where(name => name is "logs.db" or "cache.db"))
                {
                    ValidateSqlite(Path.Combine(folder, file), false, true);
                }
            }
            if (manifest.Files.ContainsKey("staging.db"))
            {
                ValidateStaging(Path.Combine(folder, "staging.db"));
            }

            if (manifest.Files.ContainsKey("config.xml"))
            {
                var config = XDocument.Load(Path.Combine(folder, "config.xml"));
                if (config.Root?.Name.LocalName != "Config")
                {
                    throw new InvalidDataException("The backup configuration is invalid.");
                }

                if (manifest.DatabaseType == DatabaseType.SQLite && config.Root.Descendants().Any(element =>
                    (string.Equals(element.Name.LocalName, "PostgresHost", StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(element.Name.LocalName, "Host", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(element.Parent?.Name.LocalName, "Postgres", StringComparison.OrdinalIgnoreCase))) &&
                    !string.IsNullOrWhiteSpace(element.Value)))
                {
                    throw new InvalidDataException("A SQLite backup must not redirect database connections to PostgreSQL.");
                }
            }

            var candidateBytes = manifest.Files.Keys.Sum(name => new FileInfo(Path.Combine(folder, name)).Length);
            EnsureSpace(_appFolderInfo.AppDataFolder, candidateBytes);
            var prepared = BackupService.CreateWorkingFolder(_appFolderInfo);
            try
            {
                BackupService.CreatePrivateFolder(prepared);
                foreach (var file in manifest.Files.Keys)
                {
                    File.Copy(Path.Combine(folder, file), Path.Combine(prepared, file));
                }

                if (manifest.DatabaseType == DatabaseType.PostgreSQL)
                {
                    PreservePostgresTarget(Path.Combine(prepared, "config.xml"));
                    var sourceDatabases = manifest.PostgresDatabases;
                    var sourceMajor = manifest.PostgresMajorVersion;
                    var sourceVersion = manifest.ChaptarrVersion;
                    var sourceFormat = manifest.FormatVersion;
                    var omitted = manifest.OmittedComponents;
                    manifest = BackupManifest.Create(prepared, DatabaseType.PostgreSQL);
                    manifest.PostgresMajorVersion = sourceMajor;
                    manifest.PostgresDatabases = sourceDatabases;
                    manifest.ChaptarrVersion = sourceVersion;
                    manifest.FormatVersion = sourceFormat;
                    manifest.OmittedComponents = omitted;
                }
                WriteReceipt(prepared, new Receipt { Manifest = manifest, Legacy = legacy });
                // Publish the cohort only after every candidate is copied and validated.
                Directory.Move(prepared, bundle);
                Logger.Info("Restore staged: {0} bytes in {1} ms", candidateBytes, timer.ElapsedMilliseconds);
            }
            finally
            {
                if (Directory.Exists(prepared))
                {
                    Directory.Delete(prepared, true);
                }
            }
        }

        public static void PrepareConfiguration(IAppFolderInfo appFolderInfo)
        {
            try
            {
                PrepareConfigurationCore(appFolderInfo);
            }
            catch
            {
                if (!AbortConfiguration(appFolderInfo, true))
                {
                    throw;
                }
            }
        }

        public static bool AbortConfiguration(IAppFolderInfo appFolderInfo, bool discardUnprepared = false)
        {
            var bundle = BundlePath(appFolderInfo);
            if (!Directory.Exists(bundle))
            {
                return false;
            }

            var receipt = ReadPendingReceipt(bundle);
            if (receipt == null || receipt.State != "Ready" || (!discardUnprepared && !receipt.Originals.ContainsKey("config.xml")))
            {
                return false;
            }

            var path = appFolderInfo.GetConfigPath();
            var previous = Path.Combine(bundle, "previous", "config.xml");
            if (File.Exists(previous))
            {
                File.Copy(previous, path, true);
            }
            else if (receipt.Originals.TryGetValue("config.xml", out var existed) && !existed)
            {
                File.Delete(path);
            }

            Directory.Move(bundle, bundle + ".failed." + Guid.NewGuid().ToString("N"));
            Logger.Warn("The pending restore was cancelled because its configuration could not start Chaptarr; the original configuration was preserved");
            return true;
        }

        private static void PrepareConfigurationCore(IAppFolderInfo appFolderInfo)
        {
            var bundle = BundlePath(appFolderInfo);
            if (!Directory.Exists(bundle))
            {
                return;
            }

            CleanupPasswordFiles(bundle);
            var receipt = ReadPendingReceipt(bundle);
            if (receipt == null || receipt.State == "Committed")
            {
                return;
            }

            var path = appFolderInfo.GetConfigPath();
            var previous = Path.Combine(bundle, "previous", "config.xml");
            if (receipt.State == "Applied")
            {
                // Listener and connection settings must also return to the original before bootstrap reads them.
                if (File.Exists(previous))
                {
                    File.Copy(previous, path, true);
                }
                else if (receipt.Originals.TryGetValue("config.xml", out var existed) && !existed)
                {
                    File.Delete(path);
                }

                return;
            }

            receipt.Manifest.Validate(bundle, !receipt.Legacy);
            if (!receipt.Manifest.Files.ContainsKey("config.xml"))
            {
                return;
            }

            if (!receipt.Originals.ContainsKey("config.xml"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(previous));
                var existed = File.Exists(path);
                if (existed && !File.Exists(previous))
                {
                    File.Copy(path, previous + ".tmp", true);
                    File.Move(previous + ".tmp", previous, true);
                }

                receipt.Originals["config.xml"] = existed;
                WriteReceipt(bundle, receipt);
            }

            File.Copy(Path.Combine(bundle, "config.xml"), path, true);
        }

        public bool Restore(DatabaseType databaseType = DatabaseType.SQLite, CancellationToken cancellationToken = default)
        {
            if (Directory.Exists(BundlePath(_appFolderInfo)))
            {
                var pending = ReadPendingReceipt(BundlePath(_appFolderInfo));
                if (pending == null)
                {
                    return false;
                }
                if (pending.Manifest.DatabaseType != databaseType)
                {
                    throw new InvalidDataException("The pending restore and destination use different database types.");
                }
                _restoreApplied = RestoreBundle(cancellationToken);
                return _restoreApplied;
            }
            if (databaseType != DatabaseType.SQLite)
            {
                if (Directory.Exists(BundlePath(_appFolderInfo)) || _diskProvider.FileExists(_appFolderInfo.GetDatabaseRestore()))
                {
                    throw new InvalidDataException("A pending SQLite restore cannot be applied to a PostgreSQL instance.");
                }

                return false;
            }

            var dbRestorePath = _appFolderInfo.GetDatabaseRestore();
            var dbPath = _appFolderInfo.GetDatabase();
            var previousDatabasePath = GetPreviousDatabasePath(dbPath);

            if (!_diskProvider.FileExists(dbRestorePath))
            {
                RecoverInterruptedRestore(dbPath, previousDatabasePath);
                return false;
            }

            try
            {
                Validate(dbRestorePath);
                RecoverInterruptedRestore(dbPath, previousDatabasePath);
                var folder = BackupService.CreateWorkingFolder(_appFolderInfo);
                try
                {
                    BackupService.CreatePrivateFolder(folder);
                    File.Copy(dbRestorePath, Path.Combine(folder, "chaptarr.db"));
                    Stage(folder, true, true);
                }
                finally
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }
                }

                _diskProvider.DeleteFile(dbRestorePath);
                _restoreApplied = RestoreBundle(cancellationToken);
                return _restoreApplied;
            }
            catch (Exception e)
            {
                if (_diskProvider.FileExists(dbRestorePath))
                {
                    QuarantineRestore(dbRestorePath);
                }

                Logger.Error(e, "Failed to restore database");
                throw;
            }
        }

        public void Commit()
        {
            CommitFailed = false;
            try
            {
                var bundle = BundlePath(_appFolderInfo);
                if (!Directory.Exists(bundle))
                {
                    return;
                }

                var receipt = ReadReceipt(bundle);
                if (receipt.State != "Applied")
                {
                    throw new InvalidOperationException("Only an applied restore can be committed.");
                }
                receipt.State = "Committed";
                WriteReceipt(bundle, receipt);
                _restoreApplied = false;
                CleanupCommittedBundle(bundle);
                CleanupFailedRestores(_appFolderInfo);
            }
            catch (Exception ex)
            {
                CommitFailed = true;
                Logger.Error(ex, "Failed to finalize database restore");
                throw;
            }
        }

        public void Rollback()
        {
            // Only this process's failed startup can undo an applied restore. A retiring process
            // must not undo a restore already being applied by its replacement.
            if (!_restoreApplied)
            {
                return;
            }

            var bundle = BundlePath(_appFolderInfo);
            if (Directory.Exists(bundle))
            {
                var receipt = ReadReceipt(bundle);
                if (receipt.State == "Applied")
                {
                    RollbackBundle(bundle, receipt);
                }
            }

            _restoreApplied = false;
        }

        private bool RestoreBundle(CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            var bundle = BundlePath(_appFolderInfo);
            var receipt = ReadReceipt(bundle);
            if (receipt.State == "Committed")
            {
                CleanupCommittedBundle(bundle);
                return false;
            }

            if (receipt.State == "Applied")
            {
                Logger.Warn("Recovering the original stores after an interrupted restore");
                RollbackBundle(bundle, receipt);
                return false;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                receipt.Manifest.Validate(bundle, !receipt.Legacy);
                if (receipt.Manifest.DatabaseType == DatabaseType.PostgreSQL)
                {
                    return RestorePostgresBundle(bundle, receipt, cancellationToken);
                }
                Validate(Path.Combine(bundle, "chaptarr.db"));
                if (receipt.Manifest.Files.ContainsKey("staging.db"))
                {
                    ValidateStaging(Path.Combine(bundle, "staging.db"));
                }

                var components = receipt.Manifest.Files.Keys.Where(name => name is "chaptarr.db" or "logs.db" or "cache.db" or "staging.db").ToList();
                if (!components.Contains("staging.db"))
                {
                    components.Add("staging.db");
                }
                foreach (var component in components.Where(name => name is "logs.db" or "cache.db"))
                {
                    ValidateSqlite(Path.Combine(bundle, component), false, true);
                }
                foreach (var component in components)
                {
                    receipt.Originals[component] = File.Exists(ComponentPath(component));
                }

                EnsureSpace(_appFolderInfo.AppDataFolder, components.Sum(component => DatabaseFileSuffixes.Sum(suffix =>
                    File.Exists(ComponentPath(component) + suffix) ? new FileInfo(ComponentPath(component) + suffix).Length : 0)));
                cancellationToken.ThrowIfCancellationRequested();
                receipt.State = "Applied";
                WriteReceipt(bundle, receipt);
                Directory.CreateDirectory(Path.Combine(bundle, "previous"));
                foreach (var component in components)
                {
                    var active = ComponentPath(component);
                    if (receipt.Originals[component])
                    {
                        SnapshotOriginal(active, Path.Combine(bundle, "previous", component));
                    }

                    DeleteDatabaseFiles(active);
                    if (receipt.Manifest.Files.ContainsKey(component))
                    {
                        _diskProvider.MoveFile(Path.Combine(bundle, component), active);
                    }
                }

                Logger.Info("SQLite restore applied: {0} components in {1} ms; originals retained until startup succeeds", components.Count, timer.ElapsedMilliseconds);
                return true;
            }
            catch
            {
                RollbackBundle(bundle, receipt);
                throw;
            }
        }

        private Dictionary<string, string> PostgresConnections()
        {
            if (_connectionStrings?.MainDbConnection.DatabaseType != DatabaseType.PostgreSQL)
            {
                throw new InvalidOperationException("A PostgreSQL restore requires the destination's configured database connections.");
            }
            var connections = new Dictionary<string, string>
            {
                ["chaptarr.dump"] = _connectionStrings.MainDbConnection.ConnectionString,
                ["logs.dump"] = _connectionStrings.LogDbConnection.ConnectionString,
                ["cache.dump"] = _connectionStrings.CacheDbConnection.ConnectionString
            };
            var main = new NpgsqlConnectionStringBuilder(connections["chaptarr.dump"]);
            var builders = connections.Values.Select(value => new NpgsqlConnectionStringBuilder(value)).ToList();
            if (builders.Any(value => value.Host != main.Host || value.Port != main.Port || value.Username != main.Username) ||
                builders.Select(value => value.Database).Distinct(StringComparer.Ordinal).Count() != 3 ||
                builders.Any(value => value.Database is "postgres" or "template0" or "template1"))
            {
                throw new InvalidOperationException("A restore requires three distinct Chaptarr databases on the configured PostgreSQL server.");
            }
            return connections;
        }

        private void PreservePostgresTarget(string path)
        {
            var config = XDocument.Load(path);
            var original = File.Exists(_appFolderInfo.GetConfigPath()) ? XDocument.Load(_appFolderInfo.GetConfigPath()) : new XDocument(new XElement("Config"));
            // Copy persisted connection configuration, never the effective environment overrides.
            // The destination's environment remains authoritative after restart.
            var names = new HashSet<string>(new[] { "Host", "Port", "User", "Password", "MainDb", "LogDb", "CacheDb" }, StringComparer.OrdinalIgnoreCase);
            bool IsPostgres(XElement element) => (element.Name.LocalName.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) && names.Contains(element.Name.LocalName[8..])) ||
                (element.Parent?.Name.LocalName.Equals("Postgres", StringComparison.OrdinalIgnoreCase) == true && names.Contains(element.Name.LocalName));
            foreach (var element in config.Descendants().Where(IsPostgres).ToList())
            {
                element.Remove();
            }

            foreach (var element in original.Descendants().Where(IsPostgres).Where(element => !element.Ancestors().Any(IsPostgres)))
            {
                var parent = config.Root;
                foreach (var ancestor in element.Ancestors().Reverse().Skip(1))
                {
                    var child = parent.Element(ancestor.Name);
                    if (child == null)
                    {
                        child = new XElement(ancestor.Name);
                        parent.Add(child);
                    }
                    parent = child;
                }
                parent.Add(new XElement(element));
            }
            config.Save(path);
        }

        private bool RestorePostgresBundle(string bundle, Receipt receipt, CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            var connections = PostgresConnections().Where(entry => receipt.Manifest.Files.ContainsKey(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value);
            using var admin = PostgresBackup.Open(connections["chaptarr.dump"], "postgres");
            var major = admin.ExecuteScalar<int>("SHOW server_version_num") / 10000;
            if (receipt.Manifest.PostgresMajorVersion > major)
            {
                throw new InvalidDataException("The backup requires a newer PostgreSQL server.");
            }
            PostgresBackup.Tool("pg_restore", major, cancellationToken);
            var originals = ValidatePostgresDestination(admin, connections);

            if (receipt.Postgres == null)
            {
                var id = Guid.NewGuid().ToString("N");
                receipt.Postgres = connections.ToDictionary(entry => entry.Key, entry => new PostgresStore
                {
                    Target = new NpgsqlConnectionStringBuilder(entry.Value).Database,
                    Staged = "chaptarr_restore_" + id + "_" + Path.GetFileNameWithoutExtension(entry.Key),
                    Previous = "chaptarr_previous_" + id + "_" + Path.GetFileNameWithoutExtension(entry.Key)
                });
                foreach (var store in receipt.Postgres.Values)
                {
                    store.OriginalOid = originals[store.Target];
                }
                WriteReceipt(bundle, receipt);
            }

            foreach (var entry in receipt.Postgres)
            {
                var store = entry.Value;
                if (store.Target != new NpgsqlConnectionStringBuilder(connections[entry.Key]).Database || PostgresOid(admin, store.Target) != store.OriginalOid)
                {
                    throw new InvalidOperationException("The destination database changed after this restore was prepared; no cutover was performed.");
                }
                if (PostgresOid(admin, store.Staged) != 0)
                {
                    RecoverStagedIdentity(admin, store);
                    DropPostgresStore(admin, store.Staged, store.StagedOid);
                }
                var settings = receipt.Manifest.PostgresDatabases[entry.Key];
                admin.Execute(PostgresBackup.CreateDatabaseSql(store.Staged, settings, major));
                store.StagedOid = PostgresOid(admin, store.Staged);
                WriteReceipt(bundle, receipt);
                var actual = PostgresBackup.DatabaseSettings(admin, store.Staged);
                if (actual.Encoding != settings.Encoding || actual.Collation != settings.Collation || actual.CharacterType != settings.CharacterType ||
                    actual.Provider != settings.Provider || actual.Locale != settings.Locale || actual.IcuRules != settings.IcuRules)
                {
                    throw new InvalidDataException("The destination PostgreSQL server cannot reproduce the backup's encoding and collation. Original databases were not replaced.");
                }
                Logger.Info("Restoring {0}: source collation version {1}, destination {2}; indexes are rebuilt by pg_restore", entry.Key, settings.CollationVersion, actual.CollationVersion);
                var stagedConnection = new NpgsqlConnectionStringBuilder(connections[entry.Key]) { Database = store.Staged, Pooling = false }.ConnectionString;
                PostgresBackup.Restore(stagedConnection, Path.Combine(bundle, entry.Key), major, cancellationToken);
                using (var staged = PostgresBackup.Open(stagedConnection))
                {
                    if (entry.Key == "chaptarr.dump" && staged.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Authors' AND column_name IN ('AudiobookRootFolderPath', 'EbookRootFolderPath')") != 2)
                    {
                        throw new InvalidDataException("The restore database is not a Chaptarr database.");
                    }
                    if (staged.ExecuteScalar<long>("SELECT COALESCE(MAX(\"Version\"), 0) FROM \"VersionInfo\"") > LatestSchemaVersion.Value)
                    {
                        throw new InvalidDataException("The restore database schema requires a newer Chaptarr version.");
                    }
                }
                var type = entry.Key == "chaptarr.dump" ? MigrationType.Main : entry.Key == "logs.dump" ? MigrationType.Log : MigrationType.Cache;
                new MigrationController(Logger, null).Migrate(stagedConnection, new MigrationContext(type), DatabaseType.PostgreSQL);
                using var analyzed = PostgresBackup.Open(stagedConnection);
                analyzed.Execute("ANALYZE", commandType: CommandType.Text);
            }

            var staging = ComponentPath("staging.db");
            if (receipt.Manifest.Files.ContainsKey("staging.db"))
            {
                ValidateStaging(Path.Combine(bundle, "staging.db"));
            }
            receipt.Originals["staging.db"] = File.Exists(staging);
            Directory.CreateDirectory(Path.Combine(bundle, "previous"));
            if (receipt.Originals["staging.db"])
            {
                SnapshotOriginal(staging, Path.Combine(bundle, "previous", "staging.db"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            receipt.State = "Applied";
            WriteReceipt(bundle, receipt);
            using (var transaction = admin.BeginTransaction())
            {
                foreach (var store in receipt.Postgres.Values)
                {
                    admin.Execute("ALTER DATABASE " + PostgresBackup.Identifier(store.Target) + " WITH ALLOW_CONNECTIONS false", transaction: transaction);
                    admin.Execute("ALTER DATABASE " + PostgresBackup.Identifier(store.Target) + " RENAME TO " + PostgresBackup.Identifier(store.Previous), transaction: transaction);
                    admin.Execute("ALTER DATABASE " + PostgresBackup.Identifier(store.Staged) + " RENAME TO " + PostgresBackup.Identifier(store.Target), transaction: transaction);
                }
                transaction.Commit();
            }
            DeleteDatabaseFiles(staging);
            if (receipt.Manifest.Files.ContainsKey("staging.db"))
            {
                _diskProvider.MoveFile(Path.Combine(bundle, "staging.db"), staging);
            }
            Logger.Info("PostgreSQL restore applied: {0} databases in {1} ms; originals retained until startup succeeds", receipt.Postgres.Count, timer.ElapsedMilliseconds);
            return true;
        }

        private static Dictionary<string, long> ValidatePostgresDestination(NpgsqlConnection admin, Dictionary<string, string> connections)
        {
            var names = connections.Values.Select(value => new NpgsqlConnectionStringBuilder(value).Database).ToArray();
            var stores = admin.Query<(string Name, long Oid, bool CanRestore)>(@"
                SELECT datname, oid::bigint, pg_has_role(datdba, 'USAGE') AND
                    (SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = CURRENT_USER)
                FROM pg_database WHERE datname = ANY(@Names)", new { Names = names }).ToList();
            if (stores.Count != names.Length || stores.Any(store => !store.CanRestore))
            {
                throw new InvalidOperationException("Staged PostgreSQL restore requires CREATEDB and ownership of Chaptarr's three databases. Superuser access is not required.");
            }
            return stores.ToDictionary(store => store.Name, store => store.Oid, StringComparer.Ordinal);
        }

        private void RestorePostgresOriginals(Receipt receipt)
        {
            var connections = PostgresConnections();
            foreach (var connectionString in connections.Values)
            {
                using var poolKey = new NpgsqlConnection(connectionString);
                // Disposing the failed host returns idle sessions to pools; renames still require their release.
                NpgsqlConnection.ClearPool(poolKey);
            }
            using var admin = PostgresBackup.Open(connections["chaptarr.dump"], "postgres");
            var previousCount = receipt.Postgres.Values.Count(store => PostgresOid(admin, store.Previous) != 0);
            if (previousCount != 0)
            {
                if (previousCount != receipt.Postgres.Count || receipt.Postgres.Values.Any(store =>
                    PostgresOid(admin, store.Previous) != store.OriginalOid || PostgresOid(admin, store.Target) != store.StagedOid))
                {
                    throw new InvalidOperationException("PostgreSQL restore recovery found changed database identities; saved originals were retained.");
                }
                using var transaction = admin.BeginTransaction();
                foreach (var store in receipt.Postgres.Values)
                {
                    admin.Execute("ALTER DATABASE " + PostgresBackup.Identifier(store.Target) + " WITH ALLOW_CONNECTIONS false", transaction: transaction);
                    admin.Execute("ALTER DATABASE " + PostgresBackup.Identifier(store.Target) + " RENAME TO " + PostgresBackup.Identifier(store.Staged), transaction: transaction);
                    admin.Execute("ALTER DATABASE " + PostgresBackup.Identifier(store.Previous) + " RENAME TO " + PostgresBackup.Identifier(store.Target), transaction: transaction);
                    admin.Execute("ALTER DATABASE " + PostgresBackup.Identifier(store.Target) + " WITH ALLOW_CONNECTIONS true", transaction: transaction);
                }
                transaction.Commit();
            }
            foreach (var store in receipt.Postgres.Values)
            {
                RecoverStagedIdentity(admin, store);
                DropPostgresStore(admin, store.Staged, store.StagedOid);
            }
        }

        private static void RecoverStagedIdentity(NpgsqlConnection admin, PostgresStore store)
        {
            if (store.StagedOid == 0 && PostgresOid(admin, store.Staged) != 0)
            {
                // CREATE DATABASE cannot share the receipt's transaction; recover only our reserved, owned stage.
                if (PostgresOid(admin, store.Target) != store.OriginalOid || !admin.ExecuteScalar<bool>(
                    "SELECT datdba = (SELECT oid FROM pg_roles WHERE rolname = CURRENT_USER) FROM pg_database WHERE datname = @Name", new { Name = store.Staged }))
                {
                    throw new InvalidOperationException("The incomplete PostgreSQL stage changed ownership; it was retained instead of deleted.");
                }
                store.StagedOid = PostgresOid(admin, store.Staged);
            }
        }

        private static long PostgresOid(NpgsqlConnection admin, string name) =>
            admin.ExecuteScalar<long>("SELECT oid::bigint FROM pg_database WHERE datname = @Name", new { Name = name });

        private static void DropPostgresStore(NpgsqlConnection admin, string name, long oid)
        {
            var actual = PostgresOid(admin, name);
            if (actual == 0)
            {
                return;
            }
            if (oid == 0 || actual != oid)
            {
                throw new InvalidOperationException("A temporary PostgreSQL database changed identity; it was retained instead of deleted.");
            }
            admin.Execute("DROP DATABASE " + PostgresBackup.Identifier(name));
        }

        private void SnapshotOriginal(string active, string previous)
        {
            var folder = previous + ".capture";
            try
            {
                Directory.CreateDirectory(folder);
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = active,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString();
                // Capture committed WAL pages before replacing files; interrupted moves cannot split the original.
                try
                {
                    new MakeDatabaseBackup(Logger).BackupDatabase(new Database("restore-original", () => new SqliteConnection(connectionString)), folder);
                    _diskProvider.MoveFile(Path.Combine(folder, Path.GetFileName(active)), previous);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26 || ex.SqliteExtendedErrorCode == 776)
                {
                    // Corruption or a hot journal can prevent a read-only snapshot; preserve the complete original bytes.
                    Logger.Warn("Preserving unreadable original database files before applying the validated restore");
                    foreach (var suffix in DatabaseFileSuffixes)
                    {
                        if (_diskProvider.FileExists(active + suffix))
                        {
                            _diskProvider.CopyFile(active + suffix, Path.Combine(folder, Path.GetFileName(active)) + suffix, true);
                        }
                    }

                    MoveDatabaseFiles(Path.Combine(folder, Path.GetFileName(active)), previous);
                }
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        private void RollbackBundle(string bundle, Receipt receipt)
        {
            var timer = Stopwatch.StartNew();
            if (receipt.Postgres != null)
            {
                RestorePostgresOriginals(receipt);
            }
            foreach (var original in receipt.Originals)
            {
                var active = ComponentPath(original.Key);
                if (original.Key != "config.xml")
                {
                    using var poolKey = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = active,
                        Cache = SqliteCacheMode.Private,
                        Pooling = true
                    }.ToString());
                    // Only the failed restore's stores are being replaced; unrelated pools remain available.
                    SqliteConnection.ClearPool(poolKey);
                }

                var previous = Path.Combine(bundle, "previous", original.Key);
                if (File.Exists(previous))
                {
                    MoveDatabaseFiles(active, active + ".failed-restore");
                    MoveDatabaseFiles(previous, active, true);
                }
                else if (!original.Value)
                {
                    MoveDatabaseFiles(active, active + ".failed-restore");
                }
            }

            Directory.Move(bundle, bundle + ".failed." + Guid.NewGuid().ToString("N"));
            RecoveryCompleted = true;
            Logger.Warn("Restore rolled back to the original stores in {0} ms; failed restore files were retained", timer.ElapsedMilliseconds);
        }

        private void EnsureSpace(string folder, long required)
        {
            var available = _diskProvider.GetAvailableSpace(folder)
                ?? throw new IOException("Unable to determine free disk space for backup restoration.");
            if (available < required)
            {
                throw new IOException($"Insufficient space for backup restoration: need {required} bytes, have {available} bytes.");
            }
        }

        private string ComponentPath(string name) => name switch
        {
            "chaptarr.db" => _appFolderInfo.GetDatabase(),
            "staging.db" => _appFolderInfo.GetStagingDatabase(),
            "logs.db" => _appFolderInfo.GetLogDatabase(),
            "cache.db" => _appFolderInfo.GetCacheDatabase(),
            "config.xml" => _appFolderInfo.GetConfigPath(),
            _ => throw new InvalidDataException("Unknown restore component.")
        };

        private static void CleanupPasswordFiles(string bundle)
        {
            foreach (var path in Directory.GetDirectories(bundle, "chaptarr_pgpass_*"))
            {
                var parts = Path.GetFileName(path).Split('_');
                if (parts.Length == 4 && int.TryParse(parts[2], out _) && Guid.TryParseExact(parts[3], "N", out _))
                {
                    Directory.Delete(path, true);
                }
            }
        }

        private static string BundlePath(IAppFolderInfo folders) => folders.GetDatabaseRestoreBundle();

        private static Receipt ReadPendingReceipt(string bundle)
        {
            try
            {
                return ReadReceipt(bundle);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or FileNotFoundException)
            {
                // Configuration and store cutover both create this folder before touching live data.
                // Without a readable receipt after that point, recovery cannot be guessed safely.
                if (Directory.Exists(Path.Combine(bundle, "previous")))
                {
                    throw new InvalidDataException("The restore receipt is unreadable and recovery may be required. The current and saved databases were retained; automatic startup cannot safely continue.", ex);
                }

                Directory.Move(bundle, bundle + ".failed." + Guid.NewGuid().ToString("N"));
                Logger.Warn(ex, "The pending restore receipt was unreadable; the unapplied restore was set aside and original data was left unchanged");
                return null;
            }
        }

        private static Receipt ReadReceipt(string bundle)
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(Path.Combine(bundle, ReceiptName)));
            if (receipt?.Manifest == null || receipt.Originals == null || receipt.State is not ("Ready" or "Applied" or "Committed") ||
                receipt.Originals.Keys.Any(name => name is not ("chaptarr.db" or "logs.db" or "cache.db" or "staging.db" or "config.xml")))
            {
                throw new InvalidDataException("Invalid pending restore receipt.");
            }

            receipt.Manifest.ValidateStructure(!receipt.Legacy, false);
            if (receipt.State == "Applied" && (!receipt.Originals.ContainsKey("staging.db") || receipt.Manifest.Files == null ||
                receipt.Manifest.Files.Keys.Where(name => name is "chaptarr.db" or "logs.db" or "cache.db" or "staging.db")
                    .Any(name => !receipt.Originals.ContainsKey(name)) ||
                (receipt.Manifest.DatabaseType == DatabaseType.PostgreSQL && receipt.Postgres == null)))
            {
                throw new InvalidDataException("The applied restore receipt is missing original database identities; automatic recovery cannot safely continue.");
            }

            if (receipt.Postgres != null && (receipt.Manifest.DatabaseType != DatabaseType.PostgreSQL ||
                !receipt.Postgres.Keys.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(receipt.Manifest.Files.Keys.Where(name => name.EndsWith(".dump", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal)) ||
                receipt.Postgres.Any(entry => !ValidPostgresStore(entry.Key, entry.Value))))
            {
                throw new InvalidDataException("Invalid PostgreSQL recovery identities in the restore receipt.");
            }

            return receipt;
        }

        private static bool ValidPostgresStore(string component, PostgresStore store)
        {
            const string prefix = "chaptarr_restore_";
            var suffix = "_" + Path.GetFileNameWithoutExtension(component);
            if (store == null || string.IsNullOrEmpty(store.Target) || store.OriginalOid <= 0 || store.StagedOid < 0 ||
                store.Staged == null || !store.Staged.StartsWith(prefix, StringComparison.Ordinal) || !store.Staged.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }
            var id = store.Staged[prefix.Length..^suffix.Length];
            return Guid.TryParseExact(id, "N", out _) && store.Previous == "chaptarr_previous_" + id + suffix &&
                store.Target != store.Staged && store.Target != store.Previous;
        }

        private static void WriteReceipt(string bundle, Receipt receipt)
        {
            var path = Path.Combine(bundle, ReceiptName);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt);
                stream.Flush(true);
            }
            File.Move(path + ".tmp", path, true);
        }

        private void CleanupCommittedBundle(string bundle)
        {
            try
            {
                var receipt = ReadReceipt(bundle);
                // Committed data can never be applied or rolled back again, even if deleting originals fails.
                var completed = bundle + ".committed." + Guid.NewGuid().ToString("N");
                _diskProvider.MoveFolder(bundle, completed);
                if (receipt.Postgres != null)
                {
                    using var admin = PostgresBackup.Open(PostgresConnections()["chaptarr.dump"], "postgres");
                    foreach (var store in receipt.Postgres.Values)
                    {
                        DropPostgresStore(admin, store.Previous, store.OriginalOid);
                    }
                }
                _diskProvider.DeleteFolder(completed, true);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "The restore succeeded but its saved originals could not be removed");
            }
        }

        public static void DismissFailedRestores(IAppFolderInfo folders)
        {
            lock (RestoreGate)
            {
                if (GetStatus(folders).PendingRestore)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "A restore is pending; its recovery files must be retained.");
                }
                CleanupFailedRestores(folders);
            }
        }

        private static void CleanupFailedRestores(IAppFolderInfo folders)
        {
            // Failed bundles are published only after recovery finishes or before any live stores change.
            var prefix = Path.GetFileName(BundlePath(folders)) + ".failed.";
            foreach (var path in Directory.EnumerateDirectories(folders.AppDataFolder, prefix + "*")
                .Where(path => Guid.TryParseExact(Path.GetFileName(path)[prefix.Length..], "N", out _)))
            {
                try
                {
                    Directory.Delete(path, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warn(ex, "Unable to remove completed failed-restore files at {0}", path);
                }
            }
            foreach (var path in new[] { folders.GetConfigPath(), folders.GetDatabase(), folders.GetLogDatabase(), folders.GetCacheDatabase(), folders.GetStagingDatabase() })
            {
                foreach (var suffix in DatabaseFileSuffixes)
                {
                    try
                    {
                        File.Delete(path + ".failed-restore" + suffix);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Logger.Warn(ex, "Unable to remove failed-restore component {0}", Path.GetFileName(path));
                    }
                }
            }
        }

        private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

            using var reader = command.ExecuteReader();
            var nameOrdinal = reader.GetOrdinal("name");

            while (reader.Read())
            {
                if (string.Equals(reader.GetString(nameOrdinal), columnName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void RecoverInterruptedRestore(string dbPath, string previousDatabasePath)
        {
            if (!_diskProvider.FileExists(previousDatabasePath))
            {
                return;
            }

            if (_diskProvider.FileExists(dbPath))
            {
                Logger.Warn("An old pre-restore database exists alongside the current database; retaining both without replacing current data");
                return;
            }

            Validate(previousDatabasePath);
            Logger.Warn("Recovering the saved original because the live database is missing");
            MoveDatabaseFiles(previousDatabasePath, dbPath, true);
        }

        private void QuarantineRestore(string dbRestorePath)
        {
            _diskProvider.MoveFile(dbRestorePath, dbRestorePath + ".failed", true);
        }

        private void MoveDatabaseFiles(string sourcePath, string destinationPath, bool recoveringOriginal = false)
        {
            foreach (var suffix in DatabaseFileSuffixes)
            {
                var source = sourcePath + suffix;

                if (_diskProvider.FileExists(source))
                {
                    // Keep original sidecars available until the final base-file move makes recovery complete.
                    if (recoveringOriginal && suffix.Length != 0)
                    {
                        _diskProvider.CopyFile(source, destinationPath + suffix, true);
                    }
                    else
                    {
                        _diskProvider.MoveFile(source, destinationPath + suffix, true);
                    }
                }
            }
        }

        private void DeleteDatabaseFiles(string path)
        {
            foreach (var suffix in DatabaseFileSuffixes)
            {
                _diskProvider.DeleteFile(path + suffix);
            }
        }

        private static string GetPreviousDatabasePath(string dbPath)
        {
            return dbPath + ".pre-restore";
        }

        private static readonly string[] DatabaseFileSuffixes = { "-shm", "-wal", "-journal", string.Empty };
    }
}
