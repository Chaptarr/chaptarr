using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public sealed class BookImportIdentitySubgroup
    {
        public string IdentityKey { get; set; }
        public List<DiscoveredFileWithMetadata> Files { get; set; }
    }

    public sealed class BookImportUnit
    {
        public string Key { get; set; }
        public string RootPath { get; set; }
        public List<BookFile> Files { get; set; }
    }

    /// <summary>
    /// Defines the pre-match file-unit contract shared by matching and the unmapped-files API.
    /// This is deliberately evidence grouping, not Book identity: the normal matcher remains the
    /// authority for deciding which provider-owned Book and Edition a unit represents.
    /// </summary>
    public static class BookImportUnitGroupingService
    {
        internal static readonly IReadOnlySet<string> HardNoiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // English stopwords (minimal)
            "a", "an", "and", "or", "the", "of", "in", "on", "at", "by", "for", "with", "to", "from", "as",

            // Common audiobook/ebook/file noise
            "audiobook", "audio", "ebook", "e-book", "unabridged", "abridged",
            "narrated", "narrator", "narration", "read", "performed",
            "disc", "cd", "track", "trk",

            // Common edition descriptors
            "complete", "special", "deluxe", "expanded", "illustrated", "anniversary",
            "collector", "collectors", "collection",

            // Common subtitle boilerplate
            "novel"
        };

        internal static readonly IReadOnlySet<string> StructuralTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "part", "pt",
            "chapter", "chapters", "chap", "ch",
            "vol", "volume",
            "book",
            "edition"
        };

        // When the only unexplained leftovers are numeric tokens, allow them if the evidence field
        // clearly contains packaging markers (e.g., "Volume 1", "Disc 2", "Track 01").
        // Some rippers embed track packaging as "T01-19" in TITLE/TIT2: "t" is kept as a packaging
        // prefix so "t01" splits into ["t", "01"] and a trailing total-track token cannot block
        // matching on short titles.
        internal static readonly IReadOnlySet<string> NumericPackagingTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "part", "pt",
            "chapter", "chapters", "chap", "ch",
            "disc", "cd", "track", "trk",
            "t"
        };

        public static List<BookImportIdentitySubgroup> BuildIdentitySubgroups(
            IReadOnlyList<DiscoveredFileWithMetadata> files,
            ISet<string> excludedIdentityValues = null)
        {
            if (files == null || files.Count == 0)
            {
                return new List<BookImportIdentitySubgroup>();
            }

            var pairSupport = BuildIdentityPairSupport(files, excludedIdentityValues);

            return files
                .GroupBy(
                    file => BuildIdentityKey(file?.AllTags, pairSupport, excludedIdentityValues),
                    StringComparer.Ordinal)
                .Select(group => new BookImportIdentitySubgroup
                {
                    IdentityKey = group.Key,
                    Files = group
                        .Where(file => file != null)
                        .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                })
                .Where(group => group.Files.Count > 0)
                .OrderBy(group => string.IsNullOrWhiteSpace(group.IdentityKey) ? 1 : 0)
                .ThenBy(group => group.IdentityKey, StringComparer.Ordinal)
                .ToList();
        }

        public static IReadOnlyDictionary<string, int> BuildIdentityPairSupport(
            IEnumerable<DiscoveredFileWithMetadata> files,
            ISet<string> excludedIdentityValues = null)
        {
            var pairSupport = new Dictionary<string, int>(StringComparer.Ordinal);
            if (files == null)
            {
                return pairSupport;
            }

            foreach (var file in files)
            {
                var filePairs = BuildIdentityPairs(file?.AllTags, excludedIdentityValues)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                foreach (var pair in filePairs)
                {
                    pairSupport[pair] = pairSupport.TryGetValue(pair, out var count) ? count + 1 : 1;
                }
            }

            return pairSupport;
        }

        public static string BuildIdentityKey(
            Dictionary<string, List<string>> tags,
            IReadOnlyDictionary<string, int> pairSupport = null,
            ISet<string> excludedIdentityValues = null)
        {
            var pairs = BuildIdentityPairs(tags, excludedIdentityValues)
                .Where(pair =>
                    pairSupport == null ||
                    (pairSupport.TryGetValue(pair, out var support) && support >= 2))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(pair => pair, StringComparer.Ordinal)
                .ToList();

            return pairs.Count == 0 ? null : string.Join("\u001F", pairs);
        }

        public static IReadOnlyList<BookImportUnit> BuildUnmappedUnits(
            IReadOnlyCollection<BookFile> files,
            Func<string, string> rootPathResolver = null)
        {
            var candidates = files?
                .Where(file => file != null && file.EditionId == 0 && !string.IsNullOrWhiteSpace(file.Path))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<BookFile>();

            if (candidates.Count == 0)
            {
                return Array.Empty<BookImportUnit>();
            }

            var units = new List<BookImportUnit>();
            var standalone = candidates
                .Where(file => BookCoalescingHelper.IsStandaloneUnitExtension(Path.GetExtension(file.Path)))
                .ToList();

            foreach (var file in standalone)
            {
                units.Add(CreateUnit(
                    new[] { file },
                    GetDirectory(file.Path),
                    $"standalone\u001D{NormalizePath(file.Path)}"));
            }

            var audioFiles = candidates.Except(standalone).ToList();
            var discPoolRoots = BuildDiscPoolRoots(audioFiles);
            var physicalPools = audioFiles
                .GroupBy(
                    file => BuildPhysicalPoolKey(file.Path, discPoolRoots),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var physicalPool in physicalPools)
            {
                var poolFiles = physicalPool
                    .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (poolFiles.Count == 0)
                {
                    continue;
                }

                var poolRoot = ResolvePhysicalPoolRoot(poolFiles[0].Path, discPoolRoots);
                var excludedIdentityValues = BuildAuthorFolderExclusions(
                    poolFiles[0].Path,
                    rootPathResolver);
                var discovered = poolFiles
                    .Select(file => new DiscoveredFileWithMetadata
                    {
                        Path = file.Path,
                        Size = file.Size,
                        Modified = file.Modified,
                        AllTags = file.AllTags,
                        DurationSeconds = file.DurationSeconds
                    })
                    .ToList();
                var identitySubgroups = BuildIdentitySubgroups(discovered, excludedIdentityValues);
                var byPath = poolFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

                foreach (var subgroup in identitySubgroups)
                {
                    var subgroupFiles = subgroup.Files
                        .Where(file => file?.Path != null && byPath.ContainsKey(file.Path))
                        .Select(file => byPath[file.Path])
                        .ToList();
                    if (subgroupFiles.Count == 0)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(subgroup.IdentityKey) &&
                        subgroupFiles.Count > 1 &&
                        subgroupFiles.Any(file => BuildIdentityPairs(file.AllTags, excludedIdentityValues).Any()))
                    {
                        // The files have meaningful but non-repeating evidence. Treating the folder as
                        // identity here would merge distinct single-file books. Fail closed per file.
                        foreach (var file in subgroupFiles)
                        {
                            units.Add(CreateUnit(
                                new[] { file },
                                GetDirectory(file.Path),
                                $"file\u001D{NormalizePath(file.Path)}"));
                        }

                        continue;
                    }

                    var identity = string.IsNullOrWhiteSpace(subgroup.IdentityKey)
                        ? "tagless-folder-fallback"
                        : subgroup.IdentityKey;
                    units.Add(CreateUnit(
                        subgroupFiles,
                        poolRoot,
                        $"audio\u001D{NormalizePath(poolRoot)}\u001D{Path.GetExtension(poolFiles[0].Path).ToLowerInvariant()}\u001D{identity}"));
                }
            }

            return units
                .OrderBy(unit => unit.Files[0].Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string BuildFallbackUnitKey(BookFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                return null;
            }

            return HashKey($"file\u001D{NormalizePath(file.Path)}");
        }

        public static string GetFallbackUnitRoot(string path)
        {
            return GetDirectory(path);
        }

        internal static string NormalizeIdentityValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = NormalizeForPathTokens(value).ToLowerInvariant();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        internal static string NormalizeForPathTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            text = text.Replace('\u2018', '\'')
                .Replace('\u2019', '\'')
                .Replace('\u201B', '\'')
                .Replace('\u02BC', '\'')
                .Replace('\u0060', '\'')
                .Replace('\u00B4', '\'')
                .Replace('\uFF07', '\'');

            text = text.Replace('\u2010', '-')
                .Replace('\u2011', '-')
                .Replace('\u2012', '-')
                .Replace('\u2013', '-')
                .Replace('\u2014', '-')
                .Replace('\u2212', '-')
                .Replace('\uFF0D', '-');

            text = text.Replace("\u00AD", string.Empty);
            text = text.Replace('\uFF0E', '.');
            text = text.Replace('\u00B7', ' ')
                .Replace('\u30FB', ' ')
                .Replace('\u3002', ' ');

            text = text.Replace('\u00A0', ' ')
                .Replace('\u2000', ' ')
                .Replace('\u2001', ' ')
                .Replace('\u2002', ' ')
                .Replace('\u2003', ' ')
                .Replace('\u2004', ' ')
                .Replace('\u2005', ' ')
                .Replace('\u2006', ' ')
                .Replace('\u2007', ' ')
                .Replace('\u2008', ' ')
                .Replace('\u2009', ' ')
                .Replace('\u200A', ' ')
                .Replace('\u202F', ' ')
                .Replace('\u205F', ' ')
                .Replace('\u3000', ' ')
                .Replace('\t', ' ');

            text = text.Replace("\u200B", string.Empty)
                .Replace("\u200C", string.Empty)
                .Replace("\u200D", string.Empty)
                .Replace("\uFEFF", string.Empty);

            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        // Align leftover-token validation with containment smoke-test behavior: containment
        // splits hyphenated/dotted words while FTS tokenization preserves "-." for querying.
        internal static List<string> TokenizeForLeftoverGate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            text = Regex.Replace(text, @"[–—-]", " ");
            text = text.Replace('.', ' ');
            return TokenizeText(text);
        }

        internal static List<string> TokenizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            text = Regex.Replace(text, "['\\u2018\\u2019]s\\b", "s");
            text = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            foreach (var character in text)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(character);
                }
            }

            // CRITICAL: FTS tokenchars is '-.' so hyphens and periods stay inside tokens
            // (the FTS index holds "high-opp" as one token; splitting here would break querying).
            var matches = Regex.Matches(
                builder.ToString().ToLowerInvariant(),
                @"[\p{L}\p{Nd}]+(?:[-\.][\p{L}\p{Nd}]+)*");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tokens = new List<string>();
            foreach (Match match in matches)
            {
                if (seen.Add(match.Value))
                {
                    tokens.Add(match.Value);
                }
            }

            return tokens;
        }

        private static IEnumerable<string> BuildIdentityPairs(
            IDictionary<string, List<string>> tags,
            ISet<string> excludedIdentityValues = null)
        {
            if (tags == null || tags.Count == 0)
            {
                yield break;
            }

            foreach (var pair in tags)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) ||
                    pair.Value == null ||
                    pair.Value.Count == 0 ||
                    TagExclusionPolicy.IsExcludedFromMatching(pair.Key))
                {
                    continue;
                }

                var identityKey = pair.Key.Trim().ToLowerInvariant();
                foreach (var value in pair.Value)
                {
                    var identityValue = NormalizeIdentityValue(value);
                    if (string.IsNullOrWhiteSpace(identityValue) ||
                        IsLikelyTrackOrChapterIdentityValue(identityValue) ||
                        (excludedIdentityValues != null && excludedIdentityValues.Contains(identityValue)))
                    {
                        continue;
                    }

                    yield return $"{identityKey}\u001E{identityValue}";
                }
            }
        }

        private static bool IsLikelyTrackOrChapterIdentityValue(string value)
        {
            var tokens = TokenizeForLeftoverGate(value);
            if (tokens.Count == 0)
            {
                return true;
            }

            var hasPackagingMarker = tokens.Any(token =>
                NumericPackagingTokens.Contains(token) ||
                StructuralTokens.Contains(token));
            var hasNumber = tokens.Any(token =>
                token.All(char.IsDigit) ||
                TryParseRomanNumeral(token, out _));

            if (hasPackagingMarker && hasNumber)
            {
                return true;
            }

            return tokens.All(token =>
                token.All(char.IsDigit) ||
                TryParseRomanNumeral(token, out _) ||
                HardNoiseTokens.Contains(token) ||
                StructuralTokens.Contains(token));
        }

        private static HashSet<string> BuildAuthorFolderExclusions(
            string filePath,
            Func<string, string> rootPathResolver)
        {
            var output = new HashSet<string>(StringComparer.Ordinal);
            if (rootPathResolver == null || string.IsNullOrWhiteSpace(filePath))
            {
                return output;
            }

            try
            {
                var rootPath = rootPathResolver(filePath);
                if (string.IsNullOrWhiteSpace(rootPath) || !rootPath.IsParentPath(filePath))
                {
                    return output;
                }

                var relativePath = rootPath.GetRelativePath(filePath);
                var separatorIndex = relativePath.IndexOfAny(new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                });
                var authorFolder = separatorIndex > 0
                    ? relativePath.Substring(0, separatorIndex)
                    : null;
                var normalizedAuthor = NormalizeIdentityValue(authorFolder);
                if (!string.IsNullOrWhiteSpace(normalizedAuthor))
                {
                    output.Add(normalizedAuthor);
                }
            }
            catch
            {
                // Grouping must remain conservative when root context is unavailable.
            }

            return output;
        }

        private static Dictionary<string, string> BuildDiscPoolRoots(IReadOnlyCollection<BookFile> files)
        {
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var candidates = files
                .Select(file => new
                {
                    File = file,
                    Directory = GetDirectory(file.Path),
                    Extension = Path.GetExtension(file.Path).ToLowerInvariant()
                })
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Directory) &&
                    BookCoalescingHelper.IsDiscOnlyFolderName(Path.GetFileName(item.Directory)))
                .GroupBy(
                    item => $"{NormalizePath(GetDirectory(item.Directory))}\u001D{item.Extension}",
                    StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                var distinctFolders = candidate
                    .Select(item => item.Directory)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (distinctFolders.Count <= 1)
                {
                    continue;
                }

                var root = GetDirectory(distinctFolders[0]);
                foreach (var folder in distinctFolders)
                {
                    roots[NormalizePath(folder)] = root;
                }
            }

            return roots;
        }

        private static string BuildPhysicalPoolKey(
            string filePath,
            IReadOnlyDictionary<string, string> discPoolRoots)
        {
            var root = ResolvePhysicalPoolRoot(filePath, discPoolRoots);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return $"{NormalizePath(root)}\u001D{extension}";
        }

        private static string ResolvePhysicalPoolRoot(
            string filePath,
            IReadOnlyDictionary<string, string> discPoolRoots)
        {
            var directory = GetDirectory(filePath);
            return discPoolRoots.TryGetValue(NormalizePath(directory), out var discRoot)
                ? discRoot
                : directory;
        }

        private static BookImportUnit CreateUnit(
            IEnumerable<BookFile> files,
            string rootPath,
            string keyMaterial)
        {
            return new BookImportUnit
            {
                Key = HashKey(keyMaterial),
                RootPath = rootPath,
                Files = files
                    .Where(file => file != null)
                    .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        private static string HashKey(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return $"unit:{Convert.ToHexString(bytes).ToLowerInvariant()}";
        }

        private static string GetDirectory(string path)
        {
            try
            {
                return BookCoalescingHelper.NormalizeDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizePath(string path)
        {
            return (BookCoalescingHelper.NormalizeDirectory(path) ?? path ?? string.Empty).ToLowerInvariant();
        }

        private static bool TryParseRomanNumeral(string raw, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var map = new Dictionary<char, int>
            {
                ['I'] = 1,
                ['V'] = 5,
                ['X'] = 10,
                ['L'] = 50,
                ['C'] = 100,
                ['D'] = 500,
                ['M'] = 1000
            };
            var total = 0;
            var previous = 0;
            foreach (var character in raw.Trim().ToUpperInvariant().Reverse())
            {
                if (!map.TryGetValue(character, out var current))
                {
                    return false;
                }

                if (current < previous)
                {
                    total -= current;
                }
                else
                {
                    total += current;
                    previous = current;
                }
            }

            if (total <= 0)
            {
                return false;
            }

            value = total;
            return true;
        }
    }
}
