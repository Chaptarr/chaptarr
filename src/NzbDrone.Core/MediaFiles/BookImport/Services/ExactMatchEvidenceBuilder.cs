using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    internal sealed class ExactMatchEvidence
    {
        public Dictionary<string, List<string>> AuthorTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> BookTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> NarratorTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> CombinedTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal static class ExactMatchEvidenceBuilder
    {
        private static readonly Regex ProofTokenRegex = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

        internal static ExactMatchEvidence Build(
            string authorName,
            string bookTitle,
            Edition edition,
            IDictionary<string, List<string>> tags,
            IContainmentValidator containmentValidator)
        {
            var authorTags = BuildAuthorEvidenceTags(authorName, tags, containmentValidator);
            var bookTags = BuildBookEvidenceTags(edition, bookTitle, tags, containmentValidator);
            var narratorTags = edition != null
                ? BuildNarratorEvidenceTags(edition, authorName, tags)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            return new ExactMatchEvidence
            {
                AuthorTags = authorTags,
                BookTags = bookTags,
                NarratorTags = narratorTags,
                CombinedTags = MergeTagSets(authorTags, bookTags, narratorTags)
            };
        }

        internal static Dictionary<string, List<string>> BuildAuthorEvidenceTags(
            string authorName,
            IDictionary<string, List<string>> tags,
            IContainmentValidator containmentValidator)
        {
            var evidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(authorName) || tags == null || tags.Count == 0 || containmentValidator == null)
            {
                return evidence;
            }

            foreach (var kvp in tags)
            {
                if (TagExclusionPolicy.IsExcludedFromMatching(kvp.Key) || kvp.Value == null || kvp.Value.Count == 0)
                {
                    continue;
                }

                foreach (var value in kvp.Value.Where(v => !string.IsNullOrWhiteSpace(v)))
                {
                    var singleField = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [kvp.Key] = new List<string> { value }
                    };

                    if (!containmentValidator.ValidateAuthorInTags(authorName, singleField))
                    {
                        continue;
                    }

                    if (!evidence.TryGetValue(kvp.Key, out var values))
                    {
                        values = new List<string>();
                        evidence[kvp.Key] = values;
                    }

                    if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        values.Add(value);
                    }
                }
            }

            return evidence;
        }

        internal static Dictionary<string, List<string>> BuildBookEvidenceTags(
            Edition edition,
            string bookTitle,
            IDictionary<string, List<string>> tags,
            IContainmentValidator containmentValidator)
        {
            var evidenceTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (tags == null || tags.Count == 0 || containmentValidator == null)
            {
                return evidenceTags;
            }

            var evidence = !string.IsNullOrWhiteSpace(edition?.Title)
                ? containmentValidator.GetEditionTitleEvidence(edition.Title, tags)
                : Array.Empty<EditionTitleEvidence>();

            if ((evidence == null || evidence.Count == 0) && !string.IsNullOrWhiteSpace(bookTitle))
            {
                evidence = containmentValidator.GetEditionTitleEvidence(bookTitle, tags);
            }

            foreach (var item in (evidence ?? Array.Empty<EditionTitleEvidence>())
                         .Where(x => x != null && !string.IsNullOrWhiteSpace(x.FieldName) && !string.IsNullOrWhiteSpace(x.FieldValue)))
            {
                if (!evidenceTags.TryGetValue(item.FieldName, out var values))
                {
                    values = new List<string>();
                    evidenceTags[item.FieldName] = values;
                }

                if (!values.Contains(item.FieldValue, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(item.FieldValue);
                }
            }

            return evidenceTags;
        }

        internal static Dictionary<string, List<string>> BuildNarratorEvidenceTags(
            Edition edition,
            string authorName,
            IDictionary<string, List<string>> tags)
        {
            var evidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (edition == null || tags == null || tags.Count == 0)
            {
                return evidence;
            }

            var narratorCandidates = GetNarratorCandidates(edition);
            if (narratorCandidates.Count == 0)
            {
                return evidence;
            }

            foreach (var kvp in tags)
            {
                if (TagExclusionPolicy.IsExcludedFromMatching(kvp.Key) || kvp.Value == null || kvp.Value.Count == 0)
                {
                    continue;
                }

                foreach (var value in kvp.Value.Where(v => !string.IsNullOrWhiteSpace(v)))
                {
                    var singleField = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [kvp.Key] = new List<string> { value }
                    };

                    if (!TryFindNarratorEvidence(narratorCandidates, authorName, singleField))
                    {
                        continue;
                    }

                    if (!evidence.TryGetValue(kvp.Key, out var values))
                    {
                        values = new List<string>();
                        evidence[kvp.Key] = values;
                    }

                    if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        values.Add(value);
                    }
                }
            }

            return evidence;
        }

        internal static Dictionary<string, List<string>> MergeTagSets(params IDictionary<string, List<string>>[] tagSets)
        {
            var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var tagSet in tagSets.Where(set => set != null))
            {
                foreach (var kvp in tagSet)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null || kvp.Value.Count == 0)
                    {
                        continue;
                    }

                    if (!merged.TryGetValue(kvp.Key, out var values))
                    {
                        values = new List<string>();
                        merged[kvp.Key] = values;
                    }

                    foreach (var value in kvp.Value.Where(v => !string.IsNullOrWhiteSpace(v)))
                    {
                        if (!values.Any(existing => string.Equals(existing, value, StringComparison.Ordinal)))
                        {
                            values.Add(value);
                        }
                    }
                }
            }

            return merged;
        }

        internal static IReadOnlyList<string> GetNarratorCandidates(Edition edition)
        {
            var output = new List<string>();

            if (edition?.NarratorNames != null && edition.NarratorNames.Any())
            {
                output.AddRange(edition.NarratorNames);
            }

            if (!string.IsNullOrWhiteSpace(edition?.Narrator))
            {
                output.Add(edition.Narrator);
            }

            return output
                .SelectMany(ExpandNarratorVariants)
                .Select(n => n?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> ExpandNarratorVariants(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield break;
            }

            yield return raw;

            if (!raw.Contains(','))
            {
                yield break;
            }

            var parts = raw.Split(',')
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();

            if (parts.Length >= 2)
            {
                yield return string.Join(" ", parts.Skip(1).Concat(parts.Take(1)));
            }
        }

        private static bool TryFindNarratorEvidence(
            IReadOnlyList<string> narratorCandidates,
            string authorName,
            IDictionary<string, List<string>> tags)
        {
            foreach (var narrator in narratorCandidates.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                if (FindNarratorEvidenceFields(narrator, authorName, tags).Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<KeyValuePair<string, string>> FindNarratorEvidenceFields(
            string narratorRaw,
            string authorName,
            IDictionary<string, List<string>> tags)
        {
            var evidence = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(narratorRaw) || tags == null || tags.Count == 0)
            {
                return evidence;
            }

            const int maxValueLength = 400;
            var narrator = NormalizePersonNameForMatch(narratorRaw);
            if (string.IsNullOrWhiteSpace(narrator))
            {
                return evidence;
            }

            var narratorNoSpace = narrator.Replace(" ", string.Empty);
            var narratorWords = narrator.Split(' ').Where(word => word.Length > 1).ToList();
            var normalizedAuthor = NormalizePersonNameForMatch(authorName);
            var selfNarrated = !string.IsNullOrWhiteSpace(normalizedAuthor) &&
                               (string.Equals(narrator, normalizedAuthor, StringComparison.Ordinal) ||
                                IsAuthorAsNarrator(narratorRaw, authorName));

            foreach (var kvp in tags)
            {
                if (TagExclusionPolicy.IsExcludedFromMatching(kvp.Key) || kvp.Value == null || kvp.Value.Count == 0)
                {
                    continue;
                }

                foreach (var value in kvp.Value.Where(v => !string.IsNullOrWhiteSpace(v) && v.Length <= maxValueLength))
                {
                    var normalizedValue = NormalizePersonNameForMatch(value);
                    if (string.IsNullOrWhiteSpace(normalizedValue))
                    {
                        continue;
                    }

                    var normalizedValueNoSpace = normalizedValue.Replace(" ", string.Empty);
                    var matchesNarrator =
                        normalizedValueNoSpace.Contains(narratorNoSpace, StringComparison.Ordinal) ||
                        (narratorWords.Count >= 2 && narratorWords.All(word => normalizedValueNoSpace.Contains(word, StringComparison.Ordinal)));

                    if (!matchesNarrator)
                    {
                        continue;
                    }

                    if (!evidence.Any(existing =>
                            string.Equals(existing.Key, kvp.Key, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existing.Value, value, StringComparison.Ordinal)))
                    {
                        evidence.Add(new KeyValuePair<string, string>(kvp.Key, value));
                    }
                }
            }

            if (!selfNarrated)
            {
                return evidence;
            }

            var distinctFieldCount = evidence
                .Select(item => item.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return distinctFieldCount >= 2
                ? evidence
                : new List<KeyValuePair<string, string>>();
        }

        private static string NormalizePersonNameForMatch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim().ToLowerInvariant();
            var builder = new StringBuilder(trimmed.Length);
            foreach (var ch in trimmed)
            {
                builder.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            }

            return Regex.Replace(builder.ToString(), "\\s+", " ").Trim();
        }

        private static bool IsAuthorAsNarrator(string narrator, string authorName)
        {
            if (string.IsNullOrWhiteSpace(narrator) || string.IsNullOrWhiteSpace(authorName))
            {
                return false;
            }

            var normalizedNarrator = NormalizePersonNameForMatch(narrator);
            var normalizedAuthor = NormalizePersonNameForMatch(authorName);
            if (!string.IsNullOrWhiteSpace(normalizedNarrator) &&
                string.Equals(normalizedNarrator, normalizedAuthor, StringComparison.Ordinal))
            {
                return true;
            }

            var narratorTokens = TokenizeNameTokens(narrator)
                .Where(token => token.Length > 1)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var authorTokens = TokenizeNameTokens(authorName)
                .Where(token => token.Length > 1)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (narratorTokens.Count == 0 || authorTokens.Count == 0)
            {
                return false;
            }

            var overlap = authorTokens.Count(token => narratorTokens.Contains(token));
            if (authorTokens.Count == 1)
            {
                return overlap == 1;
            }

            var required = authorTokens.Count == 2 ? 2 : Math.Max(2, (int)Math.Ceiling(authorTokens.Count * 0.6));
            return overlap >= required;
        }

        private static IEnumerable<string> TokenizeNameTokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Enumerable.Empty<string>();
            }

            // Match TokenizeForLeftoverGate/TokenizeText behavior from FileMatchingService:
            // 1. Normalize possessives ('s → s) before tokenizing
            value = Regex.Replace(value, "['\\u2018\\u2019]s\\b", "s");

            // 2. Strip diacritics (NFD decomposition + remove combining marks)
            value = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }

            value = sb.ToString();

            // 3. Replace dashes and dots with spaces (same as TokenizeForLeftoverGate)
            value = Regex.Replace(value, @"[–—-]", " ");
            value = value.Replace('.', ' ');

            return ProofTokenRegex.Matches(value.ToLowerInvariant())
                .Cast<Match>()
                .Select(match => match.Value);
        }
    }
}
