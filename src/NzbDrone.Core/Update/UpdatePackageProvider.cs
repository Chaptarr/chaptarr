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
                            "New \"Library covers only\" option in Settings > Media Management keeps covers just for monitored books and books with files, and removes the rest when you save. \"Calculate savings\" estimates the space freed first. Other books show a placeholder in lists and a temporary cover on their details page.",
                            "Author Search (Search tab) searches each monitored book and groups results by book, with a preferred release per book and expandable alternatives. It follows the selected format, asks before searching more than five books, and has a Stop button that finishes the current book and skips the remaining searches.",
                            "Series labels, folder tokens and ebook/Calibre tags come from Goodreads-backed series memberships in a consistent order. Thanks @tillkruss (PR #48).",
                            "{Book SeriesPosition:00} zero-pads individual numeric series positions.",
                            "Author deletion can target audiobooks or ebooks independently, can add an import-list exclusion in bulk deletes, and explains the scope of a single-format delete. Thanks @benjitobz (PR #134).",
                            "Naming settings preview filenames and validate multi-part formats.",
                            "Changed the messy narrator icon to the simple microphone.",
                            "Finally showing some love to the mobile layout. Many screens should work and look better on mobile.",
                            "Adjusted book search results when searching your local library to hopefully be more accurate."
                        },
                        Fixed = new List<string>
                        {
                            "MAM account status reads the tracker's new response format. With slot protection enabled, the indexer test fails when no usable status is available instead of showing stale counts, and refresh failures log at Warning. Thanks @jjbobzin (PR #189) and @jbob06 (#193).",
                            "Author Search searches every currently monitored book instead of a stale list.",
                            "Release matching keeps part and volume identifiers from the selected edition title and prefers an indexer's structured book title.",
                            "Release matching recognizes equivalent numeric spellings, with English number words limited to English-tagged editions and safeguards for Roman-looking words.",
                            "Unmarked track numbers in quoted audio filenames no longer cause false mismatches when the complete title appears separately. Conflicting book positions remain rejected.",
                            "Structured series metadata no longer labels a release as a pack when its positions match the target book's own positions. Thanks @jonnotk (PR #137).",
                            "Adding one format clears only that side's import-list exclusion; authors with exclusions on both sides no longer fail to add; queued import-list requests respect exclusions.",
                            "Requesting a book also monitors its matching row in the other format when format sync is enabled. Thanks @JordanFromIT (PR #178).",
                            "Improved author refresh and monitoring-sync performance for large catalogs. Thanks @jordanfelle (PR #174).",
                            "SQLite library search keeps author names current after metadata ownership changes.",
                            "Improved primary-series selection for books belonging to multiple series.",
                            "Preferred narrators are chosen from audio editions only.",
                            "Cancel Conversion also stops conversions started by manual import.",
                            "Author photos reuse verified cached images when the provider link is unavailable, and the selected photo stays selected when the photo list is reordered.",
                            "Ebook files are no longer moved into a colocated folder when no matching destination applies. Thanks @jbob06 (PR #127).",
                            "Fixed title-processing crashes involving emoji and rare Unicode characters. Thanks @jbob06 (PR #117).",
                            "Improved input placeholder contrast in dark mode. Thanks @jberlyn (PR #124).",
                            "Indexer seed time and torrent preservation settings are explained clearly. Thanks @jberlyn (PR #129).",
                            "Newznab logs why a search was skipped when no configured category matches the media type.",
                            "Search result badges stay inside their cells and the table no longer collapses on narrow screens.",
                            "Empty or unavailable root folders are refused before an author's files are deleted.",
                            "Full HTTP response bodies are restricted to Trace, with expanded credential and download-link redaction.",
                            "Pressing Enter on a focused Cancel button in a confirmation dialog no longer also confirms it."
                            }
                    }
                }
            };
        }
    }
}
