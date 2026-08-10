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
using NzbDrone.Core.MediaFiles.BookImport.Services;
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
        public string SeriesName { get; set; }
        public string SeriesPosition { get; set; }
        public List<string> PrimaryVariants { get; } = new List<string>();
        public HashSet<string> PrefixAllowanceTokens { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class ContradictoryVariant
    {
        public string Title { get; set; }
        public IReadOnlyList<string> Tokens { get; set; }
    }

    public static class ReleaseTitleMatchScorer
    {
        private const int HintOnlyPrefixGuardTokenThreshold = 2;
        private const string DecimalPointToken = "chaptarrdecimalpoint";

        private static readonly Regex TokenRegex = new Regex(@"\p{Nd}+(?:\.\p{Nd}+)+|[\p{L}\p{Nd}]+", RegexOptions.Compiled);
        private static readonly Regex PossessiveRegex = new Regex(@"['\u2018\u2019]s\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DecimalPointBetweenDigitsRegex = new Regex(@"(?<=\p{Nd})\.(?=\p{Nd})", RegexOptions.Compiled);
        private static readonly Regex OptionalQualifierSegmentRegex = new Regex(@"\s*[\(\[](?<label>[^\)\]]+)[\)\]]\s*", RegexOptions.Compiled);
        private static readonly Regex SubtitleLeadingArticleRegex = new Regex(@"(?<prefix>[:;\-\u2013\u2014]\s+)(?<article>a|an|the)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SubtitleArticleInsertionPointRegex = new Regex(@"(?<prefix>[:;\-\u2013\u2014]\s+)(?<head>[\p{L}\p{Nd}])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex LeadingOptionalArticleRegex = new Regex(@"^(?:a|an|the)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex SpaceBeforePunctuationRegex = new Regex(@"\s+([:;,])", RegexOptions.Compiled);
        private static readonly Regex SpaceAfterPunctuationRegex = new Regex(@"([:;,])(?=\S)", RegexOptions.Compiled);
        private static readonly Regex YearTokenRegex = new Regex(@"^(?:18\d{2}|19\d{2}|20\d{2}|21\d{2})$", RegexOptions.Compiled);
        private static readonly Regex CompactMetadataCodeRegex = new Regex(@"^[\p{L}]{1,3}\d{1,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NumericTokenRegex = new Regex(@"^\d+(?:\.\d+)?$", RegexOptions.Compiled);
        private static readonly Regex BareSeriesPositionRegex = new Regex(@"^\s*#?\s*(?<number>\d+(?:\.\d+)?)(?:\s*,?\s*(?:part|pt)\s+\d+\s+of\s+\d+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MarkedSeriesPositionRegex = new Regex(@"^\s*(?:book|bk|vol|volume|tome)\s+#?\s*(?<number>\d+(?:\.\d+)?)(?:\s*,?\s*(?:part|pt)\s+\d+\s+of\s+\d+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> KnownLanguageNames = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Select(culture => NormalizeLanguageName(culture.EnglishName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        private static readonly HashSet<string> OptionalProductionQualifierLabels = new HashSet<string>(
            AudioProductionConstants.GraphicAudioIndicators
                .Concat(new[]
                {
                    "abridged",
                    "unabridged",
                    "10th anniversary recording",
                    "full cast edition",
                    "graphic audio llc",
                    "radio theater",
                    "radio theatre"
                })
                .Select(NormalizeProductionQualifierLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label)),
            StringComparer.OrdinalIgnoreCase);

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

            var hasAuthorInTitle = HasAuthorEvidence(authorName, cleanedReleaseTitle, null);
            var hasAuthorEvidence = hasAuthorInTitle || HasAuthorEvidence(authorName, cleanedReleaseTitle, releaseAuthorHint);

            TitleMatchResult best = null;

            foreach (var book in books)
            {
                var candidate = ScoreAgainstBook(releaseTokens, hasAuthorEvidence, hasAuthorInTitle, book, authorCatalog);
                best = ChooseBetterResult(best, candidate);
            }

            return best;
        }

        private static TitleMatchResult ScoreAgainstBook(IReadOnlyList<string> releaseTokens, bool hasAuthorEvidence, bool hasAuthorInTitle, Book book, IEnumerable<Book> authorCatalogBooks)
        {
            var context = GetBookTitleMatchContext(book);
            if (context.PrimaryVariants.Count == 0)
            {
                return null;
            }

            var contradictoryVariants = BuildContradictoryVariants(book, authorCatalogBooks).ToList();
            return ScoreAgainstVariants(releaseTokens, hasAuthorEvidence, hasAuthorInTitle, book, context.PrimaryVariants, context, contradictoryVariants);
        }

        private static TitleMatchResult ScoreAgainstVariants(IReadOnlyList<string> releaseTokens, bool hasAuthorEvidence, bool hasAuthorInTitle, Book book, IEnumerable<string> variants, BookTitleMatchContext context, IReadOnlyCollection<ContradictoryVariant> contradictoryVariants)
        {
            TitleMatchResult best = null;

            foreach (var variant in variants ?? Enumerable.Empty<string>())
            {
                var candidate = ScoreAgainstTitleVariant(releaseTokens, hasAuthorEvidence, hasAuthorInTitle, book, variant, context, contradictoryVariants);
                best = ChooseBetterResult(best, candidate);
            }

            return best;
        }

        private static TitleMatchResult ScoreAgainstTitleVariant(IReadOnlyList<string> releaseTokens, bool hasAuthorEvidence, bool hasAuthorInTitle, Book book, string bookTitleVariant, BookTitleMatchContext context, IReadOnlyCollection<ContradictoryVariant> contradictoryVariants)
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
                var problems = GetProblems(releaseTokens, span.Start, span.End, titleTokens.Count, hasAuthorInTitle, book?.Author?.Name, context, contradictoryVariants);
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
                    IsMatch = (hasAuthorEvidence || IsLongAuthorlessYearTitleMatch(releaseTokens, span.Start, span.End, titleTokens, problems)) && problems.Count == 0
                };

                best = ChooseBetterResult(best, candidate);
            }

            return best;
        }

        private static bool IsLongAuthorlessYearTitleMatch(IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, IReadOnlyCollection<string> titleTokens, IReadOnlyCollection<TitleMatchProblem> problems)
        {
            var yearIndex = matchedEnd + 1;
            return matchedStart == 0 &&
                   problems.Count == 0 &&
                   titleTokens.Count >= 4 &&
                   yearIndex < releaseTokens.Count &&
                   YearTokenRegex.IsMatch(releaseTokens[yearIndex]) &&
                   releaseTokens.Skip(yearIndex + 1).All(IsMetadataToken);
        }

        private static List<TitleMatchProblem> GetProblems(IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, int matchedTokenCount, bool hasAuthorInTitle, string authorName, BookTitleMatchContext context, IReadOnlyCollection<ContradictoryVariant> contradictoryVariants)
        {
            var problems = new List<TitleMatchProblem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var problem in GetEmbeddedShortTitleProblems(releaseTokens, matchedStart, matchedEnd, matchedTokenCount, authorName, context))
            {
                if (seen.Add(problem.Value))
                {
                    problems.Add(problem);
                }
            }

            if (!hasAuthorInTitle && matchedTokenCount <= HintOnlyPrefixGuardTokenThreshold)
            {
                foreach (var token in GetHintOnlyPrefixContradictions(releaseTokens, matchedStart, context))
                {
                    if (seen.Add(token))
                    {
                        problems.Add(new TitleMatchProblem
                        {
                            Code = TitleMatchProblemCode.PrefixContradiction,
                            Value = token
                        });
                    }
                }
            }

            foreach (var problem in GetAdjacentSeriesNumberProblems(releaseTokens, matchedStart, matchedEnd, context))
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
                    if (span.Start >= matchedStart && span.End <= matchedEnd)
                    {
                        continue;
                    }

                    if (!IsAdjacentToMatchedTitle(span.Start, span.End, matchedStart, matchedEnd))
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

            if (problems.Any(problem => problem.Code == TitleMatchProblemCode.EmbeddedShortTitle))
            {
                return TitleMatchProblemCode.EmbeddedShortTitle;
            }

            return problems.First().Code;
        }

        private static bool IsAdjacentToMatchedTitle(int candidateStart, int candidateEnd, int matchedStart, int matchedEnd)
        {
            return candidateStart <= matchedEnd + 1 && candidateEnd >= matchedStart - 1;
        }

        private static IEnumerable<TitleMatchProblem> GetEmbeddedShortTitleProblems(IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, int matchedTokenCount, string authorName, BookTitleMatchContext context)
        {
            if (matchedTokenCount != 1)
            {
                yield break;
            }

            var previousToken = matchedStart > 0 ? releaseTokens[matchedStart - 1] : null;
            var nextToken = matchedEnd + 1 < releaseTokens.Count ? releaseTokens[matchedEnd + 1] : null;

            if (IsAllowedShortTitleNeighbor(nextToken, authorName, context))
            {
                yield break;
            }

            yield return new TitleMatchProblem
            {
                Code = TitleMatchProblemCode.EmbeddedShortTitle,
                Value = string.Join(" ", new[] { previousToken, nextToken }.Where(token => !string.IsNullOrWhiteSpace(token)))
            };
        }

        private static bool IsAllowedShortTitleNeighbor(string token, string authorName, BookTitleMatchContext context)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            if (IsShortTitleBoundaryMetadataToken(token, context))
            {
                return true;
            }

            return Tokenize(authorName).Any(authorToken => TokensMatch(authorToken, token));
        }

        private static bool IsShortTitleBoundaryMetadataToken(string token, BookTitleMatchContext context)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            return MetadataTokens.Contains(token) ||
                   YearTokenRegex.IsMatch(token) ||
                   token.All(char.IsDigit) ||
                   CompactMetadataCodeRegex.IsMatch(token) ||
                   SeriesPositionMatches(context, token) ||
                   (context?.PrefixAllowanceTokens.Contains(token) ?? false);
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

        private static bool IsNumericMetadataTail(IReadOnlyList<string> releaseTokens, int numberIndex)
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

            return TryParseSeriesPositionPattern(seriesPosition, BareSeriesPositionRegex, out number) ||
                   TryParseSeriesPositionPattern(seriesPosition, MarkedSeriesPositionRegex, out number);
        }

        private static bool TryParseSeriesPositionPattern(string seriesPosition, Regex regex, out decimal number)
        {
            number = default;

            var match = regex.Match(seriesPosition ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }

            return TryParseNumberToken(match.Groups["number"].Value, out number);
        }

        private static bool TryParseNumberToken(string token, out decimal number)
        {
            number = default;

            if (string.IsNullOrWhiteSpace(token) || !NumericTokenRegex.IsMatch(token))
            {
                return false;
            }

            return decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
        }

        private static bool IsPartMarker(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && PartMarkerTokens.Contains(token);
        }

        private static bool IsSeriesPositionMarker(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && SeriesPositionMarkerTokens.Contains(token);
        }

        private static IEnumerable<string> GetHintOnlyPrefixContradictions(IReadOnlyList<string> releaseTokens, int matchedStart, BookTitleMatchContext context)
        {
            for (var index = 0; index < matchedStart; index++)
            {
                var token = releaseTokens[index];
                if (IsIgnorablePrefixToken(token, context))
                {
                    continue;
                }

                yield return token;
            }
        }

        private static IEnumerable<ContradictoryVariant> BuildContradictoryVariants(Book targetBook, IEnumerable<Book> authorCatalogBooks)
        {
            if (targetBook == null || authorCatalogBooks == null)
            {
                return Enumerable.Empty<ContradictoryVariant>();
            }

            var variants = new List<ContradictoryVariant>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var otherBook in authorCatalogBooks.Where(book => CanContradict(targetBook, book)))
            {
                foreach (var variant in GetContradictoryTitleVariants(otherBook))
                {
                    if (string.IsNullOrWhiteSpace(variant) || !seen.Add(variant))
                    {
                        continue;
                    }

                    var tokens = Tokenize(variant);
                    if (tokens.Count < 2)
                    {
                        continue;
                    }

                    variants.Add(new ContradictoryVariant
                    {
                        Title = variant,
                        Tokens = tokens
                    });
                }
            }

            return variants;
        }

        private static IEnumerable<string> GetContradictoryTitleVariants(Book book)
        {
            var variants = new List<string>();

            AddTitleVariants(variants, GetPrimaryBookTitle(book));
            AddTitleVariants(variants, book?.Title);
            AddTitleVariants(variants, book?.OriginalTitle);

            foreach (var editionTitle in (book?.Editions ?? Enumerable.Empty<Edition>())
                         .Where(edition => edition != null && !string.IsNullOrWhiteSpace(edition.Title))
                         .Select(edition => edition.Title.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AddTitleVariants(variants, editionTitle);
            }

            return variants;
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

            var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELEASE_TITLE"] = new List<string> { releaseTitle ?? string.Empty }
            };

            if (!string.IsNullOrWhiteSpace(releaseAuthorHint))
            {
                fields["RELEASE_AUTHOR"] = new List<string> { releaseAuthorHint };
            }

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
            var context = new BookTitleMatchContext();
            var primaryTitle = GetPrimaryBookTitle(book);

            AddTitleVariants(context.PrimaryVariants, primaryTitle);
            context.PrimaryTitle = primaryTitle;

            foreach (var variant in context.PrimaryVariants)
            {
                AddTokens(context.PrefixAllowanceTokens, variant);
            }

            AddTokens(context.PrefixAllowanceTokens, book?.SeriesName);
            context.SeriesName = book?.SeriesName;
            context.SeriesPosition = book?.SeriesPosition;
            AddSeriesPositionTokens(context.PrefixAllowanceTokens, book?.SeriesPosition);
            foreach (var narrator in GetKnownNarrators(book))
            {
                AddTokens(context.PrefixAllowanceTokens, narrator);
            }

            return context;
        }

        private static IEnumerable<string> GetKnownNarrators(Book book)
        {
            if (book == null)
            {
                yield break;
            }

            if (book.Narrator.IsNotNullOrWhiteSpace())
            {
                yield return book.Narrator;
            }

            foreach (var edition in book.Editions ?? Enumerable.Empty<Edition>())
            {
                if (edition?.Narrator.IsNotNullOrWhiteSpace() == true)
                {
                    yield return edition.Narrator;
                }

                foreach (var narrator in edition?.NarratorNames ?? Enumerable.Empty<string>())
                {
                    if (narrator.IsNotNullOrWhiteSpace())
                    {
                        yield return narrator;
                    }
                }
            }
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
            return book?.Editions?
                .Where(edition => edition != null && edition.Monitored)
                .OrderBy(edition => edition.Id)
                .FirstOrDefault();
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

            var matches = OptionalQualifierSegmentRegex.Matches(title)
                .Cast<Match>()
                .Where(match => IsOptionalProductionQualifier(match.Groups["label"].Value))
                .ToList();

            if (matches.Count == 0)
            {
                yield break;
            }

            var withoutOptionalQualifiers = OptionalQualifierSegmentRegex.Replace(title, match =>
                IsOptionalProductionQualifier(match.Groups["label"].Value) ? " " : match.Value);
            withoutOptionalQualifiers = NormalizeExpandedVariant(withoutOptionalQualifiers);
            if (!string.IsNullOrWhiteSpace(withoutOptionalQualifiers) &&
                !string.Equals(withoutOptionalQualifiers, title, StringComparison.OrdinalIgnoreCase))
            {
                yield return withoutOptionalQualifiers;
            }

            // Some metadata sources store production labels before a series subtitle:
            // "Storm Front (Dramatized Adaptation): Dresden Files, Book 1".
            // The release may only carry the base title plus a GraphicAudio flag.
            var titleBeforeQualifier = NormalizeExpandedVariant(title.Substring(0, matches[0].Index));
            if (!string.IsNullOrWhiteSpace(titleBeforeQualifier) &&
                !string.Equals(titleBeforeQualifier, title, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(titleBeforeQualifier, withoutOptionalQualifiers, StringComparison.OrdinalIgnoreCase))
            {
                yield return titleBeforeQualifier;
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

        private static bool IsOptionalProductionQualifier(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            var normalized = NormalizeProductionQualifierLabel(label);
            return OptionalProductionQualifierLabels.Contains(normalized);
        }

        private static string NormalizeProductionQualifierLabel(string label)
        {
            return string.Join(" ", Tokenize(label));
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

        private static void AddTokens(HashSet<string> bucket, string text)
        {
            if (bucket == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            foreach (var token in Tokenize(text))
            {
                bucket.Add(token);
            }
        }

        private static void AddSeriesPositionTokens(HashSet<string> bucket, string seriesPosition)
        {
            if (bucket == null || string.IsNullOrWhiteSpace(seriesPosition))
            {
                return;
            }

            AddTokens(bucket, seriesPosition);
            bucket.Add("book");
            bucket.Add("bk");
            bucket.Add("series");
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
            var builder = new StringBuilder(text.Length);
            var spans = new List<(int Start, int End)>(text.Length);

            for (var i = 0; i < text.Length; i++)
            {
                builder.Append(text[i]);
                spans.Add((i, i + 1));
            }

            return new MappedText
            {
                Text = builder.ToString(),
                SourceSpans = spans
            };
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

        private static bool IsIgnorablePrefixToken(string token, BookTitleMatchContext context)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            return IsMetadataToken(token) ||
                   SeriesPositionMatches(context, token) ||
                   (context?.PrefixAllowanceTokens.Contains(token) ?? false);
        }

        private static bool IsMetadataToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            return MetadataTokens.Contains(token) ||
                   YearTokenRegex.IsMatch(token) ||
                   token.All(char.IsDigit) ||
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
