using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Backup
{
    public partial class BackupServiceCoverageFixture
    {
        [Test]
        public void host_stop_should_cancel_a_blocked_postgres_startup_restore()
        {
            using var role = new PostgresRole(canCreate: true);
            using var source = new PostgresStores("restore-source", role);
            using var target = new PostgresStores("restore-original", role);
            using (var connection = PostgresBackup.Open(source.Connections["chaptarr.dump"]))
            {
                connection.Execute(@"
                    CREATE FUNCTION public.backup_shutdown_probe() RETURNS boolean LANGUAGE plpgsql AS $$
                    BEGIN
                        IF current_database() LIKE 'chaptarr_restore_%' THEN
                            PERFORM pg_sleep(60);
                        END IF;
                        RETURN true;
                    END $$;
                    ALTER TABLE ""BackupProbe"" ADD CONSTRAINT backup_shutdown_probe CHECK (public.backup_shutdown_probe());
                ");
            }
            var original = target.Snapshot();
            var folders = new AppFolders(Path.Combine(_root, "restore-stop"));
            SeedConfig(folders);
            SeedStaging(folders);
            var originalConfig = File.ReadAllBytes(folders.GetConfigPath());
            var originalStaging = Snapshot(ConnectionString(folders.GetStagingDatabase()));
            var archive = PostgresArchive(source, new AppFolders(Path.Combine(_root, "restore-source")));
            var restoration = new DatabaseRestorationService(_disk, folders, target.Factory);
            CreateService(target.Main, folders, restoration, logs: target.Logs, cache: target.Cache).Restore(archive);
            DatabaseRestorationService.PrepareConfiguration(folders);
            using var started = new CancellationTokenSource();
            using var stopping = new CancellationTokenSource();
            using var stopped = new CancellationTokenSource();
            var lifetime = InterfaceProxy.Proxy<IHostApplicationLifetime>((method, _) => method.Name switch
            {
                "get_ApplicationStarted" => started.Token,
                "get_ApplicationStopping" => stopping.Token,
                "get_ApplicationStopped" => stopped.Token,
                "StopApplication" => Stop(),
                _ => throw new NotImplementedException(method.Name)
            });
            object Stop() { stopping.Cancel(); return null; }
            var initialize = typeof(Startup).GetMethod("InitializeDatabases", BindingFlags.NonPublic | BindingFlags.Static);
            var task = Task.Run(() =>
            {
                try
                {
                    initialize.Invoke(null, new object[]
                    {
                        restoration, new Lazy<IMainDatabase>(() => target.Main), new Lazy<ILogDatabase>(() => target.Logs),
                        new Lazy<ICacheDatabase>(() => target.Cache), new StagingDbContext(folders, _logger), lifetime, DatabaseType.PostgreSQL
                    });
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw ex.InnerException;
                }
            });
            using var admin = PostgresBackup.Open(target.Connections["chaptarr.dump"], "postgres");
            List<int> Waiting() => admin.Query<int>(@"SELECT pid FROM pg_stat_activity WHERE datname LIKE 'chaptarr_restore_%'
                AND application_name LIKE 'chaptarr_backup_%' AND wait_event = 'PgSleep'").ToList();
            var result = new Dictionary<string, object>();
            try
            {
                Assert.That(SpinWait.SpinUntil(() => Waiting().Count == 1, TimeSpan.FromSeconds(20)), Is.True);
                var timer = Stopwatch.StartNew();
                stopping.Cancel();
                result["host_stopping"] = stopping.IsCancellationRequested;
                result["native_backend_stopped"] = SpinWait.SpinUntil(() => Waiting().Count == 0, TimeSpan.FromSeconds(5));
                result["native_stop_milliseconds"] = timer.ElapsedMilliseconds;
                result["restore_exited_after_stop"] = WaitForOperation(task, TimeSpan.FromSeconds(15));
                result["recovery_finish_milliseconds"] = timer.ElapsedMilliseconds;
                result["blocked_backends_after_stop"] = Waiting().Count;
            }
            finally
            {
                foreach (var pid in Waiting()) admin.Execute("SELECT pg_cancel_backend(@Pid)", new { Pid = pid });
                Assert.That(WaitForOperation(task, TimeSpan.FromSeconds(10)), Is.True, "Private staged restore must finish during diagnostic cleanup");
                Assert.That(() => task.GetAwaiter().GetResult(), Throws.Exception);
                restoration.Rollback();
                result["cleanup_completed"] = true;
                result["recovery_completed"] = restoration.RecoveryCompleted;
                result["staged_databases_after_cleanup"] = admin.ExecuteScalar<int>("SELECT COUNT(*) FROM pg_database WHERE datname LIKE 'chaptarr_restore_%' OR datname LIKE 'chaptarr_previous_%'");
                result["pending_bundle_after_cleanup"] = Directory.Exists(folders.GetDatabaseRestoreBundle());
                result["config_recovered"] = File.ReadAllBytes(folders.GetConfigPath()).SequenceEqual(originalConfig);
                result["staging_recovered"] = JsonSerializer.Serialize(Snapshot(ConnectionString(folders.GetStagingDatabase()))) == JsonSerializer.Serialize(originalStaging);
                WriteLifecycleEvidence("stop-startup-restore.json", result);
                AssertSnapshot(original, target);
                Assert.That(result["staged_databases_after_cleanup"], Is.EqualTo(0));
                Assert.That(result["pending_bundle_after_cleanup"], Is.EqualTo(false));
                Assert.That(result["config_recovered"], Is.EqualTo(true));
                Assert.That(result["staging_recovered"], Is.EqualTo(true));
            }
            Assert.That(result["native_backend_stopped"], Is.EqualTo(true), "Host stopping must cancel the actual staged PostgreSQL load");
            Assert.That(result["restore_exited_after_stop"], Is.EqualTo(true), "Cancellation must finish uncanceled restore compensation before startup returns");
        }

        public sealed class CompletionObserver : IHandle<CommandExecutedEvent>, IDisposable
        {
            public ManualResetEventSlim Finished { get; } = new();
            public void Handle(CommandExecutedEvent message) => Finished.Set();
            public void Dispose() => Finished.Dispose();
        }

        [TestCase("shutdown")]
        [TestCase("restart")]
        [TestCase("host-stop")]
        public void application_stop_should_cancel_a_blocked_postgres_backup(string route)
        {
            using var role = new PostgresRole();
            using var source = new PostgresStores("global-stop", role);
            var original = source.Snapshot();
            var folders = new AppFolders(Path.Combine(_root, "global-stop"));
            SeedConfig(folders);
            SeedStaging(folders);
            BackupService service = null;
            using var blocker = PostgresBackup.Open(source.Connections["chaptarr.dump"]);
            using var transaction = blocker.BeginTransaction();
            var locked = false;
            _disk.SpaceForPath = _ =>
            {
                if (!locked)
                {
                    blocker.Execute("LOCK TABLE \"BackupProbe\" IN ACCESS EXCLUSIVE MODE", transaction: transaction);
                    locked = true;
                }
                return long.MaxValue;
            };

            using var observer = new CompletionObserver();
            CommandExecutor executor = null;
            AppLifetime application = null;
            var factory = InterfaceProxy.Proxy<IServiceFactory>((method, args) =>
            {
                if (method.Name == "Build") return service;
                if (method.Name == "BuildAll")
                {
                    var contract = method.GetGenericArguments()[0];
                    if (contract == typeof(IHandle<ApplicationStartedEvent>))
                        return new IHandle<ApplicationStartedEvent>[] { executor };
                    if (contract == typeof(IHandle<ApplicationShutdownRequested>))
                        return new IHandle<ApplicationShutdownRequested>[] { executor, application };
                    if (contract == typeof(IHandle<CommandExecutedEvent>))
                        return new IHandle<CommandExecutedEvent>[] { observer };
                    return Array.CreateInstance(contract, 0);
                }
                throw new NotImplementedException(method.Name);
            });
            var events = new EventAggregator(_logger, factory);
            var repo = InterfaceProxy.Proxy<ICommandRepository>((method, args) =>
            {
                if (method.Name == "Insert")
                {
                    var model = (CommandModel)args[0];
                    model.Id = 1;
                    return model;
                }
                return method.ReturnType == typeof(void) || !method.ReturnType.IsValueType ? null : Activator.CreateInstance(method.ReturnType);
            });
            var manager = new CommandQueueManager(repo, factory, null, _logger);
            executor = new CommandExecutor(factory, manager, events, _logger);
            using var started = new CancellationTokenSource();
            using var stopping = new CancellationTokenSource();
            using var stopped = new CancellationTokenSource();
            var fakeLifetime = InterfaceProxy.Proxy<IHostApplicationLifetime>((method, _) => method.Name switch
            {
                "get_ApplicationStarted" => started.Token,
                "get_ApplicationStopping" => stopping.Token,
                "get_ApplicationStopped" => stopped.Token,
                "StopApplication" => Stop(),
                _ => throw new NotImplementedException(method.Name)
            });
            object Stop() { stopping.Cancel(); return null; }
            IHost host = route == "host-stop" ? new HostBuilder()
                .ConfigureServices(services => services.AddSingleton<IHostedService>(_ => application)).Build() : null;
            var lifetime = host?.Services.GetRequiredService<IHostApplicationLifetime>() ?? fakeLifetime;
            var runtime = InterfaceProxy.Proxy<IRuntimeInfo>((method, _) => method.Name.StartsWith("set_", StringComparison.Ordinal)
                ? null : method.ReturnType == typeof(bool) ? false : null);
            var config = InterfaceProxy.Proxy<IConfigFileProvider>((method, _) => method.ReturnType == typeof(bool) ? false : null);
            application = new AppLifetime(lifetime, config, runtime, new StartupContext(new[] { "/nobrowser" }), null, null, events, new Lazy<IBackupService>(() => service), _logger);
            service = CreateService(source.Main, folders, logs: source.Logs, cache: source.Cache, connectionStrings: source.Factory, hostLifetime: lifetime);
            var serviceProvider = InterfaceProxy.Proxy<NzbDrone.Common.IServiceProvider>((_, _) => null);
            var lifecycle = new LifecycleService(events, runtime, serviceProvider, _logger);
            var command = manager.Push(new BackupCommand());
            using var admin = PostgresBackup.Open(source.Connections["chaptarr.dump"], "postgres");
            var database = new Npgsql.NpgsqlConnectionStringBuilder(source.Connections["chaptarr.dump"]).Database;
            int Waiting() => admin.ExecuteScalar<int>("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @Name AND application_name LIKE 'chaptarr_backup_%' AND wait_event_type = 'Lock'", new { Name = database });
            if (host != null) host.StartAsync().GetAwaiter().GetResult();
            else started.Cancel();
            var result = new Dictionary<string, object> { ["route"] = route };
            try
            {
                Assert.That(SpinWait.SpinUntil(() => Waiting() == 1, TimeSpan.FromSeconds(15)), Is.True);
                var token = manager.GetCancellationToken(command.Id);
                Assert.That(token.CanBeCanceled, Is.True);
                if (route == "shutdown") lifecycle.Shutdown();
                else if (route == "restart") lifecycle.Restart();
                else
                {
                    host.StopAsync().GetAwaiter().GetResult();
                }
                if (host == null) application.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.That(observer.Finished.Wait(TimeSpan.FromSeconds(5)), Is.True);
                result["host_stopping"] = lifetime.ApplicationStopping.IsCancellationRequested;
                result["host_stopped"] = lifetime.ApplicationStopped.IsCancellationRequested;
                result["active_token_cancelled"] = token.IsCancellationRequested;
                result["command_status"] = command.Status.ToString();
                result["blocked_backends_after_stop"] = Waiting();
                result["handler_exited_after_stop"] = observer.Finished.IsSet;
            }
            finally
            {
                if (host != null)
                {
                    host.Dispose();
                    result["host_disposed_while_backup_handler_running"] = !observer.Finished.IsSet;
                }
                if (!observer.Finished.IsSet) manager.Cancel(command.Id);
                Assert.That(observer.Finished.Wait(TimeSpan.FromSeconds(10)), Is.True, "Private diagnostic backup must exit during cleanup");
                executor.Handle(new ApplicationShutdownRequested());
                Assert.That(SpinWait.SpinUntil(() => admin.ExecuteScalar<int>("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @Name AND application_name LIKE 'chaptarr_backup_%'", new { Name = database }) == 0, TimeSpan.FromSeconds(5)), Is.True);
                result["cleanup_completed"] = true;
                result["archives_after_cleanup"] = Directory.GetFileSystemEntries(service.GetBackupFolder(BackupType.Manual)).Length;
                result["temporary_entries_after_cleanup"] = Directory.GetFileSystemEntries(folders.TempFolder).Length;
                Assert.That(result["archives_after_cleanup"], Is.EqualTo(0));
                Assert.That(result["temporary_entries_after_cleanup"], Is.EqualTo(0));
                WriteLifecycleEvidence("stop-" + route + ".json", result);
                transaction.Rollback();
                AssertSnapshot(original, source);
            }
            Assert.That(result["handler_exited_after_stop"], Is.EqualTo(true), "Application stop must finish backup cleanup before returning");
            Assert.That(result["blocked_backends_after_stop"], Is.EqualTo(0));
        }
    }
}
