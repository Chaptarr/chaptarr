using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MediaCover
{
    public static class MediaCoverRendition
    {
        private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".avif"
        };

        // Some providers publish one shared placeholder URL, while Hardcover assigns its
        // mascot a different per-author URL. Keep exact shared URLs for the cheap pre-fetch
        // gate and content hashes for aliases, redirects, and per-author assets.
        private static readonly HashSet<string> KnownPlaceholderImageUrls = new(StringComparer.OrdinalIgnoreCase)
        {
            "https://m.media-amazon.com/images/I/01Kv-W2ysOL.png"
        };

        private static readonly HashSet<string> KnownPlaceholderImageHashes = new(StringComparer.OrdinalIgnoreCase)
        {
            "47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e", // JPEG, 32,615 bytes
            "db25714c302dcc8ccca766d734947df2931fcc74cbed1656ad2eb470613db981", // WebP, 2,600 bytes
            "8280bac30e108aa599176cc0737e1179e8225fe2b08d98187a6ebcb22b126a6e", // WebP, 3,228 bytes
            "38eb593837bb848a936fae31d959d4795f1b846b3cd57d956483d721dda39478", // PNG, 43,146 bytes
            "a5efe6ec77a9e993915eece1864c4fae3e49e13773f573aa99eb950fd4089b60", // Amazon shared silhouette PNG, 1,868 bytes (Accept: image/png)
            "d667fdb7c52bf78de62b8eee4bfa3c5874abbb9e52a1aa0014cfe55db52080d7"  // Amazon shared silhouette PNG, 1,743 bytes (CDN re-encodes per Accept header)
        };

        // A content verdict is immediately shared by ingestion, persistence, API, proxy,
        // canonical-cover, and carousel paths for the rest of the process lifetime. The
        // author row is also scrubbed at the first boundary that has author identity.
        private static readonly ConcurrentDictionary<string, byte> RejectedPlaceholderImageUrls = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex AuthorImageSizeSuffixRegex = new(@"-\d+(\.[^.]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private const string AuthorCoverIdentityVersion = "content-sha256-v1";

        private static readonly int[] PosterHeights = { 250, 500 };
        private static readonly int[] BannerHeights = { 35, 70 };
        private static readonly int[] FanartHeights = { 180, 360 };
        private static readonly int[] NoHeights = Array.Empty<int>();

        public static IReadOnlyList<int> GetHeights(MediaCoverTypes coverType)
        {
            return coverType switch
            {
                MediaCoverTypes.Poster or
                MediaCoverTypes.Disc or
                MediaCoverTypes.Cover or
                MediaCoverTypes.Logo or
                MediaCoverTypes.Headshot => PosterHeights,
                MediaCoverTypes.Banner => BannerHeights,
                MediaCoverTypes.Fanart or MediaCoverTypes.Screenshot => FanartHeights,
                _ => NoHeights
            };
        }

        public static string ComputeStableAuthorImageHash(string url, MediaCoverTypes coverType)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var trimmed = url.Trim();
            string normalized;

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
            {
                normalized = $"{absolute.IdnHost.ToLowerInvariant()}{absolute.AbsolutePath}";
            }
            else
            {
                normalized = trimmed.Split('#')[0].Split('?')[0];
            }

            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < normalized.Length - 1)
            {
                var directory = normalized.Substring(0, lastSlash + 1);
                var file = AuthorImageSizeSuffixRegex.Replace(normalized.Substring(lastSlash + 1), "$1");
                normalized = directory + file;
            }
            else
            {
                normalized = AuthorImageSizeSuffixRegex.Replace(normalized, "$1");
            }

            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{normalized}:{(int)coverType}"))).ToLowerInvariant();
        }

        public static bool IsSupportedImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var suffixStart = path.IndexOfAny(new[] { '?', '#' });
            var pathOnly = suffixStart >= 0 ? path.Substring(0, suffixStart) : path;
            return SupportedImageExtensions.Contains(Path.GetExtension(pathOnly));
        }

        public static IReadOnlyList<MediaCover> SelectCanonicalCovers(IEnumerable<MediaCover> covers)
        {
            return SelectCandidates(covers)
                .GroupBy(cover => cover.CoverType)
                .Select(group => group.First())
                .ToList();
        }

        public static IReadOnlyList<MediaCover> SelectCandidates(IEnumerable<MediaCover> covers)
        {
            return (covers ?? Enumerable.Empty<MediaCover>())
                .Where(cover => cover != null &&
                                cover.CoverType != MediaCoverTypes.Unknown &&
                                !string.IsNullOrWhiteSpace(cover.Url) &&
                                !IsKnownPlaceholderImageUrl(cover.Url))
                .ToList();
        }

        public static bool IsKnownPlaceholderImageUrl(string url)
        {
            var normalized = NormalizeRemoteImageUrl(url);
            if (normalized == null)
            {
                return false;
            }

            return normalized.Contains("nophoto", StringComparison.OrdinalIgnoreCase) ||
                   KnownPlaceholderImageUrls.Contains(normalized) ||
                   RejectedPlaceholderImageUrls.ContainsKey(normalized);
        }

        public static bool InspectDownloadedImage(string url, byte[] imageData, out string contentHash)
        {
            contentHash = ComputeContentSha256(imageData);
            return RegisterKnownPlaceholderImage(url, contentHash);
        }

        public static bool RegisterKnownPlaceholderImage(string url, string contentHash)
        {
            if (!IsKnownPlaceholderImageHash(contentHash))
            {
                return false;
            }

            var normalized = NormalizeRemoteImageUrl(url);
            if (normalized != null)
            {
                RejectedPlaceholderImageUrls.TryAdd(normalized, 0);
            }

            return true;
        }

        public static bool IsKnownPlaceholderImageHash(string contentHash)
        {
            return !string.IsNullOrWhiteSpace(contentHash) &&
                   KnownPlaceholderImageHashes.Contains(contentHash.Trim());
        }

        public static string ComputeContentSha256(byte[] imageData)
        {
            return imageData == null || imageData.Length == 0
                ? null
                : Convert.ToHexString(SHA256.HashData(imageData)).ToLowerInvariant();
        }

        public static string BuildAuthorCoverIdentity(string remoteUrl, string contentHash)
        {
            var normalizedHash = contentHash?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(remoteUrl) || !IsVerifiedNonPlaceholderImageHash(normalizedHash))
            {
                throw new ArgumentException("A verified non-placeholder image hash and remote URL are required.");
            }

            return string.Join('\n', AuthorCoverIdentityVersion, normalizedHash, remoteUrl.Trim());
        }

        public static bool TryParseAuthorCoverIdentity(string value, out string remoteUrl, out string contentHash)
        {
            remoteUrl = null;
            contentHash = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', 3);
            if (lines.Length != 3 ||
                !lines[0].Equals(AuthorCoverIdentityVersion, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(lines[2]) ||
                !IsVerifiedNonPlaceholderImageHash(lines[1]))
            {
                return false;
            }

            contentHash = lines[1].Trim().ToLowerInvariant();
            remoteUrl = lines[2].Trim();
            return true;
        }

        public static bool StoredContentHashIsVerified(string verificationPath, IDiskProvider diskProvider)
        {
            if (string.IsNullOrWhiteSpace(verificationPath) ||
                diskProvider == null ||
                !diskProvider.FileExists(verificationPath))
            {
                return false;
            }

            try
            {
                return IsVerifiedNonPlaceholderImageHash(diskProvider.ReadAllText(verificationPath));
            }
            catch
            {
                return false;
            }
        }

        public static string GetAuthorCoverIdentityFileName(MediaCoverTypes coverType)
        {
            return $"{coverType.ToString().ToLowerInvariant()}.url";
        }

        public static bool StoredRemoteUrlMatches(string identityPath, string remoteUrl, IDiskProvider diskProvider)
        {
            if (string.IsNullOrWhiteSpace(identityPath) ||
                string.IsNullOrWhiteSpace(remoteUrl) ||
                diskProvider == null ||
                !diskProvider.FileExists(identityPath))
            {
                return false;
            }

            try
            {
                return TryParseAuthorCoverIdentity(diskProvider.ReadAllText(identityPath), out var storedUrl, out _) &&
                       storedUrl.Equals(remoteUrl.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool HasAllGeneratedRenditions(
            Func<int?, string> getPath,
            IDiskProvider diskProvider,
            MediaCoverTypes coverType)
        {
            if (getPath == null || diskProvider == null)
            {
                return false;
            }

            var heights = GetHeights(coverType);
            return heights.Count > 0
                ? heights.All(height => IsUsable(getPath(height), diskProvider))
                : IsUsable(getPath(null), diskProvider);
        }

        public static IReadOnlyList<BookCoverSelection> SelectMonitoredBookCovers(Book book)
        {
            var monitoredEdition = book?.Editions?
                .Where(edition => edition != null && edition.Monitored)
                .OrderBy(edition => edition.Id)
                .FirstOrDefault();

            if (monitoredEdition == null)
            {
                return Array.Empty<BookCoverSelection>();
            }

            return SelectCandidates(monitoredEdition.Images)
                .Where(cover => cover.CoverType == MediaCoverTypes.Cover)
                .Select(cover => new BookCoverSelection(monitoredEdition, cover))
                .ToList();
        }

        public static bool HasAllRenditions(
            Func<int?, string> getPath,
            IDiskProvider diskProvider,
            MediaCoverTypes coverType)
        {
            if (getPath == null || diskProvider == null || !IsUsable(getPath(null), diskProvider))
            {
                return false;
            }

            return GetHeights(coverType).All(height => IsUsable(getPath(height), diskProvider));
        }

        public static string GetOriginalPath(string renditionPath)
        {
            if (string.IsNullOrWhiteSpace(renditionPath))
            {
                return renditionPath;
            }

            var extension = Path.GetExtension(renditionPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return renditionPath;
            }

            var stem = renditionPath.Substring(0, renditionPath.Length - extension.Length);
            var separator = stem.LastIndexOf('-');
            if (separator < 0 || separator == stem.Length - 1)
            {
                return renditionPath;
            }

            var suffix = stem.Substring(separator + 1);
            return suffix.All(char.IsDigit)
                ? stem.Substring(0, separator) + extension
                : renditionPath;
        }

        public static string FindExistingPath(
            Func<int?, string> getPath,
            IDiskProvider diskProvider,
            MediaCoverTypes coverType)
        {
            if (getPath == null || diskProvider == null)
            {
                return null;
            }

            var original = getPath(null);
            if (IsUsable(original, diskProvider))
            {
                return original;
            }

            // Prefer the smallest generated rendition. It is a valid URL for API clients
            // as-is, while Chaptarr's image components can select a larger sibling for HiDPI.
            foreach (var height in GetHeights(coverType))
            {
                var rendition = getPath(height);
                if (IsUsable(rendition, diskProvider))
                {
                    return rendition;
                }
            }

            return null;
        }

        public static bool IsUsable(string path, IDiskProvider diskProvider)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   diskProvider.FileExists(path) &&
                   diskProvider.GetFileSize(path) > 0;
        }

        private static bool IsVerifiedNonPlaceholderImageHash(string contentHash)
        {
            var normalized = contentHash?.Trim();
            return normalized?.Length == 64 &&
                   normalized.All(character => Uri.IsHexDigit(character)) &&
                   !IsKnownPlaceholderImageHash(normalized);
        }

        private static string NormalizeRemoteImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var normalized = url.Trim();
            var suffixStart = normalized.IndexOfAny(new[] { '?', '#' });
            if (suffixStart >= 0)
            {
                normalized = normalized.Substring(0, suffixStart);
            }

            return normalized.TrimEnd('/');
        }
    }

    public sealed class PlaceholderImageException : Exception
    {
        public PlaceholderImageException(string url, string contentHash)
            : base($"Remote image '{url}' is a known provider placeholder ({contentHash}).")
        {
        }
    }

    public sealed record BookCoverSelection(Edition Edition, MediaCover Cover);
}
