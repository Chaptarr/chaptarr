using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Backup
{
    public partial class BackupServiceCoverageFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void runhost_should_recover_startup_failure_but_preserve_intentional_stop(bool cancellation)
        {
            var rollbacks = 0;
            var restore = InterfaceProxy.Proxy<IRestoreDatabase>((method, _) =>
            {
                if (method.Name == "Rollback") rollbacks++;
                return method.ReturnType == typeof(bool) ? true : null;
            });
            using var host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton(restore);
                services.AddSingleton<IHostedService>(provider => new FailingStarter(provider.GetRequiredService<IHostApplicationLifetime>(), cancellation));
            }).Build();
            Action recovery = () => throw new AssertionException("Recovery callback must be inspected, never invoked in the private probe");
            var run = typeof(Bootstrap).GetMethod("RunHost", BindingFlags.NonPublic | BindingFlags.Static);
            Action returned = null;
            Exception error = null;
            try { returned = (Action)run.Invoke(null, new object[] { host, recovery }); }
            catch (TargetInvocationException ex) { error = ex.InnerException; }
            Assert.That(rollbacks, Is.GreaterThanOrEqualTo(1));
            WriteLifecycleEvidence("runhost-" + cancellation + ".json", new { cancellation, recovery_returned = returned != null, error = error?.GetType().Name, rollbacks });
            if (cancellation)
            {
                Assert.That(returned, Is.Null, "Intentional stop must not schedule another host after compensation");
                Assert.That(error, Is.Null, "Intentional stop must remain a normal exit after compensation");
            }
            else
            {
                Assert.That(returned, Is.SameAs(recovery));
                Assert.That(error, Is.Null);
            }
        }

        [TestCase(DatabaseType.SQLite, false)]
        [TestCase(DatabaseType.SQLite, true)]
        [TestCase(DatabaseType.PostgreSQL, false)]
        [TestCase(DatabaseType.PostgreSQL, true)]
        public void cancelled_startup_should_finish_post_cutover_or_interrupted_compensation(DatabaseType type, bool interrupted)
        {
            using var role = type == DatabaseType.PostgreSQL ? new PostgresRole(canCreate: true) : null;
            using var source = type == DatabaseType.PostgreSQL ? new PostgresStores("cancel-compensation-source", role) : null;
            using var target = type == DatabaseType.PostgreSQL ? new PostgresStores("cancel-compensation-original", role) : null;
            var sourceFolders = new AppFolders(Path.Combine(_root, "cancel-compensation-source"));
            var targetFolders = new AppFolders(Path.Combine(_root, "cancel-compensation-original"));
            SeedConfig(sourceFolders);
            SeedConfig(targetFolders);
            File.WriteAllText(targetFolders.GetConfigPath(), "<Config><ApiKey>original-before-compensation</ApiKey></Config>");
            SeedStaging(targetFolders);
            if (type == DatabaseType.SQLite)
            {
                foreach (var folders in new[] { sourceFolders, targetFolders })
                {
                    new MigrationController(_logger, null).Migrate(ConnectionString(folders.GetDatabase()), new MigrationContext(MigrationType.Main), DatabaseType.SQLite);
                    SeedAuxiliary(folders);
                    using var main = Open(ConnectionString(folders.GetDatabase()));
                    main.Execute("CREATE TABLE OperationsProbe (Value TEXT); INSERT INTO OperationsProbe VALUES (@Value)", new { Value = folders == sourceFolders ? "source" : "original" });
                }
            }
            var originalConfig = File.ReadAllBytes(targetFolders.GetConfigPath());
            var originalStaging = JsonSerializer.Serialize(Snapshot(ConnectionString(targetFolders.GetStagingDatabase())));
            var originalPg = target?.Snapshot();
            var paths = new[] { targetFolders.GetDatabase(), targetFolders.GetLogDatabase(), targetFolders.GetCacheDatabase() };
            var originalSqlite = type == DatabaseType.SQLite ? paths.ToDictionary(path => path, path => JsonSerializer.Serialize(Snapshot(ConnectionString(path)))) : null;
            var mainDb = type == DatabaseType.PostgreSQL ? source.Main : new MainDatabase(new Database("source", () => Open(ConnectionString(sourceFolders.GetDatabase()))));
            var backup = CreateService(mainDb, sourceFolders, logs: source?.Logs, cache: source?.Cache, connectionStrings: source?.Factory);
            backup.Backup(BackupType.Manual);
            var archive = Directory.GetFiles(backup.GetBackupFolder(BackupType.Manual)).Single();
            var restore = new DatabaseRestorationService(_disk, targetFolders, target?.Factory);
            CreateService(target?.Main ?? new MainDatabase(null), targetFolders, restore, logs: target?.Logs, cache: target?.Cache).Restore(archive);
            DatabaseRestorationService.PrepareConfiguration(targetFolders);
            using var stopping = new CancellationTokenSource();
            var lifetime = InterfaceProxy.Proxy<IHostApplicationLifetime>((method, _) => method.Name switch
            {
                "get_ApplicationStopping" => stopping.Token,
                "get_ApplicationStarted" or "get_ApplicationStopped" => CancellationToken.None,
                _ => null
            });
            if (interrupted)
            {
                Assert.That(restore.Restore(type), Is.True);
                stopping.Cancel();
                Assert.That(restore.Restore(type, stopping.Token), Is.False, "An interrupted applied receipt must recover originals despite cancellation");
            }
            else
            {
                var initialize = typeof(Startup).GetMethod("InitializeDatabases", BindingFlags.NonPublic | BindingFlags.Static);
                var mainFactory = new Lazy<IMainDatabase>(() => { stopping.Cancel(); throw new OperationCanceledException(stopping.Token); });
                var error = Assert.Throws<TargetInvocationException>(() => initialize.Invoke(null, new object[]
                {
                    restore, mainFactory, new Lazy<ILogDatabase>(() => throw new AssertionException("Main startup cancellation must happen first")),
                    new Lazy<ICacheDatabase>(() => throw new AssertionException("Main startup cancellation must happen first")),
                    new StagingDbContext(targetFolders, _logger), lifetime, type
                }));
                Assert.That(error.InnerException, Is.InstanceOf<OperationCanceledException>());
            }
            Assert.That(stopping.IsCancellationRequested, Is.True);
            Assert.That(restore.RecoveryCompleted, Is.True);
            Assert.That(Directory.Exists(targetFolders.GetDatabaseRestoreBundle()), Is.False);
            Assert.That(File.ReadAllBytes(targetFolders.GetConfigPath()), Is.EqualTo(originalConfig));
            Assert.That(JsonSerializer.Serialize(Snapshot(ConnectionString(targetFolders.GetStagingDatabase()))), Is.EqualTo(originalStaging));
            if (type == DatabaseType.PostgreSQL)
            {
                AssertSnapshot(originalPg, target);
                using var admin = PostgresBackup.Open(target.Connections["chaptarr.dump"], "postgres");
                Assert.That(admin.ExecuteScalar<int>("SELECT COUNT(*) FROM pg_database WHERE datname LIKE 'chaptarr_restore_%' OR datname LIKE 'chaptarr_previous_%'"), Is.Zero);
            }
            else
            {
                foreach (var path in paths)
                    Assert.That(JsonSerializer.Serialize(Snapshot(ConnectionString(path))), Is.EqualTo(originalSqlite[path]));
            }
        }

        private sealed class FailingStarter : IHostedService
        {
            private readonly IHostApplicationLifetime _lifetime;
            private readonly bool _cancel;
            public FailingStarter(IHostApplicationLifetime lifetime, bool cancel) { _lifetime = lifetime; _cancel = cancel; }
            public Task StartAsync(CancellationToken token)
            {
                if (_cancel)
                {
                    _lifetime.StopApplication();
                    return Task.FromException(new OperationCanceledException(_lifetime.ApplicationStopping));
                }
                return Task.FromException(new InvalidOperationException("Private startup failure control"));
            }
            public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        }
    }
}
