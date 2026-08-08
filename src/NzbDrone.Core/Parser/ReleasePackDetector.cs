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
