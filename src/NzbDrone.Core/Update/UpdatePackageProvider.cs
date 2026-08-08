using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Update
{
    public interface IUpdatePackageProvider
    {
        UpdatePackage GetLatestUpdate(string branch, Version currentVersion);
        List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion = null);
    }

    public class UpdatePackageProvider : IUpdatePackageProvider
    {
        private static readonly Regex Sha256Regex = new Regex(@"^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

        private readonly IHttpClient _httpClient;
        private readonly IHttpRequestBuilderFactory _requestBuilder;
        private readonly IPlatformInfo _platformInfo;
        private readonly IMainDatabase _mainDatabase;
        private readonly IConfigService _configService;
        private readonly IGitHubUpdatePackageProvider _gitHubUpdateProvider;
        private readonly Logger _logger;

        public UpdatePackageProvider(IHttpClient httpClient, IChaptarrCloudRequestBuilder requestBuilder, IPlatformInfo platformInfo, IMainDatabase mainDatabase, IConfigService configService, IGitHubUpdatePackageProvider gitHubUpdateProvider, Logger logger)
        {
            _platformInfo = platformInfo;
            _requestBuilder = requestBuilder.Services;
            _httpClient = httpClient;
            _mainDatabase = mainDatabase;
            _configService = configService;
            _gitHubUpdateProvider = gitHubUpdateProvider;
            _logger = logger;
        }

        public UpdatePackage GetLatestUpdate(string branch, Version currentVersion)
        {
            // Use GitHub if configured
            if (_configService.UseGitHubUpdates)
            {
                _logger.Debug("Using GitHub for update check");
                var gitHubUpdate = _gitHubUpdateProvider.GetLatestUpdate(branch, currentVersion);

                // Safety: never treat non-newer or incomplete packages as "available".
                if (gitHubUpdate?.Version == null || gitHubUpdate.Version <= currentVersion)
                {
                    return null;
                }

                if (!IsVerifiedUpdatePackage(gitHubUpdate))
                {
                    _logger.Warn("GitHub update package {0} is missing required verification metadata. Skipping update.", gitHubUpdate.Version);
                    return null;
                }

                return gitHubUpdate;
            }

            try
            {
                var request = _requestBuilder.Create()
                                             .Resource("/update/{branch}")
                                             .AddQueryParam("version", currentVersion)
                                             .AddQueryParam("os", OsInfo.Os.ToString().ToLowerInvariant())
                                             .AddQueryParam("arch", RuntimeInformation.OSArchitecture)
                                             .AddQueryParam("runtime", "netcore")
                                             .AddQueryParam("runtimeVer", _platformInfo.Version)
                                             .AddQueryParam("dbType", _mainDatabase.DatabaseType)
                                             .AddQueryParam("includeMajorVersion", true)
                                             .SetSegment("branch", branch);

                var update = _httpClient.Get<UpdatePackageAvailable>(request.Build()).Resource;

                if (!update.Available)
                {
                    return null;
                }

                var available = update.UpdatePackage;

                // Safety: the update server should only return newer versions, but guard anyway so health checks
                // and the installer never treat the current version (or older) as an update.
                if (available?.Version == null || available.Version <= currentVersion)
                {
                    return null;
                }

                if (!IsVerifiedUpdatePackage(available))
                {
                    _logger.Warn("Services update package {0} is missing required verification metadata. Skipping update.", available.Version);
                    return null;
                }

                return available;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Services update check failed");
                return null;
            }
        }

        private static bool IsVerifiedUpdatePackage(UpdatePackage package)
        {
            if (package == null)
            {
                return false;
            }

            if (package.FileName == null || package.FileName.Trim().Length == 0)
            {
                return false;
            }

            if (package.Url == null || package.Url.Trim().Length == 0)
            {
                return false;
            }

            if (package.Hash == null || package.Hash.Trim().Length == 0)
            {
                return false;
            }

            return Sha256Regex.IsMatch(package.Hash.Trim());
        }

        public List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion)
        {
            // Use GitHub if configured
            if (_configService.UseGitHubUpdates)
            {
                _logger.Debug("Using GitHub for recent updates check");
                var gitHubUpdates = _gitHubUpdateProvider.GetRecentUpdates(branch, currentVersion, previousVersion);
                if (gitHubUpdates != null && gitHubUpdates.Count > 0)
                {
                    return gitHubUpdates;
                }

                _logger.Warn("GitHub recent updates check failed, falling back to local enhancements");
                return GetChaptarrEnhancements(currentVersion, previousVersion);
            }

            try
            {
                var request = _requestBuilder.Create()
                                             .Resource("/update/{branch}/changes")
                                             .AddQueryParam("version", currentVersion)
                                             .AddQueryParam("os", OsInfo.Os.ToString().ToLowerInvariant())
                                             .AddQueryParam("arch", RuntimeInformation.OSArchitecture)
                                             .AddQueryParam("runtime", "netcore")
                                             .AddQueryParam("runtimeVer", _platformInfo.Version)
                                             .SetSegment("branch", branch);

                if (previousVersion != null && previousVersion != currentVersion)
                {
                    request.AddQueryParam("prevVersion", previousVersion);
                }

                var updates = _httpClient.Get<List<UpdatePackage>>(request.Build());

                // Supplement with Chaptarr enhancements
                return SupplementWithChaptarrEnhancements(updates.Resource, GetChaptarrEnhancements(currentVersion, null), currentVersion);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Services recent updates check failed");
                return GetChaptarrEnhancements(currentVersion, previousVersion);
            }
        }

        // internal static for direct testing (InternalsVisibleTo Chaptarr.Core.Test).
        internal static List<UpdatePackage> SupplementWithChaptarrEnhancements(List<UpdatePackage> originalUpdates, List<UpdatePackage> chaptarrEnhancements, Version currentVersion)
        {
            if (originalUpdates == null || originalUpdates.Count == 0)
            {
                return chaptarrEnhancements;
            }

            // Server rows are the canonical changelog history. The in-binary enhancements only
            // fill the gap for the running version: when the server doesn't know it yet (manifest
            // lagging a fresh release) or knows it without notes. Appending them onto whatever
            // row happens to be first would duplicate bullets or file them under the wrong version.
            var installedUpdate = originalUpdates.FirstOrDefault(u => IsSameRelease(u.Version, currentVersion));

            if (installedUpdate == null)
            {
                originalUpdates.Add(chaptarrEnhancements[0]);
            }
            else if (!HasChanges(installedUpdate.Changes))
            {
                installedUpdate.Changes = chaptarrEnhancements[0].Changes;
            }

            return originalUpdates;
        }

        private static bool HasChanges(UpdateChanges changes)
        {
            if (changes == null)
            {
                return false;
            }

            return (changes.New?.Count ?? 0) > 0 || (changes.Fixed?.Count ?? 0) > 0;
        }

        // BuildInfo.Version is always 4-part (AssemblyVersion 0.9.X.0) while manifest version
        // strings may be 3-part; System.Version treats the missing component as -1, so plain
        // equality calls 0.9.578 and 0.9.578.0 different releases. Compare with -1 read as 0.
        private static bool IsSameRelease(Version left, Version right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return left.Major == right.Major &&
                   left.Minor == right.Minor &&
                   Math.Max(left.Build, 0) == Math.Max(right.Build, 0) &&
                   Math.Max(left.Revision, 0) == Math.Max(right.Revision, 0);
        }

        private List<UpdatePackage> GetChaptarrEnhancements(Version currentVersion, Version previousVersion)
        {
            return new List<UpdatePackage>
            {
                new UpdatePackage
                {
                    Version = currentVersion,
                    ReleaseDate = DateTime.UtcNow,
                    Branch = "chaptarr",
                    Changes = new UpdateChanges
                    {
                        New = new List<string>
                        {
                            "Chaptarr is now open source at github.com/Chaptarr/chaptarr.",
                            "The wiki is being updated and will be available asap."
                        },
                        Fixed = new List<string>
                        {
                            "Third-party tools that connect to Chaptarr as Readarr (Unpackerr, Decluttarr, and others) no longer fail with \"PageSize must be between 1 and 1000\".",
                            "The web interface now works correctly behind a reverse proxy URL Base. Author statistics, the filename builder, proxy testing, and author photo selection no longer break on subpath setups.",
                            "The proxy Test button no longer hangs without giving a result."
                        }
                    }
                }
            };
        }
    }
}
