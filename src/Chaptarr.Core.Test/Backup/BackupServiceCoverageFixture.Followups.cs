using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Chaptarr.Core.Test.Common;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using NUnit.Framework;
using Npgsql;

namespace Chaptarr.Core.Test.Backup
{
    public partial class BackupServiceCoverageFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void scheduled_failure_survives_restart_and_health_clears_on_success(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var folders = new AppFolders(Path.Combine(_root, "scheduled-failure"));
                SeedConfig(folders);
                var factory = ConnectionFactory(database, postgres);
                var events = 0;
                var publisher = InterfaceProxy.Proxy<IEventAggregator>((_, args) => { if (args[0] is BackupRestoreStateChangedEvent) events++; return null; });
                var archive = InterfaceProxy.Proxy<IArchiveService>((_, _) => throw new IOException("Archive destination refused api_key=synthetic-secret"));
                var failed = CreateService(database, folders, archive: archive, includeExtras: false, events: publisher, connectionStrings: factory);
                var started = DateTime.UtcNow;
                Assert.Throws<IOException>(() => failed.Backup(BackupType.Scheduled));
                Assert.That(events, Is.EqualTo(1));
                var restarted = CreateService(database, folders, includeExtras: false, events: publisher, connectionStrings: factory);
                var state = restarted.GetStatus().ScheduledBackupFailure;
                Assert.That(state, Is.Not.Null);
                Assert.That(state.FailedAt, Is.GreaterThanOrEqualTo(started));
                Assert.That(state.Reason, Does.Contain("Archive destination refused").And.Not.Contain("synthetic-secret"));
                var health = new BackupCheck(restarted, InterfaceProxy.Proxy<ILocalizationService>((_, args) =>
                    args.Length == 1 ? (string)args[0] : string.Join(" ", ((Dictionary<string, object>)args[1]).Values)));
                Assert.That(health.Check().Type, Is.EqualTo(HealthCheckResult.Warning));
                Assert.That(health.Check().Message, Does.Contain(state.Reason).And.Contain(state.FailedAt.ToString("u")));
                restarted.Backup(BackupType.Manual);
                Assert.That(events, Is.EqualTo(1));
                Assert.That(health.Check().Type, Is.EqualTo(HealthCheckResult.Warning));
                Assert.That(File.Exists(folders.GetScheduledBackupFailure()), Is.True);
                restarted.Backup(BackupType.Scheduled);
                Assert.That(events, Is.EqualTo(2));
                Assert.That(health.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
                Assert.That(File.Exists(folders.GetScheduledBackupFailure()), Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void successful_scheduled_backup_replaces_unreadable_failure_state(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var folders = new AppFolders(Path.Combine(_root, "corrupt-scheduled-failure"));
                SeedConfig(folders);
                File.WriteAllText(folders.GetScheduledBackupFailure(), "{unfinished");
                var service = CreateService(database, folders, includeExtras: false,
                    connectionStrings: ConnectionFactory(database, postgres));
                Assert.That(service.GetStatus().ScheduledBackupFailure, Is.Null);

                service.Backup(BackupType.Scheduled);

                Assert.That(File.Exists(folders.GetScheduledBackupFailure()), Is.False);
                Assert.That(CreateService(database, folders, includeExtras: false,
                    connectionStrings: ConnectionFactory(database, postgres)).GetStatus().ScheduledBackupFailure, Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void malformed_scheduled_failure_warns_once_across_status_reads(bool postgres)
        {
            var messages = new NLog.Targets.MemoryTarget { Layout = "${message}" };
            using var factory = new NLog.LogFactory();
            var logging = new NLog.Config.LoggingConfiguration(factory);
            logging.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Warn, messages);
            factory.Configuration = logging;
            DualDatabaseFixture.Run(postgres, database =>
            {
                var folders = new AppFolders(Path.Combine(_root, "invalid-failure-status"));
                SeedConfig(folders);
                File.WriteAllText(folders.GetScheduledBackupFailure(), "{unfinished");
                var service = CreateService(database, folders, includeExtras: false,
                    connectionStrings: ConnectionFactory(database, postgres), logger: factory.GetLogger("BackupFailureReceipt"));
                for (var i = 0; i < 3; i++) Assert.That(service.GetStatus().ScheduledBackupFailure, Is.Null);
                Assert.That(messages.Logs.Count(message => message.Contains("Unable to read the last scheduled backup failure")), Is.EqualTo(1));
            });
        }

        [Test]
        public void factory_reset_removes_scheduled_failure_from_app_data()
        {
            var folders = new AppFolders(Path.Combine(_root, "reset-scheduled-failure"));
            SeedConfig(folders);
            File.WriteAllText(folders.GetScheduledBackupFailure(), "{unfinished");
            var config = InterfaceProxy.Proxy<NzbDrone.Core.Configuration.IConfigFileProvider>((_, _) => null);
            var service = new NzbDrone.Core.Reset.FactoryResetService(folders, _disk, null, config,
                NLog.LogManager.GetCurrentClassLogger());

            typeof(NzbDrone.Core.Reset.FactoryResetService).GetMethod("ResetAppDataFiles",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(service, new object[] { new Dictionary<string, object>() });

            Assert.That(File.Exists(folders.GetScheduledBackupFailure()), Is.False);
        }

        [Test]
        [Platform(Exclude = "Win")]
        public void postgres_version_probe_stops_a_hung_tool()
        {
            var executable = "chaptarr-probe-" + Guid.NewGuid().ToString("N");
            var path = Path.Combine(_root, executable);
            File.WriteAllText(path, "#!/bin/sh\nsleep 30\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var previous = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", _root + Path.PathSeparator + previous);
                var timer = Stopwatch.StartNew();
                Assert.That(() => PostgresBackup.Tool(executable, 999),
                    Throws.TypeOf<TimeoutException>().With.Message.Contains("time limit"));
                Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", previous);
            }
        }

        private static IConnectionStringFactory ConnectionFactory(IMainDatabase database, bool postgres)
        {
            if (!postgres) return null;
            using var connection = database.OpenConnection();
            var configured = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING")) { Database = connection.Database };
            return InterfaceProxy.Proxy<IConnectionStringFactory>((method, _) => method.Name == "get_MainDbConnection"
                ? new DatabaseConnectionInfo(DatabaseType.PostgreSQL, configured.ConnectionString) : null);
        }

        [TestCase("chaptarr_backup_v0.9.965_2026.10.30 (1).zip")]
        [TestCase("chaptarr_backup_before-upgrade.zip")]
        public void renamed_backup_archives_are_listed_and_validated_when_opened(string name)
        {
            var folders = new AppFolders(Path.Combine(_root, "renamed"));
            var service = CreateService(new MainDatabase(null), folders);
            var folder = service.GetBackupFolder(BackupType.Manual);
            Directory.CreateDirectory(folder);
            using (var zip = ZipFile.Open(Path.Combine(folder, name), ZipArchiveMode.Create))
                zip.CreateEntry("chaptarr.db");
            Assert.That(service.GetBackups().Single().Name, Is.EqualTo(name));
        }

        [Test]
        public void restore_space_preflight_reserves_extraction_and_staging_before_writing_payload()
        {
            var folders = new AppFolders(Path.Combine(_root, "restore-space"));
            var path = Path.Combine(_root, "chaptarr_backup_space.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("chaptarr.db").Open()) stream.Write(new byte[4096]);
            var extracted = false;
            var archive = InterfaceProxy.Proxy<IArchiveService>((_, _) => { extracted = true; throw new AssertionException("Must refuse before extraction"); });
            _disk.AvailableSpace = 8191;
            var service = CreateService(new MainDatabase(null), folders, archive: archive);
            var error = Assert.Throws<RestoreBackupFailedException>(() => service.Restore(path));
            Assert.That(error.Message, Does.Contain("need 8192 bytes, have 8191 bytes"));
            Assert.That(extracted, Is.False);
            Assert.That(Directory.Exists(folders.GetBackupWorkingFolder()), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void postgres_tool_wait_is_bounded_and_caller_cancellation_stays_cancellation(bool cancel)
        {
            using var token = new CancellationTokenSource();
            if (cancel) token.CancelAfter(100);
            var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
            var arguments = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" } : new[] { "-c", "sleep 30" };
            var timer = Stopwatch.StartNew();
            TestDelegate run = () => PostgresBackup.Run(executable, arguments, null, token.Token,
                timeout: TimeSpan.FromMilliseconds(cancel ? 30000 : 100));
            if (cancel) Assert.Catch<OperationCanceledException>(run);
            else Assert.That(Assert.Throws<TimeoutException>(run).Message, Does.Contain(Path.GetFileNameWithoutExtension(executable)).And.Contain("time limit"));
            Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        }
    }
}
