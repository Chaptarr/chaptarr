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
                            "Author monitoring is now a simple on/off setting per format. Turning a format off pauses automatic searches, grabs, and upgrades for that author without changing individual book selections. Thanks compgeek!",
                            "When adding an author, current book monitoring (All, Missing, Books with Files, or None) is separate from monitoring books pulled in from the metadata server later (All New Books, Future Releases, or None). Root folders provide the defaults for new authors.",
                            "Authors show as Active or Deceased when a death date is available, with matching filters on the Authors page.",
                            "Tags can be set separately for an author's audiobook and eBook sides everywhere an author is added, including pending requests and folder discovery.",
                            "Author search results show the metadata server's canonical name, bio, photos, book count and provider links.",
                            "Book search results now show the author. Thanks chunni!",
                            "Download clients can have multiple scoped remote path mappings, with clear field errors when mappings conflict.",
                            "Completed downloads whose files are still arriving are retried automatically a few times before asking for manual import.",
                            "Author details has a compact All button to show or hide unmonitored books you do not have.",
                            "Hover tips and popovers stay within the window, scroll when long, close with Escape or a click elsewhere, and support keyboard access."
                        },
                        Fixed = new List<string>
                        {
                            "Adding and deleting an author is much faster, especially for authors with very large catalogs.",
                            "Author progress no longer counts unreleased books as missing, and progress sorting no longer over-counts multi-file audiobooks. Thanks @digitalgp.",
                            "An audiobook made of many files now shows as complete on the Shelf and calendar instead of looking unfinished. Thanks @Blackduke77 and @sebclark.",
                            "The Authors page, Shelf, and author details now agree about whether an author is monitored. Thanks @digitalgp and @jbob06.",
                            "Authors page statistics follow the selected format, and the totals at the bottom agree with the list.",
                            "Fixed author tag handling.",
                            "In Interactive Import, rows you never selected no longer keep the Import button disabled. Thanks @ZZerker.",
                            "Skip Secondary Series Books now filters as intended. Thanks @JordanFromIT.",
                            "Root folders added through the API must declare audiobook, eBook, or mixed content and provide the required profiles. Existing incomplete sides are skipped and now surface a health warning instead of aborting the whole import. Thanks @jbob06.",
                            "Import lists now apply book monitoring to existing authors without silently enabling the author.",
                            "Removed some old hacky ways of trying to clean up Hardcover search results.",
                            "A manually requested one-book search now runs once even when that book or author side is paused; automatic and bulk searches remain gated.",
                            "Remote path mapping health checks now report permission and Docker path problems, and Test works for host-wide mappings.",
                            "Restoring a settings backup (not a full backup) better handles remote path mappings for download clients.",
                            "rTorrent files marked 'don't download' are no longer waited on during import.",
                            "API compatibility note: remote path mappings require downloadClientId; root folders require folderType; author editor and pending-import fields use the per-format model; and the obsolete POST /api/v1/author/statistics/aggregate endpoint was removed."
                        }
                    }
                }
            };
        }
    }
}
