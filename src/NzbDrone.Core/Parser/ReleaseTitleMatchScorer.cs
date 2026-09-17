using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Parser
{
    public enum TitleMatchProblemCode
    {
        None = 0,
        NoCandidate = 1,
        PrefixContradiction = 2,
        SiblingTitleContradiction = 3,
        SeriesPositionMismatch = 4,
        SuspiciousAdjacentNumber = 5,
        EmbeddedShortTitle = 6
    }

    public sealed class TitleMatchProblem
    {
        public TitleMatchProblemCode Code { get; set; }
        public string Value { get; set; }
    }

    public sealed class TitleMatchResult
    {
        public Book Book { get; set; }
        public string PrimaryTitle { get; set; }
        public string MatchedVariant { get; set; }
        public string EvidenceTitle { get; set; }
        public int MatchedStart { get; set; } = -1;
        public int MatchedEnd { get; set; } = -1;
        public int MeaningfulLeftoverCount { get; set; }
        public List<string> MeaningfulLeftovers { get; set; } = new List<string>();
        public TitleMatchProblemCode ProblemCode { get; set; }
        public List<TitleMatchProblem> Problems { get; set; } = new List<TitleMatchProblem>();
        public bool IsMatch { get; set; }
    }

    public sealed class TitleTokenSpan
    {
        public string Value { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
    }

    internal sealed class BookTitleMatchContext
    {
        public string PrimaryTitle { get; set; }
        public List<string> FallbackTitles { get; } = new List<string>();
        public string SeriesName { get; set; }
        public string SeriesPosition { get; set; }
        public List<string> PrimaryVariants { get; } = new List<string>();
    }

    internal sealed class ContradictoryVariant
    {
        public string Title { get; set; }
        public IReadOnlyList<string> Tokens { get; set; }
        public bool IsFallback { get; set; }
    }

    public static class ReleaseTitleMatchScorer
    {
        internal sealed class CatalogPreparation
        {
            private readonly Dictionary<Book, IReadOnlyList<ContradictoryVariant>> _variants = new(BookReferenceComparer.Instance);

            private readonly Dictionary<int, List<Book>> _authorCatalogs = new();

            internal List<Book> GetAuthorCatalog(int authorId, IBookService bookService)
            {
                if (!_authorCatalogs.TryGetValue(authorId, out var books))
                {
                    books = bookService.GetBooksByAuthor(authorId, onlyMonitoredEditions: true);
                    _authorCatalogs.Add(authorId, books);
                }

                return books;
            }

            internal IReadOnlyList<ContradictoryVariant> GetVariants(Book book)
            {
                if (!_variants.TryGetValue(book, out var variants))
                {
                    variants = GetContradictoryTitleVariants(book)
                        .Where(variant => !string.IsNullOrWhiteSpace(variant.Title))
                        .DistinctBy(variant => variant.Title, StringComparer.OrdinalIgnoreCase)
                        .Select(variant => new ContradictoryVariant { Title = variant.Title, Tokens = Tokenize(variant.Title), IsFallback = variant.IsFallback })
                        .ToList();
                    _variants.Add(book, variants);
                }

                return variants;
            }
        }

        private const string DecimalPointToken = "chaptarrdecimalpoint";

        // Normalization already classifies characters; rechecking UTF-16 halves would discard supplementary letters.
        private static readonly Regex NumericTokenRegex = new Regex(@"^[0-9]+(?:\.[0-9]+)?$", RegexOptions.Compiled);
        private static readonly Regex TokenRegex = new Regex(@"\S+", RegexOptions.Compiled);
        private static readonly Regex PossessiveRegex = new Regex(@"['\u2018\u2019]s\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DecimalPointBetweenDigitsRegex = new Regex(@"(?<=\p{Nd})\.(?=\p{Nd})", RegexOptions.Compiled);
        private static readonly Regex OptionalQualifierSegmentRegex = new Regex(@"\s*[\(\[（【](?<label>[^\)\]）】]+)[\)\]）】]\s*", RegexOptions.Compiled);
        private static readonly Regex SubtitleLeadingArticleRegex = new Regex(@"(?<prefix>[:;\-\u2013\u2014]\s+)(?<article>a|an|the)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SubtitleArticleInsertionPointRegex = new Regex(@"(?<prefix>[:;\-\u2013\u2014]\s+)(?<head>[\p{L}\p{Nd}])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex LeadingOptionalArticleRegex = new Regex(@"^(?:a|an|the)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex SpaceBeforePunctuationRegex = new Regex(@"\s+([:;,])", RegexOptions.Compiled);
        private static readonly Regex SpaceAfterPunctuationRegex = new Regex(@"([:;,])(?=\S)", RegexOptions.Compiled);
        private static readonly Regex YearTokenRegex = new Regex(@"^(?:18\d{2}|19\d{2}|20\d{2}|21\d{2})$", RegexOptions.Compiled);
        private static readonly Regex CompactMetadataCodeRegex = new Regex(@"^[\p{L}]{1,3}\d{1,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BareSeriesPositionRegex = new Regex(@"^\s*#?\s*(?<number>\d+(?:\.\d+)?)(?:\s*,?\s*(?:part|pt)\s+\d+\s+of\s+\d+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MarkedSeriesPositionRegex = new Regex(@"^\s*(?:book|bk|vol|volume|tome)\s+#?\s*(?<number>\d+(?:\.\d+)?)(?:\s*,?\s*(?:part|pt)\s+\d+\s+of\s+\d+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> KnownLanguageNames = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Select(culture => NormalizeLanguageName(culture.EnglishName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static readonly Logger Logger = LogManager.GetLogger(typeof(ReleaseTitleMatchScorer).FullName);

        private static readonly IContainmentValidator AuthorContainmentValidator =
            new ContainmentValidator(new TagNormalizer(), LogManager.GetLogger(typeof(ReleaseTitleMatchScorer).FullName));

        private static readonly HashSet<string> MetadataTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "or", "the", "of", "in", "on", "at", "by", "for", "with", "to", "from", "as", "is",
            "audiobook", "audio", "ebook", "e", "book", "bk", "part", "pt", "chapter", "chapters", "chap", "vol", "volume", "series", "edition",
            "unabridged", "abridged", "audible", "retail", "graphic", "graphicaudio", "aka", "complete",
            "disc", "cd", "track", "trk",
            "mp3", "m4b", "m4a", "flac", "aac", "opus", "ogg", "wav", "wma", "alac", "aax", "mp4",
            "epub", "mobi", "azw", "azw3", "pdf", "djvu", "cbz", "cbr", "fb2", "lit", "pdb", "txt",
            "language", "lang", "english", "eng",
            "kbps", "bitrate", "vbr", "cbr", "yenc", "nmr", "rnp"
        };

        private static readonly HashSet<string> PartMarkerTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "part", "pt", "chapter", "chapters", "chap", "disc", "cd", "track", "trk"
        };

        private static readonly HashSet<string> SeriesPositionMarkerTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "book", "bk", "volume", "vol", "tome"
        };

        private static readonly HashSet<string> NumericMetadataTailTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kbps", "bitrate", "vbr", "cbr", "k", "mb", "gb", "hours", "hour", "hrs", "hr", "minutes", "minute", "mins", "min"
        };

        private static readonly string[] OptionalSubtitleArticles = { "The", "A", "An" };

        public static TitleMatchResult FindBestMatch(string releaseTitle, string authorName, IEnumerable<Book> candidateBooks)
        {
            return FindBestMatch(releaseTitle, authorName, candidateBooks, null, null);
        }

        public static TitleMatchResult FindBestMatch(string releaseTitle, string authorName, IEnumerable<Book> candidateBooks, string releaseAuthorHint)
        {
            return FindBestMatch(releaseTitle, authorName, candidateBooks, releaseAuthorHint, null);
        }

        public static TitleMatchResult FindBestMatch(string releaseTitle, string authorName, IEnumerable<Book> candidateBooks, string releaseAuthorHint, IEnumerable<Book> authorCatalogBooks)
        {
            return FindBestMatch(releaseTitle, authorName, candidateBooks, releaseAuthorHint, authorCatalogBooks, new CatalogPreparation());
        }

        public static TitleMatchResult FindBestMatch(ReleaseInfo release, string authorName, IEnumerable<Book> candidateBooks, IEnumerable<Book> authorCatalogBooks)
        {
            return FindBestMatch(release, authorName, candidateBooks, release.Author, authorCatalogBooks, new CatalogPreparation());
        }

        internal static TitleMatchResult FindBestMatch(ReleaseInfo release, string authorName, IEnumerable<Book> candidateBooks, string authorHint, IEnumerable<Book> authorCatalogBooks, CatalogPreparation preparation)
        {
            var match = FindBestMatch(release.Title, authorName, candidateBooks, authorHint, authorCatalogBooks, preparation);
            if (!string.IsNullOrWhiteSpace(release.Book) && !string.Equals(release.Book, release.Title, StringComparison.Ordinal))
            {
                var structuredTitleAuthor = authorHint ?? (HasAuthorEvidence(authorName, release.Title, null) ? authorName : null);
                var nameMatched = match?.IsMatch == true;
                match = ChooseBetterResult(match, FindBestMatch(release.Book, authorName, candidateBooks, structuredTitleAuthor, authorCatalogBooks, preparation));

                // Records whether the indexer's structured title, not the release name, decided the match.
                if (Logger.IsTraceEnabled && match != null)
                {
                    Logger.Trace("STRUCTURED_TITLE_EVIDENCE: decidedBy={0}, nameAlone={1}, result={2}, book='{3}', releaseTitle='{4}', structured='{5}'",
                        string.Equals(match.EvidenceTitle, release.Book, StringComparison.Ordinal) ? "structured" : "releaseTitle",
                        nameMatched,
                        match.IsMatch,
                        match.Book?.Title ?? "<null>",
                        release.Title,
                        release.Book);
                }
            }

            return match;
        }

        internal static TitleMatchResult FindBestMatch(string releaseTitle, string authorName, IEnumerable<Book> candidateBooks, string releaseAuthorHint, IEnumerable<Book> authorCatalogBooks, CatalogPreparation preparation)
        {
            if (string.IsNullOrWhiteSpace(releaseTitle) || candidateBooks == null)
            {
                return null;
            }

            var books = candidateBooks.Where(book => book != null).ToList();
            if (books.Count == 0)
            {
                return null;
            }

            var cleanedReleaseTitle = Parser.CleanReleaseTitleForParsing(releaseTitle);
            var releaseTokens = Tokenize(cleanedReleaseTitle);
            if (releaseTokens.Count == 0)
            {
                return null;
            }

            var authorCatalog = (authorCatalogBooks ?? books)
                .Where(book => book != null)
                .ToList();

            var hasAuthorEvidence = HasAuthorEvidence(authorName, cleanedReleaseTitle, releaseAuthorHint);

            TitleMatchResult best = null;

            foreach (var book in books)
            {
                var candidate = ScoreAgainstBook(releaseTokens, hasAuthorEvidence, book, authorCatalog, preparation);
                best = ChooseBetterResult(best, candidate);
            }

            if (best != null)
            {
                best.EvidenceTitle = releaseTitle;
            }

            return best;
        }

        private static TitleMatchResult ScoreAgainstBook(IReadOnlyList<string> releaseTokens, bool hasAuthorEvidence, Book book, IEnumerable<Book> authorCatalogBooks, CatalogPreparation preparation)
        {
            var context = GetBookTitleMatchContext(book);
            if (context.PrimaryVariants.Count == 0)
            {
                return null;
            }

            var titleSpans = context.PrimaryVariants.SelectMany(variant => FindExactTitleSpans(releaseTokens, Tokenize(variant)))
                .Distinct().ToList();
            if (titleSpans.Count == 0)
            {
                var fallbackTitle = context.FallbackTitles
                    .FirstOrDefault(title => FindExactTitleSpans(releaseTokens, Tokenize(title)).Any());
                var baseTokens = Tokenize(fallbackTitle);
                titleSpans = FindExactTitleSpans(releaseTokens, baseTokens).ToList();
                if (titleSpans.Count == 0 ||
                    BuildContradictoryVariants(book, authorCatalogBooks, preparation)
                        .Any(variant => (variant.Tokens.Count == baseTokens.Count && FindExactTitleSpans(variant.Tokens, baseTokens).Any()) ||
                            (!variant.IsFallback && FindExactTitleSpans(releaseTokens, variant.Tokens).Any() &&
                             !FindExactTitleSpans(baseTokens, variant.Tokens).Any())))
                {
                    return null;
                }

                context.PrimaryVariants.Clear();
                context.PrimaryVariants.Add(fallbackTitle);
            }

            var problems = GetProblems(releaseTokens, titleSpans, book?.Author?.Name, context,
                BuildContradictoryVariants(book, authorCatalogBooks, preparation));
            TitleMatchResult best = null;
            foreach (var variant in context.PrimaryVariants)
            {
                best = ChooseBetterResult(best, ScoreAgainstTitleVariant(releaseTokens, hasAuthorEvidence, book, variant, context, problems));
            }

            return best;
        }

        private static TitleMatchResult ScoreAgainstTitleVariant(IReadOnlyList<string> releaseTokens, bool hasAuthorEvidence, Book book, string bookTitleVariant, BookTitleMatchContext context, List<TitleMatchProblem> problems)
        {
            var titleTokens = Tokenize(bookTitleVariant);
            if (titleTokens.Count == 0)
            {
                return null;
            }

            var spans = FindExactTitleSpans(releaseTokens, titleTokens).ToList();
            if (spans.Count == 0)
            {
                return null;
            }

            var primaryTitle = context?.PrimaryTitle ?? GetPrimaryBookTitle(book);
            TitleMatchResult best = null;

            foreach (var span in spans)
            {
                var leftovers = problems.Select(problem => problem.Value).ToList();

                var candidate = new TitleMatchResult
                {
                    Book = book,
                    PrimaryTitle = primaryTitle,
                    MatchedVariant = bookTitleVariant,
                    MatchedStart = span.Start,
                    MatchedEnd = span.End,
                    MeaningfulLeftoverCount = leftovers.Count,
                    MeaningfulLeftovers = leftovers,
                    ProblemCode = ChooseProblemCode(problems),
                    Problems = problems,
                    IsMatch = hasAuthorEvidence && problems.Count == 0
                };

                best = ChooseBetterResult(best, candidate);
            }

            return best;
        }

        private static List<TitleMatchProblem> GetProblems(IReadOnlyList<string> releaseTokens, IReadOnlyList<(int Start, int End)> titleSpans, string authorName, BookTitleMatchContext context, IEnumerable<ContradictoryVariant> contradictoryVariants)
        {
            var problems = new List<TitleMatchProblem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var problem in titleSpans.SelectMany(span => GetAdjacentSeriesNumberProblems(releaseTokens, span.Start, span.End, context)))
            {
                if (seen.Add(problem.Value))
                {
                    problems.Add(problem);
                }
            }

            foreach (var contradiction in contradictoryVariants ?? Array.Empty<ContradictoryVariant>())
            {
                foreach (var span in FindExactTitleSpans(releaseTokens, contradiction.Tokens))
                {
                    if (titleSpans.Any(titleSpan => span.Start >= titleSpan.Start && span.End <= titleSpan.End))
                    {
                        continue;
                    }

                    // A credited author name is not evidence of a different work with that same title.
                    if (FindExactTitleSpans(releaseTokens, Tokenize(authorName))
                        .Any(authorSpan => span.Start >= authorSpan.Start && span.End <= authorSpan.End))
                    {
                        continue;
                    }

                    if (!titleSpans.Any(titleSpan => span.Start <= titleSpan.End + 1 && span.End >= titleSpan.Start - 1) &&
                        !TokenizedEquals(context?.PrimaryTitle, context?.SeriesName))
                    {
                        continue;
                    }

                    if (IsTargetSeriesContext(contradiction.Title, context))
                    {
                        continue;
                    }

                    if (seen.Add(contradiction.Title))
                    {
                        problems.Add(new TitleMatchProblem
                        {
                            Code = TitleMatchProblemCode.SiblingTitleContradiction,
                            Value = contradiction.Title
                        });
                    }

                    break;
                }
            }

            return problems;
        }

        private static bool IsTargetSeriesContext(string title, BookTitleMatchContext context)
        {
            return TokenizedEquals(title, context?.SeriesName);
        }

        private static bool TokenizedEquals(string left, string right)
        {
            var leftTokens = Tokenize(left);
            var rightTokens = Tokenize(right);

            if (leftTokens.Count == 0 || leftTokens.Count != rightTokens.Count)
            {
                return false;
            }

            for (var index = 0; index < leftTokens.Count; index++)
            {
                if (!TokensMatch(leftTokens[index], rightTokens[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static TitleMatchProblemCode ChooseProblemCode(IReadOnlyCollection<TitleMatchProblem> problems)
        {
            if (problems == null || problems.Count == 0)
            {
                return TitleMatchProblemCode.None;
            }

            if (problems.Any(problem => problem.Code == TitleMatchProblemCode.SeriesPositionMismatch))
            {
                return TitleMatchProblemCode.SeriesPositionMismatch;
            }

            if (problems.Any(problem => problem.Code == TitleMatchProblemCode.SiblingTitleContradiction))
            {
                return TitleMatchProblemCode.SiblingTitleContradiction;
            }

            if (problems.Any(problem => problem.Code == TitleMatchProblemCode.SuspiciousAdjacentNumber))
            {
                return TitleMatchProblemCode.SuspiciousAdjacentNumber;
            }

            return problems.First().Code;
        }

        private static IEnumerable<TitleMatchProblem> GetAdjacentSeriesNumberProblems(IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, BookTitleMatchContext context)
        {
            var nextIndex = matchedEnd + 1;
            if (releaseTokens == null || nextIndex >= releaseTokens.Count)
            {
                yield break;
            }

            var nextToken = releaseTokens[nextIndex];
            if (IsPartMarker(nextToken))
            {
                yield break;
            }

            if (IsSeriesPositionMarker(nextToken) && nextIndex + 1 < releaseTokens.Count)
            {
                var markedNumber = releaseTokens[nextIndex + 1];
                var problem = GetAdjacentSeriesNumberProblem(releaseTokens, matchedStart, matchedEnd, nextIndex + 1, $"{nextToken} {markedNumber}", markedNumber, context);
                if (problem != null)
                {
                    yield return problem;
                }

                yield break;
            }

            var adjacentProblem = GetAdjacentSeriesNumberProblem(releaseTokens, matchedStart, matchedEnd, nextIndex, nextToken, nextToken, context);
            if (adjacentProblem != null)
            {
                yield return adjacentProblem;
            }
        }

        private static TitleMatchProblem GetAdjacentSeriesNumberProblem(IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, int numberIndex, string displayValue, string numberToken, BookTitleMatchContext context)
        {
            if (!IsNumericToken(numberToken))
            {
                return null;
            }

            if (YearTokenRegex.IsMatch(numberToken))
            {
                return null;
            }

            if (IsMultipartNumberPattern(releaseTokens, numberIndex) ||
                IsNumericMetadataTail(releaseTokens, numberIndex))
            {
                return null;
            }

            if (SeriesPositionMatches(context, numberToken))
            {
                return null;
            }

            return new TitleMatchProblem
            {
                Code = HasSeriesPositionNumber(context) && MatchedSpanIsTargetSeries(releaseTokens, matchedStart, matchedEnd, context)
                    ? TitleMatchProblemCode.SeriesPositionMismatch
                    : TitleMatchProblemCode.SuspiciousAdjacentNumber,
                Value = displayValue
            };
        }

        private static bool MatchedSpanIsTargetSeries(IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, BookTitleMatchContext context)
        {
            var seriesTokens = Tokenize(context?.SeriesName);
            var matchedTokenCount = matchedEnd - matchedStart + 1;
            if (seriesTokens.Count == 0 || matchedStart < 0 || matchedTokenCount != seriesTokens.Count)
            {
                return false;
            }

            return seriesTokens
                .Select((token, index) => TokensMatch(token, releaseTokens[matchedStart + index]))
                .All(matches => matches);
        }

        private static bool IsMultipartNumberPattern(IReadOnlyList<string> releaseTokens, int numberIndex)
        {
            if (numberIndex > 0 && IsPartMarker(releaseTokens[numberIndex - 1]))
            {
                return true;
            }

            if (numberIndex + 2 < releaseTokens.Count &&
                string.Equals(releaseTokens[numberIndex + 1], "of", StringComparison.OrdinalIgnoreCase) &&
                IsNumericToken(releaseTokens[numberIndex + 2]))
            {
                return true;
            }

            return numberIndex + 1 < releaseTokens.Count &&
                   IsNumericToken(releaseTokens[numberIndex + 1]) &&
                   (numberIndex + 2 == releaseTokens.Count || IsMetadataToken(releaseTokens[numberIndex + 2]));
        }

        internal static bool IsNumericMetadataTail(IReadOnlyList<string> releaseTokens, int numberIndex)
        {
            return numberIndex + 1 < releaseTokens.Count &&
                   NumericMetadataTailTokens.Contains(releaseTokens[numberIndex + 1]);
        }

        private static bool IsNumericToken(string token)
        {
            return TryParseNumberToken(token, out _);
        }

        private static bool SeriesPositionMatches(BookTitleMatchContext context, string numberToken)
        {
            return TryGetSeriesPositionNumber(context?.SeriesPosition, out var expectedNumber) &&
                   TryParseNumberToken(numberToken, out var actualNumber) &&
                   expectedNumber == actualNumber;
        }

        private static bool HasSeriesPositionNumber(BookTitleMatchContext context)
        {
            return TryGetSeriesPositionNumber(context?.SeriesPosition, out _);
        }

        private static bool TryGetSeriesPositionNumber(string seriesPosition, out decimal number)
        {
            number = default;

            if (string.IsNullOrWhiteSpace(seriesPosition))
            {
                return false;
            }

            return SeriesPositionTokenHelper.TryParseNumber(seriesPosition, out number) ||
                   TryParseSeriesPositionPattern(seriesPosition, BareSeriesPositionRegex, out number) ||
                   TryParseSeriesPositionPattern(seriesPosition, MarkedSeriesPositionRegex, out number);
        }

        private static bool TryParseSeriesPositionPattern(string seriesPosition, Regex regex, out decimal number)
        {
            number = default;

            var match = regex.Match(UnicodeComparisonNormalizer.NormalizeText(seriesPosition));
            if (!match.Success)
            {
                return false;
            }

            return TryParseNumberToken(match.Groups["number"].Value, out number);
        }

        private static bool TryParseNumberToken(string token, out decimal number)
        {
            number = default;
            return !string.IsNullOrEmpty(token) && NumericTokenRegex.IsMatch(token) &&
                   decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
        }

        private static bool IsPartMarker(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && PartMarkerTokens.Contains(token);
        }

        private static bool IsSeriesPositionMarker(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && SeriesPositionMarkerTokens.Contains(token);
        }

        private static IEnumerable<ContradictoryVariant> BuildContradictoryVariants(Book targetBook, IEnumerable<Book> authorCatalogBooks, CatalogPreparation preparation)
        {
            foreach (var otherBook in authorCatalogBooks ?? Enumerable.Empty<Book>())
            {
                if (!CanContradict(targetBook, otherBook))
                {
                    continue;
                }

                foreach (var variant in preparation.GetVariants(otherBook))
                {
                    if (variant.Tokens.Count > 0)
                    {
                        yield return variant;
                    }
                }
            }
        }

        private static IEnumerable<(string Title, bool IsFallback)> GetContradictoryTitleVariants(Book book)
        {
            var context = GetBookTitleMatchContext(book);
            return GetSelectedEdition(book) == null ? Enumerable.Empty<(string, bool)>() :
                context.PrimaryVariants.Select(title => (title, false)).Concat(context.FallbackTitles.Select(title => (title, true)));
        }

        private static bool CanContradict(Book targetBook, Book otherBook)
        {
            if (targetBook == null || otherBook == null)
            {
                return false;
            }

            if (ReferenceEquals(targetBook, otherBook))
            {
                return false;
            }

            if (targetBook.Id > 0 && otherBook.Id > 0 && targetBook.Id == otherBook.Id)
            {
                return false;
            }

            if (WorkIdMatcher.WorkIdMatches(targetBook, otherBook))
            {
                return false;
            }

            return true;
        }

        private static bool HasAuthorEvidence(string authorName, string releaseTitle, string releaseAuthorHint)
        {
            if (string.IsNullOrWhiteSpace(authorName))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(releaseAuthorHint))
            {
                return ReleaseIdentityEvidence.StructuredAuthorMatches(new Author { Name = authorName }, releaseAuthorHint);
            }

            var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELEASE_TITLE"] = new List<string> { releaseTitle ?? string.Empty }
            };

            return AuthorContainmentValidator.ValidateAuthorInTags(authorName, fields) ||
                   HasAuthorTokenSpan(authorName, releaseTitle) ||
                   HasAuthorTokenSpan(authorName, releaseAuthorHint);
        }

        private static bool HasAuthorTokenSpan(string authorName, string value)
        {
            var authorTokens = Tokenize(authorName);
            var valueTokens = Tokenize(value);

            if (authorTokens.Count == 0 || valueTokens.Count < authorTokens.Count)
            {
                return false;
            }

            for (var start = 0; start <= valueTokens.Count - authorTokens.Count; start++)
            {
                var matches = true;

                for (var offset = 0; offset < authorTokens.Count; offset++)
                {
                    var valueToken = valueTokens[start + offset];
                    var authorToken = authorTokens[offset];

                    if (!TokensMatch(valueToken, authorToken) &&
                        !(offset == authorTokens.Count - 1 &&
                          string.Equals(valueToken, authorToken + "s", StringComparison.OrdinalIgnoreCase)))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }

            return false;
        }

        public static IEnumerable<(int Start, int End)> FindExactTitleSpans(IReadOnlyList<string> releaseTokens, IReadOnlyList<string> titleTokens)
        {
            if (releaseTokens == null || titleTokens == null || titleTokens.Count == 0 || releaseTokens.Count == 0)
            {
                yield break;
            }

            for (var start = 0; start < releaseTokens.Count; start++)
            {
                if (TryMatchTitleSpan(releaseTokens, titleTokens, start, 0, out var end))
                {
                    yield return (start, end);
                }
            }
        }

        private static bool TryMatchTitleSpan(IReadOnlyList<string> releaseTokens, IReadOnlyList<string> titleTokens, int releaseIndex, int titleIndex, out int matchedEnd)
        {
            matchedEnd = -1;

            if (titleIndex >= titleTokens.Count)
            {
                matchedEnd = releaseIndex - 1;
                return true;
            }

            if (releaseIndex >= releaseTokens.Count)
            {
                return false;
            }

            if (TokensMatch(releaseTokens[releaseIndex], titleTokens[titleIndex]) &&
                TryMatchTitleSpan(releaseTokens, titleTokens, releaseIndex + 1, titleIndex + 1, out matchedEnd))
            {
                return true;
            }

            if (releaseIndex + 1 < releaseTokens.Count &&
                TitleTokenAlignment.TokensMatchCompactSplit(titleTokens[titleIndex], releaseTokens[releaseIndex], releaseTokens[releaseIndex + 1]) &&
                TryMatchTitleSpan(releaseTokens, titleTokens, releaseIndex + 2, titleIndex + 1, out matchedEnd))
            {
                return true;
            }

            if (titleIndex + 1 < titleTokens.Count &&
                TitleTokenAlignment.TokensMatchCompactSplit(releaseTokens[releaseIndex], titleTokens[titleIndex], titleTokens[titleIndex + 1]) &&
                TryMatchTitleSpan(releaseTokens, titleTokens, releaseIndex + 1, titleIndex + 2, out matchedEnd))
            {
                return true;
            }

            return false;
        }

        internal static bool TokensMatch(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) || TokenSynonyms.AreSynonyms(left, right);
        }

        private static TitleMatchResult ChooseBetterResult(TitleMatchResult current, TitleMatchResult candidate)
        {
            if (candidate == null)
            {
                return current;
            }

            if (current == null)
            {
                return candidate;
            }

            if (candidate.IsMatch && !current.IsMatch)
            {
                return candidate;
            }

            if (!candidate.IsMatch && current.IsMatch)
            {
                return current;
            }

            var candidateIsPrimary = MatchesPrimaryTitle(candidate);
            var currentIsPrimary = MatchesPrimaryTitle(current);
            if (candidateIsPrimary != currentIsPrimary)
            {
                return candidateIsPrimary ? candidate : current;
            }

            if (candidate.MeaningfulLeftoverCount != current.MeaningfulLeftoverCount)
            {
                return candidate.MeaningfulLeftoverCount < current.MeaningfulLeftoverCount ? candidate : current;
            }

            var candidateTokenCount = Tokenize(candidate.MatchedVariant).Count;
            var currentTokenCount = Tokenize(current.MatchedVariant).Count;
            return candidateTokenCount > currentTokenCount ? candidate : current;
        }

        private static bool MatchesPrimaryTitle(TitleMatchResult result)
        {
            return result != null &&
                   !string.IsNullOrWhiteSpace(result.PrimaryTitle) &&
                   string.Equals(result.PrimaryTitle, result.MatchedVariant, StringComparison.OrdinalIgnoreCase);
        }

        internal static BookTitleMatchContext GetBookTitleMatchContext(Book book)
        {
            var context = new BookTitleMatchContext
            {
                PrimaryTitle = GetPrimaryBookTitle(book),
                SeriesName = book?.SeriesName,
                SeriesPosition = book?.SeriesPosition
            };
            AddTitleVariants(context.PrimaryVariants, context.PrimaryTitle);

            // Shortened titles retain the selected edition's language; sibling checks reject ambiguous bases.
            var editionTitle = NormalizeExpandedVariant(GetSelectedEditionTitle(book));
            var subtitlePattern = string.Join("|", GetSubtitleBoundaries().Select(boundary => Regex.Escape(boundary.Prefix)));
            foreach (Match boundary in Regex.Matches(editionTitle, subtitlePattern).Cast<Match>().Reverse())
            {
                var subtitle = editionTitle.Substring(boundary.Index + boundary.Length);
                if (!string.IsNullOrWhiteSpace(subtitle) && !Tokenize(subtitle).Any(IsNumericToken))
                {
                    AddVariant(context.FallbackTitles, editionTitle.Substring(0, boundary.Index).Trim());
                }
            }

            AddSeriesTailVariant(context, editionTitle);

            if (string.IsNullOrWhiteSpace(context.SeriesName) || !TryGetSeriesPositionNumber(context.SeriesPosition, out var expected))
            {
                return context;
            }

            // A trailing "SeriesName, Book N" subtitle restates stored series metadata, so it is optional.
            foreach (var variant in context.PrimaryVariants.ToList())
            {
                var tokens = TokenizeWithSpans(variant);
                foreach (var span in FindExactTitleSpans(tokens.Select(token => token.Value).ToList(), Tokenize(context.SeriesName)))
                {
                    var position = string.Join(" ", tokens.Skip(span.End + 1).Select(token => token.Value));
                    if (span.Start > 0 && TryGetSeriesPositionNumber(position, out var actual) &&
                        actual == expected &&
                        TryGetKnownSubtitleBaseTitle(variant, variant.Substring(tokens[span.Start].Start), out var baseTitle))
                    {
                        AddVariant(context.PrimaryVariants, baseTitle);
                    }
                }
            }

            return context;
        }

        private static void AddSeriesTailVariant(BookTitleMatchContext context, string editionTitle)
        {
            if (context == null || string.IsNullOrWhiteSpace(editionTitle) || string.IsNullOrWhiteSpace(context.SeriesName))
            {
                return;
            }

            var seriesTokens = Tokenize(context.SeriesName);
            if (seriesTokens.Count == 0)
            {
                return;
            }

            foreach (var boundary in GetSubtitleBoundaries())
            {
                var boundaryText = boundary.Prefix;
                var boundaryIndex = editionTitle.IndexOf(boundaryText, StringComparison.OrdinalIgnoreCase);
                if (boundaryIndex <= 0)
                {
                    continue;
                }

                var head = editionTitle.Substring(0, boundaryIndex).Trim();
                var headTokens = Tokenize(head);
                if (headTokens.Count != seriesTokens.Count ||
                    !FindExactTitleSpans(headTokens, seriesTokens).Any(span => span.Start == 0 && span.End == headTokens.Count - 1))
                {
                    continue;
                }

                var tail = NormalizeExpandedVariant(editionTitle.Substring(boundaryIndex + boundaryText.Length));
                if (string.IsNullOrWhiteSpace(tail))
                {
                    continue;
                }

                AddVariant(context.FallbackTitles, RemoveMatchingSeriesPositionQualifier(tail, context.SeriesPosition));
            }
        }

        private static string RemoveMatchingSeriesPositionQualifier(string title, string seriesPosition)
        {
            if (!TryGetSeriesPositionNumber(seriesPosition, out var expected))
            {
                return title;
            }

            foreach (Match qualifier in OptionalQualifierSegmentRegex.Matches(title).Cast<Match>().Reverse())
            {
                if (TryGetSeriesPositionNumber(qualifier.Groups["label"].Value, out var actual) && actual == expected)
                {
                    return NormalizeExpandedVariant(title.Remove(qualifier.Index, qualifier.Length));
                }
            }

            return title;
        }

        internal static string GetPrimaryBookTitle(Book book)
        {
            if (book == null)
            {
                return string.Empty;
            }

            var selectedEditionTitle = GetSelectedEditionTitle(book);
            if (!string.IsNullOrWhiteSpace(selectedEditionTitle))
            {
                return selectedEditionTitle.Trim();
            }

            return book.Title?.Trim() ?? string.Empty;
        }

        internal static Edition GetSelectedEdition(Book book)
        {
            return BookEditionIdentity.GetMonitoredEdition(book);
        }

        private static string GetSelectedEditionTitle(Book book)
        {
            return GetSelectedEdition(book)?.Title;
        }


        private static void AddTitleVariants(List<string> variants, string title)
        {
            foreach (var variant in ExpandTitleMatchVariants(title))
            {
                AddVariant(variants, variant);
            }
        }

        private static IEnumerable<string> ExpandTitleMatchVariants(string title)
        {
            foreach (var qualifierVariant in ExpandOptionalQualifierVariants(title))
            {
                foreach (var articleVariant in ExpandOptionalSubtitleArticleVariants(qualifierVariant))
                {
                    foreach (var transliteration in ExpandTransliterationVariants(articleVariant))
                    {
                        yield return transliteration;
                    }
                }
            }
        }

        private static IEnumerable<string> ExpandTransliterationVariants(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            yield return title;

            var scandinavian = ApplyScandinavianTransliteration(title);
            if (!string.Equals(scandinavian, title, StringComparison.Ordinal))
            {
                yield return scandinavian.RemoveAccent();
            }

            var germanic = ApplyGermanicTransliteration(title);
            if (!string.Equals(germanic, title, StringComparison.Ordinal))
            {
                yield return germanic.RemoveAccent();
            }
        }

        private static string ApplyScandinavianTransliteration(string title)
        {
            return title
                .Replace("å", "aa")
                .Replace("Å", "Aa")
                .Replace("æ", "ae")
                .Replace("Æ", "Ae")
                .Replace("ø", "oe")
                .Replace("Ø", "Oe");
        }

        private static string ApplyGermanicTransliteration(string title)
        {
            return title
                .Replace("ä", "ae")
                .Replace("Ä", "Ae")
                .Replace("ö", "oe")
                .Replace("Ö", "Oe")
                .Replace("ü", "ue")
                .Replace("Ü", "Ue")
                .Replace("ß", "ss");
        }

        internal static bool IsKnownSubtitleTitleVariant(string title, string subtitle, string primaryTitle)
        {
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(subtitle) ||
                string.IsNullOrWhiteSpace(primaryTitle))
            {
                return false;
            }

            var normalizedPrimaryTitle = NormalizeExpandedVariant(primaryTitle);
            var normalizedTitle = NormalizeExpandedVariant(title);
            var normalizedSubtitle = NormalizeExpandedVariant(subtitle);
            if (string.IsNullOrWhiteSpace(normalizedPrimaryTitle) ||
                string.IsNullOrWhiteSpace(normalizedTitle) ||
                string.IsNullOrWhiteSpace(normalizedSubtitle))
            {
                return false;
            }

            foreach (var boundary in GetSubtitleBoundaries())
            {
                var combined = ComposeKnownSubtitleTitle(normalizedTitle, normalizedSubtitle, boundary);
                if (string.Equals(combined, normalizedPrimaryTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool TryGetKnownSubtitleBaseTitle(string fullTitle, string subtitle, out string baseTitle)
        {
            baseTitle = null;
            var normalizedFullTitle = NormalizeExpandedVariant(fullTitle);
            var normalizedSubtitle = NormalizeExpandedVariant(subtitle);
            if (string.IsNullOrWhiteSpace(normalizedFullTitle) ||
                string.IsNullOrWhiteSpace(normalizedSubtitle))
            {
                return false;
            }

            foreach (var boundary in GetSubtitleBoundaries())
            {
                var suffix = $"{boundary.Prefix}{normalizedSubtitle}{boundary.Suffix}";
                if (!normalizedFullTitle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidate = normalizedFullTitle.Substring(0, normalizedFullTitle.Length - suffix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    IsKnownSubtitleTitleVariant(candidate, subtitle, fullTitle))
                {
                    baseTitle = candidate;
                    return true;
                }
            }

            return false;
        }

        private static string ComposeKnownSubtitleTitle(
            string normalizedTitle,
            string normalizedSubtitle,
            (string Prefix, string Suffix) boundary)
        {
            return NormalizeExpandedVariant(
                $"{normalizedTitle}{boundary.Prefix}{normalizedSubtitle}{boundary.Suffix}");
        }

        private static IEnumerable<(string Prefix, string Suffix)> GetSubtitleBoundaries()
        {
            yield return (": ", string.Empty);
            yield return (" - ", string.Empty);
            yield return ("; ", string.Empty);
            yield return (" \u2013 ", string.Empty);
            yield return (" \u2014 ", string.Empty);
            yield return (" (", string.Empty);
            yield return (" [", string.Empty);
        }

        private static IEnumerable<string> ExpandOptionalQualifierVariants(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            yield return title;

            // Full title is tried first; a stored title's bracketed content never blocks on its own.
            // Author, sibling and series-position checks still run against this variant.
            var withoutQualifiers = OptionalQualifierSegmentRegex.Replace(title, " ");
            withoutQualifiers = NormalizeExpandedVariant(withoutQualifiers);
            if (!string.IsNullOrWhiteSpace(withoutQualifiers) && !string.Equals(withoutQualifiers, title, StringComparison.OrdinalIgnoreCase))
            {
                yield return withoutQualifiers;
            }
        }

        private static IEnumerable<string> ExpandOptionalSubtitleArticleVariants(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            var normalized = NormalizeExpandedVariant(title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            yield return normalized;

            var withoutSubtitleArticles = SubtitleLeadingArticleRegex.Replace(normalized, "${prefix}");
            withoutSubtitleArticles = NormalizeExpandedVariant(withoutSubtitleArticles);
            if (!string.Equals(withoutSubtitleArticles, normalized, StringComparison.OrdinalIgnoreCase))
            {
                yield return withoutSubtitleArticles;
            }

            foreach (Match match in SubtitleArticleInsertionPointRegex.Matches(normalized))
            {
                var insertionIndex = match.Groups["head"].Index;
                if (LeadingOptionalArticleRegex.IsMatch(normalized.Substring(insertionIndex)))
                {
                    continue;
                }

                foreach (var article in OptionalSubtitleArticles)
                {
                    yield return NormalizeExpandedVariant(normalized.Insert(insertionIndex, article + " "));
                }
            }
        }

        private static string NormalizeExpandedVariant(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var normalized = WhitespaceRegex.Replace(title, " ").Trim();
            normalized = SpaceBeforePunctuationRegex.Replace(normalized, "$1");
            normalized = SpaceAfterPunctuationRegex.Replace(normalized, "$1 ");
            return normalized.Trim().Trim('-', ':', ';', ',', '.').Trim();
        }

        private static void AddVariant(List<string> variants, string title)
        {
            if (variants == null || string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            var trimmed = title.Trim();
            if (!variants.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                variants.Add(trimmed);
            }
        }

        public static List<string> Tokenize(string text)
        {
            return TokenizeWithSpans(text)
                .Select(token => token.Value)
                .ToList();
        }

        public static List<TitleTokenSpan> TokenizeWithSpans(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<TitleTokenSpan>();
            }

            var mappedText = CreateMappedText(text);
            mappedText = ReplaceAmpersands(mappedText);
            mappedText = ApplyReplacement(mappedText, PossessiveRegex, "s");
            mappedText = ApplyReplacement(mappedText, DecimalPointBetweenDigitsRegex, DecimalPointToken);
            mappedText = NormalizeWordsWithSpans(mappedText);

            return TokenRegex.Matches(mappedText.Text)
                .Cast<Match>()
                .Select(match => CreateTokenSpan(mappedText, match))
                .Where(token => !string.IsNullOrWhiteSpace(token.Value))
                .ToList();
        }

        private sealed class MappedText
        {
            public string Text { get; set; }
            public List<(int Start, int End)> SourceSpans { get; set; }
        }

        private static MappedText CreateMappedText(string text)
        {
            var normalized = UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans(text);
            return new MappedText { Text = normalized.Text, SourceSpans = normalized.SourceSpans };
        }

        private static MappedText ReplaceAmpersands(MappedText input)
        {
            var builder = new StringBuilder(input.Text.Length);
            var spans = new List<(int Start, int End)>();

            for (var i = 0; i < input.Text.Length; i++)
            {
                if (input.Text[i] == '&')
                {
                    AppendMapped(builder, spans, " and ", input.SourceSpans[i]);
                    continue;
                }

                builder.Append(input.Text[i]);
                spans.Add(input.SourceSpans[i]);
            }

            return new MappedText
            {
                Text = builder.ToString(),
                SourceSpans = spans
            };
        }

        private static MappedText ApplyReplacement(MappedText input, Regex regex, string replacement)
        {
            var builder = new StringBuilder(input.Text.Length);
            var spans = new List<(int Start, int End)>();
            var position = 0;

            foreach (Match match in regex.Matches(input.Text))
            {
                AppendRange(input, builder, spans, position, match.Index);

                var sourceSpan = CombineSpans(input.SourceSpans, match.Index, match.Length);
                AppendMapped(builder, spans, replacement, sourceSpan);

                position = match.Index + match.Length;
            }

            AppendRange(input, builder, spans, position, input.Text.Length);

            return new MappedText
            {
                Text = builder.ToString(),
                SourceSpans = spans
            };
        }

        private static MappedText NormalizeWordsWithSpans(MappedText input)
        {
            var normalized = UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input.Text, input.SourceSpans);
            return new MappedText
            {
                Text = normalized.Text,
                SourceSpans = normalized.SourceSpans
            };
        }

        private static TitleTokenSpan CreateTokenSpan(MappedText mappedText, Match match)
        {
            var sourceSpan = CombineSpans(mappedText.SourceSpans, match.Index, match.Length);

            return new TitleTokenSpan
            {
                Value = match.Value.Replace(DecimalPointToken, ".").Normalize(NormalizationForm.FormC),
                Start = sourceSpan.Start,
                End = sourceSpan.End
            };
        }

        private static void AppendRange(MappedText input, StringBuilder builder, List<(int Start, int End)> spans, int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                builder.Append(input.Text[i]);
                spans.Add(input.SourceSpans[i]);
            }
        }

        private static void AppendMapped(StringBuilder builder, List<(int Start, int End)> spans, string value, (int Start, int End) sourceSpan)
        {
            foreach (var ch in value)
            {
                builder.Append(ch);
                spans.Add(sourceSpan);
            }
        }

        private static (int Start, int End) CombineSpans(List<(int Start, int End)> sourceSpans, int start, int length)
        {
            var end = start + length;
            var sourceStart = sourceSpans[start].Start;
            var sourceEnd = sourceSpans[start].End;

            for (var i = start + 1; i < end; i++)
            {
                sourceStart = Math.Min(sourceStart, sourceSpans[i].Start);
                sourceEnd = Math.Max(sourceEnd, sourceSpans[i].End);
            }

            return (sourceStart, sourceEnd);
        }

        private static bool IsMetadataToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            return MetadataTokens.Contains(token) ||
                   YearTokenRegex.IsMatch(token) ||
                   IsNumericToken(token) ||
                   CompactMetadataCodeRegex.IsMatch(token) ||
                   IsLanguageToken(token);
        }

        private static bool IsLanguageToken(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && IsKnownLanguageName(token);
        }

        private static bool IsKnownLanguageName(string value)
        {
            var normalized = NormalizeLanguageName(value);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   (normalized.CanonicalizeLanguage() != null || KnownLanguageNames.Contains(normalized));
        }

        private static string NormalizeLanguageName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Split('(')[0].Trim().ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}]+", " ").Trim();
            return normalized;
        }
    }
}
