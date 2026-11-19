using System;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Processes;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Host
{
    public interface IBrowserService
    {
        void LaunchWebUI();
    }

    public class BrowserService : IBrowserService
    {
        private readonly IProcessProvider _processProvider;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IRuntimeInfo _runtimeInfo;
        private readonly Logger _logger;

        public BrowserService(IProcessProvider processProvider, IConfigFileProvider configFileProvider, IRuntimeInfo runtimeInfo, Logger logger)
        {
            _processProvider = processProvider;
            _configFileProvider = configFileProvider;
            _runtimeInfo = runtimeInfo;
            _logger = logger;
        }

        public void LaunchWebUI()
        {
            LaunchWebUI(_processProvider, _configFileProvider.Port, _runtimeInfo.IsUserInteractive, _logger);
        }

        public static void LaunchWebUI(IProcessProvider processProvider, int port, bool isUserInteractive, Logger logger)
        {
            var url = string.Format("http://localhost:{0}", port);
            try
            {
                if (isUserInteractive)
                {
                    logger.Info("Starting default browser. {0}", url);
                    processProvider.OpenDefaultBrowser(url);
                }
                else
                {
                    logger.Debug("non-interactive runtime. Won't attempt to open browser.");
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Couldn't open default browser to {0}", url);
            }
        }
    }
}
