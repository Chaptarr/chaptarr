using System.Linq;
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

        public CheckUpdateService(IUpdatePackageProvider updatePackageProvider,
                                  IConfigFileProvider configFileProvider,
                                  IRecentUpdateProvider recentUpdateProvider)
        {
            _updatePackageProvider = updatePackageProvider;
            _configFileProvider = configFileProvider;
            _recentUpdateProvider = recentUpdateProvider;
        }

        public UpdatePackage AvailableUpdate()
        {
            return _updatePackageProvider.GetLatestUpdate(_configFileProvider.Branch, BuildInfo.Version);
        }

        public bool HasNewerRelease()
        {
            // Releases can publish notes without a native update package.
            // Health warnings must see those releases while installation still requires a verified package.
            return _recentUpdateProvider.GetRecentUpdatePackages().Any(update => update.Version > BuildInfo.Version);
        }
    }
}
