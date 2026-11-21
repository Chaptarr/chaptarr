using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Parser
{
    public enum ReleasePackDetectionVerdict
    {
        None = 0,
        SingleBookSplitRelease = 1,
        MultipleBooks = 2,
        AudiobookFragment = 3
    }

    public sealed class ReleasePackDetection
    {
        public ReleasePackDetectionVerdict Verdict { get; set; }
        public string PackType { get; set; }
        public string MatchedValue { get; set; }

        public static ReleasePackDetection None()
        {
            return new ReleasePackDetection { Verdict = ReleasePackDetectionVerdict.None };
        }
    }

    public static class ReleasePackDetector
    {
        private static readonly Regex BookRangeRegex = new Regex(@"\b(?:series\s*[-:]?\s*)?(?:books?|bks?|vol(?:ume)?s?)\s*#?\d{1,3}\s*(?:[-–—]|to|through|thru)\s*#?\d{1,3}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BookAndRegex = new Regex(@"\b(?:books?|bks?)\s*#?\d{1,3}\s*(?:and|&)\s*#?\d{1,3}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BracketRangeRegex = new Regex(@"\[[^\]]*\b\d{1,3}\s*[-–—]\s*\d{1,3}\b[^\]]*\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NamedPackRegex = new Regex(@"\b(?:trilogy|tetralogy|quadrilogy|pentalogy|quintilogy|hexalogy|heptalogy|octology|omnibus|discography|discografia)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CompletePackRegex = new Regex(@"\b(?:complete|full)\b.{0,80}\b(?:series|collection|saga|set|works|anthology|trilogy|tetralogy)\b|\b(?:box(?:ed)?\s+set|audio\s+collection|series\s+collection)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SingleBookSplitRegex = new Regex(@"\(\s*\d{1,3}\s+of\s+\d{1,3}\s*\)|\b(?:part|pt)\s*#?\d{1,3}(?:\s*(?:of|/)\s*\d{1,3})?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AudiobookFragmentRegex = new Regex(@"\b(?:cd|disc|disk|track|trk)\s*#?\d{1,3}(?:\s*(?:of|/)\s*\d{1,3})?\b(?!\s*(?:k|kbps|mb|gb)\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Scene names use "." and "_" where the pack patterns expect whitespace, so "Books.1-6" slips
        // past a rule that catches "Books 1-6". Only separators that are NOT between two digits are
        // rewritten, which leaves decimal positions and dotted version tags intact.
        private static readonly Regex SceneSeparatorRegex = new Regex(@"(?<!\d)[._]|[._](?!\d)", RegexOptions.Compiled);

        internal static string NormalizeSceneSeparators(string releaseTitle)
        {
            return string.IsNullOrWhiteSpace(releaseTitle) ? releaseTitle : SceneSeparatorRegex.Replace(releaseTitle, " ");
        }

        public static ReleasePackDetection Detect(string releaseTitle, Book targetBook)
        {
            releaseTitle = NormalizeSceneSeparators(TitleBracketCatalog.NormalizeForParsing(releaseTitle));

            if (string.IsNullOrWhiteSpace(releaseTitle))
            {
                return ReleasePackDetection.None();
            }

            var explicitPack = DetectExplicitMultiBookPack(releaseTitle, targetBook);
            if (explicitPack != null)
            {
                return explicitPack;
            }

            var splitMatch = SingleBookSplitRegex.Match(releaseTitle);
            if (splitMatch.Success)
            {
                return new ReleasePackDetection
                {
                    Verdict = ReleasePackDetectionVerdict.SingleBookSplitRelease,
                    PackType = "single-book-split",
                    MatchedValue = splitMatch.Value
                };
            }

            var fragmentMatch = AudiobookFragmentRegex.Match(releaseTitle);
            if (fragmentMatch.Success)
            {
                return new ReleasePackDetection
                {
                    Verdict = ReleasePackDetectionVerdict.AudiobookFragment,
                    PackType = "audiobook-fragment",
                    MatchedValue = fragmentMatch.Value
                };
            }

            return ReleasePackDetection.None();
        }

        public static ReleasePackDetection Detect(ReleaseInfo release, Book targetBook)
        {
            var structuredPack = DetectStructuredSeriesPack(release?.SeriesInfo, targetBook);
            return structuredPack ?? Detect(release?.Title, targetBook);
        }

        private static ReleasePackDetection DetectStructuredSeriesPack(IEnumerable<ReleaseSeriesInfo> seriesInfo, Book targetBook)
        {
            var targetMemberships = GetTargetSeriesMemberships(targetBook).ToList();

            foreach (var series in seriesInfo ?? Enumerable.Empty<ReleaseSeriesInfo>())
            {
                if (string.IsNullOrWhiteSpace(series?.Name) || string.IsNullOrWhiteSpace(series.Position))
                {
                    continue;
                }

                var matchingMemberships = targetMemberships
                    .Where(membership => SeriesNamesMatch(series.Name, membership.Name))
                    .ToList();
                if (matchingMemberships.Count == 0 ||
                    !ReleaseTitleMatchScorer.TryGetCompleteSeriesPositionRanges(series.Position, out var releaseRanges))
                {
                    continue;
                }

                var targetRanges = new List<(decimal Start, decimal End)>();
                var hasCompleteTargetPositions = true;

                foreach (var membership in matchingMemberships)
                {
                    if (string.IsNullOrWhiteSpace(membership.Position))
                    {
                        continue;
                    }

                    if (!ReleaseTitleMatchScorer.TryGetCompleteSeriesPositionRanges(membership.Position, out var membershipRanges))
                    {
                        hasCompleteTargetPositions = false;
                        break;
                    }

                    targetRanges.AddRange(membershipRanges);
                }

                if (!hasCompleteTargetPositions || targetRanges.Count == 0)
                {
                    continue;
                }

                var mergedTargetRanges = MergePositionRanges(targetRanges);
                var mergedReleaseRanges = MergePositionRanges(releaseRanges);

                // Structured series data rejects only when it names the target's series and covers its
                // complete known positions plus additional positions in that same series.
                if (HasAdditionalPositionCoverage(mergedReleaseRanges, mergedTargetRanges))
                {
                    return new ReleasePackDetection
                    {
                        Verdict = ReleasePackDetectionVerdict.MultipleBooks,
                        PackType = "structured-series-range",
                        MatchedValue = $"{series.Name} {series.Position}".Trim()
                    };
                }
            }

            return null;
        }

        private static IEnumerable<(string Name, string Position)> GetTargetSeriesMemberships(Book targetBook)
        {
            if (targetBook == null)
            {
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(targetBook.SeriesName))
            {
                yield return (targetBook.SeriesName, targetBook.SeriesPosition);
            }

            foreach (var link in BookSeries.GetLinks(targetBook))
            {
                var position = link.Position;
                if (string.IsNullOrWhiteSpace(position) && link.SeriesPosition > 0)
                {
                    position = link.SeriesPosition.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                yield return (link.Series.Value.Title, position);
            }
        }

        private static bool SeriesNamesMatch(string left, string right)
        {
            var leftTokens = ReleaseTitleMatchScorer.Tokenize(left);
            var rightTokens = ReleaseTitleMatchScorer.Tokenize(right);

            return leftTokens.Count > 0 && rightTokens.Count > 0 &&
                   ReleaseTitleMatchScorer.FindExactTitleSpans(leftTokens, rightTokens)
                       .Any(span => span.Start == 0 && span.End == leftTokens.Count - 1);
        }

        private static List<(decimal Start, decimal End)> MergePositionRanges(IEnumerable<(decimal Start, decimal End)> ranges)
        {
            var merged = new List<(decimal Start, decimal End)>();

            foreach (var range in ranges.OrderBy(range => range.Start).ThenBy(range => range.End))
            {
                if (merged.Count == 0 || range.Start > merged[merged.Count - 1].End)
                {
                    merged.Add(range);
                    continue;
                }

                var previous = merged[merged.Count - 1];
                merged[merged.Count - 1] = (previous.Start, System.Math.Max(previous.End, range.End));
            }

            return merged;
        }

        private static bool HasAdditionalPositionCoverage(
            IReadOnlyList<(decimal Start, decimal End)> releaseRanges,
            IReadOnlyList<(decimal Start, decimal End)> targetRanges)
        {
            var targetIsCovered = targetRanges.All(targetRange => releaseRanges.Any(releaseRange =>
                releaseRange.Start <= targetRange.Start && releaseRange.End >= targetRange.End));
            var releaseHasAdditionalCoverage = releaseRanges.Any(releaseRange => !targetRanges.Any(targetRange =>
                targetRange.Start <= releaseRange.Start && targetRange.End >= releaseRange.End));

            return targetIsCovered && releaseHasAdditionalCoverage;
        }

        private static ReleasePackDetection DetectExplicitMultiBookPack(string releaseTitle, Book targetBook)
        {
            foreach (var (regex, packType) in new[]
            {
                (BookRangeRegex, "book-range"),
                (BookAndRegex, "book-list"),
                (BracketRangeRegex, "bracketed-range"),
                (NamedPackRegex, "named-pack"),
                (CompletePackRegex, "complete-pack")
            })
            {
                var match = regex.Match(releaseTitle);
                var tokens = ReleaseTitleMatchScorer.Tokenize(match.Value);
                if (match.Success && (regex != BracketRangeRegex ||
                    (ReleaseTitleMatchScorer.FindExactTitleSpans(tokens, ReleaseTitleMatchScorer.Tokenize(targetBook?.SeriesName)).Any() &&
                     !ReleaseTitleMatchScorer.IsNumericMetadataTail(tokens, tokens.Count - 2))))
                {
                    return new ReleasePackDetection
                    {
                        Verdict = ReleasePackDetectionVerdict.MultipleBooks,
                        PackType = packType,
                        MatchedValue = match.Value
                    };
                }
            }

            return null;
        }
    }
}
