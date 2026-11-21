using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using DryIoc;
using DryIoc.Microsoft.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Lifecycle.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Backup
{
    public partial class BackupServiceCoverageFixture
    {
        private IEventAggregator QuietEvents() => InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
        private IConfigFileProvider QuietConfig() => InterfaceProxy.Proxy<IConfigFileProvider>((method, _) => method.ReturnType == typeof(bool) ? false : method.ReturnType == typeof(string) ? "" : null);
        private IRuntimeInfo QuietRuntime() => InterfaceProxy.Proxy<IRuntimeInfo>((method, _) => method.ReturnType == typeof(bool) ? false : null);
        private IRestoreDatabase QuietRestore() => InterfaceProxy.Proxy<IRestoreDatabase>((method, _) => method.ReturnType == typeof(bool) ? false : null);

        [Test]
        public void real_di_should_share_backup_interfaces_and_not_create_databases_before_startup()
        {
            var folders = new AppFolders(Path.Combine(_root, "di"));
            var databaseResolutions = 0;
            using var host = new HostBuilder()
                .UseServiceProviderFactory(new DryIocServiceProviderFactory(new Container(rules => rules.WithNzbDroneRules())))
                .ConfigureContainer<IContainer>(container =>
                {
                    container.AutoAddServices(Bootstrap.ASSEMBLIES);
                    container.RegisterInstance(QuietConfig(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance(QuietRuntime(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance<IStartupContext>(new StartupContext(new[] { "/nobrowser" }), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance(QuietEvents(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance(_logger, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance<IAppFolderInfo>(folders, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance<IDiskProvider>(_disk, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance<IDiskTransferService>(new DiskTransferService(_disk, _logger), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance<IArchiveService>(new ArchiveService(_logger), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance<IMakeDatabaseBackup>(new MakeDatabaseBackup(_logger), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance(QuietRestore(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance(InterfaceProxy.Proxy<IConfigService>((_, _) => null), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterDelegate<IMainDatabase>(_ => { databaseResolutions++; return new MainDatabase(null); }, Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterDelegate<ILogDatabase>(_ => { databaseResolutions++; return new LogDatabase(null); }, Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterDelegate<ICacheDatabase>(_ => { databaseResolutions++; return new CacheDatabase(null); }, Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                }).Build();
            var application = host.Services.GetServices<IHostedService>().OfType<AppLifetime>().Single();
            Assert.That(databaseResolutions, Is.Zero);
            application.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(databaseResolutions, Is.Zero, "Failed startup must not initialize a database through stop");
            var handler = host.Services.GetRequiredService<IExecute<BackupCommand>>();
            var backup = host.Services.GetRequiredService<IBackupService>();
            Assert.That(handler, Is.SameAs(backup));
            Assert.That(databaseResolutions, Is.EqualTo(3));
            host.StartAsync().GetAwaiter().GetResult();
            host.StopAsync().GetAwaiter().GetResult();
            Assert.That(databaseResolutions, Is.EqualTo(3));
            WriteLifecycleEvidence("prototype-di.json", new { hosted_resolution_db_count = 0, before_started_stop_db_count = 0, shared_backup_interfaces = true, final_db_count = databaseResolutions });
        }

        [Test]
        public void stop_requested_inside_backup_should_return_and_finish_cleanup_without_self_join()
        {
            var folders = new AppFolders(Path.Combine(_root, "callback-stop"));
            SeedConfig(folders);
            using var requested = new ManualResetEventSlim();
            AppLifetime application = null;
            var factory = InterfaceProxy.Proxy<IServiceFactory>((method, _) =>
            {
                var contract = method.GetGenericArguments()[0];
                return contract == typeof(IHandle<ApplicationShutdownRequested>)
                    ? new IHandle<ApplicationShutdownRequested>[] { application }
                    : Array.CreateInstance(contract, 0);
            });
            var events = new EventAggregator(_logger, factory);
            using var host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<IBackupService>(provider => CreateService(new MainDatabase(new Database("main", () => Open(ConnectionString(folders.GetDatabase())))), folders,
                    hostLifetime: provider.GetRequiredService<IHostApplicationLifetime>(), includeExtras: false));
                services.AddSingleton<IHostedService>(provider => application = new AppLifetime(provider.GetRequiredService<IHostApplicationLifetime>(), QuietConfig(), QuietRuntime(),
                    new StartupContext(new[] { "/nobrowser" }), null, null, events, new Lazy<IBackupService>(() => provider.GetRequiredService<IBackupService>()), _logger));
            }).Build();
            host.StartAsync().GetAwaiter().GetResult();
            var backup = host.Services.GetRequiredService<IBackupService>();
            var lifecycle = new LifecycleService(events, QuietRuntime(), null, _logger);
            _disk.SpaceForPath = _ =>
            {
                requested.Set();
                lifecycle.Shutdown();
                return long.MaxValue;
            };
            var task = Task.Run(() => backup.Backup(BackupType.Update));
            Assert.That(requested.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(WaitForOperation(task, TimeSpan.FromSeconds(5)), Is.True);
            Exception callbackError = null;
            try { task.GetAwaiter().GetResult(); } catch (Exception ex) { callbackError = ex; }
            host.StopAsync().GetAwaiter().GetResult();
            var archives = Directory.GetFileSystemEntries(backup.GetBackupFolder(BackupType.Update)).Length;
            var temporary = Directory.GetFileSystemEntries(folders.TempFolder).Length;
            WriteLifecycleEvidence("callback-stop.json", new { callback_returned = true, error = callbackError?.GetType().Name, archives, temporary });
            Assert.That(temporary, Is.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(callbackError, Is.InstanceOf<OperationCanceledException>());
                Assert.That(archives, Is.Zero);
            });
        }

        [Test]
        public void windows_service_shutdown_command_should_not_join_its_own_worker()
        {
            using var serviceStopEntered = new ManualResetEventSlim();
            using var observer = new CompletionObserver();
            CommandExecutor executor = null;
            LifecycleService lifecycle = null;
            AppLifetime application = null;
            IHost host = null;
            var factory = InterfaceProxy.Proxy<IServiceFactory>((method, _) =>
            {
                if (method.Name == "Build") return lifecycle;
                var contract = method.GetGenericArguments()[0];
                if (contract == typeof(IHandle<ApplicationStartedEvent>))
                    return new IHandle<ApplicationStartedEvent>[] { executor };
                if (contract == typeof(IHandle<ApplicationShutdownRequested>))
                    return new IHandle<ApplicationShutdownRequested>[] { executor, application };
                if (contract == typeof(IHandle<CommandExecutedEvent>))
                    return new IHandle<CommandExecutedEvent>[] { observer };
                return Array.CreateInstance(contract, 0);
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
            var runtime = InterfaceProxy.Proxy<IRuntimeInfo>((method, _) =>
                method.Name == "get_IsWindowsService" ? true : method.ReturnType == typeof(bool) ? false : null);
            var serviceControl = InterfaceProxy.Proxy<NzbDrone.Common.IServiceProvider>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo("Stop"));
                serviceStopEntered.Set();
                host.StopAsync().GetAwaiter().GetResult();
                return null;
            });
            lifecycle = new LifecycleService(events, runtime, serviceControl, _logger);
            var backup = CreateService(new MainDatabase(null), new AppFolders(Path.Combine(_root, "service-stop")));
            host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(provider => application = new AppLifetime(provider.GetRequiredService<IHostApplicationLifetime>(),
                    QuietConfig(), runtime, new StartupContext(new[] { "/nobrowser" }), null, null, events,
                    new Lazy<IBackupService>(() => backup), _logger));
            }).Build();
            try
            {
                host.StartAsync().GetAwaiter().GetResult();
                var command = manager.Push(new ShutdownCommand());
                Assert.That(serviceStopEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(observer.Finished.Wait(TimeSpan.FromSeconds(10)), Is.True, "Hosted stop must not wait for the shutdown command requesting it");
                Assert.That(command.Status, Is.EqualTo(CommandStatus.Completed));
                Assert.That(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.IsCancellationRequested, Is.True);
            }
            finally
            {
                executor.Handle(new ApplicationShutdownRequested());
                host.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void actual_bootstrap_should_keep_ownership_until_backup_cleanup_finishes(bool prestart)
        {
            using var role = new PostgresRole();
            using var source = new PostgresStores("bootstrap-stop", role);
            var original = source.Snapshot();
            var folders = new AppFolders(Path.Combine(_root, "bootstrap-stop"));
            SeedConfig(folders);
            using var backupEntered = new ManualResetEventSlim();
            using var allowBackup = new ManualResetEventSlim();
            using var cleanupEntered = new ManualResetEventSlim();
            using var allowCleanup = new ManualResetEventSlim();
            IHostApplicationLifetime lifetime = null;
            BackupStarter starter = null;
            BackupService backup = null;
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == "DeleteFile" && ((string)args[0]).EndsWith(".partial", StringComparison.Ordinal))
                {
                    cleanupEntered.Set();
                    allowCleanup.Wait();
                }
                try { return method.Invoke(_disk, args); }
                catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
            });
            _disk.SpaceForPath = _ =>
            {
                backupEntered.Set();
                allowBackup.Wait();
                return long.MaxValue;
            };
            var start = Task.Run(() => Bootstrap.Start(new[] { "/data=" + folders.AppDataFolder, "/nosingleinstancecheck", "/nobrowser" }, builder =>
            {
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
                builder.ConfigureContainer<IContainer>(container =>
                {
                    container.Unregister(typeof(IHostedService));
                    container.Register<IHostedService, AppLifetime>(Reuse.Singleton);
                    container.RegisterInstance(QuietConfig(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance(QuietRuntime(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance(QuietEvents(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterInstance(QuietRestore(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterDelegate<IBackupService>(resolver =>
                    {
                        lifetime = resolver.Resolve<IHostApplicationLifetime>();
                        return backup = CreateService(source.Main, folders, logs: source.Logs, cache: source.Cache, connectionStrings: source.Factory, hostLifetime: lifetime, disk: disk);
                    }, Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
                    container.RegisterDelegate<IHostedService>(resolver => starter = new BackupStarter(resolver.Resolve<IBackupService>(), prestart),
                        Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.AppendNewImplementation);
                });
            }));
            bool ownershipDuringCleanup = false;
            bool bootstrapDuringCleanup = false;
            Exception backupError = null;
            try
            {
                Assert.That(backupEntered.Wait(TimeSpan.FromSeconds(15)), Is.True);
                if (!prestart)
                    Assert.That(SpinWait.SpinUntil(() => lifetime.ApplicationStarted.IsCancellationRequested, TimeSpan.FromSeconds(10)), Is.True);
                lifetime.StopApplication();
                allowBackup.Set();
                Assert.That(cleanupEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                bootstrapDuringCleanup = !start.IsCompleted;
                ownershipDuringCleanup = !ChildCanAcquire(folders.AppDataFolder);
                // Assertions about desired behavior follow actual cleanup and source-preservation controls.

            }
            finally
            {
                lifetime?.StopApplication();
                allowBackup.Set();
                allowCleanup.Set();
                Assert.That(WaitForOperation(start, TimeSpan.FromSeconds(15)), Is.True, "Private Bootstrap must unwind after cleanup release");
                try { start.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (prestart) { }
                if (starter?.BackupTask != null)
                {
                    try { starter.BackupTask.GetAwaiter().GetResult(); } catch (Exception ex) { backupError = ex; }
                }
            }
            Assert.That(ChildCanAcquire(folders.AppDataFolder), Is.True);
            Assert.That(Directory.GetFileSystemEntries(folders.TempFolder), Is.Empty);
            var archives = Directory.GetFileSystemEntries(backup.GetBackupFolder(BackupType.Update)).Length;
            AssertSnapshot(original, source);
            WriteLifecycleEvidence("prototype-bootstrap-" + prestart + ".json", new { prestart, bootstrap_running_during_cleanup = bootstrapDuringCleanup, ownership_held_during_cleanup = ownershipDuringCleanup,
                    second_process_acquired_after_cleanup = true, cleanup_completed = true, source_unchanged = true, archives, error = backupError?.GetType().Name });
            Assert.Multiple(() =>
            {
                Assert.That(bootstrapDuringCleanup, Is.True);
                Assert.That(ownershipDuringCleanup, Is.True);
                Assert.That(backupError, Is.InstanceOf<OperationCanceledException>());
                Assert.That(archives, Is.Zero);
            });
        }

        private const string BackupLockFolderVariable = "CHAPTARR_TEST_BACKUP_LOCK_FOLDER";
        private const string BackupLockResultVariable = "CHAPTARR_TEST_BACKUP_LOCK_RESULT";

        private static bool ChildCanAcquire(string path)
        {
            var result = Path.Combine(path, "lock-probe-" + Guid.NewGuid().ToString("N"));
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("vstest");
            info.ArgumentList.Add(typeof(BackupServiceCoverageFixture).Assembly.Location);
            info.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(BackupServiceCoverageFixture).FullName + ".child_process_backup_ownership_probe");
            info.Environment[BackupLockFolderVariable] = path;
            info.Environment[BackupLockResultVariable] = result;
            using var child = Process.Start(info);
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            try
            {
                Assert.That(child.WaitForExit(30000), Is.True, "Child ownership probe did not finish");
                Assert.That(child.ExitCode, Is.Zero, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
                return File.ReadAllText(result) == "acquired";
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                    child.WaitForExit();
                }
                File.Delete(result);
            }
        }

        [Test]
        public void child_process_backup_ownership_probe()
        {
            var folder = Environment.GetEnvironmentVariable(BackupLockFolderVariable);
            if (string.IsNullOrEmpty(folder))
            {
                Assert.Ignore("This ownership probe runs only in the child process started by the backup lifecycle tests.");
            }
            var result = Environment.GetEnvironmentVariable(BackupLockResultVariable);
            try
            {
                using var owner = SingleInstancePolicy.AcquireAppData(new AppFolderInfo(new StartupContext(new[] { "/data=" + folder })));
                File.WriteAllText(result, "acquired");
            }
            catch (TerminateApplicationException)
            {
                File.WriteAllText(result, "blocked");
            }
        }

        private static bool WaitForOperation(Task task, TimeSpan timeout) => ((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeout);

        private static void WriteLifecycleEvidence(string name, object result)
        {
            var directory = Environment.GetEnvironmentVariable("CHAPTARR_OPERATIONS_EVIDENCE");
            if (!string.IsNullOrEmpty(directory))
            {
                File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private sealed class BackupStarter : IHostedService
        {
            private readonly IBackupService _backup;
            private readonly bool _prestart;
            public BackupStarter(IBackupService backup, bool prestart) { _backup = backup; _prestart = prestart; }
            public Task BackupTask { get; private set; }
            public Task StartAsync(CancellationToken token)
            {
                BackupTask = Task.Run(() => _backup.Backup(BackupType.Update));
                return _prestart ? BackupTask : Task.CompletedTask;
            }
            public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        }
    }
}
