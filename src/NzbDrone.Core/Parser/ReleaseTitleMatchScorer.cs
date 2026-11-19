using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.CustomFormats;
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
        internal bool RequiresTitleOmission { get; set; }
        internal IReadOnlyList<TitleOccurrenceExplanation> Explanations { get; set; } = Array.Empty<TitleOccurrenceExplanation>();
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
        public IReadOnlyList<string> SelectedEditionTitleTokens { get; set; }
        public Dictionary<string, bool> FallbackTitles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string SeriesName { get; set; }
        public string SeriesPosition { get; set; }
        public IReadOnlyList<IReadOnlyList<string>> SeriesTitles { get; set; }
        public List<string> PrimaryVariants { get; } = new List<string>();
    }

    internal sealed class ContradictoryVariant
    {
        public string Title { get; set; }
        public IReadOnlyList<string> Tokens { get; set; }
        public bool IsFallback { get; set; }
        public bool RequiresTitleOmission { get; set; }
        public bool IsArticleVariant { get; set; }
        public bool IsNumberEquivalent { get; set; }
    }

    public static class ReleaseTitleMatchScorer
    {
        internal sealed class CatalogPreparation
        {
            internal int ScorePasses { get; set; }
            internal long SiblingsCompared { get; set; }
            internal int LargestSiblingScan { get; set; }
            internal long NumberVariantChecks { get; set; }
            internal TimeSpan SiblingScanTime { get; set; }
            private readonly Dictionary<Book, (BookTitleMatchContext Context, IReadOnlyList<ContradictoryVariant> Variants, HashSet<string> WorkIds)> _books = new(BookReferenceComparer.Instance);
            private readonly Dictionary<string, IReadOnlyList<string>> _tokens = new(StringComparer.Ordinal);

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

            internal BookTitleMatchContext GetContext(Book book) => PrepareBook(book).Context;

            internal IReadOnlyList<ContradictoryVariant> GetVariants(Book book) => PrepareBook(book).Variants;

            internal IReadOnlyList<string> GetTokens(string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return Array.Empty<string>();
                }

                if (!_tokens.TryGetValue(text, out var tokens))
                {
                    tokens = Tokenize(text);
                    _tokens.Add(text, tokens);
                }

                return tokens;
            }

            internal bool TitlesEqual(string left, string right) => TokenizedEquals(GetTokens(left), GetTokens(right));

            internal bool IsSameWork(Book left, Book right) => left != null && right != null &&
                BookIdentity.MatchesByProviderIdIntersection(left, right, PrepareBook(left).WorkIds, PrepareBook(right).WorkIds);

            private (BookTitleMatchContext Context, IReadOnlyList<ContradictoryVariant> Variants, HashSet<string> WorkIds) PrepareBook(Book book)
            {
                if (!_books.TryGetValue(book, out var prepared))
                {
                    var context = GetBookTitleMatchContext(book);
                    var titles = GetSelectedEdition(book) == null ? Enumerable.Empty<(string Title, bool IsFallback, bool RequiresTitleOmission)>() :
                        context.PrimaryVariants.Select(title => (Title: title, IsFallback: false, RequiresTitleOmission: false))
                            .Concat(context.FallbackTitles.Select(title => (Title: title.Key, IsFallback: true, RequiresTitleOmission: title.Value)));
                    var variants = titles
                        .Where(variant => !string.IsNullOrWhiteSpace(variant.Title))
                        .DistinctBy(variant => variant.Title, StringComparer.OrdinalIgnoreCase)
                        .Select(variant => new ContradictoryVariant
                        {
                            Title = variant.Title,
                            Tokens = GetTokens(variant.Title),
                            IsFallback = variant.IsFallback,
                            RequiresTitleOmission = variant.RequiresTitleOmission
                        })
                        .Concat(GetFullTitleArticleVariants(book)
                            .Select(variant => new ContradictoryVariant
                            {
                                Title = variant,
                                Tokens = GetTokens(variant),
                                IsArticleVariant = true
                            }))
                        .ToList();
                    prepared = (context, variants, BookIdentity.GetStableWorkProviderIdentityTokens(book));
                    _books.Add(book, prepared);
                }

                return prepared;
            }
        }

        private sealed class ReleasePreparation
        {
            private readonly Dictionary<IReadOnlyList<string>, List<(int Start, int End)>> _spans = new();
            private readonly Dictionary<Book, IReadOnlyList<ContradictoryVariant>> _numberVariants = new(BookReferenceComparer.Instance);
            private readonly Dictionary<Book, IReadOnlyList<ContradictoryVariant>> _variants = new(BookReferenceComparer.Instance);

            public ReleasePreparation(string title, IEnumerable<Book> catalog, CatalogPreparation preparation)
            {
                Title = title;
                CleanedTitle = Parser.CleanReleaseTitleForParsing(title);
                TokenSpans = TokenizeWithSpans(CleanedTitle);
                Tokens = TokenSpans.Select(token => token.Value).ToList();
                Catalog = Tokens.Count == 0 ? Array.Empty<Book>() :
                    (catalog ?? Enumerable.Empty<Book>()).Where(book => book != null).ToList();
                Preparation = preparation;
                preparation.ScorePasses++;
            }

            public string Title { get; }
            public string CleanedTitle { get; }
            public IReadOnlyList<TitleTokenSpan> TokenSpans { get; }
            public IReadOnlyList<string> Tokens { get; }
            public IReadOnlyList<Book> Catalog { get; }
            public CatalogPreparation Preparation { get; }

            public List<(int Start, int End)> GetSpans(IReadOnlyList<string> titleTokens)
            {
                if (!_spans.TryGetValue(titleTokens, out var spans))
                {
                    spans = FindExactTitleSpans(Tokens, titleTokens).ToList();
                    _spans.Add(titleTokens, spans);
                }

                return spans;
            }

            public IReadOnlyList<ContradictoryVariant> GetNumberVariants(Book book)
            {
                if (!_numberVariants.TryGetValue(book, out var variants))
                {
                    variants = GetNumberEquivalentVariants(book, this).ToList();
                    _numberVariants.Add(book, variants);
                }

                return variants;
            }

            public IReadOnlyList<ContradictoryVariant> GetVariants(Book book)
            {
                if (!_variants.TryGetValue(book, out var variants))
                {
                    variants = Preparation.GetVariants(book).Where(variant => variant.Tokens.Count > 0 &&
                        (!variant.IsArticleVariant || (GetSpans(variant.Tokens).Count > 0 &&
                            !IsArticleAliasAmbiguous(book, variant, Catalog, Preparation))))
                        .Concat(GetNumberVariants(book)).ToList();
                    _variants.Add(book, variants);
                }

                return variants;
            }
        }

        private const string DecimalPointToken = "chaptarrdecimalpoint";

        // Normalization already classifies characters; rechecking UTF-16 halves would discard supplementary letters.
        private static readonly Regex NumericTokenRegex = new Regex(@"^[0-9]+(?:\.[0-9]+)?$", RegexOptions.Compiled);
        private static readonly Regex TokenRegex = new Regex(@"\S+", RegexOptions.Compiled);
        private static readonly Regex DecimalPointBetweenDigitsRegex = new Regex(@"(?<=\p{Nd})\.(?=\p{Nd})", RegexOptions.Compiled);
        private static readonly Regex OptionalQualifierSegmentRegex = new Regex(@"\s*(?:" + TitleBracketCatalog.PairedSegmentPattern + @")\s*", RegexOptions.Compiled);
        private static readonly Regex OptionalArticleSubtitleBoundaryRegex = new Regex(@"[:;\-\u2013\u2014]\s+(?<head>[\p{L}\p{Nd}])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex LeadingWordRegex = new Regex(@"^(?<word>[\p{L}\p{Nd}]+)(?:\s+|$)", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex SpaceBeforePunctuationRegex = new Regex(@"\s+([:;,])", RegexOptions.Compiled);
        private static readonly Regex SpaceAfterPunctuationRegex = new Regex(@"([:;,])(?=\S)", RegexOptions.Compiled);
        private static readonly Regex YearTokenRegex = new Regex(@"^(?:18\d{2}|19\d{2}|20\d{2}|21\d{2})$", RegexOptions.Compiled);
        private static readonly (char Open, char Close)[] QuotedFilenameDelimiters =
        {
            ('"', '"'),
            ('\u201c', '\u201d'),
            ('\u2018', '\u2019')
        };

        private static readonly Regex CompactMetadataCodeRegex = new Regex(@"^[\p{L}]{1,3}\d{1,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BareSeriesPositionRegex = new Regex(@"^\s*#?\s*(?<number>\d+(?:\.\d+)?)(?:\s*,?\s*(?:part|pt)\s+\d+\s+of\s+\d+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MarkedSeriesPositionRegex = new Regex(@"^\s*(?:book|bk|vol|volume|tome)\s+#?\s*(?<number>\d+(?:\.\d+)?)(?:\s*,?\s*(?:part|pt)\s+\d+\s+of\s+\d+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> KnownLanguageNames = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Select(culture => NormalizeLanguageName(culture.EnglishName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static readonly Logger Logger = LogManager.GetLogger(typeof(ReleaseTitleMatchScorer).FullName);

        private static readonly HashSet<string> MetadataTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
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

        internal static TitleMatchResult FindBestMatchForSearch(ReleaseInfo release,
                                                                string authorName,
                                                                IReadOnlyList<Book> searchedBooks,
                                                                string authorHint,
                                                                IEnumerable<Book> authorCatalog,
                                                                CatalogPreparation preparation)
        {
            return FindBestMatches(release, authorName, searchedBooks, authorHint, authorCatalog, preparation)
                .Aggregate((TitleMatchResult)null, ChooseBetterResult);
        }

        private static bool IsSearchedBook(Book book, IReadOnlyList<Book> searchedBooks, CatalogPreparation preparation)
        {
            return searchedBooks.Any(searched => ReferenceEquals(searched, book) ||
                                                 (searched?.Id > 0 && searched.Id == book.Id) ||
                                                 preparation.IsSameWork(searched, book));
        }

        // Only a sibling whose own title is actually present can out-explain the searched book, so
        // the competition stays a handful of candidates instead of the whole catalog.
        private static List<Book> GetCompetingSiblings(ReleasePreparation release, IReadOnlyList<Book> searchedBooks)
        {
            var competitors = new List<Book>();
            if (searchedBooks == null || searchedBooks.Count == 0)
            {
                return competitors;
            }

            var releaseTokens = release.Tokens;
            var preparation = release.Preparation;
            if (releaseTokens.Count == 0)
            {
                return competitors;
            }

            var started = Stopwatch.GetTimestamp();
            var compared = 0;
            var catalogBooks = release.Catalog;
            foreach (var other in catalogBooks)
            {
                if (other == null || IsSearchedBook(other, searchedBooks, preparation))
                {
                    continue;
                }

                compared++;

                // An edition titled as the author's own name is author evidence, never a rival title.
                if (preparation.TitlesEqual(GetPrimaryBookTitle(other), other.Author?.Name))
                {
                    continue;
                }

                if (!searchedBooks.All(searched => CanContradict(searched, other, preparation)))
                {
                    continue;
                }

                if (preparation.GetVariants(other).Any(variant => variant.Tokens.Count > 0 &&
                                                                  release.GetSpans(variant.Tokens).Count > 0 &&
                                                                  (!variant.IsArticleVariant || !IsArticleAliasAmbiguous(other, variant, catalogBooks, preparation))) ||
                    release.GetNumberVariants(other).Count > 0)
                {
                    competitors.Add(other);
                }
            }

            preparation.SiblingsCompared += compared;
            preparation.LargestSiblingScan = Math.Max(preparation.LargestSiblingScan, compared);
            preparation.SiblingScanTime += Stopwatch.GetElapsedTime(started);
            return competitors;
        }

        internal static TitleMatchResult FindBestMatch(ReleaseInfo release, string authorName, IEnumerable<Book> candidateBooks, string authorHint, IEnumerable<Book> authorCatalogBooks, CatalogPreparation preparation)
        {
            var evidenceTitle = GetBookTitleEvidence(release);
            var usesStructuredBook = !string.IsNullOrWhiteSpace(release?.Book);
            var selectedAuthorHint = GetReleaseAuthorHint(release, authorName, candidateBooks, authorHint, out var authorHintFromRawTitle);

            var match = FindBestMatch(evidenceTitle, authorName, candidateBooks, selectedAuthorHint, authorCatalogBooks, preparation);

            if (usesStructuredBook && !string.Equals(release.Book, release.Title, StringComparison.Ordinal) &&
                Logger.IsTraceEnabled && match != null)
            {
                Logger.Trace("STRUCTURED_TITLE_EVIDENCE: decidedBy=structured, authorHintFromRawTitle={0}, result={1}, book='{2}', releaseTitle='{3}', structured='{4}'",
                    authorHintFromRawTitle,
                    match.IsMatch,
                    match.Book?.Title ?? "<null>",
                    release.Title,
                    release.Book);
            }

            return match;
        }

        private static string GetReleaseAuthorHint(ReleaseInfo release, string authorName, IEnumerable<Book> candidateBooks, string authorHint, out bool authorHintFromRawTitle)
        {
            var evidenceTitle = GetBookTitleEvidence(release);
            var selectedAuthorHint = authorHint;
            var usesStructuredBook = !string.IsNullOrWhiteSpace(release?.Book);
            authorHintFromRawTitle = false;

            if (usesStructuredBook && string.IsNullOrWhiteSpace(selectedAuthorHint))
            {
                var rawTokens = TokenizeWithSpans(release.Title);
                var rawTitleSpans = FindExactTitleSpans(rawTokens.Select(token => token.Value).ToList(), Tokenize(evidenceTitle)).ToList();
                var authorSpans = ReleaseIdentityEvidence.FindAuthorNameSpans(release.Title, rawTokens, GetExpectedAuthor(authorName, candidateBooks)).ToList();
                authorHintFromRawTitle = rawTitleSpans.Count == 0
                    ? ReleaseIdentityEvidence.HasAuthorEvidence(release.Title, rawTokens, authorSpans)
                    : rawTitleSpans.Any(span => ReleaseIdentityEvidence.HasAuthorEvidence(release.Title, rawTokens, authorSpans, span.Start, span.End));
                if (authorHintFromRawTitle)
                {
                    selectedAuthorHint = authorName;
                }
            }

            return selectedAuthorHint;
        }

        internal static List<TitleMatchResult> FindBestMatches(ReleaseInfo release, string authorName, IEnumerable<Book> candidateBooks, string authorHint, IEnumerable<Book> authorCatalogBooks, CatalogPreparation preparation)
        {
            var candidates = (candidateBooks ?? Enumerable.Empty<Book>()).Where(book => book != null).ToList();
            if (candidates.Count == 0)
            {
                return new List<TitleMatchResult>();
            }

            var selectedAuthorHint = GetReleaseAuthorHint(release, authorName, candidates, authorHint, out _);
            var evidence = new ReleasePreparation(GetBookTitleEvidence(release), authorCatalogBooks ?? candidates, preparation);
            var matches = ScoreBooks(evidence, authorName, candidates, selectedAuthorHint).ToList();
            var targets = GetEquivalentBestMatches(matches).ToList();
            if (targets.Count == 0)
            {
                return targets;
            }

            // Siblings can disprove a target without becoming eligible download targets themselves.
            var competitors = GetCompetingSiblings(evidence, candidates);
            if (competitors.Count == 0)
            {
                return targets;
            }

            var contested = matches.Concat(ScoreBooks(evidence, authorName, competitors, selectedAuthorHint))
                .Aggregate((TitleMatchResult)null, ChooseBetterResult);
            if (contested?.IsMatch != true || contested.Book == null || IsSearchedBook(contested.Book, candidates, preparation))
            {
                return targets;
            }

            var winner = GetPrimaryBookTitle(contested.Book);
            foreach (var target in targets)
            {
                target.IsMatch = false;
                target.ProblemCode = TitleMatchProblemCode.SiblingTitleContradiction;
                target.Problems.Add(new TitleMatchProblem { Code = TitleMatchProblemCode.SiblingTitleContradiction, Value = winner });
                target.MeaningfulLeftovers = target.MeaningfulLeftovers.Concat(Tokenize(winner))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                target.MeaningfulLeftoverCount = target.MeaningfulLeftovers.Count;
            }

            return targets;
        }

        private static string GetBookTitleEvidence(ReleaseInfo release)
        {
            return !string.IsNullOrWhiteSpace(release?.Book) ? release.Book : release?.Title;
        }

        internal static TitleMatchResult FindBestMatch(string releaseTitle, string authorName, IEnumerable<Book> candidateBooks, string releaseAuthorHint, IEnumerable<Book> authorCatalogBooks, CatalogPreparation preparation)
        {
            if (string.IsNullOrWhiteSpace(releaseTitle) || candidateBooks == null)
            {
                return null;
            }

            var candidates = (candidateBooks ?? Enumerable.Empty<Book>()).Where(book => book != null).ToList();
            var evidence = new ReleasePreparation(releaseTitle, authorCatalogBooks ?? candidates, preparation);
            return ScoreBooks(evidence, authorName, candidates, releaseAuthorHint)
                .Aggregate((TitleMatchResult)null, ChooseBetterResult);
        }

        internal static List<Book> FindBooksByTitle(string title, string authorName, IEnumerable<Book> candidateBooks, IEnumerable<Book> authorCatalogBooks, CatalogPreparation preparation = null)
        {
            var candidates = (candidateBooks ?? Enumerable.Empty<Book>()).Where(book => book != null).ToList();
            var catalog = (authorCatalogBooks ?? candidates).Where(book => book != null).ToList();
            preparation ??= new CatalogPreparation();
            var result = new List<Book>();
            foreach (var group in candidates.GroupBy(book => (book.AuthorId, book.MediaType)))
            {
                var targets = group.ToList();
                var scopedCatalog = catalog.Where(book => book.AuthorId == group.Key.AuthorId && book.MediaType == group.Key.MediaType).ToList();
                var targetSet = targets.ToHashSet(BookReferenceComparer.Instance);
                var evidence = new ReleasePreparation(title, scopedCatalog, preparation);
                var competitors = scopedCatalog.Any(book => !targetSet.Contains(book))
                    ? GetCompetingSiblings(evidence, targets)
                    : new List<Book>();
                var matches = ScoreBooks(evidence, authorName, targets.Concat(competitors), authorName)
                    .Where(match => match.IsMatch).ToList();
                // Equivalent evidence must retain every row; text matching cannot select an arbitrary copy.
                result.AddRange(GetEquivalentBestMatches(matches).Where(match => targetSet.Contains(match.Book))
                    .Select(match => match.Book));
            }

            return result.DistinctBy(book => book.Id).ToList();
        }

        private static IEnumerable<TitleMatchResult> GetEquivalentBestMatches(List<TitleMatchResult> matches)
        {
            var best = matches.Aggregate((TitleMatchResult)null, ChooseBetterResult);
            return matches.Where(match =>
                ReferenceEquals(ChooseBetterResult(best, match), best) &&
                ReferenceEquals(ChooseBetterResult(match, best), match));
        }

        private static IEnumerable<TitleMatchResult> ScoreBooks(ReleasePreparation release, string authorName, IEnumerable<Book> candidateBooks, string releaseAuthorHint)
        {
            if (release.Tokens.Count == 0 || candidateBooks == null)
            {
                yield break;
            }

            var books = candidateBooks.Where(book => book != null).ToList();
            if (books.Count == 0)
            {
                yield break;
            }

            var expectedAuthor = GetExpectedAuthor(authorName, books);
            var hasAuthorHint = !string.IsNullOrWhiteSpace(releaseAuthorHint);
            var hasAuthorEvidence = hasAuthorHint && ReleaseIdentityEvidence.StructuredAuthorMatches(expectedAuthor, releaseAuthorHint);
            var authorSpans = hasAuthorHint || hasAuthorEvidence
                ? new List<(int Start, int End, bool Inverted)>()
                : ReleaseIdentityEvidence.FindAuthorNameSpans(release.CleanedTitle, release.TokenSpans, expectedAuthor).ToList();
            foreach (var book in books)
            {
                var candidate = ScoreAgainstBook(release, hasAuthorEvidence, authorSpans, book);
                if (candidate != null)
                {
                    candidate.EvidenceTitle = release.Title;
                    yield return candidate;
                }
            }
        }

        private static TitleMatchResult ScoreAgainstBook(ReleasePreparation release, bool hasAuthorEvidence,
            IReadOnlyList<(int Start, int End, bool Inverted)> authorSpans, Book book)
        {
            var preparation = release.Preparation;
            var releaseTokens = release.Tokens;
            var releaseTokenSpans = release.TokenSpans;
            var cleanedReleaseTitle = release.CleanedTitle;
            var authorCatalogBooks = release.Catalog;
            var context = preparation.GetContext(book);
            if (context.PrimaryVariants.Count == 0)
            {
                return null;
            }

            var scoringVariants = context.PrimaryVariants;
            var useNumberEquivalence = false;
            var titleSpans = scoringVariants.SelectMany(variant => release.GetSpans(preparation.GetTokens(variant)))
                .Distinct().ToList();

            if (titleSpans.Count == 0)
            {
                scoringVariants = release.GetNumberVariants(book)
                    .Select(variant => variant.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                titleSpans = scoringVariants.SelectMany(variant => FindNumberEquivalentTitleSpans(releaseTokens, preparation.GetTokens(variant), GetSelectedEdition(book)?.Language))
                    .Distinct().ToList();
                useNumberEquivalence = titleSpans.Count > 0;
            }

            if (titleSpans.Count == 0)
            {
                scoringVariants = preparation.GetVariants(book)
                    .Where(variant => variant.IsArticleVariant &&
                                      variant.Tokens.Count > 0 &&
                                      release.GetSpans(variant.Tokens).Count > 0 &&
                                      !IsArticleAliasAmbiguous(book, variant, authorCatalogBooks, preparation))
                    .Select(variant => variant.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                titleSpans = scoringVariants.SelectMany(variant => release.GetSpans(preparation.GetTokens(variant)))
                    .Distinct().ToList();
            }

            // Catalog evidence is needed only after some complete or shortened target title occurs.
            if (titleSpans.Count == 0 && !context.FallbackTitles.Keys.Any(candidateTitle =>
                    release.GetSpans(preparation.GetTokens(candidateTitle)).Count > 0))
            {
                return null;
            }

            var contradictoryVariants = BuildContradictoryVariants(book, release);
            var seriesSiblings = string.IsNullOrWhiteSpace(book?.SeriesName) ? new List<Book>() :
                (authorCatalogBooks ?? Enumerable.Empty<Book>())
                .Where(other => CanContradict(book, other, preparation) && preparation.TitlesEqual(other.SeriesName, book?.SeriesName))
                .ToList();

            if (titleSpans.Count > 0)
            {
                var explanations = GetTitleExplanations(release, titleSpans, context, contradictoryVariants);
                var problems = GetProblems(release, titleSpans, book?.Author?.Name, context, contradictoryVariants, seriesSiblings, hasAuthorEvidence || titleSpans.Any(span => ReleaseIdentityEvidence.HasAuthorEvidence(cleanedReleaseTitle, releaseTokenSpans, authorSpans, span.Start, span.End)), explanations);
                TitleMatchResult primaryBest = null;
                foreach (var variant in scoringVariants)
                {
                    primaryBest = ChooseBetterResult(primaryBest, ScoreAgainstTitleVariant(release, hasAuthorEvidence, authorSpans, book, variant, context, problems, useNumberEquivalence));
                }

                if (primaryBest != null)
                {
                    primaryBest.Explanations = explanations;
                }

                return primaryBest;
            }

            // No full title in the release. Every shortened title present is scored on its own spans and
            // the best result wins, so an ambiguous or position-contradicted prefix cannot hide a later
            // unique tail. Screening candidates instead of scoring them stops at the prefix and rejects.
            TitleMatchResult fallbackBest = null;
            foreach (var (candidateTitle, requiresTitleOmission) in context.FallbackTitles)
            {
                var baseTokens = preparation.GetTokens(candidateTitle);
                var candidateSpans = release.GetSpans(baseTokens);
                if (candidateSpans.Count == 0 ||
                    contradictoryVariants
                        .Any(variant => ((requiresTitleOmission ||
                            (!variant.RequiresTitleOmission && release.GetSpans(variant.Tokens).Count > 0)) &&
                            variant.Tokens.Count >= baseTokens.Count && FindExactTitleSpans(variant.Tokens, baseTokens).Any(span => span.Start == 0)) ||
                            (!variant.IsFallback && release.GetSpans(variant.Tokens).Count > 0 &&
                             !FindExactTitleSpans(baseTokens, variant.Tokens).Any())))
                {
                    continue;
                }

                var explanations = GetTitleExplanations(release, candidateSpans, context,
                    requiresTitleOmission ? null : contradictoryVariants);
                var candidateProblems = GetProblems(release, candidateSpans, book?.Author?.Name, context, contradictoryVariants, seriesSiblings, hasAuthorEvidence || candidateSpans.Any(span => ReleaseIdentityEvidence.HasAuthorEvidence(cleanedReleaseTitle, releaseTokenSpans, authorSpans, span.Start, span.End)), explanations);
                var candidate = ScoreAgainstTitleVariant(release, hasAuthorEvidence, authorSpans, book, candidateTitle, context, candidateProblems);
                candidate.RequiresTitleOmission = requiresTitleOmission;
                candidate.Explanations = explanations;
                fallbackBest = ChooseBetterResult(fallbackBest, candidate);
            }

            return fallbackBest;
        }

        private static TitleMatchResult ScoreAgainstTitleVariant(ReleasePreparation release, bool hasAuthorEvidence, IReadOnlyList<(int Start, int End, bool Inverted)> authorSpans, Book book, string bookTitleVariant, BookTitleMatchContext context, List<TitleMatchProblem> problems, bool useNumberEquivalence = false)
        {
            var releaseTokens = release.Tokens;
            var titleTokens = release.Preparation.GetTokens(bookTitleVariant);
            if (titleTokens.Count == 0)
            {
                return null;
            }

            var spans = useNumberEquivalence
                ? FindNumberEquivalentTitleSpans(releaseTokens, titleTokens, GetSelectedEdition(book)?.Language).ToList()
                : release.GetSpans(titleTokens);
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
                    IsMatch = problems.Count == 0 && (hasAuthorEvidence || ReleaseIdentityEvidence.HasAuthorEvidence(release.CleanedTitle, release.TokenSpans, authorSpans, span.Start, span.End))
                };

                best = ChooseBetterResult(best, candidate);
            }

            return best;
        }

        private static List<TitleMatchProblem> GetProblems(ReleasePreparation release,
                                                           IReadOnlyList<(int Start, int End)> titleSpans,
                                                           string authorName,
                                                           BookTitleMatchContext context,
                                                           IEnumerable<ContradictoryVariant> contradictoryVariants,
                                                           IReadOnlyList<Book> seriesSiblings,
                                                           bool hasAuthorIdentityEvidence,
                                                           IReadOnlyList<TitleOccurrenceExplanation> explanations)
        {
            var releaseTokens = release.Tokens;
            var releaseTokenSpans = release.TokenSpans;
            var cleanedReleaseTitle = release.CleanedTitle;
            var problems = new List<TitleMatchProblem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var problem in titleSpans.SelectMany(span => GetAdjacentSeriesNumberProblems(releaseTokens, releaseTokenSpans, cleanedReleaseTitle, span.Start, span.End, context, seriesSiblings, hasAuthorIdentityEvidence)))
            {
                if (seen.Add(problem.Value))
                {
                    problems.Add(problem);
                }
            }

            var authorSpans = release.GetSpans(release.Preparation.GetTokens(authorName));
            var titleIsSeries = release.Preparation.TitlesEqual(context?.PrimaryTitle, context?.SeriesName);
            foreach (var contradiction in contradictoryVariants ?? Array.Empty<ContradictoryVariant>())
            {
                var titleOccurrences = release.GetSpans(contradiction.Tokens);
                if (titleOccurrences.Count == 0)
                {
                    continue;
                }

                var spans = titleOccurrences.Where(span => !authorSpans.Any(authorSpan => span.Start >= authorSpan.Start && span.End <= authorSpan.End))
                    .Where(span => !contradiction.IsNumberEquivalent || titleSpans.Any(titleSpan => span.Start <= titleSpan.Start && span.End >= titleSpan.End))
                    .Where(span => titleSpans.Any(titleSpan => span.Start <= titleSpan.End + 1 && span.End >= titleSpan.Start - 1) ||
                        titleIsSeries);
                if (GetTitleExplanations(release, spans, null).Any(title =>
                        !TitleOccurrenceComparison.ExplainsTitle(title, explanations)) &&
                    seen.Add(contradiction.Title))
                {
                    problems.Add(new TitleMatchProblem
                    {
                        Code = TitleMatchProblemCode.SiblingTitleContradiction,
                        Value = contradiction.Title
                    });
                }
            }

            return problems;
        }

        private static List<TitleOccurrenceExplanation> GetTitleExplanations(ReleasePreparation release,
            IEnumerable<(int Start, int End)> titles, BookTitleMatchContext context,
            IEnumerable<ContradictoryVariant> contradictions = null)
        {
            var seriesTitles = context?.SeriesTitles ?? Array.Empty<IReadOnlyList<string>>();
            var spans = titles.Select(span => (Span: span, Series: false))
                .Concat(seriesTitles.SelectMany(release.GetSpans).Select(span => (Span: span, Series: true))).ToList();
            if (seriesTitles.Count > 0 && contradictions != null)
            {
                var explained = spans.SelectMany(item => Enumerable.Range(item.Span.Start, item.Span.End - item.Span.Start + 1)).ToHashSet();
                // After independent title proof, an attested series can explain the remaining
                // contiguous phrase of a competing title. Loose word overlap is not evidence.
                foreach (var occurrence in contradictions.SelectMany(variant => release.GetSpans(variant.Tokens)).Distinct())
                {
                    for (var start = occurrence.Start; start <= occurrence.End; start++)
                    {
                        if (explained.Contains(start))
                        {
                            continue;
                        }

                        var end = start;
                        while (end < occurrence.End && !explained.Contains(end + 1))
                        {
                            end++;
                        }

                        var phrase = release.Tokens.Skip(start).Take(end - start + 1).ToList();
                        if (seriesTitles.Any(series => FindExactTitleSpans(series, phrase).Any()))
                        {
                            spans.Add(((start, end), true));
                        }

                        start = end;
                    }
                }
            }

            var key = string.Join("\u001f", release.Tokens);
            return spans.Distinct().Select(item => new TitleOccurrenceExplanation
            {
                OccurrenceKey = key,
                IsSeriesContext = item.Series,
                MeaningfulConsumedIndexes = Enumerable.Range(item.Span.Start, item.Span.End - item.Span.Start + 1).ToHashSet()
            }).ToList();
        }

        private static bool TokenizedEquals(string left, string right)
        {
            return TokenizedEquals(Tokenize(left), Tokenize(right));
        }

        private static bool TokenizedEquals(IReadOnlyList<string> leftTokens, IReadOnlyList<string> rightTokens)
        {
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

        private static IEnumerable<TitleMatchProblem> GetAdjacentSeriesNumberProblems(IReadOnlyList<string> releaseTokens,
                                                                                       IReadOnlyList<TitleTokenSpan> releaseTokenSpans,
                                                                                       string cleanedReleaseTitle,
                                                                                       int matchedStart,
                                                                                       int matchedEnd,
                                                                                       BookTitleMatchContext context,
                                                                                       IReadOnlyList<Book> seriesSiblings,
                                                                                       bool hasAuthorIdentityEvidence)
        {
            var previousIndex = matchedStart - 1;
            if (!hasAuthorIdentityEvidence && releaseTokens != null && previousIndex >= 0)
            {
                var previousToken = releaseTokens[previousIndex];
                var precedingProblem = GetAdjacentSeriesNumberProblem(releaseTokens, releaseTokenSpans, cleanedReleaseTitle, matchedStart, matchedEnd, previousIndex, previousToken, previousToken, context, seriesSiblings, hasAuthorIdentityEvidence);
                if (precedingProblem != null)
                {
                    yield return precedingProblem;
                }
            }

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
                var problem = GetAdjacentSeriesNumberProblem(releaseTokens, releaseTokenSpans, cleanedReleaseTitle, matchedStart, matchedEnd, nextIndex + 1, $"{nextToken} {markedNumber}", markedNumber, context, seriesSiblings, hasAuthorIdentityEvidence);
                if (problem != null)
                {
                    yield return problem;
                }

                yield break;
            }

            var adjacentProblem = GetAdjacentSeriesNumberProblem(releaseTokens, releaseTokenSpans, cleanedReleaseTitle, matchedStart, matchedEnd, nextIndex, nextToken, nextToken, context, seriesSiblings, hasAuthorIdentityEvidence);
            if (adjacentProblem != null)
            {
                yield return adjacentProblem;
            }
        }

        private static TitleMatchProblem GetAdjacentSeriesNumberProblem(IReadOnlyList<string> releaseTokens,
                                                                        IReadOnlyList<TitleTokenSpan> releaseTokenSpans,
                                                                        string cleanedReleaseTitle,
                                                                        int matchedStart,
                                                                        int matchedEnd,
                                                                        int numberIndex,
                                                                        string displayValue,
                                                                        string numberToken,
                                                                        BookTitleMatchContext context,
                                                                        IReadOnlyList<Book> seriesSiblings,
                                                                        bool hasAuthorIdentityEvidence)
        {
            var isSeriesSlot = MatchedSpanIsTargetSeries(releaseTokens, matchedStart, matchedEnd, context);

            // Infer a Roman position only where the matched title is its own series name.
            if (!(isSeriesSlot
                    ? TryParseSeriesPositionSlotToken(numberToken, out var slotNumber)
                    : TryParseNumberToken(numberToken, out slotNumber)))
            {
                return null;
            }

            if (YearTokenRegex.IsMatch(slotNumber.ToString("0", CultureInfo.InvariantCulture)))
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

            if (numberIndex == matchedEnd + 1 && TryParseNumberToken(numberToken, out _) &&
                seriesSiblings?.Any(sibling => SeriesPositionSetContains(sibling.SeriesPosition, slotNumber)) != true &&
                IsIncidentalQuotedFilenameNumber(releaseTokens, releaseTokenSpans, cleanedReleaseTitle, numberIndex, context))
            {
                return null;
            }

            if (SeriesPositionSetContains(context?.SeriesPosition, slotNumber) &&
                (hasAuthorIdentityEvidence || isSeriesSlot ||
                 (numberIndex > 0 && IsSeriesPositionMarker(releaseTokens[numberIndex - 1])) ||
                 FindExactTitleSpans(releaseTokens, Tokenize(context?.SeriesName))
                     .Any(span => span.End == numberIndex - 1)))
            {
                return null;
            }

            // A Roman-shaped word needs corroborating series metadata. A following title does not
            // erase a position that identifies another work; numerals inside the matched title are exempt.
            if (!TryParseNumberToken(numberToken, out _) &&
                seriesSiblings?.Any(sibling => SeriesPositionSetContains(sibling?.SeriesPosition, slotNumber)) != true)
            {
                return null;
            }

            return new TitleMatchProblem
            {
                Code = HasSeriesPositionNumber(context) && isSeriesSlot
                    ? TitleMatchProblemCode.SeriesPositionMismatch
                    : TitleMatchProblemCode.SuspiciousAdjacentNumber,
                Value = displayValue
            };
        }

        private static bool IsIncidentalQuotedFilenameNumber(IReadOnlyList<string> releaseTokens,
                                                             IReadOnlyList<TitleTokenSpan> releaseTokenSpans,
                                                             string cleanedReleaseTitle,
                                                             int numberIndex,
                                                             BookTitleMatchContext context)
        {
            if (releaseTokens == null ||
                releaseTokenSpans == null ||
                string.IsNullOrWhiteSpace(cleanedReleaseTitle) ||
                numberIndex < 0 ||
                numberIndex >= releaseTokenSpans.Count)
            {
                return false;
            }

            if (!TryGetQuotedMediaFilenameRange(cleanedReleaseTitle, releaseTokenSpans[numberIndex], out var quotedRange))
            {
                return false;
            }

            // A marked position remains identity evidence even when a publisher places it in
            // the quoted filename. Only an unmarked number immediately before an audio extension
            // has the shape of a track number.
            if (numberIndex > 0 &&
                (IsSeriesPositionMarker(releaseTokens[numberIndex - 1]) ||
                 cleanedReleaseTitle.Substring(releaseTokenSpans[numberIndex - 1].End,
                     releaseTokenSpans[numberIndex].Start - releaseTokenSpans[numberIndex - 1].End).Contains('#')))
            {
                return false;
            }

            // A separate complete title distinguishes attachment numbering from the only title evidence.
            return (context?.PrimaryVariants ?? Enumerable.Empty<string>())
                .SelectMany(variant => FindExactTitleSpans(releaseTokens, Tokenize(variant)))
                .Any(span => releaseTokenSpans[span.End].End <= quotedRange.Start ||
                             releaseTokenSpans[span.Start].Start >= quotedRange.End);
        }

        private static bool TryGetQuotedMediaFilenameRange(string source, TitleTokenSpan token, out (int Start, int End) range)
        {
            range = default;
            if (string.IsNullOrWhiteSpace(source) || token == null)
            {
                return false;
            }

            foreach (var delimiter in QuotedFilenameDelimiters)
            {
                var openIndex = token.Start > 0 ? source.LastIndexOf(delimiter.Open, token.Start - 1) : -1;
                if (openIndex < 0)
                {
                    continue;
                }

                var closeIndex = source.IndexOf(delimiter.Close, token.End);
                if (closeIndex <= openIndex)
                {
                    continue;
                }

                var filename = source.Substring(openIndex + 1, closeIndex - openIndex - 1);
                var extensionIndex = filename.LastIndexOf('.');
                var extension = extensionIndex >= 0 ? filename.Substring(extensionIndex) : string.Empty;
                if (!MediaFileExtensions.AudioExtensions.Contains(extension))
                {
                    continue;
                }

                var tokenSuffix = source.Substring(token.End, closeIndex - token.End).Trim();
                if (!string.Equals(tokenSuffix, extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                range = (openIndex, closeIndex + 1);
                return token.Start >= range.Start && token.End <= range.End;
            }

            return false;
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
                   !YearTokenRegex.IsMatch(releaseTokens[numberIndex + 1]) &&
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

        // A scalar mismatch cannot be inferred from a work occupying several positions.
        private static bool HasSeriesPositionNumber(BookTitleMatchContext context)
        {
            return TryGetSeriesPositionNumber(context?.SeriesPosition, out _);
        }

        // Lists and ranges retain every position occupied by the work.
        private static List<(decimal Start, decimal End)> GetSeriesPositionRanges(string seriesPosition)
        {
            return ParseSeriesPositionRanges(seriesPosition, out _);
        }

        internal static bool TryGetCompleteSeriesPositionRanges(
            string seriesPosition,
            out List<(decimal Start, decimal End)> ranges)
        {
            ranges = ParseSeriesPositionRanges(seriesPosition, out var isComplete);
            return isComplete && ranges.Count > 0;
        }

        private static List<(decimal Start, decimal End)> ParseSeriesPositionRanges(
            string seriesPosition,
            out bool isComplete)
        {
            var ranges = new List<(decimal Start, decimal End)>();
            isComplete = !string.IsNullOrWhiteSpace(seriesPosition);

            if (!isComplete)
            {
                return ranges;
            }

            var isWholePosition = TryGetSeriesPositionNumber(seriesPosition, out var whole);
            if (isWholePosition)
            {
                ranges.Add((whole, whole));
            }

            var normalized = seriesPosition.Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2212', '-');

            foreach (var part in normalized.Split(',', StringSplitOptions.None))
            {
                var bounds = part.Split('-', StringSplitOptions.RemoveEmptyEntries);
                var rawBounds = part.Split('-');
                var parsedPart = false;
                var parsedAsRange = false;

                if (bounds.Length == 2 &&
                    TryGetSeriesPositionNumber(bounds[0], out var start) &&
                    TryGetSeriesPositionNumber(bounds[1], out var end) &&
                    start <= end)
                {
                    ranges.Add((start, end));
                    parsedPart = true;
                    parsedAsRange = true;
                }
                else if (TryGetSeriesPositionNumber(part, out var member))
                {
                    ranges.Add((member, member));
                    parsedPart = true;
                }

                if (!parsedPart && !isWholePosition)
                {
                    isComplete = false;
                }

                if (parsedPart && (parsedAsRange ? rawBounds.Length != 2 : rawBounds.Length != 1))
                {
                    isComplete = false;
                }
            }

            return ranges;
        }

        private static bool SeriesPositionSetContains(string seriesPosition, decimal observed)
        {
            return GetSeriesPositionRanges(seriesPosition).Any(range => observed >= range.Start && observed <= range.End);
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

        // Interpret Roman numerals only in a proven series-position slot; titles, initials and
        // scene tags can contain the same tokens without expressing a position.
        private static bool TryParseSeriesPositionSlotToken(string token, out decimal number)
        {
            if (TryParseNumberToken(token, out number))
            {
                return true;
            }

            number = default;
            if (!UnicodeComparisonNormalizer.TryParseRomanNumeral(token, out var roman) || roman <= 0)
            {
                return false;
            }

            number = roman;
            return true;
        }

        private static bool IsPartMarker(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && PartMarkerTokens.Contains(token);
        }

        private static bool IsSeriesPositionMarker(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && SeriesPositionMarkerTokens.Contains(token);
        }

        private static IEnumerable<ContradictoryVariant> BuildContradictoryVariants(Book targetBook, ReleasePreparation release)
        {
            return release.Catalog.Where(other => CanContradict(targetBook, other, release.Preparation))
                .SelectMany(release.GetVariants);
        }

        private static IEnumerable<ContradictoryVariant> GetNumberEquivalentVariants(Book book, ReleasePreparation release)
        {
            var releaseTokens = release.Tokens;
            var authorCatalogBooks = release.Catalog;
            var preparation = release.Preparation;
            var language = GetSelectedEdition(book)?.Language;
            foreach (var variant in preparation.GetVariants(book).Where(variant => !variant.IsFallback && !variant.IsArticleVariant))
            {
                if (release.GetSpans(variant.Tokens).Count > 0)
                {
                    continue;
                }

                preparation.NumberVariantChecks++;
                var spans = FindNumberEquivalentTitleSpans(releaseTokens, variant.Tokens, language).ToList();
                if (spans.Count == 0 || (authorCatalogBooks ?? Enumerable.Empty<Book>())
                    .Where(other => AreSameAuthor(book, other) && CanContradict(book, other, preparation))
                    .Any(other => preparation.GetVariants(other)
                        .Where(otherVariant => !otherVariant.IsFallback && !otherVariant.IsArticleVariant)
                        .Any(otherVariant => AreNumberEquivalentTitles(
                            variant.Tokens, language, otherVariant.Tokens, GetSelectedEdition(other)?.Language))))
                {
                    continue;
                }

                // Preserve the observed span; numeric aliases never grant another work's identity.
                foreach (var span in spans)
                {
                    yield return new ContradictoryVariant
                    {
                        Title = variant.Title,
                        IsNumberEquivalent = true,
                        Tokens = releaseTokens.Skip(span.Start).Take(span.End - span.Start + 1).ToList()
                    };
                }
            }
        }

        private static IEnumerable<string> GetFullTitleArticleVariants(Book book)
        {
            var edition = GetSelectedEdition(book);
            var title = NormalizeExpandedVariant(edition?.Title);
            if (edition == null || string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            var articles = TitleArticleCatalog.GetArticles(edition.Language);
            if (articles.Count == 0)
            {
                yield break;
            }

            foreach (var qualifierVariant in ExpandOptionalQualifierVariants(title, book))
            {
                var qualifierTokens = Tokenize(qualifierVariant);
                foreach (var articleVariant in ExpandOptionalArticleBoundaryVariants(qualifierVariant, articles))
                {
                    foreach (var variant in ExpandTransliterationVariants(articleVariant))
                    {
                        var variantTokens = Tokenize(variant);
                        if (variantTokens.Count > 0 && !variantTokens.SequenceEqual(qualifierTokens, StringComparer.Ordinal))
                        {
                            yield return variant;
                        }
                    }
                }
            }
        }

        private static IEnumerable<string> ExpandOptionalArticleBoundaryVariants(string title, IReadOnlyCollection<string> articles)
        {
            var normalized = NormalizeExpandedVariant(title);
            if (string.IsNullOrWhiteSpace(normalized) || articles == null || articles.Count == 0)
            {
                yield break;
            }

            var articleSet = articles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var boundaryIndexes = new[] { 0 }
                .Concat(OptionalArticleSubtitleBoundaryRegex.Matches(normalized).Cast<Match>().Select(match => match.Groups["head"].Index))
                .Distinct()
                .ToList();
            var articleRanges = new List<(int Start, int Length)>();
            var insertionIndexes = new List<int>();

            foreach (var boundaryIndex in boundaryIndexes)
            {
                var word = LeadingWordRegex.Match(normalized.Substring(boundaryIndex));
                if (!word.Success)
                {
                    continue;
                }

                var article = word.Groups["word"].Value;
                if (articleSet.Contains(article))
                {
                    articleRanges.Add((boundaryIndex, word.Groups["word"].Length));
                }
                else if (boundaryIndex > 0)
                {
                    // A leading addition already leaves the original title as an exact span.
                    insertionIndexes.Add(boundaryIndex);
                }
            }

            foreach (var range in articleRanges)
            {
                yield return NormalizeExpandedVariant(normalized.Remove(range.Start, range.Length));
            }

            if (articleRanges.Count > 1)
            {
                var withoutBoundaryArticles = normalized;
                foreach (var range in articleRanges.OrderByDescending(range => range.Start))
                {
                    withoutBoundaryArticles = withoutBoundaryArticles.Remove(range.Start, range.Length);
                }

                yield return NormalizeExpandedVariant(withoutBoundaryArticles);
            }

            foreach (var insertionIndex in insertionIndexes)
            {
                foreach (var article in articles)
                {
                    var displayArticle = char.ToUpperInvariant(article[0]) + article.Substring(1);
                    yield return NormalizeExpandedVariant(normalized.Insert(insertionIndex, displayArticle + " "));
                }
            }
        }

        private static bool IsArticleAliasAmbiguous(Book book,
                                                    ContradictoryVariant variant,
                                                    IEnumerable<Book> authorCatalogBooks,
                                                    CatalogPreparation preparation)
        {
            if (book == null || variant?.Tokens == null || variant.Tokens.Count == 0)
            {
                return false;
            }

            foreach (var otherBook in authorCatalogBooks ?? Enumerable.Empty<Book>())
            {
                if (otherBook == null || !AreSameAuthor(book, otherBook) || !CanContradict(book, otherBook, preparation))
                {
                    continue;
                }

                // Primary variants retain the existing qualifier policy; subtitle fragments cannot own an alias.
                if (preparation.GetVariants(otherBook)
                    .Where(otherVariant => !otherVariant.IsFallback && otherVariant.Tokens.Count > 0)
                    .Any(otherVariant => CompleteTitleAliasesMatch(variant.Tokens, otherVariant.Tokens)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AreSameAuthor(Book left, Book right)
        {
            if (left?.AuthorId > 0 && right?.AuthorId > 0)
            {
                return left.AuthorId == right.AuthorId;
            }

            return left?.Author != null && ReferenceEquals(left.Author, right?.Author);
        }

        private static bool CompleteTitleAliasesMatch(IReadOnlyList<string> leftTokens, IReadOnlyList<string> rightTokens)
        {
            return FindExactTitleSpans(leftTokens, rightTokens)
                       .Any(span => span.Start == 0 && span.End == leftTokens.Count - 1) &&
                   FindExactTitleSpans(rightTokens, leftTokens)
                       .Any(span => span.Start == 0 && span.End == rightTokens.Count - 1);
        }

        private static bool CanContradict(Book targetBook, Book otherBook, CatalogPreparation preparation)
        {
            if (targetBook == null || otherBook == null || targetBook.MediaType != otherBook.MediaType)
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

            if (preparation.IsSameWork(targetBook, otherBook))
            {
                return false;
            }

            // Identical full edition titles cannot distinguish competing works; shortened variants can.
            var targetTitle = preparation.GetContext(targetBook).SelectedEditionTitleTokens;
            return targetTitle.Count == 0 ||
                   !targetTitle.SequenceEqual(preparation.GetContext(otherBook).SelectedEditionTitleTokens, StringComparer.Ordinal);
        }

        private static Author GetExpectedAuthor(string authorName, IEnumerable<Book> books)
        {
            return books?.Select(book => book?.LazyAuthor)
                .FirstOrDefault(author => author?.IsLoaded == true && string.Equals(author.Value?.Name, authorName, StringComparison.OrdinalIgnoreCase))?.Value
                ?? new Author { Name = authorName };
        }

        public static IEnumerable<(int Start, int End)> FindExactTitleSpans(IReadOnlyList<string> releaseTokens, IReadOnlyList<string> titleTokens)
        {
            return FindTitleSpans(releaseTokens, titleTokens, null, null, false);
        }

        internal static IEnumerable<(int Start, int End)> FindNumberEquivalentTitleSpans(IReadOnlyList<string> releaseTokens, IReadOnlyList<string> titleTokens, string editionLanguage)
        {
            return FindTitleSpans(releaseTokens, titleTokens, editionLanguage, editionLanguage, true);
        }

        internal static bool AreNumberEquivalentTitles(IReadOnlyList<string> leftTokens, string leftLanguage,
                                                       IReadOnlyList<string> rightTokens, string rightLanguage)
        {
            // Collision checks are symmetric even when only one title establishes numeric intent.
            // Each stored title supplies its own language.
            return FindTitleSpans(leftTokens, rightTokens, rightLanguage, leftLanguage, true)
                .Any(span => span.Start == 0 && span.End == leftTokens.Count - 1) ||
                FindTitleSpans(rightTokens, leftTokens, leftLanguage, rightLanguage, true)
                    .Any(span => span.Start == 0 && span.End == rightTokens.Count - 1);
        }

        private static IEnumerable<(int Start, int End)> FindTitleSpans(IReadOnlyList<string> releaseTokens, IReadOnlyList<string> titleTokens,
                                                                       string editionLanguage, string observedLanguage, bool allowNumbers)
        {
            if (releaseTokens == null || titleTokens == null || titleTokens.Count == 0 || releaseTokens.Count == 0)
            {
                yield break;
            }

            for (var start = 0; start < releaseTokens.Count; start++)
            {
                if (TryMatchTitleSpan(releaseTokens, titleTokens, start, 0, out var end, editionLanguage, observedLanguage, allowNumbers))
                {
                    yield return (start, end);
                }
            }
        }

        private static bool TryMatchTitleSpan(IReadOnlyList<string> releaseTokens, IReadOnlyList<string> titleTokens, int releaseIndex, int titleIndex, out int matchedEnd, string editionLanguage, string observedLanguage, bool allowNumbers)
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
                TryMatchTitleSpan(releaseTokens, titleTokens, releaseIndex + 1, titleIndex + 1, out matchedEnd, editionLanguage, observedLanguage, allowNumbers))
            {
                return true;
            }

            if (releaseIndex + 1 < releaseTokens.Count &&
                TitleTokenAlignment.TokensMatchCompactSplit(titleTokens[titleIndex], releaseTokens[releaseIndex], releaseTokens[releaseIndex + 1]) &&
                TryMatchTitleSpan(releaseTokens, titleTokens, releaseIndex + 2, titleIndex + 1, out matchedEnd, editionLanguage, observedLanguage, allowNumbers))
            {
                return true;
            }

            if (titleIndex + 1 < titleTokens.Count &&
                TitleTokenAlignment.TokensMatchCompactSplit(releaseTokens[releaseIndex], titleTokens[titleIndex], titleTokens[titleIndex + 1]) &&
                TryMatchTitleSpan(releaseTokens, titleTokens, releaseIndex + 1, titleIndex + 2, out matchedEnd, editionLanguage, observedLanguage, allowNumbers))
            {
                return true;
            }

            if (allowNumbers)
            {
                foreach (var expected in TitleNumberCatalog.GetNumberMatches(titleTokens, titleIndex, editionLanguage))
                {
                    foreach (var observed in TitleNumberCatalog.GetNumberMatches(releaseTokens, releaseIndex, observedLanguage))
                    {
                        if (expected.Value == observed.Value &&
                            TryMatchTitleSpan(releaseTokens, titleTokens, releaseIndex + observed.TokenCount, titleIndex + expected.TokenCount,
                                out matchedEnd, editionLanguage, observedLanguage, true))
                        {
                            return true;
                        }
                    }
                }
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

            if (!ReferenceEquals(candidate.Book, current.Book) && candidate.Book?.MediaType == current.Book?.MediaType &&
                AreSameAuthor(candidate.Book, current.Book) && !WorkIdMatcher.WorkIdMatches(candidate.Book, current.Book))
            {
                // I may want to revisit this later. Will check server side about reimplementing noise gating, but this seems sound in principle.
                if (candidate.RequiresTitleOmission != current.RequiresTitleOmission)
                {
                    var complete = candidate.RequiresTitleOmission ? current : candidate;
                    var shortened = candidate.RequiresTitleOmission ? candidate : current;
                    // Prefer an explanation without omissions only when it covers the rival's
                    // title evidence too. A shorter contained title cannot win by being shorter.
                    if (shortened.Explanations.Count > 0 && shortened.Explanations.Where(item => !item.IsSeriesContext)
                        .All(title => TitleOccurrenceComparison.ExplainsTitle(title, complete.Explanations)))
                    {
                        return complete;
                    }
                }

                var comparison = TitleOccurrenceComparison.CompareWorkOccurrenceExplanations(candidate.Explanations, current.Explanations, true);
                if (comparison != 0)
                {
                    return comparison > 0 ? candidate : current;
                }
            }

            if (candidate.MeaningfulLeftoverCount != current.MeaningfulLeftoverCount)
            {
                return candidate.MeaningfulLeftoverCount < current.MeaningfulLeftoverCount ? candidate : current;
            }

            var candidateTokenCount = Tokenize(candidate.MatchedVariant).Count;
            var currentTokenCount = Tokenize(current.MatchedVariant).Count;
            return candidateTokenCount > currentTokenCount ? candidate : current;
        }

        internal static BookTitleMatchContext GetBookTitleMatchContext(Book book)
        {
            var context = new BookTitleMatchContext
            {
                PrimaryTitle = GetPrimaryBookTitle(book),
                SelectedEditionTitleTokens = Tokenize(GetSelectedEditionTitle(book)),
                SeriesName = book?.SeriesName,
                SeriesPosition = book?.SeriesPosition,
                SeriesTitles = BookSeries.GetNames(book).SelectMany(TitleBracketCatalog.FullThenBase)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Select(Tokenize).ToList()
            };
            AddTitleVariants(context.PrimaryVariants, context.PrimaryTitle, book);

            // Shortened titles retain the selected edition's language; sibling checks reject ambiguous bases.
            var editionTitle = NormalizeExpandedVariant(GetSelectedEditionTitle(book));
            var subtitlePattern = string.Join("|", GetSubtitleBoundaries().Select(boundary => Regex.Escape(boundary.Prefix)));
            foreach (Match boundary in Regex.Matches(editionTitle, subtitlePattern).Cast<Match>().Reverse())
            {
                var subtitle = editionTitle.Substring(boundary.Index + boundary.Length);
                var isBracketBoundary = TitleBracketCatalog.Openers.Contains(boundary.Value.TrimEnd()[^1]);
                if (!string.IsNullOrWhiteSpace(subtitle) &&
                    !Tokenize(subtitle).Any(IsNumericToken) &&
                    !SeriesPositionTokenHelper.HasPositionIdentity(subtitle) && !isBracketBoundary)
                {
                    context.FallbackTitles.TryAdd(editionTitle.Substring(0, boundary.Index).Trim(), true);
                }
            }

            AddSeriesTailVariant(context, editionTitle);

            foreach (var baseTitle in GetSeriesMetadataBaseTitles(context.PrimaryVariants, context.SeriesName, context.SeriesPosition))
            {
                AddVariant(context.PrimaryVariants, baseTitle);
            }

            return context;
        }

        internal static IReadOnlyList<string> GetSeriesMetadataBaseTitles(IEnumerable<string> variants, string seriesName, string seriesPosition)
        {
            var baseTitles = new List<string>();
            if (string.IsNullOrWhiteSpace(seriesName) || !TryGetSeriesPositionNumber(seriesPosition, out var expected))
            {
                return baseTitles;
            }

            // A complete trailing series and position restates the book's stored metadata.
            foreach (var variant in variants)
            {
                var tokens = TokenizeWithSpans(variant);
                foreach (var span in FindExactTitleSpans(tokens.Select(token => token.Value).ToList(), Tokenize(seriesName)))
                {
                    var position = string.Join(" ", tokens.Skip(span.End + 1).Select(token => token.Value));
                    if (span.Start > 0 && TryGetSeriesPositionNumber(position, out var actual) &&
                        actual == expected &&
                        TryGetKnownSubtitleBaseTitle(variant, variant.Substring(tokens[span.Start].Start), out var baseTitle))
                    {
                        AddVariant(baseTitles, baseTitle);
                    }
                }
            }

            return baseTitles;
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

                tail = RemoveMatchingSeriesPositionQualifier(tail, context.SeriesPosition);
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    context.FallbackTitles[tail] = false;
                }
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


        private static void AddTitleVariants(List<string> variants, string title, Book book)
        {
            foreach (var variant in ExpandOptionalQualifierVariants(title, book).SelectMany(ExpandTransliterationVariants))
            {
                AddVariant(variants, variant);
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
                yield return scandinavian;
            }

            var germanic = ApplyGermanicTransliteration(title);
            if (!string.Equals(germanic, title, StringComparison.Ordinal))
            {
                yield return germanic;
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
            foreach (var opener in TitleBracketCatalog.Openers)
            {
                yield return (" " + opener, string.Empty);
            }
        }

        private static IEnumerable<string> ExpandOptionalQualifierVariants(string title, Book book)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            yield return title;

            // Brackets do not make title content optional. Only independently known metadata can be omitted.
            var withoutNarratorNotes = ReleaseNarratorEvidenceExtractor.RemoveKnownNarratorNotes(
                title, ReleaseIdentityEvidence.GetKnownNarrators(book));
            var withoutQualifiers = OptionalQualifierSegmentRegex.Replace(withoutNarratorNotes, match =>
            {
                var label = match.Groups["label"].Value;
                return TokenizedEquals(label, book?.SeriesName) ||
                       AudioProductionDetector.IsProductionLabel(label) ||
                       AudioProductionConstants.AudiobookIndicators
                           .Any(indicator => TokenizedEquals(label, indicator))
                    ? " " : match.Value;
            });
            withoutQualifiers = NormalizeExpandedVariant(withoutQualifiers);
            if (!string.IsNullOrWhiteSpace(withoutQualifiers) && !string.Equals(withoutQualifiers, title, StringComparison.OrdinalIgnoreCase))
            {
                yield return withoutQualifiers;
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
            mappedText = ApplyReplacement(mappedText, StringSuperNormalizer.PossessiveRegex, "s");
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
            if (!input.Text.Contains('&'))
            {
                return input;
            }

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
            var matches = regex.Matches(input.Text);
            if (matches.Count == 0)
            {
                return input;
            }

            var builder = new StringBuilder(input.Text.Length);
            var spans = new List<(int Start, int End)>();
            var position = 0;

            foreach (Match match in matches)
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

        internal static bool IsMetadataToken(string token)
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
