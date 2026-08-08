using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Update
{
    public interface IGitHubUpdatePackageProvider
    {
        UpdatePackage GetLatestUpdate(string branch, Version currentVersion);
        List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion = null);
    }

    public class GitHubUpdatePackageProvider : IGitHubUpdatePackageProvider
    {
        // Default to the Chaptarr repository
        private const string DefaultOwner = "chaptarr";
        private const string DefaultRepo = "chaptarr";
        private static readonly string[] Sha256AssetSuffixes = { ".sha256", ".sha256sum", ".sha256.txt", ".sha256sums", ".sha256sums.txt" };

        private readonly IHttpClient _httpClient;
        private readonly IHttpRequestBuilderFactory _gitHubRequestBuilder;
        private readonly IPlatformInfo _platformInfo;
        private readonly Logger _logger;

        public GitHubUpdatePackageProvider(
            IHttpClient httpClient,
            IChaptarrCloudRequestBuilder requestBuilder,
            IPlatformInfo platformInfo,
            Logger logger)
        {
            _httpClient = httpClient;
            _gitHubRequestBuilder = requestBuilder.GitHubApi;
            _platformInfo = platformInfo;
            _logger = logger;
        }

        public UpdatePackage GetLatestUpdate(string branch, Version currentVersion)
        {
            try
            {
                var releases = GetReleases();
                if (releases == null || !releases.Any())
                {
                    return null;
                }

                // Filter by branch if specified
                var latestRelease = releases
                    .Where(r => !r.Prerelease || branch == "develop")
                    .FirstOrDefault(r => IsNewerVersion(r.TagName, currentVersion));

                if (latestRelease == null)
                {
                    return null;
                }

                return ConvertToUpdatePackage(latestRelease);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get latest update from GitHub");
                return null;
            }
        }

        public List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion = null)
        {
            try
            {
                var releases = GetReleases();
                if (releases == null || !releases.Any())
                {
                    return new List<UpdatePackage>();
                }

                var updates = releases
                    .Where(r => !r.Prerelease || branch == "develop")
                    .Where(r => IsNewerVersion(r.TagName, previousVersion ?? new Version(0, 0, 0)))
                    .Where(r => !IsNewerVersion(r.TagName, currentVersion))
                    .Select(ConvertToUpdatePackage)
                    .Where(p => p != null)
                    .ToList();

                return updates;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get recent updates from GitHub");
                return new List<UpdatePackage>();
            }
        }

        private List<GitHubRelease> GetReleases()
        {
            var owner = DefaultOwner;
            var repo = DefaultRepo;

            var request = _gitHubRequestBuilder.Create()
                .Resource($"repos/{owner}/{repo}/releases")
                .Build();

            var response = _httpClient.Get<List<GitHubRelease>>(request);
            return response.Resource;
        }

        private UpdatePackage ConvertToUpdatePackage(GitHubRelease release)
        {
            var version = ParseVersion(release.TagName);
            var asset = SelectAssetForPlatform(release.Assets);
            if (asset == null)
            {
                _logger.Warn("GitHub release {0} has no compatible assets for the current platform.", release.TagName);
                return null;
            }

            var hash = TryResolveSha256(release, asset);
            if (hash.IsNullOrWhiteSpace())
            {
                _logger.Warn("GitHub release {0} does not provide a SHA256 hash for asset '{1}'. Skipping update to preserve update verification.", release.TagName, asset.Name);
                return null;
            }

            return new UpdatePackage
            {
                Version = version,
                ReleaseDate = release.PublishedAt ?? release.CreatedAt,
                FileName = asset.Name,
                Url = asset.BrowserDownloadUrl,
                Branch = release.Prerelease ? "develop" : "main",
                Changes = ParseReleaseNotes(release.Body),
                Hash = hash
            };
        }

        private string TryResolveSha256(GitHubRelease release, GitHubAsset asset)
        {
            if (release?.Assets == null || asset == null)
            {
                return null;
            }

            var shaAsset = FindSha256Asset(release.Assets, asset);
            if (shaAsset != null && shaAsset.BrowserDownloadUrl.IsNotNullOrWhiteSpace())
            {
                try
                {
                    var response = _httpClient.Get(new HttpRequest(shaAsset.BrowserDownloadUrl)
                    {
                        RequestTimeout = TimeSpan.FromSeconds(30),
                        AllowAutoRedirect = true
                    });

                    if (TryExtractSha256FromText(response.Content, asset.Name, out var hash))
                    {
                        return hash;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to download/parse SHA256 asset '{0}' for release {1}", shaAsset.Name, release.TagName);
                }
            }

            // Fallback: allow hashes embedded in release notes.
            if (TryExtractSha256FromText(release.Body, asset.Name, out var notesHash))
            {
                return notesHash;
            }

            return null;
        }

        private static GitHubAsset FindSha256Asset(List<GitHubAsset> assets, GitHubAsset payloadAsset)
        {
            if (assets.Empty() || payloadAsset?.Name.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            foreach (var suffix in Sha256AssetSuffixes)
            {
                var direct = assets.FirstOrDefault(a => a.Name.Equals(payloadAsset.Name + suffix, StringComparison.OrdinalIgnoreCase));
                if (direct != null)
                {
                    return direct;
                }
            }

            // Common patterns: SHA256SUMS, sha256sums.txt, checksums.txt, etc.
            return assets.FirstOrDefault(a =>
                a.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains("checksums", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryExtractSha256FromText(string content, string fileName, out string sha256)
        {
            sha256 = null;

            if (content.IsNullOrWhiteSpace() || fileName.IsNullOrWhiteSpace())
            {
                return false;
            }

            var escaped = Regex.Escape(fileName);

            // sha256sum format: "<hash>  <filename>"
            foreach (Match lineMatch in Regex.Matches(content, @"^(?<hash>[a-fA-F0-9]{64})\s+\*?(?<file>.+)$", RegexOptions.Multiline))
            {
                if (!lineMatch.Success)
                {
                    continue;
                }

                var file = lineMatch.Groups["file"].Value.Trim();
                if (file.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    sha256 = lineMatch.Groups["hash"].Value.ToLowerInvariant();
                    return true;
                }
            }

            // Openssl format: "SHA256 (filename) = <hash>"
            foreach (Match lineMatch in Regex.Matches(content, @"^SHA256\s*\((?<file>.+?)\)\s*=\s*(?<hash>[a-fA-F0-9]{64})\s*$", RegexOptions.Multiline))
            {
                if (!lineMatch.Success)
                {
                    continue;
                }

                var file = lineMatch.Groups["file"].Value.Trim();
                if (file.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    sha256 = lineMatch.Groups["hash"].Value.ToLowerInvariant();
                    return true;
                }
            }

            // Same-line association: "<filename> ... <hash>" or "<hash> ... <filename>"
            var fileLineMatch = Regex.Match(content, @$"^(?<line>.*{escaped}.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (fileLineMatch.Success)
            {
                var line = fileLineMatch.Groups["line"].Value;
                var hashMatch = Regex.Match(line, @"(?<hash>[a-fA-F0-9]{64})");
                if (hashMatch.Success)
                {
                    sha256 = hashMatch.Groups["hash"].Value.ToLowerInvariant();
                    return true;
                }
            }

            // Fallback: if there's exactly one SHA256 in the text, assume it's for this asset.
            var hashes = Regex.Matches(content, @"\b[a-fA-F0-9]{64}\b")
                              .Cast<Match>()
                              .Select(m => m.Value)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();
            if (hashes.Count == 1)
            {
                sha256 = hashes[0].ToLowerInvariant();
                return true;
            }

            return false;
        }

        private GitHubAsset SelectAssetForPlatform(List<GitHubAsset> assets)
        {
            if (assets == null || !assets.Any())
            {
                return null;
            }

            var osName = OsInfo.Os.ToString().ToLowerInvariant();
            var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

            // Try to find a matching asset for the current platform
            var patterns = new[]
            {
                $"{osName}-{arch}",
                $"{osName}.{arch}",
                osName,
                "core" // fallback to .NET Core builds
            };

            foreach (var pattern in patterns)
            {
                var asset = assets.FirstOrDefault(a =>
                    a.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

                if (asset != null)
                {
                    return asset;
                }
            }

            // Return the first asset as a fallback
            return assets.FirstOrDefault();
        }

        private Version ParseVersion(string tagName)
        {
            // Remove common prefixes like "v" or "release-"
            var versionString = Regex.Replace(tagName, @"^(v|release-)", "", RegexOptions.IgnoreCase);

            // Try to parse as a standard version
            if (Version.TryParse(versionString, out var version))
            {
                return version;
            }

            // Try to extract version numbers
            var match = Regex.Match(versionString, @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (match.Success)
            {
                var major = int.Parse(match.Groups[1].Value);
                var minor = int.Parse(match.Groups[2].Value);
                var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                var revision = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;

                return new Version(major, minor, build, revision);
            }

            // Fallback to 0.0.0
            _logger.Warn("Could not parse version from tag: {0}", tagName);
            return new Version(0, 0, 0);
        }

        private bool IsNewerVersion(string tagName, Version currentVersion)
        {
            var version = ParseVersion(tagName);
            return version > currentVersion;
        }

        private UpdateChanges ParseReleaseNotes(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return new UpdateChanges();
            }

            var changes = new UpdateChanges();
            var lines = body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var currentSection = "new";
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Check for section headers
                if (trimmed.StartsWith("### ") || trimmed.StartsWith("## "))
                {
                    var header = trimmed.TrimStart('#').Trim().ToLowerInvariant();
                    if (header.Contains("fix") || header.Contains("bug"))
                    {
                        currentSection = "fixed";
                    }
                    else if (header.Contains("new") || header.Contains("feature") || header.Contains("add"))
                    {
                        currentSection = "new";
                    }

                    continue;
                }

                // Parse bullet points
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
                {
                    var item = trimmed.Substring(2).Trim();
                    if (currentSection == "fixed")
                    {
                        changes.Fixed.Add(item);
                    }
                    else
                    {
                        changes.New.Add(item);
                    }
                }
            }

            // If no structured notes, add the whole body as a single item
            if (!changes.New.Any() && !changes.Fixed.Any() && !string.IsNullOrWhiteSpace(body))
            {
                changes.New.Add(body.Trim());
            }

            return changes;
        }
    }

    // GitHub API models
    public class GitHubRelease
    {
        [JsonProperty("tag_name")]
        public string TagName { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }

        [JsonProperty("draft")]
        public bool Draft { get; set; }

        [JsonProperty("prerelease")]
        public bool Prerelease { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("published_at")]
        public DateTime? PublishedAt { get; set; }

        [JsonProperty("assets")]
        public List<GitHubAsset> Assets { get; set; }

        [JsonProperty("zipball_url")]
        public string ZipballUrl { get; set; }

        [JsonProperty("tarball_url")]
        public string TarballUrl { get; set; }
    }

    public class GitHubAsset
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("content_type")]
        public string ContentType { get; set; }
    }
}
