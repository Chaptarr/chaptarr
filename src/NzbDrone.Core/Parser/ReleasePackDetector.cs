using System;
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

        /// <summary>
        /// For a <see cref="ReleasePackDetectionVerdict.SingleBookSplitRelease"/>, which part this
        /// release is ("Part 2 of 2"). Null when the numbers are missing or implausible.
        /// </summary>
        public ReleaseSplitPart SplitPart { get; set; }

        public static ReleasePackDetection None()
        {
            return new ReleasePackDetection { Verdict = ReleasePackDetectionVerdict.None };
        }
    }

    /// <summary>
    /// One part of a book that was published as several separate releases, such as
    /// GraphicAudio's "Fourth Wing (Part 1 of 2)" / "Fourth Wing (Part 2 of 2)".
    /// Each part is its own download; together they make up the whole book.
    /// </summary>
    public sealed class ReleaseSplitPart : IEquatable<ReleaseSplitPart>
    {
        public ReleaseSplitPart(int part, int count)
        {
            Part = part;
            Count = count;
        }

        public int Part { get; }

        /// <summary>Total number of parts, or 0 when the release only says "Part N".</summary>
        public int Count { get; }

        /// <summary>
        /// True when both describe parts of the same split, but not the same part. A part
        /// without a known total is treated as compatible with any total.
        /// </summary>
        public bool IsSiblingOf(ReleaseSplitPart other)
        {
            return other != null &&
                   Part != other.Part &&
                   (Count == 0 || other.Count == 0 || Count == other.Count);
        }

        public bool Equals(ReleaseSplitPart other) => other != null && Part == other.Part && Count == other.Count;

        public override bool Equals(object obj) => Equals(obj as ReleaseSplitPart);

        public override int GetHashCode() => HashCode.Combine(Part, Count);

        public override string ToString() => Count > 0 ? $"Part {Part} of {Count}" : $"Part {Part}";
    }

    public static class ReleasePackDetector
    {
        private const int MaxSplitParts = 20;

        private const string SplitNumber = @"\d{1,3}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve";

        private static readonly Dictionary<string, int> SplitNumberWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "one", 1 }, { "two", 2 }, { "three", 3 }, { "four", 4 }, { "five", 5 }, { "six", 6 },
            { "seven", 7 }, { "eight", 8 }, { "nine", 9 }, { "ten", 10 }, { "eleven", 11 }, { "twelve", 12 }
        };

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

        // "(1 of 2)" / "[1 of 2]", or "Part 1", "Pt. 1 of 2", "Part 1/2", "Part One of Two". A bare
        // "N of M" only counts when bracketed, so "Book 1 of 3" or "Disc 2 of 10" is never a part.
        private static readonly Regex SingleBookSplitRegex = new Regex(
            $@"[\(\[]\s*(?<part>{SplitNumber})\s+of\s+(?<count>{SplitNumber})\s*[\)\]]|\b(?<label>part|pt)\.?\s*#?(?<part>{SplitNumber})(?:\s*(?:of|/)\s*(?<count>{SplitNumber}))?\b",
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

            var splitMatch = SingleBookSplitRegex.Match(releaseTitle);
            if (splitMatch.Success)
            {
                return new ReleasePackDetection
                {
                    Verdict = ReleasePackDetectionVerdict.SingleBookSplitRelease,
                    PackType = "single-book-split",
                    MatchedValue = splitMatch.Value,
                    SplitPart = ToSplitPart(splitMatch)
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
        /// Which part of a split release a title, folder or file name names, using the same rule
        /// <see cref="Detect"/> applies to release titles. Null when it names no part.
        /// </summary>
        /// <param name="text">The release title, folder name or file name (without extension).</param>
        /// <param name="fileName">
        /// The text is a single file's name. Only an explicit "Part N of M" counts then: inside a
        /// multi-file release, "(3 of 12)" or "Part 3" number tracks, not separate releases.
        /// </param>
        public static ReleaseSplitPart DetectSplitPart(string text, bool fileName = false)
        {
            if (string.IsNullOrWhiteSpace(text) || DetectExplicitMultiBookPack(text) != null)
            {
                return null;
            }

            foreach (Match match in SingleBookSplitRegex.Matches(text))
            {
                if (!fileName || (match.Groups["label"].Success && match.Groups["count"].Success))
                {
                    return ToSplitPart(match);
                }
            }

            return null;
        }

        private static ReleaseSplitPart ToSplitPart(Match match)
        {
            var part = ToSplitNumber(match.Groups["part"].Value);

            if (!match.Groups["count"].Success)
            {
                return part >= 1 && part <= MaxSplitParts ? new ReleaseSplitPart(part, 0) : null;
            }

            var count = ToSplitNumber(match.Groups["count"].Value);

            return part >= 1 && count >= 2 && part <= count && count <= MaxSplitParts
                ? new ReleaseSplitPart(part, count)
                : null;
        }

        private static int ToSplitNumber(string value)
        {
            if (int.TryParse(value, out var number))
            {
                return number;
            }

            return SplitNumberWords.TryGetValue(value, out number) ? number : 0;
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
