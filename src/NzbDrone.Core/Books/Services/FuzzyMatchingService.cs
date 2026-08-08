using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Books.Services
{
    public interface IFuzzyMatchingService
    {
        List<string> GenerateTrigrams(string text);
        double CalculateTrigramSimilarity(string s1, string s2);
        int CalculateLevenshteinDistance(string s1, string s2);
        string NormalizeForMatching(string text);
        string NormalizeNarrator(string narrator);
        double CalculateSimilarityScore(string s1, string s2);
    }

    public class FuzzyMatchingService : IFuzzyMatchingService
    {
        private readonly Logger _logger;

        public FuzzyMatchingService(Logger logger)
        {
            _logger = logger;
            _logger.Debug("[FUZZY-INIT] FuzzyMatchingService initialized");
        }

        public List<string> GenerateTrigrams(string text)
        {
            var startTime = DateTime.UtcNow;
            _logger.Debug("[FUZZY-TRIGRAM] Generating trigrams for: '{0}'",
                text?.Substring(0, Math.Min(text?.Length ?? 0, 50)) ?? "null");

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Trace("[FUZZY-TRIGRAM] Text is null/empty, returning empty list");
                return new List<string>();
            }

            // Normalize and pad with spaces for edge trigrams
            var normalized = NormalizeForMatching(text);
            text = " " + normalized + " ";
            _logger.Trace("[FUZZY-TRIGRAM] Padded normalized text: '{0}' (length: {1})", text, text.Length);

            var trigrams = new List<string>();

            // Generate all 3-character substrings
            for (int i = 0; i <= text.Length - 3; i++)
            {
                var trigram = text.Substring(i, 3);
                trigrams.Add(trigram);

                if (i < 5 || i >= text.Length - 8) // Log first few and last few
                {
                    _logger.Trace("[FUZZY-TRIGRAM] Position {0}: '{1}'", i, trigram);
                }
            }

            var distinctTrigrams = trigrams.Distinct().ToList();
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger.Debug("[FUZZY-TRIGRAM] Generated {0} total trigrams, {1} unique (took {2:F2}ms)",
                trigrams.Count, distinctTrigrams.Count, elapsed);

            if (distinctTrigrams.Count > 0)
            {
                _logger.Trace("[FUZZY-TRIGRAM] Sample trigrams: {0}",
                    string.Join(", ", distinctTrigrams.Take(10).Select(t => $"'{t}'")));
            }

            return distinctTrigrams;
        }

        public double CalculateTrigramSimilarity(string s1, string s2)
        {
            var startTime = DateTime.UtcNow;
            _logger.Debug("[FUZZY-SIMILARITY] Calculating trigram similarity between:\n  Text1: '{0}'\n  Text2: '{1}'",
                s1?.Substring(0, Math.Min(s1?.Length ?? 0, 50)) ?? "null",
                s2?.Substring(0, Math.Min(s2?.Length ?? 0, 50)) ?? "null");

            if (string.IsNullOrWhiteSpace(s1) || string.IsNullOrWhiteSpace(s2))
            {
                _logger.Debug("[FUZZY-SIMILARITY] One or both texts are null/empty, returning 0.0");
                return 0.0;
            }

            var trigrams1 = GenerateTrigrams(s1);
            var trigrams2 = GenerateTrigrams(s2);

            if (!trigrams1.Any() || !trigrams2.Any())
            {
                _logger.Debug("[FUZZY-SIMILARITY] One or both texts have no trigrams, returning 0.0");
                return 0.0;
            }

            // Create sets for efficient lookup
            var set1 = new HashSet<string>(trigrams1);
            var set2 = new HashSet<string>(trigrams2);

            // Calculate intersection
            var intersection = set1.Intersect(set2).Count();
            _logger.Trace("[FUZZY-SIMILARITY] Common trigrams: {0}",
                string.Join(", ", set1.Intersect(set2).Take(10).Select(t => $"'{t}'")));

            // Calculate Jaccard similarity
            var union = set1.Count + set2.Count - intersection;
            if (union == 0)
            {
                _logger.Debug("[FUZZY-SIMILARITY] Union is 0, returning 0.0");
                return 0.0;
            }

            var similarity = (double)intersection / union;
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger.Debug("[FUZZY-SIMILARITY] Jaccard similarity = {0:F4} (intersection: {1}, set1: {2}, set2: {3}, union: {4}, took {5:F2}ms)",
                similarity, intersection, set1.Count, set2.Count, union, elapsed);

            return similarity;
        }

        public int CalculateLevenshteinDistance(string s1, string s2)
        {
            var startTime = DateTime.UtcNow;
            _logger.Trace("[FUZZY-LEVENSHTEIN] Calculating distance between:\n  S1: '{0}' (len: {1})\n  S2: '{2}' (len: {3})",
                s1?.Substring(0, Math.Min(s1?.Length ?? 0, 30)) ?? "null", s1?.Length ?? 0,
                s2?.Substring(0, Math.Min(s2?.Length ?? 0, 30)) ?? "null", s2?.Length ?? 0);

            if (string.IsNullOrEmpty(s1))
            {
                var result = string.IsNullOrEmpty(s2) ? 0 : s2.Length;
                _logger.Trace("[FUZZY-LEVENSHTEIN] S1 is empty, distance = {0}", result);
                return result;
            }

            if (string.IsNullOrEmpty(s2))
            {
                _logger.Trace("[FUZZY-LEVENSHTEIN] S2 is empty, distance = {0}", s1.Length);
                return s1.Length;
            }

            var length1 = s1.Length;
            var length2 = s2.Length;
            var distance = new int[length1 + 1, length2 + 1];

            // Initialize first column and row
            for (int i = 0; i <= length1; i++)
            {
                distance[i, 0] = i;
            }

            for (int j = 0; j <= length2; j++)
            {
                distance[0, j] = j;
            }

            // Calculate distances
            for (int i = 1; i <= length1; i++)
            {
                for (int j = 1; j <= length2; j++)
                {
                    var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;

                    distance[i, j] = Math.Min(
                        Math.Min(
                            distance[i - 1, j] + 1,      // Deletion
                            distance[i, j - 1] + 1),     // Insertion
                        distance[i - 1, j - 1] + cost);  // Substitution

                    // Log progress for long strings
                    if ((i == length1 / 2 && j == length2 / 2) || (i == length1 && j == length2))
                    {
                        _logger.Trace("[FUZZY-LEVENSHTEIN] Progress: position [{0},{1}] = {2}", i, j, distance[i, j]);
                    }
                }
            }

            var finalDistance = distance[length1, length2];
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger.Debug("[FUZZY-LEVENSHTEIN] Distance = {0} (took {1:F2}ms)", finalDistance, elapsed);

            return finalDistance;
        }

        public string NormalizeForMatching(string text)
        {
            var startTime = DateTime.UtcNow;
            _logger.Trace("[FUZZY-NORMALIZE] Starting normalization for text: '{0}' (length: {1})",
                text?.Substring(0, Math.Min(text?.Length ?? 0, 50)) ?? "null", text?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Trace("[FUZZY-NORMALIZE] Text is null/empty, returning empty");
                return string.Empty;
            }

            var originalText = text;

            // Convert to lowercase
            text = text.ToLowerInvariant();
            if (text != originalText)
            {
                _logger.Trace("[FUZZY-NORMALIZE] After lowercase: '{0}'", text);
            }

            // Remove common articles at the beginning
            var beforeArticles = text;
            text = Regex.Replace(text, @"^(the|a|an)\s+", "", RegexOptions.IgnoreCase);
            if (text != beforeArticles)
            {
                _logger.Trace("[FUZZY-NORMALIZE] Removed article, now: '{0}'", text);
            }

            var beforeNormalized = text;
            text = UnicodeComparisonNormalizer.NormalizeWords(text);
            if (text != beforeNormalized)
            {
                _logger.Trace("[FUZZY-NORMALIZE] After shared normalization: '{0}'", text);
            }

            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.Trace("[FUZZY-NORMALIZE] Final normalized text: '{0}' (took {1:F2}ms)", text, elapsed);

            return text;
        }

        public string NormalizeNarrator(string narrator)
        {
            _logger.Trace("[FUZZY-NARRATOR] Normalizing narrator: '{0}'", narrator ?? "null");

            if (string.IsNullOrWhiteSpace(narrator))
            {
                _logger.Trace("[FUZZY-NARRATOR] Narrator is empty, returning empty string");
                return string.Empty;
            }

            var original = narrator;

            // Remove common prefixes
            narrator = Regex.Replace(narrator, @"^(read by|narrated by|narrator:?)\s*", "",
                RegexOptions.IgnoreCase);
            if (narrator != original)
            {
                _logger.Trace("[FUZZY-NARRATOR] Removed prefix, now: '{0}'", narrator);
                original = narrator;
            }

            // Remove "full cast" variations
            narrator = Regex.Replace(narrator, @"\s*(full cast|cast recording|ensemble cast).*$", "",
                RegexOptions.IgnoreCase);
            if (narrator != original)
            {
                _logger.Trace("[FUZZY-NARRATOR] Removed cast variation, now: '{0}'", narrator);
            }

            // Apply standard normalization
            var normalized = NormalizeForMatching(narrator);
            _logger.Trace("[FUZZY-NARRATOR] Final normalized narrator: '{0}'", normalized);

            return normalized;
        }

        public double CalculateSimilarityScore(string s1, string s2)
        {
            var startTime = DateTime.UtcNow;
            _logger.Debug("[FUZZY-SCORE] === Starting similarity calculation ===");
            _logger.Debug("[FUZZY-SCORE] S1: '{0}'", s1?.Substring(0, Math.Min(s1?.Length ?? 0, 100)) ?? "null");
            _logger.Debug("[FUZZY-SCORE] S2: '{0}'", s2?.Substring(0, Math.Min(s2?.Length ?? 0, 100)) ?? "null");

            // Combine multiple similarity metrics for best results
            _logger.Debug("[FUZZY-SCORE] Calculating trigram similarity...");
            var trigramSim = CalculateTrigramSimilarity(s1, s2);

            // Normalized Levenshtein distance (0-1 scale)
            _logger.Debug("[FUZZY-SCORE] Calculating Levenshtein distance...");
            var maxLen = Math.Max(s1?.Length ?? 0, s2?.Length ?? 0);
            var distance = CalculateLevenshteinDistance(s1, s2);
            var levenshteinSim = maxLen > 0
                ? 1.0 - (double)distance / maxLen
                : 0.0;

            _logger.Debug("[FUZZY-SCORE] Levenshtein distance: {0}, max length: {1}, similarity: {2:F4}",
                distance, maxLen, levenshteinSim);

            // Weight trigram similarity more heavily
            var finalScore = (trigramSim * 0.7) + (levenshteinSim * 0.3);

            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.Debug("[FUZZY-SCORE] === Final similarity score: {0:F4} === (trigram: {1:F4}, levenshtein: {2:F4}, took {3:F2}ms)",
                finalScore, trigramSim, levenshteinSim, elapsed);

            return finalScore;
        }

    }
}
