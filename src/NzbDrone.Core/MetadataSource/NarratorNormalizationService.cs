using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.MetadataSource
{
    public interface INarratorNormalizationService
    {
        List<string> NormalizeAndMergeNarrators(params List<string>[] narratorLists);
        List<string> NormalizeNarratorList(List<string> narrators);
        bool AreNarratorsEquivalent(string narrator1, string narrator2);
    }

    public class NarratorNormalizationService : INarratorNormalizationService
    {
        private readonly Logger _logger;

        // Regex patterns for narrator name normalization
        private static readonly RegexReplace WhitespaceRegex = new RegexReplace(@"\s+", " ", RegexOptions.Compiled);
        private static readonly RegexReplace SpecialCharsRegex = new RegexReplace(@"[^\w\s\-\.]", "", RegexOptions.Compiled);
        private static readonly RegexReplace TitlesRegex = new RegexReplace(@"\b(dr|dr\.|doctor|prof|prof\.|professor|mr|mr\.|mrs|mrs\.|ms|ms\.)\s+", " ", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly RegexReplace SuffixesRegex = new RegexReplace(@"\s+(jr|jr\.|sr|sr\.|ii|iii|iv)\b", " ", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly RegexReplace RolesRegex = new RegexReplace(@"\s+(narrator|reader|voice|actor)\b", " ", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly RegexReplace PunctuationRegex = new RegexReplace(@"[^\w\s]", "", RegexOptions.Compiled);

        public NarratorNormalizationService(Logger logger)
        {
            _logger = logger;
        }

        public List<string> NormalizeAndMergeNarrators(params List<string>[] narratorLists)
        {
            if (narratorLists == null || !narratorLists.Any())
            {
                return new List<string>();
            }

            _logger.Debug("Normalizing and merging {0} narrator lists", narratorLists.Length);

            // Flatten all lists into a single collection
            var allNarrators = narratorLists
                .Where(list => list != null)
                .SelectMany(list => list)
                .Where(narrator => narrator.IsNotNullOrWhiteSpace())
                .ToList();

            if (!allNarrators.Any())
            {
                _logger.Debug("No narrators found to normalize");
                return new List<string>();
            }

            _logger.Debug("Found {0} total narrator entries before normalization: {1}", allNarrators.Count, string.Join(", ", allNarrators.Take(10)));

            // Normalize the combined list
            var normalizedNarrators = NormalizeNarratorList(allNarrators);

            _logger.Debug("Normalized to {0} unique narrators: {1}", normalizedNarrators.Count, string.Join(", ", normalizedNarrators));

            return normalizedNarrators;
        }

        public List<string> NormalizeNarratorList(List<string> narrators)
        {
            if (!narrators?.Any() == true)
            {
                return new List<string>();
            }

            var processedNarrators = new List<string>();

            foreach (var narrator in narrators)
            {
                if (narrator.IsNullOrWhiteSpace())
                {
                    continue;
                }

                // Split combined narrator strings (e.g., "Michael Kramer & Kate Reading")
                var splitNarrators = SplitCombinedNarrators(narrator);

                foreach (var splitNarrator in splitNarrators)
                {
                    var normalized = NormalizeNarratorName(splitNarrator);
                    if (normalized.IsNotNullOrWhiteSpace())
                    {
                        processedNarrators.Add(normalized);
                    }
                }
            }

            // Remove duplicates using equivalence checking
            var uniqueNarrators = RemoveDuplicateNarrators(processedNarrators);

            // Apply GraphicAudio consolidation rule
            var finalNarrators = ApplyGraphicAudioRule(uniqueNarrators);

            return finalNarrators;
        }

        public bool AreNarratorsEquivalent(string narrator1, string narrator2)
        {
            if (narrator1.IsNullOrWhiteSpace() && narrator2.IsNullOrWhiteSpace())
            {
                return true;
            }

            if (narrator1.IsNullOrWhiteSpace() || narrator2.IsNullOrWhiteSpace())
            {
                return false;
            }

            var normalized1 = NormalizeForComparison(narrator1);
            var normalized2 = NormalizeForComparison(narrator2);

            // Exact match
            if (normalized1.Equals(normalized2, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check for containment (handles "Michael Kramer" vs "Kramer, Michael")
            if (normalized1.Contains(normalized2, StringComparison.OrdinalIgnoreCase) ||
                normalized2.Contains(normalized1, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check for name order variations (First Last vs Last, First)
            return AreNameVariations(normalized1, normalized2);
        }

        private List<string> SplitCombinedNarrators(string narrator)
        {
            if (narrator.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            // NARRATOR TRUNCATION FIX: Be more careful about splitting narrators
            // Don't split on commas by default as they might be part of a name
            // Only split on clear separators like "&", "and", "with", etc.
            var separators = new[] { "&", " and ", " with ", " featuring ", "/" };

            var parts = new List<string> { narrator };

            foreach (var separator in separators)
            {
                var newParts = new List<string>();
                foreach (var part in parts)
                {
                    if (part.ContainsIgnoreCase(separator))
                    {
                        var splitParts = part.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim())
                            .Where(p => p.IsNotNullOrWhiteSpace() && p.Length > 2); // Filter out tiny fragments
                        newParts.AddRange(splitParts);
                    }
                    else
                    {
                        newParts.Add(part);
                    }
                }

                parts = newParts;
            }

            // Clean up each part for common data corruption patterns
            var cleanedParts = new List<string>();
            foreach (var part in parts.Where(p => p.IsNotNullOrWhiteSpace()))
            {
                var cleanedPart = part;

                // Remove trailing fragments that look like data corruption
                if (cleanedPart.EndsWith(", er") || cleanedPart.EndsWith(", or") || cleanedPart.EndsWith(", and"))
                {
                    cleanedPart = cleanedPart.Substring(0, cleanedPart.LastIndexOf(",")).Trim();
                    _logger.Debug("Cleaned corrupted narrator name from '{0}' to '{1}'", part, cleanedPart);
                }

                if (cleanedPart.IsNotNullOrWhiteSpace() && cleanedPart.Length > 2)
                {
                    cleanedParts.Add(cleanedPart);
                }
            }

            return cleanedParts;
        }

        private string NormalizeNarratorName(string narrator)
        {
            if (narrator.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var normalized = narrator.Trim();

            // Handle special GraphicAudio cases
            if (IsGraphicAudioIndicator(normalized))
            {
                return "GraphicAudio";
            }

            // Clean up common formatting issues
            normalized = WhitespaceRegex.Replace(normalized);     // Multiple spaces to single space
            normalized = SpecialCharsRegex.Replace(normalized);   // Remove special characters except hyphens and periods
            normalized = normalized.Trim();

            // Handle titles and suffixes
            normalized = CleanTitlesAndSuffixes(normalized);

            return normalized;
        }

        private bool IsGraphicAudioIndicator(string narrator)
        {
            var lowercaseNarrator = narrator.ToLowerInvariant();

            return lowercaseNarrator.Contains("graphic audio") ||
                   lowercaseNarrator.Contains("graphicaudio") ||
                   lowercaseNarrator.Contains("full cast") ||
                   lowercaseNarrator.Contains("various narrators") ||
                   lowercaseNarrator.Contains("multiple narrators") ||
                   lowercaseNarrator.Contains("ensemble cast") ||
                   lowercaseNarrator.Equals("various") ||
                   lowercaseNarrator.Equals("cast");
        }

        private string CleanTitlesAndSuffixes(string name)
        {
            // Remove common titles and suffixes using predefined regex patterns
            var cleaned = name;
            cleaned = TitlesRegex.Replace(cleaned);
            cleaned = SuffixesRegex.Replace(cleaned);
            cleaned = RolesRegex.Replace(cleaned);

            return WhitespaceRegex.Replace(cleaned).Trim();
        }

        private List<string> RemoveDuplicateNarrators(List<string> narrators)
        {
            var uniqueNarrators = new List<string>();

            foreach (var narrator in narrators)
            {
                var isDuplicate = uniqueNarrators.Any(existing => AreNarratorsEquivalent(existing, narrator));

                if (!isDuplicate)
                {
                    uniqueNarrators.Add(narrator);
                }
            }

            return uniqueNarrators;
        }

        private List<string> ApplyGraphicAudioRule(List<string> narrators)
        {
            // If more than 2 narrators and none are already "GraphicAudio", consider consolidating
            if (narrators.Count > 2 && !narrators.Any(n => n.EqualsIgnoreCase("GraphicAudio")))
            {
                // Check if this looks like a GraphicAudio production
                var hasGraphicAudioIndicators = narrators.Any(IsGraphicAudioIndicator);
                var hasTooManyNarrators = narrators.Count > 5;

                if (hasGraphicAudioIndicators || hasTooManyNarrators)
                {
                    _logger.Debug("Consolidating {0} narrators to GraphicAudio: {1}", narrators.Count, string.Join(", ", narrators));
                    return new List<string> { "GraphicAudio" };
                }
            }

            return narrators;
        }

        private string NormalizeForComparison(string text)
        {
            if (text.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            return UnicodeComparisonNormalizer.NormalizeWords(text);
        }

        private bool AreNameVariations(string name1, string name2)
        {
            // Check for "First Last" vs "Last, First" variations
            var parts1 = name1.Split(' ').Where(p => p.IsNotNullOrWhiteSpace()).ToArray();
            var parts2 = name2.Split(' ').Where(p => p.IsNotNullOrWhiteSpace()).ToArray();

            if (parts1.Length >= 2 && parts2.Length >= 2)
            {
                // Check if "John Smith" matches "Smith John" or similar variations
                var firstLast1 = $"{parts1[0]} {parts1[parts1.Length - 1]}";
                var lastFirst1 = $"{parts1[parts1.Length - 1]} {parts1[0]}";

                var firstLast2 = $"{parts2[0]} {parts2[parts2.Length - 1]}";
                var lastFirst2 = $"{parts2[parts2.Length - 1]} {parts2[0]}";

                return firstLast1.EqualsIgnoreCase(firstLast2) ||
                       firstLast1.EqualsIgnoreCase(lastFirst2) ||
                       lastFirst1.EqualsIgnoreCase(firstLast2) ||
                       lastFirst1.EqualsIgnoreCase(lastFirst2);
            }

            return false;
        }
    }
}
