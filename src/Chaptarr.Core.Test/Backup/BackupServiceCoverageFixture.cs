using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using NLog;
using Npgsql;
using NzbDrone.Common;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Options;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Lifecycle;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Backup
{
    [TestFixture]
    [NonParallelizable]
    public partial class BackupServiceCoverageFixture
    {
        // Scope records explain current omissions; they do not authorize adding these stores to backups.
        private static readonly Dictionary<string, (bool Included, bool Folder, string Reason)> AppDataScope = new(StringComparer.Ordinal)
        {
            ["backup-temp"] = (false, true, "Instance-owned, private temporary backup payloads are removed after use"),
            ["backup-scheduled-failure.json"] = (false, false, "Current installation backup health; not restored from historical backups"),
            ["chaptarr.lock"] = (false, false, "Exclusive process ownership is runtime state and must never be restored"),
            ["chaptarr.pid"] = (false, false, "The process identifier must never be restored"),
            ["update_required"] = (false, false, "An update request marker must never be restored"),
            [".config"] = (false, true, "Runtime XDG configuration is installation-specific"),
            [".config/share"] = (false, true, "Runtime XDG data is installation-specific"),
            ["."] = (false, true, "The app-data directory is a container, not an archive entry"),
            ["config.xml"] = (true, false, "Host configuration is copied without selecting individual XML keys"),
            ["chaptarr.db"] = (true, false, "SQLite main database is copied in full, including future tables and columns"),
            ["chaptarr.restore"] = (false, false, "An in-progress database restore is transient"),
            ["chaptarr.restore.bundle"] = (false, true, "Validated pending restores and saved originals are transient"),
            ["logs.db"] = (true, false, "Optional diagnostic database; restored only when included"),
            ["logs"] = (false, true, "Diagnostic files are outside current backup scope"),
            ["UpdateLogs"] = (false, true, "Updater diagnostic files are outside current backup scope"),
            ["cache.db"] = (true, false, "Optional cache database; restored only when included"),
            ["MediaCover"] = (false, true, "Downloaded covers are outside current backup scope"),
            ["asp"] = (false, true, "Data-protection key files are outside current backup scope"),
            ["staging.db"] = (false, false, "Scratch import work is rebuilt from the restored library and a scan")
        };

        private sealed class AppFolders : IAppFolderInfo
        {
            public AppFolders(string root)
            {
                AppDataFolder = Path.Combine(root, "appdata");
                TempFolder = Path.Combine(root, "temp");
                StartUpFolder = Path.Combine(root, "startup");
                Directory.CreateDirectory(AppDataFolder);
                Directory.CreateDirectory(TempFolder);
                Directory.CreateDirectory(StartUpFolder);
            }

            public string AppDataFolder { get; }
            public string TempFolder { get; }
            public string StartUpFolder { get; }
        }

        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider()
                : base(new FileSystem())
            {
            }

            public long? AvailableSpace { get; set; }
            public Func<string, long?> SpaceForPath { get; set; }
            public override long? GetAvailableSpace(string path) => SpaceForPath?.Invoke(path) ?? AvailableSpace ?? new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace;
            public override void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public override void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public override void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public override long? GetTotalSize(string path) => throw new NotImplementedException();
            public override bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();
        }

        private string _root;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly TestDiskProvider _disk = new();

        [SetUp]
        public void SetUp()
        {
            // Leave room for native SQLite journal paths under Windows temporary directories.
            _root = Path.Combine(TestContext.CurrentContext.WorkDirectory.GetActualCasing(), Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            _disk.AvailableSpace = null;
            _disk.SpaceForPath = null;
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [TestCase("chaptarr.db", false)]
        [TestCase("readarr.db", false)]
        [TestCase("audioarr.db", false)]
        [TestCase("chaptarr.db", true)]
        public void sqlite_archive_should_restore_every_discovered_table_column_row_and_xml_value(string databaseName, bool exceedsUploadLimit)
        {
            var source = new AppFolders(Path.Combine(_root, "source"));
            var connectionString = ConnectionString(Path.Combine(source.AppDataFolder, databaseName));
            new MigrationController(_logger, null).Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
            using (var connection = Open(connectionString))
            {
                // An unregistered table ensures coverage is driven by the database, not a model/table allowlist.
                connection.Execute("CREATE TABLE BackupCoverageProbe (Id INTEGER PRIMARY KEY, TextValue TEXT, BinaryValue BLOB, OptionalValue TEXT, NumberValue REAL)");
                connection.Execute("INSERT INTO BackupCoverageProbe VALUES (1, @Text, @Blob, NULL, 1.25), (2, '', NULL, @Text, -2.5)",
                    new { Text = "archive value \" & Unicode Å", Blob = new byte[] { 0, 1, 255 } });
                connection.Execute("INSERT INTO Config (\"Key\", \"Value\") VALUES ('backupcoverage', 'preserve-me')");
            }

            var xml = SeedConfig(source);
            SeedStaging(source);
            SeedExcludedStores(source);
            SeedAuxiliary(source);
            var expected = Snapshot(connectionString);
            var database = new MainDatabase(new Database("backup-coverage", () => Open(connectionString)));
            var backup = CreateService(database, source);
            backup.Backup(BackupType.Manual);
            var archive = Directory.GetFiles(backup.GetBackupFolder(BackupType.Manual)).Single();
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var permissions = new FileInfo(archive).GetAccessControl();
                Assert.That(permissions.AreAccessRulesProtected, Is.True);
                Assert.That(permissions.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow)
                    .All(rule => rule.IdentityReference.Equals(identity.User)), Is.True);
            }
            else
            {
                Assert.That(File.GetUnixFileMode(archive), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
            if (exceedsUploadLimit)
            {
                var large = Path.Combine(_root, "large.zip");
                using (var output = File.Create(large))
                {
                    // ZIP permits a prefix; a sparse prefix tests total archive size without inflating database fixtures.
                    output.Position = BackupService.RestoreUploadLimitBytes;
                    using var destination = new ZipArchive(output, ZipArchiveMode.Create);
                    using var original = ZipFile.OpenRead(archive);
                    foreach (var entry in original.Entries)
                    {
                        using var input = entry.Open();
                        using var data = destination.CreateEntry(entry.FullName).Open();
                        input.CopyTo(data);
                    }
                }
                File.Move(large, archive, true);
                Assert.That(new FileInfo(archive).Length, Is.GreaterThan(BackupService.RestoreUploadLimitBytes));
            }
            AssertArchive(archive, source, true, xml);
            var artifactFolder = Environment.GetEnvironmentVariable("CHAPTARR_BACKUP_TEST_ARTIFACTS");
            if (!exceedsUploadLimit && !string.IsNullOrWhiteSpace(artifactFolder))
            {
                Directory.CreateDirectory(artifactFolder);
                File.Copy(archive, Path.Combine(artifactFolder, "sqlite-round-trip.zip"), true);
            }

            var target = new AppFolders(Path.Combine(_root, "target"));
            var restoration = new DatabaseRestorationService(_disk, target);
            var targetService = CreateService(new MainDatabase(null), target, restoration);
            if (exceedsUploadLimit)
            {
                var folder = targetService.GetBackupFolder(BackupType.Manual);
                Directory.CreateDirectory(folder);
                File.Move(archive, Path.Combine(folder, Path.GetFileName(archive)));
                var controller = new Chaptarr.Api.V1.System.Backup.BackupController(targetService, target, _disk, InterfaceProxy.Proxy<ILifecycleService>((_, _) => null));
                var listed = controller.GetBackupFiles().Single();
                Assert.That(listed.Size, Is.GreaterThan(BackupService.RestoreUploadLimitBytes));
                controller.Restore(listed.Id);
            }
            else
            {
                targetService.Restore(archive);
            }
            Assert.That(File.Exists(target.GetDatabase()), Is.False, "Archive restore stages the database before the startup cutover");
            Assert.That(File.Exists(target.GetConfigPath()), Is.False, "Accepting a backup must not change the live config");
            DatabaseRestorationService.PrepareConfiguration(target);
            Assert.That(restoration.Restore(), Is.True);
            restoration.Commit();
            var actual = Snapshot(ConnectionString(target.GetDatabase()));
            Assert.Multiple(() =>
            {
                Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys), "Every discovered schema object and table must survive");
                foreach (var entry in expected)
                {
                    Assert.That(actual.GetValueOrDefault(entry.Key), Is.EqualTo(entry.Value), entry.Key);
                }

                Assert.That(File.ReadAllBytes(target.GetConfigPath()), Is.EqualTo(xml), "All XML values, including unknown future keys, must survive verbatim");
                Assert.That(File.Exists(target.GetStagingDatabase()), Is.False, "Scratch work must not be restored");
                Assert.That(File.Exists(target.GetDatabaseRestore()), Is.False);
                Assert.That(File.Exists(target.GetDatabase() + ".pre-restore"), Is.False);
                Assert.That(Snapshot(ConnectionString(target.GetLogDatabase())), Is.EquivalentTo(Snapshot(ConnectionString(source.GetLogDatabase()))));
                Assert.That(Snapshot(ConnectionString(target.GetCacheDatabase())), Is.EquivalentTo(Snapshot(ConnectionString(source.GetCacheDatabase()))));
                AssertExcludedStoresAbsent(target);
            });
            TestContext.Progress.WriteLine($"Full backup preserved {expected.Count(k => k.Key.StartsWith("table:", StringComparison.Ordinal))} discovered tables and all XML values");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void omitted_sqlite_auxiliary_stores_should_survive_restore_and_scratch_work_should_be_cleared(bool damagedOptionalLog)
        {
            var source = new AppFolders(Path.Combine(_root, "source"));
            var target = new AppFolders(Path.Combine(_root, "target"));
            new MigrationController(_logger, null).Migrate(ConnectionString(source.GetDatabase()), new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
            SeedConfig(source);
            SeedAuxiliary(source);
            SeedAuxiliary(target);
            SeedStaging(target);
            foreach (var path in new[] { target.GetLogDatabase(), target.GetCacheDatabase() })
            {
                using var connection = Open(ConnectionString(path));
                connection.Execute("CREATE TABLE TargetOnly (Id integer)");
            }
            if (damagedOptionalLog) File.WriteAllText(source.GetLogDatabase(), "not a database");
            File.WriteAllText(source.GetStagingDatabase(), "damaged scratch work must not block backup");
            var sourceDb = new MainDatabase(new Database("source", () => Open(ConnectionString(source.GetDatabase()))));
            var backup = CreateService(sourceDb, source, includeExtras: damagedOptionalLog);
            var logs = Snapshot(ConnectionString(target.GetLogDatabase()));
            var cache = Snapshot(ConnectionString(target.GetCacheDatabase()));
            backup.Backup(BackupType.Manual);
            var item = backup.GetBackups().Single();
            Assert.That(item.IncludedComponents, Does.Not.Contain("staging.db").And.Not.Contain("logs.db"));
            Assert.That(item.OmittedComponents, Is.EquivalentTo(damagedOptionalLog ? new[] { "logs.db" } : Array.Empty<string>()));
            var restoration = new DatabaseRestorationService(_disk, target);
            CreateService(new MainDatabase(null), target, restoration).Restore(Path.Combine(backup.GetBackupFolder(BackupType.Manual), item.Name));
            DatabaseRestorationService.PrepareConfiguration(target);
            Assert.That(restoration.Restore(), Is.True);
            restoration.Commit();
            Assert.That(Snapshot(ConnectionString(target.GetLogDatabase())), Is.EquivalentTo(logs));
            Assert.That(Snapshot(ConnectionString(target.GetCacheDatabase())), Is.EquivalentTo(damagedOptionalLog ? Snapshot(ConnectionString(source.GetCacheDatabase())) : cache));
            Assert.That(File.Exists(target.GetStagingDatabase()), Is.False);
        }

        [Test]
        public void postgres_backup_without_database_access_must_fail_without_publishing_a_config_only_archive()
        {
            var source = new AppFolders(Path.Combine(_root, "postgres"));
            var xml = SeedConfig(source);
            SeedStaging(source);
            SeedExcludedStores(source);
            var database = InterfaceProxy.Proxy<IMainDatabase>((method, _) => method.Name == "get_DatabaseType"
                ? DatabaseType.PostgreSQL
                : throw new InvalidOperationException("PostgreSQL backup must not query or copy the database"));
            var backup = CreateService(database, source);
            Assert.Throws<InvalidOperationException>(() => backup.Backup(BackupType.Manual));
            Assert.That(Directory.GetFiles(backup.GetBackupFolder(BackupType.Manual)), Is.Empty);
            Assert.That(Directory.GetFileSystemEntries(source.TempFolder), Is.Empty);
        }

        [Test]
        public void every_declared_app_data_path_should_have_an_explicit_current_backup_scope()
        {
            var paths = DeclaredPaths(new AppFolders(Path.Combine(_root, "paths")));
            Assert.Multiple(() =>
            {
                Assert.That(paths.Keys.Except(AppDataScope.Keys), Is.Empty,
                    "Classify every newly declared persistent store; do not silently assume it is backed up or disposable");
                Assert.That(AppDataScope.Keys.Except(paths.Keys), Is.Empty, "Remove stale classifications when a store disappears");
                Assert.That(AppDataScope.Values.Select(p => p.Reason), Has.None.Empty);
            });
        }

        private BackupService CreateService(IMainDatabase database, IAppFolderInfo folders, IRestoreDatabase restoration = null, IArchiveService archive = null,
            ILogDatabase logs = null, ICacheDatabase cache = null, IConnectionStringFactory connectionStrings = null, bool includeExtras = true, int interval = 7, IHostApplicationLifetime hostLifetime = null, IDiskProvider disk = null, IEventAggregator events = null, Logger logger = null)
        {
            var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.Name switch
            {
                "get_BackupFolder" => "Backups",
                "get_BackupIncludeLogsAndCache" => includeExtras,
                "get_BackupInterval" => interval,
                "get_BackupRetention" => 28,
                _ => throw new NotImplementedException(method.Name)
            });
            hostLifetime ??= InterfaceProxy.Proxy<IHostApplicationLifetime>((method, _) => method.Name.StartsWith("get_", StringComparison.Ordinal)
                ? CancellationToken.None : null);
            return new BackupService(hostLifetime, database, new MakeDatabaseBackup(_logger, connectionStrings), new DiskTransferService(disk ?? _disk, _logger),
                disk ?? _disk, folders, archive ?? new ArchiveService(_logger), config, restoration ?? new DatabaseRestorationService(_disk, folders),
                events ?? InterfaceProxy.Proxy<IEventAggregator>((_, _) => null), logger ?? _logger,
                logs ?? new LogDatabase(new Database("logs", () => new SqliteConnection(ConnectionString(folders.GetLogDatabase())))),
                cache ?? new CacheDatabase(new Database("cache", () => new SqliteConnection(ConnectionString(folders.GetCacheDatabase())))));
        }

        [TestCase("chaptarr_backup_v1.2.3.4_2026.10.02_12.30.00.zip", true)]
        [TestCase("readarr_backup_2026.10.02_12.30.00.zip", true)]
        [TestCase("chaptarr_backup_v1.2.3.4_2026.10.02_12.30.00.zip.partial", false)]
        [TestCase("chaptarr_backup_v1.2.3.4_2026.10.02_12.30.00.zip.failed", false)]
        public void only_completed_archive_names_may_be_listed_or_downloaded(string name, bool expected)
        {
            Assert.That(BackupService.BackupFileRegex.IsMatch(name), Is.EqualTo(expected));
        }

        [Test]
        public void one_unreadable_archive_must_not_hide_other_backups_or_prevent_listing_its_identity()
        {
            var folders = new AppFolders(Path.Combine(_root, "list"));
            var service = CreateService(new MainDatabase(null), folders);
            var folder = service.GetBackupFolder(BackupType.Manual);
            Directory.CreateDirectory(folder);
            var denied = Path.Combine(folder, "chaptarr_backup_v1.2.3.4_2026.10.04_12.00.00.zip");
            var healthy = Path.Combine(folder, "chaptarr_backup_v1.2.3.4_2026.10.04_13.00.00.zip");
            File.WriteAllText(denied, "unreadable backup");
            using (var archive = ZipFile.Open(healthy, ZipArchiveMode.Create)) archive.CreateEntry("config.xml");
            var rows = service.GetBackups(path => path == denied ? throw new UnauthorizedAccessException("Injected unreadable archive") : ZipFile.OpenRead(path));
            Assert.That(rows.Select(row => row.Name), Is.EquivalentTo(new[] { Path.GetFileName(denied), Path.GetFileName(healthy) }));
            Assert.That(rows.Single(row => row.Name == Path.GetFileName(healthy)).IncludedComponents, Does.Contain("config.xml"));
            Assert.That(rows.Single(row => row.Name == Path.GetFileName(denied)).IncludedComponents, Is.Null.Or.Empty);
        }

        [TestCase("healthy", null)]
        [TestCase("failed", "BackupRestoreFailedWarning")]
        [TestCase("pending", "BackupRestorePendingWarning")]
        [TestCase("tools", "BackupToolsUnavailableWarning")]
        public void backup_health_check_must_report_recoverable_problems(string state, string expected)
        {
            var status = new BackupStatus { RestoreFailed = state == "failed", PendingRestore = state == "pending", DatabaseBackupUnavailableReason = state == "tools" ? "missing tools" : null };
            var service = InterfaceProxy.Proxy<IBackupService>((_, _) => status);
            var localization = InterfaceProxy.Proxy<NzbDrone.Core.Localization.ILocalizationService>((_, args) => args[0]);
            var check = new NzbDrone.Core.HealthCheck.Checks.BackupCheck(service, localization).Check();
            Assert.That(check.Type, Is.EqualTo(expected == null ? NzbDrone.Core.HealthCheck.HealthCheckResult.Ok : NzbDrone.Core.HealthCheck.HealthCheckResult.Warning));
            Assert.That(check.Message, Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void staging_health_check_must_report_import_unavailability(bool failed)
        {
            var staging = InterfaceProxy.Proxy<IStagingDbContext>((_, _) => failed ? new IOException("Unreadable scratch database") : null);
            var localization = InterfaceProxy.Proxy<NzbDrone.Core.Localization.ILocalizationService>((_, args) => args[0]);
            var check = new NzbDrone.Core.HealthCheck.Checks.StagingDatabaseCheck(staging, new AppFolders(_root), localization).Check();
            Assert.That(check.Type, Is.EqualTo(failed ? NzbDrone.Core.HealthCheck.HealthCheckResult.Error : NzbDrone.Core.HealthCheck.HealthCheckResult.Ok));
        }

        [TestCase(0, true)]
        [TestCase(1, false)]
        public void status_must_report_schedule_off_without_disabling_manual_backups(int interval, bool disabled)
        {
            var folders = new AppFolders(Path.Combine(_root, "status"));
            var service = CreateService(new MainDatabase(null), folders, interval: interval);
            Assert.That(service.GetStatus().ScheduleDisabled, Is.EqualTo(disabled));
            Assert.That(service.GetStatus().DatabaseBackupUnavailableReason, Is.Null);
        }

        [Test]
        public void connection_failure_must_not_be_reported_as_missing_postgres_tools()
        {
            var folders = new AppFolders(Path.Combine(_root, "status"));
            var database = InterfaceProxy.Proxy<IMainDatabase>((method, _) => method.Name == "get_DatabaseType" ? DatabaseType.PostgreSQL : throw new IOException("Injected unavailable database"));
            var status = CreateService(database, folders).GetStatus();
            Assert.That(status.DatabaseBackupUnavailableReason, Does.Contain("connect").And.Not.Contain("Install"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void free_space_preflight_must_protect_backup_creation_and_restore_extraction(bool restore)
        {
            var folders = new AppFolders(Path.Combine(_root, "source"));
            var connectionString = ConnectionString(folders.GetDatabase());
            new MigrationController(_logger, null).Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
            var xml = SeedConfig(folders);
            var database = new MainDatabase(new Database("space-check", () => Open(connectionString)));
            var stages = 0;
            var restoration = InterfaceProxy.Proxy<IRestoreDatabase>((_, _) => { stages++; return null; });
            var service = CreateService(database, folders, restoration, includeExtras: false);
            if (restore)
            {
                service.Backup(BackupType.Manual);
                var archive = Directory.GetFiles(service.GetBackupFolder(BackupType.Manual)).Single();
                _disk.AvailableSpace = 0;
                var error = Assert.Throws<RestoreBackupFailedException>(() => service.Restore(archive));
                Assert.That(error.ToString(), Does.Contain("have 0 bytes"));
                Assert.That(Directory.Exists(folders.GetDatabaseRestoreBundle()), Is.False);
                Assert.That(stages, Is.Zero, "Extraction must fail before restore staging");
            }
            else
            {
                _disk.AvailableSpace = 0;
                var error = Assert.Throws<IOException>(() => service.Backup(BackupType.Manual));
                Assert.That(error.Message, Does.Contain("backup creation").And.Contain("have 0 bytes"));
                Assert.That(service.GetBackups(), Is.Empty);
            }
            Assert.That(File.ReadAllBytes(folders.GetConfigPath()), Is.EqualTo(xml));
            using var connection = Open(connectionString);
            Assert.That(connection.ExecuteScalar<string>("PRAGMA integrity_check"), Is.EqualTo("ok"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void snapshot_and_zip_space_checks_must_each_prevent_publishing_a_backup(bool zipDestination)
        {
            var folders = new AppFolders(Path.Combine(_root, "source"));
            var connectionString = ConnectionString(folders.GetDatabase());
            new MigrationController(_logger, null).Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
            SeedConfig(folders);
            var database = new MainDatabase(new Database("space-check", () => Open(connectionString)));
            var service = CreateService(database, folders, includeExtras: false);
            var destination = service.GetBackupFolder(BackupType.Manual);
            _disk.SpaceForPath = path => (path == destination) == zipDestination ? 0 : long.MaxValue;
            var error = Assert.Throws<IOException>(() => service.Backup(BackupType.Manual));
            Assert.That(error.Message, Does.Contain("have 0 bytes"));
            Assert.That(service.GetBackups(), Is.Empty);
        }

        [TestCase(typeof(IOException))]
        [TestCase(typeof(UnauthorizedAccessException))]
        [TestCase(typeof(InvalidOperationException))]
        [TestCase(typeof(NotSupportedException))]
        [TestCase(typeof(PlatformNotSupportedException))]
        public void unsupported_archive_permissions_must_warn_without_failing_backup(Type failure)
        {
            var folders = new AppFolders(Path.Combine(_root, "source"));
            var database = new MainDatabase(null);
            var service = CreateService(database, folders, includeExtras: false);
            var calls = 0;
            Assert.DoesNotThrow(() => service.TryRestrictArchiveAccess("synthetic-backup.zip", _ =>
            {
                calls++;
                throw (Exception)Activator.CreateInstance(failure);
            }));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void insufficient_extraction_space_must_leave_live_stores_and_no_pending_restore()
        {
            var source = new AppFolders(Path.Combine(_root, "source"));
            File.WriteAllText(source.GetConfigPath(), "<Config />");
            var archive = Path.Combine(_root, "large.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using var output = zip.CreateEntry("chaptarr.db").Open();
                output.Write(new byte[1024]);
            }
            var destination = Path.Combine(_root, "extracted");
            var exception = Assert.Throws<IOException>(() => new ArchiveService(_logger).ExtractZip(archive, destination, 512));
            Assert.That(exception.Message, Does.Contain("need 1024 bytes, have 512 bytes"));
            Assert.That(Directory.GetFiles(destination), Is.Empty);
            Assert.That(File.ReadAllText(source.GetConfigPath()), Is.EqualTo("<Config />"));
            Assert.That(Directory.Exists(source.GetDatabaseRestoreBundle()), Is.False);
        }

        [Test]
        public void backup_extraction_must_not_use_the_generic_per_file_limit()
        {
            var archive = Path.Combine(_root, "large.zip");
            const long length = 512L * 1024 * 1024 + 1;
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using var output = zip.CreateEntry("chaptarr.db", CompressionLevel.Fastest).Open();
                var buffer = new byte[1024 * 1024];
                for (long remaining = length; remaining > 0; remaining -= buffer.Length)
                {
                    output.Write(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                }
            }
            var destination = Path.Combine(_root, "extracted");
            new ArchiveService(_logger).ExtractZip(archive, destination, length);
            Assert.That(new FileInfo(Path.Combine(destination, "chaptarr.db")).Length, Is.EqualTo(length));
        }

        [Test]
        public void temporary_payloads_must_not_inherit_a_shared_parents_read_permissions()
        {
            var parent = Path.Combine(_root, "shared");
            if (OperatingSystem.IsWindows())
            {
                var permissions = new DirectorySecurity();
                permissions.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(parent).Create(permissions);
            }
            else
            {
                Directory.CreateDirectory(parent);
                File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }

            var privateFolder = Path.Combine(parent, "private");
            BackupService.CreatePrivateFolder(privateFolder);
            AssertPrivateFolder(privateFolder);
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(privateFolder, "payload");
                File.WriteAllText(path, "synthetic confidential backup");
                using var identity = WindowsIdentity.GetCurrent();
                Assert.That(new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow)
                    .All(rule => rule.IdentityReference.Equals(identity.User)), Is.True);
            }
        }

        private static void AssertPrivateFolder(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var permissions = new DirectoryInfo(path).GetAccessControl();
                Assert.That(permissions.AreAccessRulesProtected, Is.True);
                Assert.That(permissions.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow)
                    .All(rule => rule.IdentityReference.Equals(identity.User)), Is.True);
            }
            else
            {
                Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            }
        }

        [Test]
        public void failed_archive_creation_should_not_publish_a_backup_or_leave_temporary_payloads()
        {
            var folders = new AppFolders(Path.Combine(_root, "failed-backup"));
            var connectionString = ConnectionString(folders.GetDatabase());
            new MigrationController(_logger, null).Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
            SeedConfig(folders);
            SeedStaging(folders);
            var archive = InterfaceProxy.Proxy<IArchiveService>((method, args) =>
            {
                var payload = ((IEnumerable<string>)args[1]).First();
                AssertPrivateFolder(Path.GetDirectoryName(payload));
                var marker = Path.GetDirectoryName(payload) + ".partial-path";
                Assert.That(File.ReadAllText(marker), Is.EqualTo(Path.GetFullPath((string)args[0])));

                File.WriteAllText((string)args[0], "partial archive");
                throw new IOException("Injected archive failure");
            });
            var service = CreateService(new MainDatabase(new Database("failure", () => Open(connectionString))), folders, archive: archive);
            Assert.Throws<IOException>(() => service.Backup(BackupType.Manual));
            Assert.Multiple(() =>
            {
                Assert.That(Directory.GetFiles(service.GetBackupFolder(BackupType.Manual)), Is.Empty);
                Assert.That(Directory.GetFileSystemEntries(folders.TempFolder), Is.Empty);
                var working = folders.GetBackupWorkingFolder();
                Assert.That(Directory.Exists(working) ? Directory.GetFileSystemEntries(working) : Array.Empty<string>(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async System.Threading.Tasks.Task an_oversized_upload_must_be_rejected_before_staging_it(bool streamed)
        {
            var folders = new AppFolders(Path.Combine(_root, "upload-limit"));
            var context = new DefaultHttpContext();
            context.Request.ContentType = "multipart/form-data; boundary=backup";
            if (streamed)
            {
                context.Request.Body = new OversizedUploadStream();
            }
            else
            {
                context.Request.ContentLength = BackupService.RestoreUploadLimitBytes + 1;
            }
            var controller = new Chaptarr.Api.V1.System.Backup.BackupController(
                InterfaceProxy.Proxy<IBackupService>((_, _) => throw new AssertionException("Oversized upload must not be staged")), folders, _disk,
                InterfaceProxy.Proxy<ILifecycleService>((_, _) => throw new AssertionException("Rejected uploads must not restart")))
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
            var exception = Assert.ThrowsAsync<RestoreBackupFailedException>(async () => await controller.UploadAndRestore());
            context.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>(new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature { Error = exception });
            using var body = new MemoryStream();
            context.Response.Body = body;
            await new Chaptarr.Http.ErrorManagement.ChaptarrErrorPipeline(_logger).HandleException(context);
            Assert.That(context.Response.StatusCode, Is.EqualTo(413));
            Assert.That(System.Text.Encoding.UTF8.GetString(body.ToArray()), Does.Contain("Backups folder"));
            var method = controller.GetType().GetMethod(nameof(controller.UploadAndRestore));
            Assert.That(((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)method.GetCustomAttribute<RequestSizeLimitAttribute>()).MaxRequestBodySize, Is.EqualTo(5_000_000_000));
            Assert.That(method.GetCustomAttribute<RequestFormLimitsAttribute>().MultipartBodyLengthLimit, Is.EqualTo(5_000_000_000));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async System.Threading.Tasks.Task upload_must_check_space_before_staging_even_without_content_length(bool knownLength)
        {
            var folders = new AppFolders(Path.Combine(_root, "upload-space"));
            using var multipart = new System.Net.Http.MultipartFormDataContent("backup-boundary");
            multipart.Add(new System.Net.Http.StringContent("synthetic backup payload"), "restore", "uploaded.zip");
            var context = new DefaultHttpContext();
            context.Request.ContentType = multipart.Headers.ContentType.ToString();
            context.Request.Body = await multipart.ReadAsStreamAsync();
            if (knownLength)
            {
                context.Request.ContentLength = context.Request.Body.Length;
                context.Request.Body = new OversizedUploadStream();
            }
            _disk.AvailableSpace = 1;
            var controller = new Chaptarr.Api.V1.System.Backup.BackupController(
                InterfaceProxy.Proxy<IBackupService>((_, _) => throw new AssertionException("Insufficient space must not stage")), folders, _disk,
                InterfaceProxy.Proxy<ILifecycleService>((_, _) => throw new AssertionException("Rejected upload must not restart")))
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
            var error = Assert.ThrowsAsync<RestoreBackupFailedException>(async () => await controller.UploadAndRestore());
            Assert.That(error.Message, Does.Contain("have 1 bytes"));
            Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
        }

        private sealed class OversizedUploadStream : MemoryStream
        {
            public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
                => throw new BadHttpRequestException("Request body too large", 413);
        }

        [Test]
        public void startup_cleanup_under_the_appdata_lock_removes_reused_pid_work_but_preserves_other_instances_and_recovery()
        {
            var folders = new AppFolders(Path.Combine(_root, "cleanup"));
            var reusedPid = BackupService.CreateWorkingFolder(folders);
            File.WriteAllText(Path.Combine(reusedPid, "private-payload"), "synthetic secret");
            var originals = Path.Combine(folders.GetDatabaseRestoreBundle(), "previous");
            Directory.CreateDirectory(originals);
            File.WriteAllText(Path.Combine(originals, "chaptarr.db"), "recovery copy");
            var other = new AppFolders(Path.Combine(_root, "other-instance"));
            var otherWork = BackupService.CreateWorkingFolder(other);
            var destination = Path.Combine(_root, "shared-backups");
            Directory.CreateDirectory(destination);
            var published = Path.Combine(destination, "chaptarr_backup_v1.2.3.4_2026.10.04_12.00.00.zip");
            File.WriteAllText(published, "finished backup");
            var partial = published + "." + Path.GetFileName(reusedPid) + ".partial";
            var otherPartial = published + "." + Path.GetFileName(otherWork) + ".partial";
            File.WriteAllText(partial, "interrupted own archive");
            File.WriteAllText(otherPartial, "other instance archive");
            File.WriteAllText(reusedPid + ".partial-path", partial);
            File.WriteAllText(otherWork + ".partial-path", otherPartial);
            using var owner = NzbDrone.Host.SingleInstancePolicy.AcquireAppData(folders);
            BackupService.CleanupWorkingFolder(folders);
            Assert.That(Directory.Exists(reusedPid), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(originals, "chaptarr.db")), Is.EqualTo("recovery copy"));
            Assert.That(Directory.Exists(otherWork), Is.True);
            Assert.That(File.Exists(partial), Is.False);
            Assert.That(File.ReadAllText(otherPartial), Is.EqualTo("other instance archive"));
            Assert.That(File.ReadAllText(published), Is.EqualTo("finished backup"));
            Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
        }

        [Test]
        public void invalid_partial_ownership_record_must_never_delete_a_published_backup()
        {
            var folders = new AppFolders(Path.Combine(_root, "invalid-partial-marker"));
            var work = BackupService.CreateWorkingFolder(folders);
            var published = Path.Combine(_root, "chaptarr_backup_v1.2.3.4_2026.10.04_12.00.00.zip");
            File.WriteAllText(published, "finished backup");
            File.WriteAllText(work + ".partial-path", published);
            using var owner = NzbDrone.Host.SingleInstancePolicy.AcquireAppData(folders);
            BackupService.CleanupWorkingFolder(folders);
            Assert.That(File.ReadAllText(published), Is.EqualTo("finished backup"));
            Assert.That(File.Exists(work + ".partial-path"), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async System.Threading.Tasks.Task uploads_stream_privately_and_restart_only_after_success_even_without_a_browser_response(bool reject)
        {
            var folders = new AppFolders(Path.Combine(_root, "upload"));
            string uploaded = null;
            var events = new List<string>();
            var service = InterfaceProxy.Proxy<IBackupService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBackupService.Restore)));
                uploaded = (string)args[0];
                Assert.That(File.ReadAllText(uploaded), Is.EqualTo("synthetic confidential backup"));
                Assert.That(uploaded, Does.StartWith(folders.GetBackupWorkingFolder()));
                if (!OperatingSystem.IsWindows())
                {
                    Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(uploaded)),
                        Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
                }
                events.Add("stage");
                if (reject)
                {
                    throw new InvalidDataException("Injected invalid archive");
                }
                return null;
            });
            var context = new DefaultHttpContext();
            using var multipart = new System.Net.Http.MultipartFormDataContent("backup-boundary");
            multipart.Add(new System.Net.Http.StringContent("synthetic confidential backup"), "restore", "uploaded.zip");
            context.Request.ContentType = multipart.Headers.ContentType.ToString();
            context.Request.Body = await multipart.ReadAsStreamAsync();
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(
                InterfaceProxy.Proxy<Microsoft.AspNetCore.Http.Features.IFormFeature>((_, _) => throw new AssertionException("Do not buffer uploads through Request.Form")));
            var lifecycle = InterfaceProxy.Proxy<ILifecycleService>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(ILifecycleService.Restart)));
                Assert.That(File.Exists(uploaded), Is.False, "Cleanup must precede process shutdown");
                events.Add("restart");
                return null;
            });
            var controller = new Chaptarr.Api.V1.System.Backup.BackupController(service, folders, _disk, lifecycle)
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
            if (reject)
            {
                Assert.ThrowsAsync<Chaptarr.Http.REST.BadRequestException>(async () => await controller.UploadAndRestore());
            }
            else
            {
                await controller.UploadAndRestore();
            }
            Assert.That(events, Is.EqualTo(reject ? new[] { "stage" } : new[] { "stage", "restart" }));
            Assert.That(Directory.GetFileSystemEntries(folders.GetBackupWorkingFolder()), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void restoring_a_listed_backup_restarts_without_a_browser_callback(bool reject)
        {
            var folders = new AppFolders(Path.Combine(_root, "listed"));
            var events = new List<string>();
            var service = InterfaceProxy.Proxy<IBackupService>((method, _) =>
            {
                if (method.Name == nameof(IBackupService.GetBackups))
                {
                    return new List<NzbDrone.Core.Backup.Backup> { new() { Name = "chaptarr_backup_2026.01.01_00.00.00.zip", Type = BackupType.Manual } };
                }
                if (method.Name == nameof(IBackupService.GetBackupFolder))
                {
                    return folders.AppDataFolder;
                }
                events.Add("stage");
                if (reject)
                {
                    throw new InvalidDataException("Injected invalid archive");
                }
                return null;
            });
            var lifecycle = InterfaceProxy.Proxy<ILifecycleService>((_, _) => { events.Add("restart"); return null; });
            var controller = new Chaptarr.Api.V1.System.Backup.BackupController(service, folders, _disk, lifecycle);
            var id = controller.GetBackupFiles().Single().Id;
            if (reject)
            {
                Assert.Throws<InvalidDataException>(() => controller.Restore(id));
            }
            else
            {
                controller.Restore(id);
            }
            Assert.That(events, Is.EqualTo(reject ? new[] { "stage" } : new[] { "stage", "restart" }));
        }

        [TestCase("crc")]
        [TestCase("smaller-header")]
        [TestCase("larger-header")]
        public void extraction_must_verify_actual_bytes_and_crc_even_when_zip_headers_agree(string defect)
        {
            var input = Path.Combine(_root, "payload");
            File.WriteAllBytes(input, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
            var zip = Path.Combine(_root, "damaged.zip");
            new ArchiveService(_logger).CreateZip(zip, new[] { input });
            var bytes = File.ReadAllBytes(zip);
            for (var offset = 0; offset < bytes.Length - 28; offset++)
            {
                var signature = BitConverter.ToUInt32(bytes, offset);
                if (signature is 0x04034b50 or 0x02014b50)
                {
                    var field = defect == "crc" ? (signature == 0x04034b50 ? 14 : 16) : (signature == 0x04034b50 ? 22 : 24);
                    BitConverter.GetBytes(defect == "crc" ? 0u : defect == "smaller-header" ? 1u : 4097u).CopyTo(bytes, offset + field);
                }
            }
            File.WriteAllBytes(zip, bytes);
            var error = Assert.Throws<IOException>(() => new ArchiveService(_logger).ExtractZip(zip, Path.Combine(_root, "extracted"), 10000));
            Assert.That(error.Message, Does.Contain(defect == "crc" ? "checksum" : "size"));
            if (defect == "smaller-header")
            {
                Assert.That(new FileInfo(Path.Combine(_root, "extracted", "payload")).Length, Is.LessThanOrEqualTo(1),
                    "Reject a lying header while streaming, before its undeclared bytes consume disk space");
            }
        }

        private byte[] SeedConfig(IAppFolderInfo folders)
        {
            var config = new ConfigFileProvider(folders, new CacheManager(), InterfaceProxy.Proxy<IEventAggregator>((_, _) => null), _disk,
                Options.Create(new PostgresOptions()), Options.Create(new AuthOptions()), Options.Create(new AppOptions()),
                Options.Create(new ServerOptions()), Options.Create(new UpdateOptions()), Options.Create(new LogOptions()));
            config.EnsureDefaultConfigFile();
            // Materialize actual serialized keys through their getters; computed aliases do not create fake XML fields.
            config.GetConfigDictionary();
            config.SetValue("BackupCoverageFutureKey", "unknown value & Unicode Å");
            var document = XDocument.Load(folders.GetConfigPath());
            Assert.That(document.Root.Elements(), Is.Not.Empty);
            return File.ReadAllBytes(folders.GetConfigPath());
        }

        private static string ConnectionString(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

        private static SqliteConnection Open(string connectionString)
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection;
        }

        private static Dictionary<string, string[]> Snapshot(string connectionString)
        {
            using var connection = Open(connectionString);
            var snapshot = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var objects = connection.Query<(string Type, string Name, string Sql)>("SELECT type, name, sql FROM sqlite_schema ORDER BY type, name").ToList();
            foreach (var item in objects)
            {
                snapshot[$"schema:{item.Type}:{item.Name}"] = new[] { item.Sql };
                if (item.Type != "table")
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{item.Name.Replace("\"", "\"\"")}\"";
                using var reader = command.ExecuteReader();
                var rows = new List<string>();
                snapshot[$"columns:{item.Name}"] = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                while (reader.Read())
                {
                    rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i)
                        ? null
                        : reader.GetValue(i) is byte[] bytes
                            ? "blob:" + Convert.ToBase64String(bytes)
                            : reader.GetFieldType(i).Name + ":" + Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray()));
                }

                snapshot[$"table:{item.Name}"] = rows.OrderBy(row => row, StringComparer.Ordinal).ToArray();
            }

            return snapshot;
        }

        private void AssertArchive(string archivePath, IAppFolderInfo source, bool hasDatabase, byte[] xml)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var included = new[] { Path.GetFileName(source.GetConfigPath()), "INFO" }.ToList();
            if (hasDatabase)
            {
                included.Add("chaptarr.db");
                included.Add("logs.db");
                included.Add("cache.db");
                included.Add(BackupManifest.FileName);
            }

            Assert.That(archive.Entries.Select(e => e.FullName), Is.EquivalentTo(included), "Only the declared backup components belong in the archive");
            using var stream = archive.GetEntry(Path.GetFileName(source.GetConfigPath())).Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(xml));
        }

        [TestCase("duplicate")]
        [TestCase("unexpected")]
        public void incomplete_or_ambiguous_archive_should_not_stage_a_restore(string defect)
        {
            var source = new AppFolders(Path.Combine(_root, "source"));
            var connectionString = ConnectionString(source.GetDatabase());
            new MigrationController(_logger, null).Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
            SeedConfig(source);
            SeedStaging(source);
            var service = CreateService(new MainDatabase(new Database("archive", () => Open(connectionString))), source);
            service.Backup(BackupType.Manual);
            var path = Directory.GetFiles(service.GetBackupFolder(BackupType.Manual)).Single();
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry(defect == "duplicate" ? "INFO" : "unexpected.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("unexpected archive payload");
            }

            var target = new AppFolders(Path.Combine(_root, "target"));
            Assert.Throws<RestoreBackupFailedException>(() => CreateService(new MainDatabase(null), target).Restore(path));
            Assert.That(Directory.GetFileSystemEntries(target.AppDataFolder), Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void a_second_raw_upload_should_not_replace_or_hide_an_already_pending_restore(bool firstIsArchive)
        {
            var source = new AppFolders(Path.Combine(_root, "source"));
            var connectionString = ConnectionString(source.GetDatabase());
            new MigrationController(_logger, null).Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
            SeedConfig(source);
            SeedStaging(source);
            var database = new MainDatabase(new Database("archive", () => Open(connectionString)));
            var service = CreateService(database, source);
            service.Backup(BackupType.Manual);
            var target = new AppFolders(Path.Combine(_root, "target"));
            var targetService = CreateService(new MainDatabase(null), target);
            targetService.Restore(firstIsArchive ? Directory.GetFiles(service.GetBackupFolder(BackupType.Manual)).Single() : source.GetDatabase());
            var receiptPath = Path.Combine(target.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = File.ReadAllBytes(receiptPath);
            Assert.Throws<RestoreBackupFailedException>(() => targetService.Restore(source.GetDatabase()));
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(receiptPath), Is.EqualTo(receipt));
                Assert.That(File.Exists(target.GetDatabaseRestore()), Is.False);
                Assert.That(File.Exists(target.GetConfigPath()), Is.False);
            });
        }

        private void SeedExcludedStores(IAppFolderInfo folders)
        {
            var declared = DeclaredPaths(folders);
            Assert.That(declared.Keys.Except(AppDataScope.Keys), Is.Empty, "Classify new stores before seeding backup scope");
            foreach (var path in declared.Keys.Where(p => p != "." && !AppDataScope[p].Included))
            {
                var fullPath = Path.Combine(folders.AppDataFolder, path);
                if (AppDataScope[path].Folder)
                {
                    Directory.CreateDirectory(fullPath);
                    fullPath = Path.Combine(fullPath, "backup-coverage-marker");
                }

                File.WriteAllText(fullPath, "excluded current store");
            }
        }

        private void SeedStaging(IAppFolderInfo folders)
        {
            new StagingDbContext(folders, _logger).InitializeDatabase();
            using var connection = Open(ConnectionString(folders.GetStagingDatabase()));
            connection.Execute("INSERT INTO ingest_queue (path, mtime_ns, size_bytes, tags_json, created_at, updated_at) VALUES ('queue-test-path', 1, 2, '{}', 3, 4)");
        }

        private void SeedAuxiliary(IAppFolderInfo folders)
        {
            foreach (var path in new[] { folders.GetLogDatabase(), folders.GetCacheDatabase() })
            {
                using var connection = Open(ConnectionString(path));
                connection.Execute("CREATE TABLE AuxiliaryProbe (Value TEXT); INSERT INTO AuxiliaryProbe VALUES ('retained auxiliary value')");
            }
        }

        private static void AssertExcludedStoresAbsent(IAppFolderInfo folders)
        {
            foreach (var entry in AppDataScope.Where(p => p.Key != "." && !p.Value.Included))
            {
                var path = Path.Combine(folders.AppDataFolder, entry.Key);
                if (entry.Key == "backup-temp")
                {
                    Assert.That(Directory.Exists(path) ? Directory.GetFileSystemEntries(path) : Array.Empty<string>(), Is.Empty, entry.Value.Reason);
                }
                else
                {
                    Assert.That(File.Exists(path) || Directory.Exists(path), Is.False, entry.Value.Reason);
                }
            }
        }

        private Dictionary<string, string> DeclaredPaths(IAppFolderInfo folders)
        {
            var declarations = typeof(PathExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.ReturnType == typeof(string) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(IAppFolderInfo))
                .Select(m => (Name: m.Name, Path: (string)m.Invoke(null, new object[] { folders }))).ToList();
            return declarations.Select(p => (p.Name, Relative: p.Name == nameof(PathExtensions.GetDatabase)
                    ? "chaptarr.db"
                    : Path.GetRelativePath(folders.AppDataFolder, p.Path).TrimEnd(Path.DirectorySeparatorChar).Replace('\\', '/')))
                .Where(p => !Path.IsPathRooted(p.Relative) && p.Relative != ".." && !p.Relative.StartsWith("../", StringComparison.Ordinal))
                .GroupBy(p => p.Relative, StringComparer.Ordinal)
                .ToDictionary(p => p.Key, p => string.Join(", ", p.Select(v => v.Name)), StringComparer.Ordinal);
        }
    }
}
