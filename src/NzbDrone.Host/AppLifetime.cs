using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Processes;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Host
{
    public class AppLifetime : IHostedService, IHandle<ApplicationShutdownRequested>
    {
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IRuntimeInfo _runtimeInfo;
        private readonly IStartupContext _startupContext;
        private readonly IBrowserService _browserService;
        private readonly IProcessProvider _processProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly Lazy<IBackupService> _backupService;
        private readonly IOsInfo _osInfo;

        public AppLifetime(IHostApplicationLifetime appLifetime,
            IConfigFileProvider configFileProvider,
            IRuntimeInfo runtimeInfo,
            IStartupContext startupContext,
            IBrowserService browserService,
            IProcessProvider processProvider,
            IEventAggregator eventAggregator,
            Lazy<IBackupService> backupService,
            Logger logger,
            IOsInfo osInfo = null)
        {
            _appLifetime = appLifetime;
            _configFileProvider = configFileProvider;
            _runtimeInfo = runtimeInfo;
            _startupContext = startupContext;
            _browserService = browserService;
            _processProvider = processProvider;
            _eventAggregator = eventAggregator;
            _backupService = backupService;
            _logger = logger;
            _osInfo = osInfo;

            appLifetime.ApplicationStarted.Register(OnAppStarted);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return _appLifetime.ApplicationStarted.IsCancellationRequested
                ? _backupService.Value.WaitForBackupCompletion()
                : Task.CompletedTask;
        }

        private void OnAppStarted()
        {
            if (_appLifetime.ApplicationStopping.IsCancellationRequested)
            {
                return;
            }

            _runtimeInfo.IsStarting = false;
            _runtimeInfo.IsExiting = false;

            if (!_startupContext.Flags.Contains(StartupContext.NO_BROWSER)
                && _configFileProvider.LaunchBrowser)
            {
                _browserService.LaunchWebUI();
            }

            _eventAggregator.PublishEvent(new ApplicationStartedEvent());
        }

        public void RestartIfPending()
        {
            if (_runtimeInfo.RestartPending && !_runtimeInfo.IsWindowsService)
            {
                // Logging is already shut down here, so container output goes straight to stdout for `docker logs`.
                if (ShouldReplaceProcess(_osInfo?.IsDocker == true, Environment.ProcessId))
                {
                    var argv = BuildReplacementArguments(
                        _runtimeInfo.ExecutingApplication,
                        Assembly.GetEntryAssembly()?.Location,
                        _startupContext.Args.TryGetValue(StartupContext.APPDATA, out var appData) ? appData : null);

                    Console.WriteLine("Restart requested inside a container: starting Chaptarr again in place.");

                    // Only returns when the replacement could not be started.
                    var error = ProcessReplacement.Replace(argv);
                    Console.WriteLine("Could not start Chaptarr again in place (error {0}); exiting so the container restart policy " +
                                      "(e.g. 'restart: unless-stopped') can start it.", error);
                    return;
                }

                var (path, restartArgs) = BuildRestartCommand(
                    _runtimeInfo.ExecutingApplication,
                    Assembly.GetEntryAssembly()?.Location,
                    GetRestartArgs());

                _logger.Info("Attempting restart with arguments: {0}", restartArgs);
                _processProvider.SpawnNewProcess(path, restartArgs);
            }
        }

        // A child spawned by a container's main process (PID 1) is killed as soon as that process
        // exits, and exiting would leave the restart to the container's restart policy (none, or
        // 'on-failure', leaves Chaptarr stopped). So in a container the stopped process is replaced
        // in place by a new one (exec): same PID, no state carried over from the old host.
        public static bool ShouldReplaceProcess(bool isDocker, int processId)
        {
            return isDocker || processId == 1;
        }

        // The argument vector for the replacement process. Under `dotnet Chaptarr.dll` (the Docker
        // image's CMD) the executing module is the dotnet host, so the entry assembly comes first.
        public static string[] BuildReplacementArguments(string executingApplication, string entryAssemblyPath, string appDataPath)
        {
            var argv = new System.Collections.Generic.List<string> { executingApplication };

            var hostName = Path.GetFileNameWithoutExtension(executingApplication ?? string.Empty);
            if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                argv.Add(entryAssemblyPath);
            }

            if (!string.IsNullOrWhiteSpace(appDataPath))
            {
                argv.Add("/" + StartupContext.APPDATA + "=" + appDataPath);
            }

            argv.Add("/" + StartupContext.NO_BROWSER);
            argv.Add("/" + StartupContext.RESTART);

            return argv.ToArray();
        }

        // Under `dotnet Chaptarr.dll` (the Docker image's CMD) the executing module is the dotnet
        // host, so the entry assembly has to come first or dotnet treats "/data=..." as the app.
        public static (string Path, string Args) BuildRestartCommand(string executingApplication, string entryAssemblyPath, string args)
        {
            var hostName = Path.GetFileNameWithoutExtension(executingApplication ?? string.Empty);
            if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                return (executingApplication, $"\"{entryAssemblyPath}\" {args}");
            }

            return (executingApplication, args);
        }

        private void Shutdown()
        {
            _logger.Info("Attempting to stop application.");
            _logger.Info("Application has finished stop routine.");
            _runtimeInfo.IsExiting = true;
            _appLifetime.StopApplication();
        }

        private string GetRestartArgs()
        {
            var args = _startupContext.PreservedArguments;

            args += " /restart";

            if (!args.Contains("/nobrowser"))
            {
                args += " /nobrowser";
            }

            return args;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(ApplicationShutdownRequested message)
        {
            if (!_runtimeInfo.IsWindowsService)
            {
                if (message.Restarting)
                {
                    _runtimeInfo.RestartPending = true;
                }

                LogManager.Configuration = null;
                Shutdown();
            }
        }
    }
}
