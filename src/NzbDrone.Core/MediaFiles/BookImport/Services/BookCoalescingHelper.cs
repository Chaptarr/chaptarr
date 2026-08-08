using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public static class BookCoalescingHelper
    {
        public static (bool Coalesced, List<(string Path, Dictionary<string, List<string>> Tags)> ExtraFiles, string DestUnitKey)
            Coalesce(Author author,
                     string authorPrefix,
                     string sampleFilePath,
                     Edition matchedEdition,
                     Dictionary<string, IngestQueueItem> byPath,
                     IIngestQueueRepository ingestQueue,
                     BookMediaType mediaType,
                     Logger logger)
        {
            var result = new List<(string Path, Dictionary<string, List<string>> Tags)>();
            try
            {
                var sampleExt = string.Empty;
                try { sampleExt = Path.GetExtension(sampleFilePath) ?? string.Empty; } catch { sampleExt = string.Empty; }

                var unitFolder = Path.GetDirectoryName(sampleFilePath) ?? string.Empty;
                var bookRoot = FindBookRootFolder(unitFolder, matchedEdition?.Title);
                if (string.IsNullOrWhiteSpace(bookRoot))
                {
                    logger.Debug("[BOOK-COALESCE][SKIP] No strict root for unitFolder='{0}'", unitFolder);
                    return (false, result, BuildRootUnitKey(sampleFilePath, matchedEdition?.Title, mediaType));
                }

                // Safety rail: ensure the root belongs to this author
                var normAuthorPrefix = NormalizeDirectory(authorPrefix) ?? string.Empty;
                var normRoot = NormalizeDirectory(bookRoot) ?? string.Empty;
                if (!normRoot.StartsWith(normAuthorPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normRoot, normAuthorPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Debug("[BOOK-COALESCE][SKIP] Root '{0}' not under author prefix '{1}'", normRoot, normAuthorPrefix);
                    return (false, result, BuildRootUnitKey(sampleFilePath, matchedEdition?.Title, mediaType));
                }

                logger.Debug("[BOOK-COALESCE][ROOT] unit='{0}' root='{1}' title='{2}'", unitFolder, normRoot, matchedEdition?.Title);

                // Build sibling folder set (immediate children under root) from queued items in memory
                var siblingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in byPath)
                {
                    var path = kv.Key;
                    if (!path.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    var dir = Path.GetDirectoryName(path) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var parent = Path.GetDirectoryName(dir) ?? string.Empty;
                    if (string.Equals(NormalizeDirectory(parent), normRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        siblingFolders.Add(NormalizeDirectory(dir));
                    }
                }

                if (siblingFolders.Count <= 1)
                {
                    return (false, result, BuildRootUnitKey(sampleFilePath, matchedEdition?.Title, mediaType));
                }

                try
                {
                    var sampleNames = siblingFolders.Select(p => { try { return new DirectoryInfo(p).Name; } catch { return p; } }).Take(5);
                    logger.Debug("[BOOK-COALESCE][SIBLINGS] root='{0}' count={1} samples=[{2}]", normRoot, siblingFolders.Count, string.Join(", ", sampleNames));
                }
                catch { }

                if (!LooksLikeEnumeratedSiblings(siblingFolders, logger))
                {
                    logger.Debug("[BOOK-COALESCE][SKIP] Siblings not enumerated discs under root '{0}'", normRoot);
                    return (false, result, BuildRootUnitKey(sampleFilePath, matchedEdition?.Title, mediaType));
                }

                // Claim missing siblings and gather items
                var claimedItems = new List<IngestQueueItem>();
                var newlyClaimedIds = new List<int>();
                try
                {
                    foreach (var folder in siblingFolders)
                    {
                        var existing = byPath.Values.Where(v =>
                        {
                            try { return string.Equals(NormalizeDirectory(Path.GetDirectoryName(v.Path) ?? string.Empty), folder, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        }).ToList();

                        if (existing.Any())
                        {
                            claimedItems.AddRange(existing);
                            logger.Debug("[BOOK-COALESCE][CLAIM] Using existing byPath items for '{0}' count={1}", folder, existing.Count);
                            continue;
                        }

                        var claimed = ingestQueue.TryClaimUnit(folder) ?? new List<IngestQueueItem>();
                        if (claimed.Count == 0)
                        {
                            logger.Debug("[BOOK-COALESCE] Sibling '{0}' already claimed/processed elsewhere; skipping", folder);
                            continue;
                        }
                        claimedItems.AddRange(claimed);
                        foreach (var ci in claimed)
                        {
                            byPath[ci.Path] = ci;
                            newlyClaimedIds.Add(ci.Id);
                        }
                        logger.Debug("[BOOK-COALESCE][CLAIM] Claimed '{0}' items={1}", folder, claimed.Count);
                    }
                    logger.Debug("[BOOK-COALESCE][CLAIMS] siblings={0} totalItems={1} newlyClaimed={2}", siblingFolders.Count, claimedItems.Count, newlyClaimedIds.Count);
                }
                catch
                {
                    foreach (var id in newlyClaimedIds)
                    {
                        try { ingestQueue.UpdateStatus(id, "queued"); } catch { }
                    }
                    return (false, result, BuildRootUnitKey(sampleFilePath, matchedEdition?.Title, mediaType));
                }

                foreach (var ci in claimedItems)
                {
                    try
                    {
                        var ext = Path.GetExtension(ci.Path) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(sampleExt) &&
                            !string.Equals(ext, sampleExt, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var q = MediaFileExtensions.GetQualityForExtension(ext);
                        var fileMedia = BookFile.DetermineMediaType(new NzbDrone.Core.Qualities.QualityModel { Quality = q });
                        if ((mediaType == BookMediaType.Audiobook && fileMedia != "audiobook")
                            || (mediaType == BookMediaType.Ebook && fileMedia != "ebook"))
                        {
                            continue;
                        }
                        var tags = SafeDeserializeTags(ci.TagsJson);
                        result.Add((ci.Path, tags));
                    }
                    catch { }
                }

                return (result.Count > 0, result, BuildRootUnitKey(sampleFilePath, matchedEdition?.Title, mediaType));
            }
            catch
            {
                return (false, result, BuildRootUnitKey(sampleFilePath, matchedEdition?.Title, mediaType));
            }
        }

        public static string BuildRootUnitKey(string anyFilePathInUnit, string editionTitle, BookMediaType mediaType)
        {
            try
            {
                var extension = Path.GetExtension(anyFilePathInUnit) ?? string.Empty;
                if (IsStandaloneUnitExtension(extension))
                {
                    var directory = Path.GetDirectoryName(anyFilePathInUnit) ?? string.Empty;
                    var fileStem = Path.GetFileNameWithoutExtension(anyFilePathInUnit) ?? string.Empty;
                    var standaloneRoot = NormalizeDirectory(Path.Combine(directory, fileStem)) ?? string.Empty;
                    return (standaloneRoot + "|" + mediaType).ToLowerInvariant();
                }

                var root = FindBookRootFolder(anyFilePathInUnit, editionTitle);
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetDirectoryName(anyFilePathInUnit) ?? string.Empty;
                root = NormalizeDirectory(root) ?? string.Empty;
                return (root + "|" + mediaType.ToString()).ToLowerInvariant();
            }
            catch { return anyFilePathInUnit; }
        }

        public static string BuildGroupingUnitKey(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return null;
                }

                var extension = (Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant();
                if (IsStandaloneUnitExtension(extension))
                {
                    return (NormalizeDirectory(filePath) ?? filePath).ToLowerInvariant();
                }

                var directory = NormalizeDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return null;
                }

                return (directory + "|" + extension).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        public static bool IsStandaloneUnitExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return MediaFileExtensions.IsSingleFileBookContainer(extension);
        }

        // Detect disc-only folder names: cd, disc, disk + optional separator + number
        // Deliberately excludes "part" as it's too common in real book titles
        // Uses tokenized form to catch Disc_1, CD-01, disc.2, etc.
        public static bool IsDiscOnlyFolderName(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return false;
            var tokens = TokenizeName(folderName);
            if (tokens.Count < 1 || tokens.Count > 2) return false;

            // Must have exactly: [disc-indicator, number] (TokenizeName splits "cd1" into ["cd","1"])
            var discIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cd", "disc", "disk" };

            // Two tokens: first must be disc indicator, second must be numeric
            if (tokens.Count == 2)
            {
                return discIndicators.Contains(tokens[0]) && tokens[1].All(char.IsDigit);
            }

            return false;
        }

        /// <summary>
        /// Find the book root folder by stripping disc-only leaf folders.
        /// Conservative: only strips obvious disc folders (CD1, Disc 2, etc.)
        /// Returns null if ambiguous - safer to skip coalesce than pick wrong root.
        /// Does NOT do reverse containment heuristics (risk of picking series folder).
        /// </summary>
        public static string FindBookRootFolder(string startPathOrFolder, string editionTitle)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(startPathOrFolder)) return null;
                var start = Directory.Exists(startPathOrFolder)
                    ? startPathOrFolder
                    : (Path.GetDirectoryName(startPathOrFolder) ?? string.Empty);

                var unit = NormalizeDirectory(start);
                var parent = NormalizeDirectory(Path.GetDirectoryName(unit) ?? string.Empty);
                string UnitName(string p) { try { return new DirectoryInfo(p).Name; } catch { return null; } }
                var unitNameRaw = UnitName(unit) ?? string.Empty;

                // 1. If current folder is disc-only (CD1, Disc 2, Disk_03), return parent as root
                if (IsDiscOnlyFolderName(unitNameRaw))
                {
                    return parent;
                }

                // 2. If we have an edition title, check simple containment (folder contains title)
                // This is safe because it's exact containment, not reverse
                if (!string.IsNullOrWhiteSpace(editionTitle))
                {
                    var normTitle = NormalizeTokensForCompare(editionTitle);
                    var unitName = NormalizeTokensForCompare(unitNameRaw);
                    var parentName = NormalizeTokensForCompare(UnitName(parent));

                    if (!string.IsNullOrWhiteSpace(unitName) && !string.IsNullOrWhiteSpace(normTitle)
                        && unitName.Contains(normTitle, StringComparison.Ordinal))
                        return unit;
                    if (!string.IsNullOrWhiteSpace(parentName) && !string.IsNullOrWhiteSpace(normTitle)
                        && parentName.Contains(normTitle, StringComparison.Ordinal))
                        return parent;
                }

                // 3. If ambiguous, return null - safer to skip coalesce than pick wrong root
                // NO reverse containment - risk of accidentally picking series folder
                return null;
            }
            catch { return null; }
        }

        public static bool LooksLikeEnumeratedSiblings(HashSet<string> siblingFolders, Logger logger)
        {
            try
            {
                var names = siblingFolders
                    .Select(p => { try { return new DirectoryInfo(p).Name; } catch { return null; } })
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();
                if (names.Count <= 1) return false;

                var tokenized = names.Select(TokenizeName).ToList();
                if (tokenized.Count == 0) return false;

                var common = new HashSet<string>(tokenized[0], StringComparer.Ordinal);
                for (int i = 1; i < tokenized.Count; i++)
                {
                    common.IntersectWith(tokenized[i]);
                    if (common.Count == 0) break;
                }

                var blacklist = new HashSet<string>(new[] { "version", "alt", "alternate", "remaster", "remastered", "deluxe", "extended" }, StringComparer.Ordinal);
                if (common.Any(c => blacklist.Contains(c))) return false;

                var discKeywords = new HashSet<string>(new[] { "disc", "disk", "cd", "part", "tape", "cassette", "side" }, StringComparer.Ordinal);
                var anyDiscKeyword = tokenized.Any(tks => tks.Any(t => discKeywords.Contains(t)));

                bool AllResidualNumeric()
                {
                    foreach (var tks in tokenized)
                    {
                        var residual = tks.Where(t => !common.Contains(t)).ToList();
                        if (residual.Count == 0) return false;
                        foreach (var r in residual)
                        {
                            if (!(IsDigits(r) || IsRoman(r))) return false;
                        }
                    }
                    return true;
                }

                var residualOk = AllResidualNumeric();
                logger?.Debug("[BOOK-COALESCE][ANALYZE] common=[{0}] discKeyword={1} residualNumeric={2}", string.Join(" ", common.Take(6)), anyDiscKeyword, residualOk);

                if (!anyDiscKeyword && !residualOk) return false;
                return true;
            }
            catch { return false; }
        }

        public static string NormalizeDirectory(string path)
        {
            return BookImportSerializationHelper.NormalizeDirectory(path);
        }

        private static bool IsDigits(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            for (int i = 0; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }

        private static bool IsRoman(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var v = s.ToUpperInvariant();
            return Regex.IsMatch(v, "^(M{0,4}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3}))$");
        }

        private static string NormalizeTokensForCompare(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.ToLowerInvariant();
            var arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
            {
                var ch = arr[i];
                if (!(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))) arr[i] = ' ';
            }
            var norm = Regex.Replace(new string(arr), "\\s+", " ").Trim();
            return norm;
        }

        private static List<string> TokenizeName(string s)
        {
            var norm = NormalizeTokensForCompare(s);
            norm = Regex.Replace(norm, "([a-z]+)([0-9]+)", "$1 $2");
            norm = Regex.Replace(norm, "([0-9]+)([a-z]+)", "$1 $2");
            return norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static Dictionary<string, List<string>> SafeDeserializeTags(string json)
        {
            return BookImportSerializationHelper.SafeDeserializeTags(json);
        }
    }
}
