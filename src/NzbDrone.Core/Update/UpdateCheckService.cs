using System;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Update
{
    public interface ICheckUpdateService
    {
        UpdatePackage AvailableUpdate();
        bool HasNewerRelease();
    }

    public class CheckUpdateService : ICheckUpdateService
    {
        private readonly IUpdatePackageProvider _updatePackageProvider;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IRecentUpdateProvider _recentUpdateProvider;
        private readonly Logger _logger;

        public CheckUpdateService(IUpdatePackageProvider updatePackageProvider,
                                  IConfigFileProvider configFileProvider,
                                  IRecentUpdateProvider recentUpdateProvider, Logger logger)
        {
            _updatePackageProvider = updatePackageProvider;
            _configFileProvider = configFileProvider;
            _recentUpdateProvider = recentUpdateProvider;
            _logger = logger;
        }

        public UpdatePackage AvailableUpdate()
        {
            return _updatePackageProvider.GetLatestUpdate(_configFileProvider.Branch, BuildInfo.Version);
        }

        public bool HasNewerRelease()
        {
            // Releases can publish notes without a native update package.
            // Health warnings must see those releases while installation still requires a verified package.
            try
            {
                return _recentUpdateProvider.GetRecentUpdatePackages().Any(update => update.Version > BuildInfo.Version);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to check for a newer release; the update feed is unavailable");
                return false;
            }
        }
    }
}
