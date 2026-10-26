using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;
using NUnit.Framework;
using ServiceHost = NzbDrone.Common.ServiceHost;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Processes;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class DatabaseRestorationServiceFixture
    {
        private sealed class TestDiskProvider : DiskProviderBase
        {
            public long? AvailableSpace { get; set; }
            public string FailMoveFrom { get; set; }
            public string FailCopyFrom { get; set; }
            public TestDiskProvider(IFileSystem fileSystem)
                : base(fileSystem)
            {
            }

            public override long? GetAvailableSpace(string path) => AvailableSpace ?? new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace;
            public override void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public override void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public override void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public override long? GetTotalSize(string path) => throw new NotImplementedException();
            public override bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();

            protected override void MoveFileInternal(string source, string destination)
            {
                if (source == FailMoveFrom)
                {
                    FailMoveFrom = null;
                    throw new IOException("Injected restore move failure");
                }

                base.MoveFileInternal(source, destination);
            }

            protected override void CopyFileInternal(string source, string destination, bool overwrite = false)
            {
                if (source == FailCopyFrom)
                {
                    FailCopyFrom = null;
                    throw new IOException("Injected restore copy failure");
                }

                base.CopyFileInternal(source, destination, overwrite);
            }
        }

        private sealed class TestAppFolderInfo : IAppFolderInfo
        {
            public TestAppFolderInfo(string appDataFolder)
            {
                AppDataFolder = appDataFolder;
                TempFolder = appDataFolder;
                StartUpFolder = appDataFolder;
            }

            public string AppDataFolder { get; }
            public string TempFolder { get; }
            public string StartUpFolder { get; }
        }

        private sealed class FailingHost : IHost
        {
            public FailingHost(IRestoreDatabase restore)
            {
                Services = new ServiceCollection().AddSingleton(restore)
                    .AddSingleton(InterfaceProxy.Proxy<IHostApplicationLifetime>((_, _) => CancellationToken.None)).BuildServiceProvider();
            }

            public IServiceProvider Services { get; }
            public bool Disposed { get; private set; }
            public Task StartAsync(CancellationToken cancellationToken = default) => throw new IOException("Injected listener failure");
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Dispose() => Disposed = true;
        }

        private sealed class StoppingHost : IHost
        {
            public StoppingHost(IServiceProvider services) => Services = services;
            public IServiceProvider Services { get; }
            public bool Disposed { get; private set; }
            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Dispose() => Disposed = true;
        }

        private string _tempFolder;
        private TestAppFolderInfo _appFolderInfo;
        private DatabaseRestorationService _subject;
        private TestDiskProvider _disk;

        [SetUp]
        public void SetUp()
        {
            _tempFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"database_restore_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempFolder);
            _appFolderInfo = new TestAppFolderInfo(_tempFolder);
            _disk = new TestDiskProvider(new FileSystem());
            _subject = new DatabaseRestorationService(_disk, _appFolderInfo);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, true);
            }
        }

        [Test]
        public void should_reject_a_readarr_database_without_touching_the_live_database()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            CreateReadarrDatabase(_appFolderInfo.GetDatabaseRestore());

            Assert.Throws<InvalidDataException>(() => _subject.Restore());

            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live"));
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore()), Is.False);
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore() + ".failed"), Is.True);
            });
        }

        [Test]
        public void should_reject_a_corrupt_restore_without_touching_the_live_database()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            File.WriteAllText(_appFolderInfo.GetDatabaseRestore(), "not a database");

            Assert.Throws<SqliteException>(() => _subject.Restore());

            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live"));
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore()), Is.False);
                Assert.That(File.Exists(_appFolderInfo.GetDatabaseRestore() + ".failed"), Is.True);
            });
        }

        [Test]
        public void should_restore_the_original_database_when_candidate_migration_fails()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            CreateChaptarrDatabase(_appFolderInfo.GetDatabaseRestore(), "candidate");

            Assert.That(_subject.Restore(), Is.True);
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));

            _subject.Rollback();

            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live"));
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase() + ".failed-restore"), Is.EqualTo("candidate"));
                Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.False);
            });
        }

        [Test]
        public void should_discard_the_saved_original_only_after_restore_is_committed()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            CreateChaptarrDatabase(_appFolderInfo.GetDatabaseRestore(), "candidate");

            Assert.That(_subject.Restore(), Is.True);
            Assert.That(ReadMarker(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous", "chaptarr.db")), Is.EqualTo("live"));

            _subject.Commit();

            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
                Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.False);
            });
        }

        [Test]
        public void archive_migration_failure_should_restore_main_queue_and_config()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(File.ReadAllText(_appFolderInfo.GetConfigPath()), Does.Contain("candidate"));
            Assert.That(_subject.Restore(), Is.True);
            Assert.That(ReadMarker(_appFolderInfo.GetStagingDatabase()), Is.EqualTo("candidate"));
            _subject.Rollback();
            AssertLive();
        }

        [Test]
        public void interruption_after_cutover_should_recover_all_originals_on_the_next_start()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(File.ReadAllText(_appFolderInfo.GetConfigPath()), Does.Contain("live"));
            Assert.That(new DatabaseRestorationService(_disk, _appFolderInfo).Restore(), Is.False);
            AssertLive();
        }

        [Test]
        public void a_failed_second_move_should_roll_back_the_first_move_and_config()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            _disk.FailMoveFrom = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "staging.db");
            Assert.Throws<IOException>(() => _subject.Restore());
            AssertLive();
        }

        [Test]
        public void rollback_on_a_clean_target_should_restore_the_original_absence_of_files()
        {
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            _subject.Rollback();
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(_appFolderInfo.GetDatabase()), Is.False);
                Assert.That(File.Exists(_appFolderInfo.GetStagingDatabase()), Is.False);
                Assert.That(File.Exists(_appFolderInfo.GetConfigPath()), Is.False);
            });
        }

        [TestCase("chaptarr.db")]
        [TestCase("staging.db")]
        [TestCase("config.xml")]
        public void a_changed_candidate_after_acceptance_should_preserve_the_originals(string component)
        {
            SeedLive();
            StageCandidate();
            File.AppendAllText(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), component), "changed");
            Assert.DoesNotThrow(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).RestoreFailed, Is.True);
            AssertLive();
        }

        [TestCase("staging.db")]
        [TestCase("chaptarr.db")]
        public void a_missing_manifest_component_should_be_rejected_before_staging(string component)
        {
            SeedLive();
            var folder = CandidateFolder();
            File.Delete(Path.Combine(folder, component));
            Assert.Throws<InvalidDataException>(() => _subject.Stage(folder));
            Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.False);
            AssertLive();
        }

        [Test]
        public void a_foreign_queue_with_a_valid_checksum_should_be_rejected()
        {
            SeedLive();
            var folder = CandidateFolder();
            File.Delete(Path.Combine(folder, "staging.db"));
            CreateReadarrDatabase(Path.Combine(folder, "staging.db"));
            File.Delete(Path.Combine(folder, BackupManifest.FileName));
            BackupManifest.Create(folder, DatabaseType.SQLite).Write(folder);
            Assert.Throws<InvalidDataException>(() => _subject.Stage(folder));
            AssertLive();
        }

        [TestCase("version")]
        [TestCase("format")]
        [TestCase("engine")]
        public void an_incompatible_backup_should_leave_the_receiving_instance_unchanged(string field)
        {
            SeedLive();
            var folder = CandidateFolder();
            var path = Path.Combine(folder, BackupManifest.FileName);
            var manifest = JsonNode.Parse(File.ReadAllText(path));
            if (field == "version")
            {
                manifest["ChaptarrVersion"] = "999.0.0.0";
            }
            else if (field == "format")
            {
                manifest["FormatVersion"] = 999;
            }
            else
            {
                manifest["DatabaseType"] = (int)DatabaseType.PostgreSQL;
            }

            File.WriteAllText(path, manifest.ToJsonString());
            Assert.Throws<InvalidDataException>(() => _subject.Stage(folder));
            AssertLive();
        }

        [Test]
        public void corruption_after_config_preparation_should_roll_back_the_config_too()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            File.AppendAllText(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "staging.db"), "changed");
            Assert.Throws<InvalidDataException>(() => _subject.Restore());
            AssertLive();
        }

        [TestCase("FormatVersion")]
        [TestCase("ChaptarrVersion")]
        [TestCase("DatabaseType")]
        [TestCase("Files")]
        public void a_manifest_missing_required_metadata_should_be_rejected(string field)
        {
            SeedLive();
            var folder = CandidateFolder();
            var path = Path.Combine(folder, BackupManifest.FileName);
            var manifest = JsonNode.Parse(File.ReadAllText(path)).AsObject();
            manifest.Remove(field);
            File.WriteAllText(path, manifest.ToJsonString());
            Assert.Throws<InvalidDataException>(() => _subject.Stage(folder));
            AssertLive();
        }

        [Test]
        public void a_raw_database_with_a_future_schema_must_be_rejected_before_replacing_the_current_database()
        {
            SeedLive();
            CreateChaptarrDatabase(_appFolderInfo.GetDatabaseRestore(), "candidate");
            using (var connection = Open(_appFolderInfo.GetDatabaseRestore()))
            {
                Execute(connection, "CREATE TABLE VersionInfo (Version INTEGER); INSERT INTO VersionInfo VALUES (999999)");
            }

            Assert.Throws<InvalidDataException>(() => _subject.Restore());
            AssertLive();
        }

        [Test]
        public void invalid_listener_config_should_recover_before_database_initialization()
        {
            SeedLive();
            var folder = CandidateFolder();
            File.WriteAllText(Path.Combine(folder, "config.xml"), "<Config><Port>not-an-integer</Port></Config>");
            File.Delete(Path.Combine(folder, BackupManifest.FileName));
            BackupManifest.Create(folder, DatabaseType.SQLite).Write(folder);
            _subject.Stage(folder);
            var args = new[] { "/data=" + _tempFolder };
            var recoveredStarts = 0;
            Assert.DoesNotThrow(() => Bootstrap.Start(args, _ =>
            {
                AssertLive();
                recoveredStarts++;
                throw new TerminateApplicationException("Recovered configuration reached host setup");
            }));
            Assert.That(recoveredStarts, Is.EqualTo(1));
            AssertLive();
            Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.False);
            Assert.DoesNotThrow(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
        }

        [TestCase("")]
        [TestCase("/nosingleinstancecheck")]
        [TestCase("/restart")]
        public void a_second_start_must_not_prepare_or_abort_another_instances_pending_restore(string flag)
        {
            SeedLive();
            var folder = CandidateFolder();
            File.WriteAllText(Path.Combine(folder, "config.xml"), "<Config><Port>not-an-integer</Port></Config>");
            File.Delete(Path.Combine(folder, BackupManifest.FileName));
            BackupManifest.Create(folder, DatabaseType.SQLite).Write(folder);
            _subject.Stage(folder);
            var receiptPath = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = File.ReadAllText(receiptPath);
            using var owner = new FileStream(Path.Combine(_tempFolder, "chaptarr.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            Assert.DoesNotThrow(() => Bootstrap.Start(new[] { "/data=" + _tempFolder, flag }));

            AssertLive();
            Assert.That(File.ReadAllText(receiptPath), Is.EqualTo(receipt));
            Assert.That(Directory.Exists(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous")), Is.False);
        }

        [Test]
        public void startup_owns_only_its_data_folder_and_releases_it_after_failed_configuration()
        {
            SeedLive();
            StageCandidate();
            var other = new TestAppFolderInfo(Path.Combine(_tempFolder, "other-instance"));
            var args = new[] { "/data=" + _tempFolder };

            Assert.Throws<OperationCanceledException>(() => Bootstrap.Start(args, _ =>
            {
                Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_appFolderInfo));
                using var independent = SingleInstancePolicy.AcquireAppData(other);
                throw new OperationCanceledException("Injected startup failure");
            }));

            AssertLive();
            using var nextStart = SingleInstancePolicy.AcquireAppData(_appFolderInfo);
            Assert.That(nextStart.CanWrite, Is.True);
        }

        [TestCase(true, false, 1)]
        [TestCase(false, false, 0)]
        [TestCase(true, true, 0)]
        public void restart_must_wait_for_host_disposal_and_rollback(bool restartPending, bool windowsService, int expectedSpawns)
        {
            using var stopping = new CancellationTokenSource();
            using var stopped = new CancellationTokenSource();
            StoppingHost host = null;
            var rolledBack = false;
            var spawns = 0;
            var lifetime = InterfaceProxy.Proxy<IHostApplicationLifetime>((method, _) => method.Name switch
            {
                "get_ApplicationStarted" => CancellationToken.None,
                "get_ApplicationStopping" => stopping.Token,
                "get_ApplicationStopped" => stopped.Token,
                "StopApplication" => Stop(),
                _ => throw new NotImplementedException(method.Name)
            });
            object Stop()
            {
                stopping.Cancel();
                stopped.Cancel();
                return null;
            }

            var runtime = InterfaceProxy.Proxy<IRuntimeInfo>((method, _) => method.Name switch
            {
                "get_RestartPending" => restartPending,
                "get_IsWindowsService" => windowsService,
                "get_ExecutingApplication" => "Chaptarr",
                _ => throw new NotImplementedException(method.Name)
            });
            var process = InterfaceProxy.Proxy<IProcessProvider>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("SpawnNewProcess"));
                Assert.That(host.Disposed, Is.True);
                Assert.That(rolledBack, Is.True);
                Assert.That((string)args[1], Does.Contain("/restart").And.Contain("/nobrowser"));
                spawns++;
                return null;
            });
            var restore = InterfaceProxy.Proxy<IRestoreDatabase>((method, _) =>
            {
                if (method.Name == "get_CommitFailed")
                {
                    return false;
                }
                Assert.That(method.Name, Is.EqualTo("Rollback"));
                Assert.That(host.Disposed, Is.True);
                rolledBack = true;
                return null;
            });
            var appLifetime = new AppLifetime(lifetime, null, runtime, new StartupContext(), null, process, null, null, LogManager.GetCurrentClassLogger());
            using var services = new ServiceCollection()
                .AddSingleton(lifetime)
                .AddSingleton(restore)
                .AddSingleton<IHostedService>(appLifetime)
                .BuildServiceProvider();
            host = new StoppingHost(services);

            var restart = (Action)typeof(Bootstrap).GetMethod("RunHost", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { host, null });
            Assert.That(host.Disposed, Is.True);
            Assert.That(spawns, Is.Zero, "The stopped callback runs before host disposal");
            Assert.That(restart, Is.Not.Null);
            restart();
            Assert.That(spawns, Is.EqualTo(expectedSpawns));
        }

        [Test]
        public void corruption_on_a_restart_after_config_preparation_should_recover_the_original_config()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            File.AppendAllText(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "staging.db"), "changed");
            Assert.DoesNotThrow(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).RestoreFailed, Is.True);
            AssertLive();
            Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.False);
        }

        [TestCase("<PostgresHost>unrelated-server</PostgresHost>", "PostgresHost")]
        [TestCase("<postgreshost>unrelated-server</postgreshost>", "PostgresHost")]
        [TestCase("<chaptarr><postgres><host>unrelated-server</host></postgres></chaptarr>", "Chaptarr:Postgres:Host")]
        [TestCase("<Readarr><PostGres><HOST>unrelated-server</HOST></PostGres></Readarr>", "Readarr:Postgres:Host")]
        [TestCase("<AUDIOARR><POSTGRES><host>unrelated-server</host></POSTGRES></AUDIOARR>", "Audioarr:Postgres:Host")]
        public void a_sqlite_restore_must_not_redirect_initialization_to_a_postgres_server(string content, string key)
        {
            SeedLive();
            var folder = CandidateFolder();
            var path = Path.Combine(folder, "config.xml");
            File.WriteAllText(path, "<Config>" + content + "</Config>");
            var configuration = new ConfigurationBuilder().AddXmlFile(path).Build();
            Assert.That(configuration[key], Is.EqualTo("unrelated-server"));
            File.Delete(Path.Combine(folder, BackupManifest.FileName));
            BackupManifest.Create(folder, DatabaseType.SQLite).Write(folder);
            Assert.Throws<InvalidDataException>(() => _subject.Stage(folder));
            AssertLive();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_effective_postgres_target_must_reject_pending_sqlite_stores_before_opening_database_factories(bool raw)
        {
            SeedLive();
            if (raw)
            {
                CreateChaptarrDatabase(_appFolderInfo.GetDatabaseRestore(), "candidate");
            }
            else
            {
                StageCandidate();
            }

            var exception = Assert.Throws<TargetInvocationException>(() => typeof(Startup)
                .GetMethod("InitializeDatabases", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[]
                {
                    _subject,
                    new Lazy<IMainDatabase>(() => throw new AssertionException("Main database must not be opened")),
                    new Lazy<ILogDatabase>(() => throw new AssertionException("Log database must not be opened")),
                    new Lazy<ICacheDatabase>(() => throw new AssertionException("Cache database must not be opened")),
                    null, InterfaceProxy.Proxy<IHostApplicationLifetime>((_, _) => CancellationToken.None), DatabaseType.PostgreSQL
                }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidDataException>());
            AssertLive();
        }

        [Test]
        public void a_postgres_start_without_a_restore_must_not_apply_sqlite_recovery_files()
        {
            SeedLive();
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase() + ".pre-restore", "older");
            Assert.That(_subject.Restore(DatabaseType.PostgreSQL), Is.False);
            AssertLive();
        }

        [TestCase("chaptarr.db")]
        [TestCase("staging.db")]
        public void a_valid_backup_can_recover_over_a_corrupt_original_and_still_roll_back(string component)
        {
            SeedLive();
            var path = Path.Combine(_tempFolder, component);
            File.WriteAllText(path, "not a SQLite database");
            var bytes = File.ReadAllBytes(path);
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            Assert.That(ReadMarker(path), Is.EqualTo("candidate"));
            _subject.Rollback();
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                Assert.That(File.ReadAllText(_appFolderInfo.GetConfigPath()), Does.Contain("live"));
                Assert.That(ReadMarker(Path.Combine(_tempFolder, component == "chaptarr.db" ? "staging.db" : "chaptarr.db")), Is.EqualTo("live"));
            });
        }

        [Test]
        public void a_failed_corrupt_original_sidecar_capture_must_leave_all_original_bytes_in_place()
        {
            SeedLive();
            var path = _appFolderInfo.GetDatabase();
            File.WriteAllText(path, "not a SQLite database");
            File.WriteAllText(path + "-journal", "original journal");
            var bytes = File.ReadAllBytes(path);
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            _disk.FailCopyFrom = path + "-journal";
            Assert.Throws<IOException>(() => _subject.Restore());
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                Assert.That(File.ReadAllText(path + "-journal"), Is.EqualTo("original journal"));
                Assert.That(ReadMarker(_appFolderInfo.GetStagingDatabase()), Is.EqualTo("live"));
                Assert.That(File.ReadAllText(_appFolderInfo.GetConfigPath()), Does.Contain("live"));
            });
        }

        [Test]
        public void an_interrupted_corrupt_original_rollback_must_recover_original_sidecars_on_the_next_start()
        {
            SeedLive();
            var path = _appFolderInfo.GetDatabase();
            File.WriteAllText(path, "not a SQLite database");
            File.WriteAllText(path + "-journal", "original journal");
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            _disk.FailMoveFrom = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous", "chaptarr.db");
            Assert.Throws<IOException>(() => _subject.Rollback());
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("not a SQLite database"));
                Assert.That(File.ReadAllText(path + "-journal"), Is.EqualTo("original journal"));
                Assert.That(ReadMarker(_appFolderInfo.GetStagingDatabase()), Is.EqualTo("live"));
                Assert.That(File.ReadAllText(_appFolderInfo.GetConfigPath()), Does.Contain("live"));
            });
        }

        [Test]
        public void partial_committed_cleanup_must_not_leave_a_pending_restore_without_a_receipt()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.DeleteFolder))
                {
                    File.Delete(Path.Combine((string)args[0], "restore.json"));
                    throw new IOException("Injected partial cleanup failure");
                }

                return method.Invoke(_disk, args);
            });
            new DatabaseRestorationService(disk, _appFolderInfo).Commit();
            Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.False);
            Assert.DoesNotThrow(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
            Assert.That(_subject.Restore(), Is.False);
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
        }

        [Test]
        public void a_listener_start_failure_should_dispose_the_host_then_restore_all_originals()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            FailingHost host = null;
            var restore = InterfaceProxy.Proxy<IRestoreDatabase>((method, args) =>
            {
                Assert.That(host.Disposed, Is.True, "The host must release resources before the originals move back");
                return method.Invoke(_subject, args);
            });
            host = new FailingHost(restore);
            var exception = Assert.Throws<TargetInvocationException>(() => typeof(Bootstrap)
                .GetMethod("RunHost", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { host, null }));
            Assert.That(exception.InnerException, Is.TypeOf<IOException>());
            AssertLive();
        }

        [Test]
        public void a_failed_normal_start_without_a_restore_must_not_move_the_current_database()
        {
            SeedLive();
            _subject.Rollback();
            AssertLive();
            Assert.That(File.Exists(_appFolderInfo.GetDatabase() + ".failed-restore"), Is.False);
        }

        [TestCase("")]
        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("broken")]
        public void a_corrupt_unapplied_receipt_must_not_block_repeated_startups(string content)
        {
            SeedLive();
            StageCandidate();
            File.WriteAllText(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json"), content);
            for (var start = 0; start < 2; start++)
            {
                Assert.DoesNotThrow(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
                Assert.That(_subject.Restore(), Is.False);
                AssertLive();
            }
            Assert.That(Directory.GetDirectories(_tempFolder, Path.GetFileName(_appFolderInfo.GetDatabaseRestoreBundle()) + ".failed.*"), Has.Length.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_incomplete_applied_manifest_must_not_hide_missing_originals(bool empty)
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            var path = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = JsonNode.Parse(File.ReadAllText(path)).AsObject();
            var files = receipt["Manifest"]["Files"].AsObject();
            if (empty)
            {
                files.Clear();
            }
            else
            {
                files.Remove("chaptarr.db");
            }
            receipt["Originals"].AsObject().Clear();
            File.WriteAllText(path, receipt.ToJsonString());
            Assert.Throws<InvalidDataException>(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
            Assert.That(ReadMarker(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous", "chaptarr.db")), Is.EqualTo("live"));
        }

        [TestCase("Originals")]
        [TestCase("State")]
        public void missing_applied_receipt_fields_must_not_claim_successful_recovery(string field)
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            var path = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = JsonNode.Parse(File.ReadAllText(path)).AsObject();
            receipt.Remove(field);
            File.WriteAllText(path, receipt.ToJsonString());
            Assert.Throws<InvalidDataException>(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
            Assert.That(ReadMarker(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous", "chaptarr.db")), Is.EqualTo("live"));
        }

        [Test]
        public void a_corrupt_applied_receipt_must_retain_originals_and_refuse_to_guess()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            File.WriteAllText(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json"), "broken");
            Assert.Throws<InvalidDataException>(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
            Assert.That(ReadMarker(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous", "chaptarr.db")), Is.EqualTo("live"));
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_unrelated_original_leftover_must_not_replace_a_healthy_database(bool startup)
        {
            SeedLive();
            var previous = _appFolderInfo.GetDatabase() + ".pre-restore";
            CreateChaptarrDatabase(previous, "older");
            if (startup)
            {
                Assert.That(_subject.Restore(), Is.False);
            }
            else
            {
                _subject.Rollback();
                _subject.Commit();
            }

            AssertLive();
            Assert.That(ReadMarker(previous), Is.EqualTo("older"));
        }

        [Test]
        public void shutdown_of_the_previous_process_must_not_rollback_the_new_process_restore()
        {
            SeedLive();
            var previousProcess = new DatabaseRestorationService(_disk, _appFolderInfo);
            Assert.That(previousProcess.Restore(), Is.False);
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            previousProcess.Rollback();
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
            _subject.Rollback();
            AssertLive();
        }

        [TestCase("<Config><Marker>live</Marker></Config>")]
        [TestCase("<Config><PostgresPassword>persisted-only</PostgresPassword><PostgresHost>persisted-host</PostgresHost></Config>")]
        [TestCase("<Config><Chaptarr><Postgres><Password>nested-only</Password><Host>nested-host</Host></Postgres></Chaptarr></Config>")]
        public void postgres_target_preservation_must_copy_only_persisted_settings(string liveConfig)
        {
            File.WriteAllText(_appFolderInfo.GetConfigPath(), liveConfig);
            var path = Path.Combine(_tempFolder, "candidate-config.xml");
            File.WriteAllText(path, "<Config><Marker>candidate</Marker><PostgresPassword>foreign-secret</PostgresPassword><Chaptarr><Postgres><Host>foreign-host</Host></Postgres></Chaptarr></Config>");
            typeof(DatabaseRestorationService).GetMethod("PreservePostgresTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_subject, new object[] { path });
            var actual = System.Xml.Linq.XDocument.Load(path);
            var original = System.Xml.Linq.XDocument.Parse(liveConfig);
            Assert.That(actual.Root.Element("Marker")?.Value, Is.EqualTo("candidate"));
            Assert.That(actual.Descendants().Where(e => e.Name.LocalName is "PostgresPassword" or "PostgresHost" || e.Parent?.Name.LocalName == "Postgres")
                .Select(e => e.ToString()), Is.EqualTo(original.Descendants().Where(e => e.Name.LocalName is "PostgresPassword" or "PostgresHost" || e.Parent?.Name.LocalName == "Postgres").Select(e => e.ToString())));
            Assert.That(File.ReadAllText(path), Does.Not.Contain("foreign-secret").And.Not.Contain("foreign-host"));
        }

        [Test]
        public void normal_shutdown_must_leave_an_uploaded_restore_ready_for_the_next_start()
        {
            SeedLive();
            StageCandidate();
            _subject.Rollback();
            DatabaseRestorationService.AbortConfiguration(_appFolderInfo);
            Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.True);
            AssertLive();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            _subject.Commit();
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
        }

        [Test]
        public void an_uploaded_restore_must_not_be_committed_before_it_has_been_applied()
        {
            SeedLive();
            StageCandidate();
            Assert.Throws<InvalidOperationException>(() => _subject.Commit());
            AssertLive();
            Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.True);
        }

        [Test]
        public void store_initialization_must_not_discard_originals_until_the_application_has_started()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            using var started = new CancellationTokenSource();
            var lifetime = InterfaceProxy.Proxy<IHostApplicationLifetime>((method, _) => method.Name switch
            {
                "get_ApplicationStarted" => started.Token,
                "get_ApplicationStopping" => CancellationToken.None,
                _ => throw new NotImplementedException(method.Name)
            });
            InitializeStores(lifetime, InterfaceProxy.Proxy<IStagingDbContext>((_, _) => null));
            Assert.That(ReadMarker(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous", "chaptarr.db")), Is.EqualTo("live"));
            Assert.That(ReadMarker(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous", "staging.db")), Is.EqualTo("live"));
            started.Cancel();
            Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.False);
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
        }

        [Test]
        public void a_broken_queue_without_a_restore_should_leave_the_application_available_and_imports_disabled()
        {
            SeedLive();
            File.WriteAllText(_appFolderInfo.GetStagingDatabase(), "invalid queue database");
            var staging = new StagingDbContext(_appFolderInfo, LogManager.GetCurrentClassLogger());
            Assert.DoesNotThrow(() => InitializeStores(null, staging));
            Assert.That(staging.InitializationError, Is.TypeOf<SqliteException>());
            var exception = Assert.Throws<SqliteException>(() => staging.OpenConnection());
            Assert.That(staging.InitializationError, Is.SameAs(exception));
            Assert.That(exception.SqliteErrorCode, Is.EqualTo(26));
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live"));
            Assert.That(File.ReadAllText(_appFolderInfo.GetStagingDatabase()), Is.EqualTo("invalid queue database"));
        }

        [Test]
        public void staging_initialization_failure_must_recover_the_whole_cohort()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            var staging = InterfaceProxy.Proxy<IStagingDbContext>((_, _) => throw new IOException("Injected queue initialization failure"));
            var exception = Assert.Throws<TargetInvocationException>(() => InitializeStores(null, staging));
            Assert.That(exception.InnerException, Is.TypeOf<IOException>());
            AssertLive();
        }

        [Test]
        public void a_failed_restore_commit_must_not_publish_application_readiness_or_start_background_work()
        {
            using var started = new CancellationTokenSource();
            using var stopping = new CancellationTokenSource();
            var lifetime = InterfaceProxy.Proxy<IHostApplicationLifetime>((method, _) => method.Name switch
            {
                "get_ApplicationStarted" => started.Token,
                "get_ApplicationStopping" => stopping.Token,
                "get_ApplicationStopped" => CancellationToken.None,
                "StopApplication" => Stop(),
                _ => throw new NotImplementedException(method.Name)
            });
            object Stop()
            {
                stopping.Cancel();
                return null;
            }

            var isStarting = true;
            var eventCount = 0;
            var runtime = InterfaceProxy.Proxy<IRuntimeInfo>((method, args) => method.Name switch
            {
                "set_IsStarting" => SetStarting((bool)args[0]),
                "set_IsExiting" => null,
                _ => throw new NotImplementedException(method.Name)
            });
            object SetStarting(bool value)
            {
                isStarting = value;
                return null;
            }

            var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => { eventCount++; return null; });
            _ = new AppLifetime(lifetime, InterfaceProxy.Proxy<IConfigFileProvider>((_, _) => false), runtime,
                new StartupContext(new[] { "/nobrowser" }), null, null, events, null, LogManager.GetCurrentClassLogger());
            var restore = InterfaceProxy.Proxy<IRestoreDatabase>((method, _) => method.Name switch
            {
                "Restore" => true,
                "Commit" => throw new IOException("Injected commit failure"),
                _ => throw new NotImplementedException(method.Name)
            });
            InitializeStores(lifetime, InterfaceProxy.Proxy<IStagingDbContext>((_, _) => null), restore);
            Assert.Throws<AggregateException>(() => started.Cancel());
            Assert.Multiple(() =>
            {
                Assert.That(stopping.IsCancellationRequested, Is.True);
                Assert.That(isStarting, Is.True);
                Assert.That(eventCount, Is.Zero);
            });
        }

        private void InitializeStores(IHostApplicationLifetime lifetime, IStagingDbContext staging, IRestoreDatabase restore = null)
        {
            lifetime ??= InterfaceProxy.Proxy<IHostApplicationLifetime>((_, _) => CancellationToken.None);
            typeof(Startup).GetMethod("InitializeDatabases", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[]
            {
                restore ?? _subject,
                new Lazy<IMainDatabase>(() => new MainDatabase(null)),
                new Lazy<ILogDatabase>(() => new LogDatabase(null)),
                new Lazy<ICacheDatabase>(() => new CacheDatabase(null)),
                staging,
                lifetime,
                DatabaseType.SQLite
            });
        }

        [Test]
        public void rollback_after_startup_should_release_only_the_restored_stores_pooled_handles()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _appFolderInfo.GetDatabase(), Cache = SqliteCacheMode.Private, Pooling = true
            }.ToString()))
            {
                connection.Open();
                Execute(connection, "SELECT * FROM RestoreMarker");
            }

            _subject.Rollback();
            AssertLive();
        }

        [Test]
        public void rollback_should_preserve_committed_pages_that_were_only_in_the_original_wal()
        {
            SeedLive();
            var source = Path.Combine(_tempFolder, "wal-source.db");
            CreateChaptarrDatabase(source, "live");
            using (var connection = Open(source))
            {
                Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE RestoreMarker SET Value='live-wal';");
                Assert.That(File.Exists(source + "-wal"), Is.True);
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    File.Copy(source + suffix, _appFolderInfo.GetDatabase() + suffix, true);
                }
            }

            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            _subject.Rollback();
            Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live-wal"));
        }

        [Test]
        public void a_committed_receipt_left_by_cleanup_failure_must_never_restore_the_old_data()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            var path = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = JsonNode.Parse(File.ReadAllText(path));
            receipt["State"] = "Committed";
            File.WriteAllText(path, receipt.ToJsonString());
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
                Assert.That(ReadMarker(_appFolderInfo.GetStagingDatabase()), Is.EqualTo("candidate"));
                Assert.That(File.ReadAllText(_appFolderInfo.GetConfigPath()), Does.Contain("candidate"));
            });
        }

        private string CandidateFolder()
        {
            var folder = Path.Combine(_tempFolder, "candidate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            CreateChaptarrDatabase(Path.Combine(folder, "chaptarr.db"), "candidate");
            CreateChaptarrDatabase(Path.Combine(folder, "logs.db"), "candidate");
            CreateChaptarrDatabase(Path.Combine(folder, "cache.db"), "candidate");
            CreateQueue(Path.Combine(folder, "staging.db"), "candidate");
            File.WriteAllText(Path.Combine(folder, "config.xml"), "<Config><Marker>candidate</Marker></Config>");
            BackupManifest.Create(folder, DatabaseType.SQLite).Write(folder);
            return folder;
        }

        [Test]
        [Platform("Win")]
        public void windows_appdata_lock_must_allow_a_reader_but_refuse_a_second_writer()
        {
            var path = _appFolderInfo.GetAppDataLockFile();
            using var reader = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            using var first = SingleInstancePolicy.AcquireAppData(_appFolderInfo);
            Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_appFolderInfo));
        }

        [Test]
        public void recovered_startup_must_restart_once_only_after_host_disposal_and_verified_rollback()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            var host = new FailingHost(_subject);
            var calls = 0;
            Action restart = () =>
            {
                Assert.That(host.Disposed, Is.True);
                AssertLive();
                calls++;
            };
            var action = (Action)typeof(Bootstrap).GetMethod("RunHost", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { host, restart });
            Assert.That(calls, Is.Zero);
            Assert.That(_subject.RecoveryCompleted, Is.True);
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).RestoreFailed, Is.True);
            action();
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(true, true, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, true)]
        public void real_host_restore_commit_failure_restarts_only_after_successful_rollback(bool failCommit, bool allowRestart, bool failRollback)
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            using var host = new HostBuilder()
                .ConfigureServices(services => services.AddSingleton<IRestoreDatabase>(_subject))
                .Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStarted.Register(lifetime.StopApplication);
            InitializeStores(lifetime, InterfaceProxy.Proxy<IStagingDbContext>((_, _) => null));
            if (failCommit)
            {
                Directory.CreateDirectory(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json.tmp"));
            }
            if (failRollback)
            {
                Directory.CreateDirectory(_appFolderInfo.GetDatabase() + ".failed-restore");
            }

            var calls = 0;
            Action restart = () =>
            {
                Assert.Throws<ObjectDisposedException>(() => host.Services.GetRequiredService<IRestoreDatabase>());
                AssertLive();
                calls++;
            };
            var runHost = typeof(Bootstrap).GetMethod("RunHost", BindingFlags.NonPublic | BindingFlags.Static);
            if (failRollback)
            {
                Assert.Throws<TargetInvocationException>(() => runHost.Invoke(null, new object[] { host, restart }));
                Assert.That(_subject.RecoveryCompleted, Is.False);
                Assert.That(calls, Is.Zero);
                Assert.That(ReadMarker(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "previous", "chaptarr.db")), Is.EqualTo("live"));
                return;
            }

            Action action = null;
            Assert.DoesNotThrow(() => action = (Action)runHost.Invoke(null, new object[] { host, allowRestart ? restart : null }));
            Assert.Throws<ObjectDisposedException>(() => host.Services.GetRequiredService<IRestoreDatabase>());
            Assert.That(calls, Is.Zero);
            Assert.That(_subject.RecoveryCompleted, Is.EqualTo(failCommit));
            if (failCommit)
            {
                AssertLive();
            }
            else
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
                Assert.That(Directory.Exists(_appFolderInfo.GetDatabaseRestoreBundle()), Is.False);
            }

            if (failCommit && allowRestart)
            {
                Assert.That(action, Is.SameAs(restart));
                action();
                Assert.That(calls, Is.EqualTo(1));
            }
            else
            {
                Assert.That(action, Is.Null);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void real_host_intentional_stop_does_not_restart_after_rolling_back_an_uncommitted_restore(bool cancelStartup)
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            using var host = new HostBuilder()
                .ConfigureServices(services => services.AddSingleton<IRestoreDatabase>(_subject))
                .Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            if (cancelStartup)
            {
                lifetime.StopApplication();
            }
            else
            {
                lifetime.ApplicationStarted.Register(lifetime.StopApplication);
            }

            var action = (Action)typeof(Bootstrap).GetMethod("RunHost", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { host, (Action)(() => Assert.Fail("Intentional shutdown must stay stopped")) });
            Assert.That(action, Is.Null);
            Assert.That(_subject.RecoveryCompleted, Is.True);
            AssertLive();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void service_host_keeps_running_until_real_restore_rollback_and_recovered_host_finish(bool failCommit)
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            var runs = 0;
            ServiceHost.Run(new HostBuilder(), (configureLifetime, stoppingToken) =>
            {
                var builder = new HostBuilder().ConfigureServices(services => services.AddSingleton<IRestoreDatabase>(_subject));
                configureLifetime(builder);
                var host = builder.Build();
                var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                lifetime.ApplicationStarted.Register(lifetime.StopApplication);
                InitializeStores(lifetime, InterfaceProxy.Proxy<IStagingDbContext>((_, _) => null));
                if (failCommit)
                {
                    Directory.CreateDirectory(Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json.tmp"));
                }

                Action recovered = () =>
                {
                    Assert.That(stoppingToken.IsCancellationRequested, Is.False);
                    AssertLive();
                    Assert.Throws<ObjectDisposedException>(() => host.Services.GetRequiredService<IRestoreDatabase>());
                    var recoveredBuilder = new HostBuilder();
                    configureLifetime(recoveredBuilder);
                    var recoveredHost = recoveredBuilder.Build();
                    var recoveredLifetime = recoveredHost.Services.GetRequiredService<IHostApplicationLifetime>();
                    recoveredLifetime.ApplicationStarted.Register(recoveredLifetime.StopApplication);
                    recoveredHost.Run();
                    runs++;
                };
                var action = (Action)typeof(Bootstrap).GetMethod("RunHost", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { host, recovered });
                Assert.That(stoppingToken.IsCancellationRequested, Is.False);
                runs++;
                action?.Invoke();
            });

            Assert.That(runs, Is.EqualTo(failCommit ? 2 : 1));
            Assert.That(_subject.RecoveryCompleted, Is.EqualTo(failCommit));
            if (failCommit)
            {
                AssertLive();
            }
            else
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
            }
        }

        [TestCase("Applied")]
        [TestCase("Committed")]
        public void cleanup_or_recovery_receipts_must_work_after_a_downgrade(string state)
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            var path = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = JsonNode.Parse(File.ReadAllText(path));
            receipt["State"] = state;
            receipt["Manifest"]["ChaptarrVersion"] = "9999.0.0.0";
            File.WriteAllText(path, receipt.ToJsonString());
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(new DatabaseRestorationService(_disk, _appFolderInfo).Restore(), Is.False);
            if (state == "Applied") AssertLive();
            else Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("candidate"));
        }

        [Test]
        public void prepared_configuration_must_recover_after_a_downgrade_before_store_cutover()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(File.ReadAllText(_appFolderInfo.GetConfigPath()), Does.Contain("candidate"));
            var path = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = JsonNode.Parse(File.ReadAllText(path));
            receipt["Manifest"]["ChaptarrVersion"] = "9999.0.0.0";
            File.WriteAllText(path, receipt.ToJsonString());
            Assert.DoesNotThrow(() => DatabaseRestorationService.PrepareConfiguration(_appFolderInfo));
            AssertLive();
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).RestoreFailed, Is.True);
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).PendingRestore, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void verified_failed_restore_files_must_be_removed_by_success_or_factory_reset(bool factoryReset)
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            _subject.Rollback();
            AssertLive();
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).RestoreFailed, Is.True);
            StageCandidate();
            try
            {
                if (factoryReset)
                {
                    DatabaseRestorationService.CancelPendingRestore(_appFolderInfo, true);
                    AssertLive();
                }
                else
                {
                    DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
                    Assert.That(_subject.Restore(), Is.True);
                    _subject.Commit();
                }
                Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).RestoreFailed, Is.False);
                Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).PendingRestore, Is.False);
                Assert.That(Directory.GetFileSystemEntries(_appFolderInfo.AppDataFolder, "*.failed*"), Is.Empty);
            }
            finally
            {
                typeof(FactoryResetState).GetField("_isResetting", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void dismiss_failed_restore_files_must_preserve_live_data_and_pending_recovery(bool pending)
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            _subject.Rollback();
            AssertLive();
            if (pending) StageCandidate();
            if (pending)
            {
                var error = Assert.Throws<NzbDroneClientException>(() => DatabaseRestorationService.DismissFailedRestores(_appFolderInfo));
                Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            }
            else
            {
                DatabaseRestorationService.DismissFailedRestores(_appFolderInfo);
                Assert.That(Directory.GetFileSystemEntries(_appFolderInfo.AppDataFolder, "*.failed*"), Is.Empty);
            }
            AssertLive();
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).RestoreFailed, Is.EqualTo(pending));
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).PendingRestore, Is.EqualTo(pending));
        }

        [Test]
        public void factory_reset_entry_point_must_cancel_a_pending_restore_before_resetting_anything()
        {
            SeedLive();
            StageCandidate();
            var config = InterfaceProxy.Proxy<IConfigFileProvider>((_, _) => throw new IOException("Stop after pending-restore cancellation"));
            var service = new NzbDrone.Core.Reset.FactoryResetService(_appFolderInfo, _disk, null, config, LogManager.GetCurrentClassLogger());
            try
            {
                Assert.ThrowsAsync<IOException>(() => service.ResetEverythingAsync());
                Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).PendingRestore, Is.False);
                AssertLive();
            }
            finally
            {
                typeof(FactoryResetState).GetField("_isResetting", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0);
            }
        }

        [Test]
        public void committed_cleanup_leftovers_must_not_show_a_pending_restore_or_block_another_restore()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            var path = Path.Combine(_appFolderInfo.GetDatabaseRestoreBundle(), "restore.json");
            var receipt = JsonNode.Parse(File.ReadAllText(path));
            receipt["State"] = "Committed";
            File.WriteAllText(path, receipt.ToJsonString());
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).PendingRestore, Is.False);
            StageCandidate();
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).CanCancelRestore, Is.True);
        }

        [Test]
        public void cancel_ready_restore_must_remove_only_candidates_and_leave_live_data()
        {
            SeedLive();
            StageCandidate();
            var status = DatabaseRestorationService.GetStatus(_appFolderInfo);
            Assert.That(status.PendingRestore && status.CanCancelRestore, Is.True);
            DatabaseRestorationService.CancelPendingRestore(_appFolderInfo);
            Assert.That(DatabaseRestorationService.GetStatus(_appFolderInfo).PendingRestore, Is.False);
            AssertLive();
        }

        [Test]
        public void cancel_must_refuse_an_applied_restore_and_retain_recovery_data()
        {
            SeedLive();
            StageCandidate();
            DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            Assert.That(_subject.Restore(), Is.True);
            Assert.Throws<InvalidOperationException>(() => DatabaseRestorationService.CancelPendingRestore(_appFolderInfo));
            _subject.Rollback();
            AssertLive();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void insufficient_space_must_refuse_staging_or_snapshot_before_live_data_moves(bool snapshot)
        {
            SeedLive();
            var candidate = CandidateFolder();
            if (snapshot)
            {
                _subject.Stage(candidate);
                DatabaseRestorationService.PrepareConfiguration(_appFolderInfo);
            }
            _disk.AvailableSpace = 0;
            try
            {
                var error = Assert.Throws<IOException>(() => { if (snapshot) _subject.Restore(); else _subject.Stage(candidate); });
                Assert.That(error.Message, Does.Contain("need").And.Contain("have 0"));
                AssertLive();
            }
            finally
            {
                _disk.AvailableSpace = null;
            }
        }

        private void StageCandidate() => _subject.Stage(CandidateFolder());

        private void SeedLive()
        {
            CreateChaptarrDatabase(_appFolderInfo.GetDatabase(), "live");
            CreateChaptarrDatabase(_appFolderInfo.GetLogDatabase(), "live");
            CreateChaptarrDatabase(_appFolderInfo.GetCacheDatabase(), "live");
            CreateQueue(_appFolderInfo.GetStagingDatabase(), "live");
            File.WriteAllText(_appFolderInfo.GetConfigPath(), "<Config><Marker>live</Marker></Config>");
        }

        private void AssertLive()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ReadMarker(_appFolderInfo.GetDatabase()), Is.EqualTo("live"));
                Assert.That(ReadMarker(_appFolderInfo.GetLogDatabase()), Is.EqualTo("live"));
                Assert.That(ReadMarker(_appFolderInfo.GetCacheDatabase()), Is.EqualTo("live"));
                Assert.That(ReadMarker(_appFolderInfo.GetStagingDatabase()), Is.EqualTo("live"));
                Assert.That(File.ReadAllText(_appFolderInfo.GetConfigPath()), Does.Contain("live"));
            });
        }

        private static void CreateQueue(string path, string marker)
        {
            using var connection = Open(path);
            Execute(connection, @"
                CREATE TABLE ingest_queue (path TEXT, status TEXT);
                CREATE TABLE import_results (queue_item_id INTEGER);
                CREATE TABLE RestoreMarker (Value TEXT NOT NULL);
                INSERT INTO RestoreMarker (Value) VALUES ($marker);", marker);
        }

        private static void CreateChaptarrDatabase(string path, string marker)
        {
            using var connection = Open(path);
            Execute(connection, @"
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    AudiobookRootFolderPath TEXT NULL,
                    EbookRootFolderPath TEXT NULL
                );
                CREATE TABLE RestoreMarker (Value TEXT NOT NULL);
                INSERT INTO RestoreMarker (Value) VALUES ($marker);",
                marker);
        }

        private static void CreateReadarrDatabase(string path)
        {
            using var connection = Open(path);
            Execute(connection, @"
                CREATE TABLE Authors (Id INTEGER PRIMARY KEY);
                CREATE TABLE Books (Id INTEGER PRIMARY KEY);");
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        private static void Execute(SqliteConnection connection, string sql, string marker = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            if (marker != null)
            {
                command.Parameters.AddWithValue("$marker", marker);
            }

            command.ExecuteNonQuery();
        }

        private static string ReadMarker(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM RestoreMarker;";
            return command.ExecuteScalar()?.ToString();
        }
    }
}
