using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IMakeDownloadDecision
    {
        List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false);
        List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase);
    }

    public class DownloadDecisionMaker : IMakeDownloadDecision
    {
        private readonly IEnumerable<IDecisionEngineSpecification> _specifications;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IParsingService _parsingService;
        private readonly IEditionFtsRepository _editionFtsRepository;
        private readonly IRemoteBookAggregationService _aggregationService;
        private readonly IReleaseNarratorMetadataEnricher _releaseNarratorMetadataEnricher;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public DownloadDecisionMaker(IEnumerable<IDecisionEngineSpecification> specifications,
            IParsingService parsingService,
            IEditionFtsRepository editionFtsRepository,
            ICustomFormatCalculationService formatService,
            IRemoteBookAggregationService aggregationService,
            IReleaseNarratorMetadataEnricher releaseNarratorMetadataEnricher,
            IConfigService configService,
            Logger logger)
        {
            _specifications = specifications;
            _parsingService = parsingService;
            _editionFtsRepository = editionFtsRepository;
            _formatCalculator = formatService;
            _aggregationService = aggregationService;
            _releaseNarratorMetadataEnricher = releaseNarratorMetadataEnricher;
            _configService = configService;
            _logger = logger;
        }

        public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false)
        {
            return GetBookDecisions(reports).ToList();
        }

        public List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase)
        {
            return GetBookDecisions(reports, false, searchCriteriaBase).ToList();
        }

        private IEnumerable<DownloadDecision> GetBookDecisions(List<ReleaseInfo> reports, bool pushedRelease = false, SearchCriteriaBase searchCriteria = null)
        {
            if (reports.Any())
            {
                _logger.ProgressInfo("Processing {0} releases", reports.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            EnrichReleaseNarratorMetadata(reports, searchCriteria);
            var orderedReports = reports;

            var reportNumber = 1;

            foreach (var report in orderedReports)
            {
                DownloadDecision decision = null;
                var isMamIndexer = IsMAMIndexer(report.Indexer);
                var releaseSource = pushedRelease ? ReleaseSourceType.ReleasePush : ReleaseSourceType.Rss;

                if (searchCriteria != null)
                {
                    releaseSource = searchCriteria.InteractiveSearch
                        ? ReleaseSourceType.InteractiveSearch
                        : searchCriteria.UserInvokedSearch
                            ? ReleaseSourceType.UserInvokedSearch
                            : ReleaseSourceType.Search;
                }

                _logger.ProgressTrace("Processing release {0}/{1}", reportNumber, reports.Count);
                _logger.Debug("Processing release '{0}' from '{1}'", report.Title, report.Indexer);

                try
                {
                    ParsedBookInfo parsedBookInfo = null;
                    RemoteBook remoteBook = null;
                    var attemptedCriteriaParse = false;
                    var searchDecision = TryBuildSearchCriteriaDecision(report, searchCriteria, releaseSource);

                    if (searchDecision != null)
                    {
                        decision = searchDecision;
                    }
                    else
                    {
                        // For single-book searches, prefer matching against the known target metadata first.
                    // This avoids penalizing rich titles that include subtitle/narrators/year and prevents
                    // accidental mapping to a different book by the same author.
                    if (searchCriteria?.Author != null && searchCriteria.Books?.Count == 1)
                    {
                        parsedBookInfo = Parser.Parser.ParseBookTitleWithSearchCriteria(report.Title,
                                                                                        searchCriteria.Author,
                                                                                        searchCriteria.Books);
                        attemptedCriteriaParse = true;
                    }

                    parsedBookInfo ??= Parser.Parser.ParseBookTitle(report.Title);

                    if (parsedBookInfo == null && searchCriteria != null && !attemptedCriteriaParse)
                    {
                        parsedBookInfo = Parser.Parser.ParseBookTitleWithSearchCriteria(report.Title,
                                                                                        searchCriteria.Author,
                                                                                        searchCriteria.Books);
                    }

                    if (searchCriteria == null)
                    {
                        parsedBookInfo ??= new ParsedBookInfo();

                        if (parsedBookInfo.AuthorName.IsNullOrWhiteSpace())
                        {
                            parsedBookInfo.AuthorName = report.Author;
                        }

                        if (parsedBookInfo.BookTitle.IsNullOrWhiteSpace())
                        {
                            parsedBookInfo.BookTitle = FirstNotBlank(report.Book, report.Title);
                        }

                        if (parsedBookInfo.ReleaseTitle.IsNullOrWhiteSpace())
                        {
                            parsedBookInfo.ReleaseTitle = report.Title;
                        }

                        if (parsedBookInfo.Quality == null || parsedBookInfo.Quality.Quality == Quality.Unknown)
                        {
                            parsedBookInfo.Quality = ParseQualityForSearchDecision(report);
                        }

                        remoteBook = TryMapRssRelease(report, parsedBookInfo);
                    }
                    else if (parsedBookInfo != null && !parsedBookInfo.AuthorName.IsNullOrWhiteSpace())
                    {
                        remoteBook = _parsingService.Map(parsedBookInfo, searchCriteria);
                    }

                    _logger.Trace("MAM_DEBUG_PARSING: Title='{0}', ParsedBookInfo={1}, AuthorName='{2}', BookTitle='{3}'", report.Title, parsedBookInfo != null ? "NOT_NULL" : "NULL", parsedBookInfo?.AuthorName ?? "NULL", parsedBookInfo?.BookTitle ?? "NULL");

                    if (isMamIndexer)
                    {
                        _logger.Trace("MAM_REPORT_DETAILS: Indexer='{0}', Author='{1}', Title='{2}'", report.Indexer, report.Author ?? "NULL", report.Title);
                    }

                    if (remoteBook != null)
                    {
                        remoteBook.Release = report;

                        _logger.Trace("MAPPING_RESULT: After Map() - Author={0}, Books.Count={1}, ParsedAuthor='{2}'",
                            remoteBook?.Author?.Name ?? "NULL",
                            remoteBook?.Books?.Count ?? 0,
                            parsedBookInfo.AuthorName);

                        _aggregationService.Augment(remoteBook);

                        // try parsing again using the search criteria, in case it parsed but parsed incorrectly
                        if ((remoteBook.Author == null || remoteBook.Books.Empty()) && searchCriteria != null)
                        {
                            _logger.Debug("Author/Book null for {0}, reparsing with search criteria", report.Title);
                            var parsedBookInfoWithCriteria = Parser.Parser.ParseBookTitleWithSearchCriteria(report.Title,
                                                                                                            searchCriteria.Author,
                                                                                                            searchCriteria.Books);

                            if (parsedBookInfoWithCriteria != null && parsedBookInfoWithCriteria.AuthorName.IsNotNullOrWhiteSpace())
                            {
                                remoteBook = _parsingService.Map(parsedBookInfoWithCriteria, searchCriteria);

                            }
                        }

                        remoteBook.Release = report;
                        remoteBook.ReleaseSource = releaseSource;
                        AttachPackDetection(remoteBook, report);

                        // parse quality again with title and category if unknown (or missing)
                        if (remoteBook.ParsedBookInfo.Quality == null || remoteBook.ParsedBookInfo.Quality.Quality == Quality.Unknown)
                        {
                            // For MAM releases with FileType, use direct parsing
                            var torrentInfo = report as TorrentInfo;
                            if (torrentInfo?.FileType != null && isMamIndexer)
                            {
                                _logger.Trace("MAM_FILETYPE_PARSING: Using FileType '{0}' for title '{1}'", torrentInfo.FileType, report.Title);
                                remoteBook.ParsedBookInfo.Quality = QualityParser.ParseQualityFromFileType(
                                    torrentInfo.FileType, report.Title, (int)report.IndexerFlags, report.Indexer);
                                _logger.Trace("MAM_FILETYPE_RESULT: Parsed quality '{0}' from FileType", remoteBook.ParsedBookInfo.Quality);
                            }
                            else
                            {
                                remoteBook.ParsedBookInfo.Quality = QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags);
                            }
                        }

                        // For MAM indexers, always re-parse quality to apply MAM enhancements
                        else if (isMamIndexer)
                        {
                            var torrentInfo = report as TorrentInfo;
                            if (torrentInfo?.FileType != null)
                            {
                                _logger.Trace("MAM_FILETYPE_REPARSE: Using FileType '{0}' for enhanced parsing of '{1}'", torrentInfo.FileType, report.Title);
                                remoteBook.ParsedBookInfo.Quality = QualityParser.ParseQualityFromFileType(
                                    torrentInfo.FileType, report.Title, (int)report.IndexerFlags, report.Indexer);
                                _logger.Trace("MAM_FILETYPE_REPARSE_RESULT: Enhanced quality '{0}' from FileType", remoteBook.ParsedBookInfo.Quality);
                            }
                            else
                            {
                                _logger.Trace("MAM_NO_FILETYPE: No FileType available for '{0}', using standard parsing", report.Title);
                                remoteBook.ParsedBookInfo.Quality = QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags);
                            }
                        }

                        if (remoteBook.Author == null)
                        {
                            // Hard filter: Wrong author is never bypassable
                            decision = new DownloadDecision(remoteBook, new Rejection("Unknown Author", RejectionType.Permanent, false, "Author", 3));
                        }
                        else if (remoteBook.Books.Empty())
                        {
                            // Hard filter: Wrong title/book is never bypassable
                            decision = new DownloadDecision(remoteBook, new Rejection("Unable to parse books from release name", RejectionType.Permanent, false, "Title", 3));
                        }
                        else
                        {
                            _logger.Debug("[DECISION_MAKER] Processing valid remote book with author '{0}' and {1} books",
                                remoteBook.Author?.Name ?? "NULL",
                                remoteBook.Books?.Count ?? 0);

                            _logger.Debug("[DECISION_MAKER] Author quality profile status - AudiobookProfile: {0}, EbookProfile: {1}",
                                remoteBook.Author?.AudiobookQualityProfileId.HasValue == true ? "LOADED" : "NULL",
                                remoteBook.Author?.EbookQualityProfileId.HasValue == true ? "LOADED" : "NULL");

                            _aggregationService.Augment(remoteBook);
                            PreferAllowedDetectedQuality(remoteBook);

                            remoteBook.CustomFormats = _formatCalculator.ParseCustomFormat(remoteBook, remoteBook.Release.Size);

                            _logger.Debug("[DECISION_MAKER] Getting quality profile for author '{0}' and quality '{1}'",
                                remoteBook?.Author?.Name ?? "NULL",
                                remoteBook?.ParsedBookInfo?.Quality?.Quality?.Name ?? "NULL");

                            var qualityProfile = remoteBook?.Author?.GetQualityProfileForQuality(remoteBook.ParsedBookInfo.Quality.Quality);

                            _logger.Debug("[DECISION_MAKER] Quality profile result: {0}",
                                qualityProfile != null ? $"'{qualityProfile.Name}' (ID: {qualityProfile.Id})" : "NULL");

                            remoteBook.CustomFormatScore = qualityProfile?.CalculateCustomFormatScore(remoteBook.CustomFormats) ?? 0;

                            decision = GetDecisionForReport(remoteBook, searchCriteria);
                        }
                    }

                    if (searchCriteria != null && decision == null)
                    {
                        if (parsedBookInfo == null)
                        {
                            parsedBookInfo = new ParsedBookInfo();
                        }

                        if (parsedBookInfo.Quality == null)
                        {
                            parsedBookInfo.Quality = QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags);
                        }

                        var unparsedRemoteBook = new RemoteBook
                        {
                            Release = report,
                            ParsedBookInfo = parsedBookInfo
                        };

                        // Hard filter: Unable to parse is never bypassable once the shared search matcher says no.
                        decision = new DownloadDecision(unparsedRemoteBook, new Rejection("Unable to parse release", RejectionType.Permanent, false, "Parsing", 3));
                    }
                }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't process release.");

                    // Ensure ParsedBookInfo is always present so API mapping can't null-ref on error decisions.
                    var parsedBookInfo = new ParsedBookInfo
                    {
                        AuthorName = report.Author,
                        BookTitle = report.Title,
                        ReleaseTitle = report.Title,
                        Quality = new QualityModel { Quality = Quality.Unknown }
                    };

                    try
                    {
                        var torrentInfo = report as TorrentInfo;
                        parsedBookInfo.Quality = torrentInfo?.FileType.IsNotNullOrWhiteSpace() == true && IsMAMIndexer(report.Indexer)
                            ? QualityParser.ParseQualityFromFileType(torrentInfo.FileType, report.Title, (int)report.IndexerFlags, report.Indexer)
                            : QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Failed to derive quality for errored release '{0}'", report.Title);
                    }

                    var remoteBook = new RemoteBook { Release = report, ParsedBookInfo = parsedBookInfo, ReleaseSource = releaseSource };

                    // Hard filter: Processing errors are never bypassable
                    decision = new DownloadDecision(remoteBook, new Rejection("Unexpected error processing release", RejectionType.Permanent, false, "Error", 3));
                }

                reportNumber++;

                if (decision != null)
                {
                        decision.RemoteBook.ReleaseSource = releaseSource;
                        decision.RemoteBook.DownloadAllowed = decision.Approved;

                        if (decision.Rejections.Any())
                        {
                            _logger.Debug("Release rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
                        }
                    else
                    {
                        _logger.Debug("Release accepted");
                    }

                    yield return decision;
                }
            }
        }

        private DownloadDecision TryBuildSearchCriteriaDecision(ReleaseInfo report, SearchCriteriaBase searchCriteria, ReleaseSourceType releaseSource)
        {
            if (searchCriteria?.Author == null ||
                searchCriteria.Books?.Any() != true)
            {
                return null;
            }

            var searchedBooks = searchCriteria.Books.Where(b => b != null).ToList();
            var targetBook = searchedBooks.Count == 1 ? searchedBooks[0] : null;
            var authorCatalog = GetPackDetectionCatalog(searchCriteria.AuthorCatalog ?? searchCriteria.Author?.Books ?? searchCriteria.Books, targetBook);
            var packDetection = ReleasePackDetector.Detect(report.Title, targetBook, authorCatalog);

            var searchMatch = ReleaseTitleMatchScorer.FindBestMatch(report.Title,
                                                                    searchCriteria.Author.Name,
                                                                    searchedBooks,
                                                                    report.Author,
                                                                    authorCatalog);

            var parsedBookInfo = searchMatch?.IsMatch == true
                ? CreateParsedBookInfoForSearchMatch(report, searchCriteria.Author, searchMatch)
                : CreateParsedBookInfoForSearchDisplay(report);

            var remoteBook = new RemoteBook
            {
                Release = report,
                ParsedBookInfo = parsedBookInfo,
                Author = searchCriteria.Author,
                Books = searchMatch?.IsMatch == true && searchMatch.Book != null
                    ? new List<Book> { searchMatch.Book }
                    : searchedBooks,
                SearchCriteriaMatch = searchMatch,
                PackDetection = packDetection ?? ReleasePackDetection.None(),
                ReleaseSource = releaseSource
            };

            _aggregationService.Augment(remoteBook);
            EnsureSearchDecisionQuality(remoteBook, report);
            PreferAllowedDetectedQuality(remoteBook);
            remoteBook.CustomFormats = _formatCalculator.ParseCustomFormat(remoteBook, remoteBook.Release.Size);

            var qualityProfile = remoteBook.Author?.GetQualityProfileForQuality(remoteBook.ParsedBookInfo.Quality.Quality);
            remoteBook.CustomFormatScore = qualityProfile?.CalculateCustomFormatScore(remoteBook.CustomFormats) ?? 0;

            return GetDecisionForReport(remoteBook, searchCriteria);
        }

        private RemoteBook TryMapRssRelease(ReleaseInfo report, ParsedBookInfo parsedBookInfo)
        {
            if (_editionFtsRepository is not IStagedEditionFtsRepository ftsRepository)
            {
                throw new InvalidOperationException("The configured Edition FTS repository does not support staged book recall.");
            }

            var tokens = new[]
                {
                    report.Title,
                    report.Author,
                    report.Book,
                    parsedBookInfo.AuthorName,
                    parsedBookInfo.BookTitle
                }
                .Where(value => value.IsNotNullOrWhiteSpace())
                .SelectMany(ReleaseTitleMatchScorer.Tokenize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (tokens.Count == 0)
            {
                return null;
            }

            var mediaType = QualityMediaTypeHelper.GetKnownMediaType(parsedBookInfo.Quality?.Quality);
            var mediaTypes = mediaType.HasValue
                ? new[] { mediaType.Value }
                : new[] { BookMediaType.Audiobook, BookMediaType.Ebook };

            var recalls = mediaTypes
                .SelectMany(candidateMediaType => ftsRepository.RecallBooks(
                    null,
                    tokens,
                    candidateMediaType,
                    limit: 20,
                    monitoredOnly: true))
                .GroupBy(candidate => candidate.BookId)
                .Select(group => group.OrderByDescending(candidate => candidate.MatchScore).First())
                .ToList();

            if (recalls.Count == 0)
            {
                return null;
            }

            var mapped = _parsingService.Map(parsedBookInfo, 0, recalls.Select(candidate => candidate.BookId));
            var books = mapped?.Books?
                .Where(book => book?.Author != null)
                .ToList() ?? new List<Book>();

            var matches = books
                .GroupBy(book => new { book.AuthorId, book.MediaType })
                .Select(group =>
                {
                    var candidates = group.ToList();
                    var author = candidates[0].Author;
                    var match = ReleaseTitleMatchScorer.FindBestMatch(
                        report.Title,
                        author.Name,
                        candidates,
                        FirstNotBlank(report.Author, parsedBookInfo.AuthorName),
                        candidates);

                    return new
                    {
                        Author = author,
                        Match = match,
                        Identity = match == null
                            ? null
                            : ReleaseIdentityEvidence.Analyze(report, author, match.Book, match)
                    };
                })
                .Where(candidate => candidate.Match?.Book != null &&
                                    candidate.Identity?.HasStructuredAuthorMismatch != true)
                .ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            var viableMatches = matches
                .Where(candidate => ReleaseTitleMatchSpecification.IsAcceptedMatch(
                    candidate.Match,
                    candidate.Identity,
                    _configService?.BookMatchingStrictness ?? BookMatchingStrictness.Balanced))
                .ToList();

            var selected = viableMatches.Count == 1 ? viableMatches[0] : null;

            if (selected == null)
            {
                _logger.Debug("RSS FTS recall found {0} proven matches for release '{1}' across {2} monitored author/media candidates", viableMatches.Count, report.Title, matches.Count);
                return null;
            }

            parsedBookInfo.BookTitle = FirstNotBlank(
                selected.Match.PrimaryTitle,
                ReleaseTitleMatchScorer.GetPrimaryBookTitle(selected.Match.Book),
                selected.Match.Book.Title,
                parsedBookInfo.BookTitle);
            parsedBookInfo.AuthorName = FirstNotBlank(parsedBookInfo.AuthorName, report.Author, selected.Author.Name);
            parsedBookInfo.ReleaseTitle = FirstNotBlank(parsedBookInfo.ReleaseTitle, report.Title);
            parsedBookInfo.ReleaseGroup = FirstNotBlank(parsedBookInfo.ReleaseGroup, Parser.Parser.ParseReleaseGroup(report.Title));

            _logger.Trace("RSS FTS mapped '{0}' to monitored book {1} after hydrating {2} shortlisted books",
                report.Title,
                selected.Match.Book.Id,
                books.Count);

            return new RemoteBook
            {
                ParsedBookInfo = parsedBookInfo,
                Author = selected.Author,
                Books = new List<Book> { selected.Match.Book },
                SearchCriteriaMatch = selected.Match
            };
        }

        private static void AttachPackDetection(RemoteBook remoteBook, ReleaseInfo report)
        {
            if (remoteBook == null ||
                report == null ||
                (remoteBook.PackDetection != null && remoteBook.PackDetection.Verdict != ReleasePackDetectionVerdict.None))
            {
                return;
            }

            var targetBook = remoteBook.Books?.Count == 1 ? remoteBook.Books[0] : null;
            var authorCatalog = GetPackDetectionCatalog(remoteBook.Author?.Books?.Any() == true
                ? remoteBook.Author.Books
                : remoteBook.Books, targetBook);

            remoteBook.PackDetection = ReleasePackDetector.Detect(report.Title, targetBook, authorCatalog) ?? ReleasePackDetection.None();
        }

        private static List<Book> GetPackDetectionCatalog(IEnumerable<Book> books, Book targetBook)
        {
            var catalog = (books ?? Enumerable.Empty<Book>())
                .Where(book => book != null);

            if (targetBook != null)
            {
                catalog = catalog.Where(book => book.MediaType == targetBook.MediaType);
            }

            return catalog.ToList();
        }

        private ParsedBookInfo CreateParsedBookInfoForSearchMatch(ReleaseInfo report, Author author, TitleMatchResult searchMatch)
        {
            var primaryTitle = searchMatch?.PrimaryTitle;
            if (string.IsNullOrWhiteSpace(primaryTitle))
            {
                primaryTitle = ReleaseTitleMatchScorer.GetPrimaryBookTitle(searchMatch?.Book);
            }

            if (string.IsNullOrWhiteSpace(primaryTitle))
            {
                primaryTitle = searchMatch?.Book?.Title ?? report.Title;
            }

            var parsedBookInfo = Parser.Parser.ParseBookTitle(report.Title);

            return new ParsedBookInfo
            {
                AuthorName = FirstNotBlank(parsedBookInfo?.AuthorName, report.Author, author?.Name),
                BookTitle = primaryTitle,
                ReleaseTitle = report.Title,
                ReleaseGroup = Parser.Parser.ParseReleaseGroup(report.Title),
                Quality = ParseQualityForSearchDecision(report)
            };
        }

        private ParsedBookInfo CreateParsedBookInfoForSearchDisplay(ReleaseInfo report)
        {
            var parsedBookInfo = Parser.Parser.ParseBookTitle(report.Title) ?? new ParsedBookInfo();

            if (parsedBookInfo.AuthorName.IsNullOrWhiteSpace())
            {
                parsedBookInfo.AuthorName = report.Author;
            }

            if (parsedBookInfo.BookTitle.IsNullOrWhiteSpace())
            {
                parsedBookInfo.BookTitle = report.Title;
            }

            if (parsedBookInfo.ReleaseTitle.IsNullOrWhiteSpace())
            {
                parsedBookInfo.ReleaseTitle = report.Title;
            }

            if (parsedBookInfo.ReleaseGroup.IsNullOrWhiteSpace())
            {
                parsedBookInfo.ReleaseGroup = Parser.Parser.ParseReleaseGroup(report.Title);
            }

            parsedBookInfo.Quality ??= ParseQualityForSearchDecision(report);

            return parsedBookInfo;
        }

        private static string FirstNotBlank(params string[] values)
        {
            return values?.FirstOrDefault(value => value.IsNotNullOrWhiteSpace());
        }

        private void EnsureSearchDecisionQuality(RemoteBook remoteBook, ReleaseInfo report)
        {
            if (remoteBook?.ParsedBookInfo == null)
            {
                return;
            }

            if (remoteBook.ParsedBookInfo.Quality == null || remoteBook.ParsedBookInfo.Quality.Quality == Quality.Unknown || IsMAMIndexer(report.Indexer))
            {
                remoteBook.ParsedBookInfo.Quality = ParseQualityForSearchDecision(report);
            }
        }

        private QualityModel ParseQualityForSearchDecision(ReleaseInfo report)
        {
            var torrentInfo = report as TorrentInfo;
            if (torrentInfo?.FileType.IsNotNullOrWhiteSpace() == true && IsMAMIndexer(report.Indexer))
            {
                return QualityParser.ParseQualityFromFileType(torrentInfo.FileType, report.Title, (int)report.IndexerFlags, report.Indexer);
            }

            return QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags);
        }

        private void PreferAllowedDetectedQuality(RemoteBook remoteBook)
        {
            var qualityModel = remoteBook?.ParsedBookInfo?.Quality;
            var detectedQualities = qualityModel?.DetectedQualities?
                .Where(q => q != null)
                .Distinct()
                .ToList();

            if (remoteBook?.Author == null || detectedQualities == null || detectedQualities.Count <= 1)
            {
                return;
            }

            var requestedMediaType = GetSingleMediaType(remoteBook.Books);
            if (requestedMediaType.HasValue)
            {
                var mediaScopedQualities = detectedQualities
                    .Where(q => QualityMediaTypeHelper.GetKnownMediaType(q) == requestedMediaType.Value)
                    .ToList();

                if (mediaScopedQualities.Any())
                {
                    detectedQualities = mediaScopedQualities;
                }
            }

            var allowedQualities = detectedQualities
                .Where(q => IsAllowedByAuthorProfile(remoteBook.Author, q))
                .ToList();

            if (!allowedQualities.Any())
            {
                return;
            }

            var selectedQuality = allowedQualities.Aggregate((best, candidate) =>
                CompareDetectedQuality(candidate, best, remoteBook.Author) > 0 ? candidate : best);

            if (qualityModel.Quality == null || selectedQuality.Id != qualityModel.Quality.Id)
            {
                _logger.Debug("Using detected quality '{0}' for multi-format release '{1}' instead of primary '{2}'. All detected formats: [{3}]",
                    selectedQuality.Name,
                    remoteBook.Release?.Title ?? "Unknown",
                    qualityModel.Quality?.Name ?? "Unknown",
                    qualityModel.AllDetectedFormats);

                qualityModel.Quality = selectedQuality;
            }
        }

        private static BookMediaType? GetSingleMediaType(IEnumerable<Book> books)
        {
            var mediaTypes = (books ?? Enumerable.Empty<Book>())
                .Where(book => book != null)
                .Select(book => book.MediaType)
                .Distinct()
                .ToList();

            return mediaTypes.Count == 1 ? mediaTypes[0] : null;
        }

        private static bool IsAllowedByAuthorProfile(Author author, Quality quality)
        {
            var profile = author.GetQualityProfileForQuality(quality);

            return profile?.Items?.Any(item => item.Allowed && item.GetQualities().Any(q => q.Id == quality.Id)) == true;
        }

        private static int CompareDetectedQuality(Quality left, Quality right, Author author)
        {
            var leftProfile = author.GetQualityProfileForQuality(left);
            var rightProfile = author.GetQualityProfileForQuality(right);

            if (leftProfile != null &&
                rightProfile != null &&
                leftProfile.ProfileType == rightProfile.ProfileType)
            {
                return new QualityModelComparer(leftProfile).Compare(left, right);
            }

            return new QualityModel(left).CompareTo(new QualityModel(right));
        }

        private DownloadDecision GetDecisionForReport(RemoteBook remoteBook, SearchCriteriaBase searchCriteria = null)
        {
            var reasons = new Rejection[0];

            foreach (var specifications in _specifications.GroupBy(v => v.Priority).OrderBy(v => v.Key))
            {
                reasons = specifications.Select(c => EvaluateSpec(c, remoteBook, searchCriteria))
                                                        .Where(c => c != null)
                                                        .ToArray();

                if (reasons.Any())
                {
                    break;
                }
            }

            return new DownloadDecision(remoteBook, reasons.ToArray());
        }

        private Rejection EvaluateSpec(IDecisionEngineSpecification spec, RemoteBook remoteBook, SearchCriteriaBase searchCriteriaBase = null)
        {
            try
            {
                var result = spec.IsSatisfiedBy(remoteBook, searchCriteriaBase);

                if (!result.Accepted)
                {
                    return new Rejection(result.Reason, spec.Type, result.CanBypass, result.Category, result.Severity);
                }
            }
            catch (NotImplementedException)
            {
                _logger.Trace("Spec " + spec.GetType().Name + " not implemented.");
            }
            catch (Exception e)
            {
                e.Data.Add("report", remoteBook.Release.ToJson());
                e.Data.Add("parsed", remoteBook.ParsedBookInfo.ToJson());
                _logger.Error(e, "Couldn't evaluate decision on {0}", remoteBook.Release.Title);
                return new Rejection($"{spec.GetType().Name}: {e.Message}");
            }

            return null;
        }

        private static bool IsMAMIndexer(string indexerName)
        {
            return indexerName.Contains("MyAnonamouse", StringComparison.OrdinalIgnoreCase) ||
                   indexerName.Contains("MAM", StringComparison.OrdinalIgnoreCase);
        }

        private void EnrichReleaseNarratorMetadata(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteria)
        {
            _releaseNarratorMetadataEnricher?.EnrichReleaseNarratorMetadata(reports, searchCriteria);
        }
    }
}
