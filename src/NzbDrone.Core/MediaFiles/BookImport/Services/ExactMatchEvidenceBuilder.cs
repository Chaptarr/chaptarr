using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;

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

        internal static ExactMatchEvidence Build(
            string authorName,
            string bookTitle,
            Edition edition,
            IDictionary<string, List<string>> tags,
            IContainmentValidator containmentValidator)
        {
            var authorTags = BuildAuthorEvidenceTags(authorName, tags, containmentValidator, new[] { edition?.Title, bookTitle });
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
            IContainmentValidator containmentValidator,
            IEnumerable<string> knownTitles = null)
        {
            var evidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(authorName) || tags == null || tags.Count == 0 || containmentValidator == null)
            {
                return evidence;
            }

            var titles = knownTitles?.Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

                    var hasAuthor = titles?.Count > 0
                        ? containmentValidator.ValidateAuthorInTags(authorName, singleField, titles)
                        : containmentValidator.ValidateAuthorInTags(authorName, singleField);
                    if (!hasAuthor)
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
                ? containmentValidator.GetEditionTitleEvidence(edition.Title, tags, knownNarrators: ReleaseNarratorEvidenceExtractor.GetKnownNarratorNames(edition))
                : Array.Empty<EditionTitleEvidence>();

            if ((evidence == null || evidence.Count == 0) && !string.IsNullOrWhiteSpace(bookTitle))
            {
                evidence = containmentValidator.GetEditionTitleEvidence(bookTitle, tags, knownNarrators: ReleaseNarratorEvidenceExtractor.GetKnownNarratorNames(edition));
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

            foreach (var narrator in narratorCandidates)
            {
                foreach (var field in FindNarratorEvidenceFields(narrator, authorName, tags))
                {
                    if (!evidence.TryGetValue(field.Key, out var values))
                    {
                        values = new List<string>();
                        evidence[field.Key] = values;
                    }

                    if (!values.Contains(field.Value, StringComparer.OrdinalIgnoreCase))
                    {
                        values.Add(field.Value);
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
                .Select(n => n?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<KeyValuePair<string, string>> FindNarratorEvidenceFields(
            string narratorRaw,
            string authorName,
            IDictionary<string, List<string>> tags,
            bool applySelfNarrationGate = true)
        {
            var evidence = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(narratorRaw) || tags == null || tags.Count == 0)
            {
                return evidence;
            }

            const int maxValueLength = 400;
            var narrator = UnicodeComparisonNormalizer.NormalizeWords(narratorRaw);
            if (string.IsNullOrWhiteSpace(narrator))
            {
                return evidence;
            }

            var selfNarrated = AuthorNameMatcher.ContainsAuthorName(authorName, narratorRaw);

            foreach (var kvp in tags)
            {
                if (TagExclusionPolicy.IsExcludedFromMatching(kvp.Key) || kvp.Value == null || kvp.Value.Count == 0)
                {
                    continue;
                }

                foreach (var value in kvp.Value.Where(v => !string.IsNullOrWhiteSpace(v) && v.Length <= maxValueLength))
                {
                    if (!ReleaseNarratorEvidenceExtractor.ContainsCompleteCredit(narratorRaw, value))
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

            if (!applySelfNarrationGate || !selfNarrated)
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

    }
}
