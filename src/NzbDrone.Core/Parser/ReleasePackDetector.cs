using System.Collections.Generic;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;

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

        // Which piece of a single book that was published as several releases this is, when the
        // title says so. Null when the title carries no (or an inconsistent) part marker.
        public SplitPart SplitPart { get; set; }

        public static ReleasePackDetection None()
        {
            return new ReleasePackDetection { Verdict = ReleasePackDetectionVerdict.None };
        }
    }

    public sealed class SplitPart
    {
        // 1-based position of this release within the split book.
        public int Index { get; set; }

        // Total number of releases the book was split into, or 0 when the title gives no total.
        public int Count { get; set; }
    }

    public static class ReleasePackDetector
    {
        private const string NumberWord = "one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve";

        // Marker forms: "(1 of 2)", "[1 of 2]", "Part 2", "Pt. 2/3", "Part Two of Three".
        // A spelled-out number is only a part marker when it is paired with a total, because a bare
        // "Part Two" is as likely to be a book title as a release split.
        private static readonly Regex SplitPartRegex = new Regex(
            @"[\(\[]\s*(?<bi>\d{1,3})\s+of\s+(?<bc>\d{1,3})\s*[\)\]]" +
            @"|\b(?:part|pt|prt)\b\.?\s*#?\s*(?<pi>\d{1,3})(?:\s*(?:of|/)\s*(?<pc>\d{1,3}))?(?!\d)" +
            @"|\b(?:part|pt|prt)\b\.?\s*(?<wi>" + NumberWord + @")\s+of\s+(?<wc>" + NumberWord + @"|\d{1,3})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        private static readonly Regex AudiobookFragmentRegex = new Regex(@"\b(?:cd|disc|disk|track|trk)\s*#?\d{1,3}(?:\s*(?:of|/)\s*\d{1,3})?\b(?!\s*(?:k|kbps|mb|gb)\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ReleasePackDetection Detect(string releaseTitle, Book targetBook, IEnumerable<Book> authorCatalog)
        {
            if (string.IsNullOrWhiteSpace(releaseTitle))
            {
                return ReleasePackDetection.None();
            }

            var explicitPack = DetectExplicitMultiBookPack(releaseTitle);
            if (explicitPack != null)
            {
                return explicitPack;
            }

            var splitMatch = SplitPartRegex.Match(releaseTitle);
            if (splitMatch.Success)
            {
                return new ReleasePackDetection
                {
                    Verdict = ReleasePackDetectionVerdict.SingleBookSplitRelease,
                    PackType = "single-book-split",
                    MatchedValue = splitMatch.Value,
                    SplitPart = ReadSplitPart(splitMatch)
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

        /// <summary>
        /// Reads the part marker out of a release title, or null when there is none.
        /// </summary>
        public static SplitPart ParseSplitPart(string releaseTitle)
        {
            if (string.IsNullOrWhiteSpace(releaseTitle) || DetectExplicitMultiBookPack(releaseTitle) != null)
            {
                return null;
            }

            var match = SplitPartRegex.Match(releaseTitle);
            return match.Success ? ReadSplitPart(match) : null;
        }

        private static SplitPart ReadSplitPart(Match match)
        {
            var index = ReadNumber(match, "bi", "pi", "wi");
            var count = ReadNumber(match, "bc", "pc", "wc");

            if (index <= 0 || count < 0 || (count > 0 && index > count))
            {
                // Marker present (so the release is still a split piece) but not usable as a position.
                return null;
            }

            return new SplitPart { Index = index, Count = count };
        }

        private static int ReadNumber(Match match, params string[] groups)
        {
            foreach (var name in groups)
            {
                var group = match.Groups[name];
                if (!group.Success)
                {
                    continue;
                }

                if (int.TryParse(group.Value, out var number))
                {
                    return number;
                }

                var word = System.Array.IndexOf(NumberWord.Split('|'), group.Value.ToLowerInvariant());
                if (word >= 0)
                {
                    return word + 1;
                }
            }

            return 0;
        }

        private static ReleasePackDetection DetectExplicitMultiBookPack(string releaseTitle)
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
                if (match.Success)
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
