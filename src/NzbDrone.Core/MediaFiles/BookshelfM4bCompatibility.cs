using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    /// <summary>
    /// Reads the opt-in BookshelfNG M4B settings while a library is running under ChaptarrNG.
    /// Native quality-profile conversion remains the default when the legacy setting is absent.
    /// </summary>
    public static class BookshelfM4bCompatibility
    {
        public const string MergeEnvironmentVariable = "BOOKSHELF_M4B_MERGE";
        public const string BitrateEnvironmentVariable = "BOOKSHELF_M4B_AAC_BITRATE_KBPS";
        public const string FfmpegPathEnvironmentVariable = "BOOKSHELF_FFMPEG_PATH";
        public const int DefaultBitrateKbps = 128;

        private static readonly Regex NumberRegex = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex LeadingNumberRegex = new Regex(@"^\s*(?<number>\d+)", RegexOptions.Compiled);
        private static readonly Regex DiscNumberRegex = new Regex(@"(?:disc|disk|cd)[\s._-]*(?<number>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool LegacyMergeEnabled => ParseMergeEnabled(Environment.GetEnvironmentVariable(MergeEnvironmentVariable));

        public static string ConfiguredFfmpegPath => Environment.GetEnvironmentVariable(FfmpegPathEnvironmentVariable);

        public static bool ParseMergeEnabled(string value)
        {
            return bool.TryParse(value, out var enabled) && enabled;
        }

        public static int ParseBitrateKbps(string value, out bool invalid)
        {
            invalid = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultBitrateKbps;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bitrate) || bitrate < 48 || bitrate > 320)
            {
                invalid = true;
                return DefaultBitrateKbps;
            }

            return bitrate;
        }

        public static bool ShouldUseLegacyMerge(
            bool enabled,
            QualityProfile profile,
            IEnumerable<ImportDecision<LocalBook>> decisions,
            bool hasRejectedTracksForEdition)
        {
            if (!enabled ||
                profile?.ProfileType != ProfileType.Audiobook ||
                hasRejectedTracksForEdition ||
                decisions == null)
            {
                return false;
            }

            var candidates = decisions.ToList();
            if (candidates.Count < 2 || candidates.Any(candidate => candidate == null || !candidate.Approved || candidate.Item == null))
            {
                return false;
            }

            var first = candidates[0].Item;
            var bookId = first.Book?.Id ?? 0;
            var editionId = first.Edition?.Id ?? 0;
            if (bookId <= 0 || editionId <= 0 || first.Book.MediaType != BookMediaType.Audiobook)
            {
                return false;
            }

            return candidates.All(candidate =>
                candidate.Item.Book?.Id == bookId &&
                candidate.Item.Book.MediaType == BookMediaType.Audiobook &&
                candidate.Item.Edition?.Id == editionId &&
                IsMergeSourcePath(candidate.Item.Path));
        }

        public static bool IsMergeSourcePath(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            return !extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase) &&
                   MediaFileExtensions.AudioExtensions.Any(audioExtension => extension.Equals(audioExtension, StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasRejectedTracksForEdition(IEnumerable<ImportDecision<LocalBook>> decisions, int bookId, int editionId)
        {
            return decisions?.Any(decision =>
                decision != null &&
                !decision.Approved &&
                decision.Item?.Book?.Id == bookId &&
                decision.Item?.Edition?.Id == editionId &&
                IsMergeSourcePath(decision.Item.Path)) == true;
        }

        public static List<ImportDecision<LocalBook>> OrderTracks(IEnumerable<ImportDecision<LocalBook>> decisions)
        {
            return (decisions ?? Enumerable.Empty<ImportDecision<LocalBook>>())
                .OrderBy(decision => GetDiscNumber(decision?.Item))
                .ThenBy(decision => GetTrackNumber(decision?.Item))
                .ThenBy(decision => NumberRegex.Replace(decision?.Item?.Path ?? string.Empty, match => match.Value.PadLeft(9, '0')), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string[] GetChapterTitles(IEnumerable<ImportDecision<LocalBook>> orderedDecisions)
        {
            return (orderedDecisions ?? Enumerable.Empty<ImportDecision<LocalBook>>())
                .Select(decision => GetChapterTitle(decision?.Item))
                .ToArray();
        }

        public static bool TryBuildFfmpegSearchPath(string ffmpegPath, string currentPath, out string searchPath)
        {
            searchPath = currentPath;
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                return false;
            }

            var expectedName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            if (!string.Equals(Path.GetFileName(ffmpegPath), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(ffmpegPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return true;
            }

            searchPath = directory + Path.PathSeparator + (currentPath ?? string.Empty);
            return true;
        }

        public static bool IsExpectedFfmpegExecutableName(string ffmpegPath)
        {
            var expectedName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            return !string.IsNullOrWhiteSpace(ffmpegPath) &&
                   string.Equals(Path.GetFileName(ffmpegPath), expectedName, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetDiscNumber(LocalBook localBook)
        {
            var taggedNumber = GetCanonicalNumber(localBook, "DISCNUMBER");
            if (taggedNumber > 0)
            {
                return taggedNumber;
            }

            var match = DiscNumberRegex.Match(localBook?.Path ?? string.Empty);
            return match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0;
        }

        private static int GetTrackNumber(LocalBook localBook)
        {
            var taggedNumber = GetCanonicalNumber(localBook, "TRACKNUMBER");
            if (taggedNumber > 0)
            {
                return taggedNumber;
            }

            var fileName = Path.GetFileNameWithoutExtension(localBook?.Path ?? string.Empty);
            var match = LeadingNumberRegex.Match(fileName);
            return match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : int.MaxValue;
        }

        private static string GetChapterTitle(LocalBook localBook)
        {
            var title = GetCanonicalTagValue(localBook, "TITLE");
            return string.IsNullOrWhiteSpace(title)
                ? Path.GetFileNameWithoutExtension(localBook?.Path ?? string.Empty)
                : title;
        }

        private static int GetCanonicalNumber(LocalBook localBook, string key)
        {
            var value = GetCanonicalTagValue(localBook, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            var numberText = value.Split('/')[0].Trim();
            return int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
        }

        private static string GetCanonicalTagValue(LocalBook localBook, string key)
        {
            var rawTags = localBook?.RawTags?.AllTags;
            if (rawTags == null || rawTags.Count == 0)
            {
                return null;
            }

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in rawTags)
            {
                if (!tags.TryGetValue(pair.Key, out var existingValues))
                {
                    tags[pair.Key] = pair.Value?.ToList() ?? new List<string>();
                }
                else if (pair.Value != null)
                {
                    existingValues.AddRange(pair.Value);
                }
            }

            TagCanonicalizer.AddCanonicalKeys(tags);

            return tags.TryGetValue(key, out var canonicalValues) && canonicalValues.Count == 1
                ? canonicalValues[0]
                : null;
        }
    }
}
