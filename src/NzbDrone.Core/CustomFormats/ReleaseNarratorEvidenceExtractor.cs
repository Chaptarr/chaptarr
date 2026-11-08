using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.CustomFormats
{
    internal static class ReleaseNarratorEvidenceExtractor
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly Regex NarratorNote = new Regex(
            TitleBracketCatalog.PairedSegmentPattern, RegexOptions.Compiled, RegexTimeout);

        public static ReleaseNarratorEvidence Extract(CustomFormatInput input, IEnumerable<string> configuredNames = null)
        {
            var known = DistinctNames((input?.PreferredNarratorNames ?? new List<string>()).Concat(configuredNames ?? Enumerable.Empty<string>()))
                .Where(name => !AudioProductionDetector.ContainsIndicator(new[] { name })).ToList();
            var names = AuthorNameMatcher.SplitCredits(input?.Narrator, known.Count > 0 ? known : null)
                .Where(name => !AudioProductionDetector.ContainsIndicator(new[] { name })).ToList();
            var narratorSpans = known.Count > 0 ? ReleaseTitleMatchScorer.TokenizeWithSpans(input?.Narrator ?? string.Empty) : null;
            var fields = known.Count > 0 ? input?.AudioProductionFields?.Select(field =>
                (Text: field, Spans: ReleaseTitleMatchScorer.TokenizeWithSpans(field ?? string.Empty))).ToList() : null;
            foreach (var name in known.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                if (ContainsKnownNarratorName(new[] { name }, input?.Narrator, narratorSpans) ||
                    fields?.Any(field =>
                        AuthorNameMatcher.AuthorNamesExactlyMatch(AuthorNameMatcher.PrepareName(name), AuthorNameMatcher.PrepareName(input?.Author?.Name))
                            ? NarratorNote.Matches(field.Text ?? string.Empty).Any(note => ContainsKnownNarratorName(new[] { name }, note.Groups["label"].Value))
                            : ContainsKnownNarratorName(new[] { name }, field.Text, field.Spans)) == true)
                {
                    if (!names.Any(observed => AuthorNameMatcher.AuthorNamesExactlyMatch(
                        AuthorNameMatcher.PrepareName(observed), AuthorNameMatcher.PrepareName(name))))
                    {
                        names.Add(name);
                    }
                }
            }

            return new ReleaseNarratorEvidence { Names = DistinctNames(names) };
        }

        public static NarratorNameExtraction ExtractNames(IEnumerable<string> narratorNames, string narratorText)
        {
            var structured = DistinctNames((narratorNames ?? Enumerable.Empty<string>()).SelectMany(SplitCredits));
            var names = structured.Count > 0 ? structured : DistinctNames(SplitCredits(narratorText));
            var complete = structured.Count > 0 &&
                (string.IsNullOrWhiteSpace(narratorText) || MatchesCast(structured, narratorText, requireAllMembers: false));
            return new NarratorNameExtraction
            {
                Names = names,
                TotalCountHint = names.Count + (complete || names.Count == 0 ? 0 : 1)
            };
        }

        internal static IEnumerable<string> SplitCredits(string value) => AuthorNameMatcher.SplitCredits(value);

        internal static string BuildNarratorIdentityKey(string narratorNames) => string.Join("|",
            SplitCredits(narratorNames).Select(UnicodeComparisonNormalizer.NormalizeWords)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal));

        internal static bool TryGetCompleteCast(Edition edition, out IReadOnlyList<string> names, IEnumerable<string> knownCast = null)
        {
            var structured = DistinctNames(edition?.NarratorCredits?.SelectMany(credit => SplitCredits(credit?.Name)));
            if (structured.Count == 0)
            {
                structured = DistinctNames(edition?.NarratorNames?.SelectMany(SplitCredits));
            }

            var hasStructuredCast = structured.Count > 0;
            if (!hasStructuredCast)
            {
                structured = DistinctNames(knownCast);
            }

            names = Array.Empty<string>();
            if (structured.Count == 0 ||
                (string.IsNullOrWhiteSpace(edition?.Narrator) && !hasStructuredCast) ||
                (!string.IsNullOrWhiteSpace(edition?.Narrator) && !MatchesCast(structured, edition.Narrator, requireAllMembers: !hasStructuredCast)))
            {
                return false;
            }

            names = structured;
            return true;
        }

        internal static IEnumerable<string> GetKnownNarratorNames(Edition edition)
        {
            var structured = edition?.NarratorCredits?.Select(credit => credit?.Name)
                .Concat(edition?.NarratorNames ?? Enumerable.Empty<string>()) ?? edition?.NarratorNames;
            var names = DistinctNames(structured?.SelectMany(SplitCredits));
            return names.Count > 0 ? names : SplitCredits(edition?.Narrator);
        }

        internal static bool ContainsKnownNarratorName(IEnumerable<string> knownNames, string text) =>
            !string.IsNullOrWhiteSpace(text) &&
            ContainsKnownNarratorName(knownNames, text, ReleaseTitleMatchScorer.TokenizeWithSpans(text));

        private static bool ContainsKnownNarratorName(IEnumerable<string> knownNames, string text, IReadOnlyList<TitleTokenSpan> spans)
        {
            if (spans?.Count > 0 != true)
            {
                return false;
            }
            foreach (var name in (knownNames ?? Enumerable.Empty<string>()).Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                var expected = AuthorNameMatcher.PrepareName(name);
                for (var start = 0; start + expected.Tokens.Count <= spans.Count && expected.Tokens.Count > 0; start++)
                {
                    var candidate = AuthorNameMatcher.PrepareName(text, spans, start, start + expected.Tokens.Count - 1);
                    if (AuthorNameMatcher.AuthorNamesExactlyMatch(expected, candidate) &&
                        AuthorNameMatcher.ContainsAuthorName(name, text))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static string RemoveKnownNarratorNotes(string title, IEnumerable<string> knownNames)
        {
            var names = (knownNames ?? Enumerable.Empty<string>()).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            return NarratorNote.Replace(title ?? string.Empty, match =>
                ContainsKnownNarratorName(names, match.Groups["label"].Value) ? " " : match.Value).Trim();
        }

        private static bool MatchesCast(IReadOnlyList<string> names, string text, bool requireAllMembers)
        {
            var available = names.ToList();
            foreach (var part in AuthorNameMatcher.SplitCredits(text, names))
            {
                var candidate = AuthorNameMatcher.PrepareName(part);
                var index = available.FindIndex(name =>
                    AuthorNameMatcher.AuthorNamesExactlyMatch(AuthorNameMatcher.PrepareName(name), candidate));
                if (index < 0)
                {
                    return false;
                }

                available.RemoveAt(index);
            }

            return !requireAllMembers || available.Count == 0;
        }

        private static List<string> DistinctNames(IEnumerable<string> names) => (names ?? Enumerable.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
            .Distinct(NzbDrone.Core.Parser.Parser.NarratorNameComparer).ToList();

        internal static bool ContainsCompleteCredit(string requiredCredit, string fieldValue)
        {
            var names = SplitCredits(requiredCredit).ToList();
            if ((names.Count == 1 && AuthorNameMatcher.ContainsAuthorName(requiredCredit, fieldValue)) ||
                (names.Count > 1 && ContainsKnownNarratorName(new[] { requiredCredit }, fieldValue)))
            {
                return true;
            }

            var available = SplitCredits(fieldValue).ToList();
            if (names.Count == 0 || names.Count > available.Count)
            {
                return false;
            }

            // Consume each observed credit once so one compatible person cannot prove a whole cast.
            foreach (var name in names.OrderByDescending(name => UnicodeComparisonNormalizer.WordSpans(UnicodeComparisonNormalizer.NormalizeWords(name)).Count()))
            {
                var candidate = available.FirstOrDefault(candidate => AuthorNameMatcher.AuthorNamesMatch(name, candidate));
                if (candidate == null)
                {
                    return false;
                }

                available.Remove(candidate);
            }

            return true;
        }


    }

    internal sealed class ReleaseNarratorEvidence
    {
        public List<string> Names { get; init; } = new List<string>();
    }

    internal sealed class NarratorNameExtraction
    {
        public List<string> Names { get; init; } = new List<string>();
        public int TotalCountHint { get; init; }
    }
}
