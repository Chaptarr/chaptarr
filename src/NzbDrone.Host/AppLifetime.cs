using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Processes;
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
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDeploymentInfoProvider _deploymentInfoProvider;
        private readonly IStartupContext _startupContext;
        private readonly IBrowserService _browserService;
        private readonly IProcessProvider _processProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public AppLifetime(IHostApplicationLifetime appLifetime,
            IConfigFileProvider configFileProvider,
            IRuntimeInfo runtimeInfo,
            IAppFolderInfo appFolderInfo,
            IDeploymentInfoProvider deploymentInfoProvider,
            IStartupContext startupContext,
            IBrowserService browserService,
            IProcessProvider processProvider,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _appLifetime = appLifetime;
            _configFileProvider = configFileProvider;
            _runtimeInfo = runtimeInfo;
            _appFolderInfo = appFolderInfo;
            _deploymentInfoProvider = deploymentInfoProvider;
            _startupContext = startupContext;
            _browserService = browserService;
            _processProvider = processProvider;
            _eventAggregator = eventAggregator;
            _logger = logger;

            appLifetime.ApplicationStarted.Register(OnAppStarted);
            appLifetime.ApplicationStopped.Register(OnAppStopped);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private void OnAppStarted()
        {
            _runtimeInfo.IsStarting = false;
            _runtimeInfo.IsExiting = false;

            LogStartupDiagnostics();

            if (!_startupContext.Flags.Contains(StartupContext.NO_BROWSER)
                && _configFileProvider.LaunchBrowser)
            {
                _browserService.LaunchWebUI();
            }

            _eventAggregator.PublishEvent(new ApplicationStartedEvent());
        }

        private void LogStartupDiagnostics()
        {
            var imageVersion = GetEnvironmentValue("BOOKSHELF_IMAGE_VERSION", _deploymentInfoProvider.PackageVersion);
            var imageTag = GetEnvironmentValue("BOOKSHELF_IMAGE_TAG", "unknown");
            var imageRevision = GetEnvironmentValue("BOOKSHELF_IMAGE_REVISION", "unknown");
            var imageRef = GetEnvironmentValue("BOOKSHELF_IMAGE_REF", _deploymentInfoProvider.PackageBranch);
            var imageFlavor = GetEnvironmentValue("BOOKSHELF_IMAGE_FLAVOR", "unknown");
            var imageBuildDate = GetEnvironmentValue("BOOKSHELF_IMAGE_BUILD_DATE", "unknown");
            var container = GetEnvironmentValue("BOOKSHELF_CONTAINER", GetEnvironmentValue("DOTNET_RUNNING_IN_CONTAINER", "unknown"));
            var uiFolder = Path.Combine(_appFolderInfo.StartUpFolder, _configFileProvider.UiFolder);
            var uiIndex = Path.Combine(uiFolder, "index.html");

            _logger.Info("Build/runtime: appVersion={0}; build={1}; imageVersion={2}; imageTag={3}; imageRef={4}; imageRevision={5}; flavor={6}; imageBuildDate={7}; OS={8}; OSArchitecture={9}; ProcessArchitecture={10}; runtime={11}; container={12}",
                BuildInfo.Release,
                BuildInfo.BuildDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                imageVersion,
                imageTag,
                imageRef,
                imageRevision,
                imageFlavor,
                imageBuildDate,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.FrameworkDescription,
                container);

            _logger.Info("HTTP server: bindAddress={0}; port={1}; sslEnabled={2}; sslPort={3}; urlBase={4}; logLevel={5}; startupFolder={6}",
                _configFileProvider.BindAddress,
                _configFileProvider.Port,
                _configFileProvider.EnableSsl,
                _configFileProvider.SslPort,
                string.IsNullOrWhiteSpace(_configFileProvider.UrlBase) ? "/" : "/" + _configFileProvider.UrlBase.Trim('/') + "/",
                _configFileProvider.LogLevel,
                _appFolderInfo.StartUpFolder);

            if (File.Exists(uiIndex))
            {
                _logger.Info("Web UI entry point found: {0}", uiIndex);
            }
            else if (RuntimeInfo.IsProduction)
            {
                _logger.Error("Web UI entry point is missing: {0}. The package must include UI/index.html beside the application binaries.", uiIndex);
            }
            else
            {
                _logger.Warn("Web UI entry point is missing: {0}. A backend-only development build may not include frontend assets.", uiIndex);
            }
        }

        private static string GetEnvironmentValue(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = fallback;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            return value.Replace('\r', ' ').Replace('\n', ' ');
        }

        private void OnAppStopped()
        {
            if (_runtimeInfo.RestartPending && !_runtimeInfo.IsWindowsService)
            {
                var restartArgs = GetRestartArgs();

                _logger.Info("Attempting restart with arguments: {0}", restartArgs);
                _processProvider.SpawnNewProcess(_runtimeInfo.ExecutingApplication, restartArgs);
            }
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
