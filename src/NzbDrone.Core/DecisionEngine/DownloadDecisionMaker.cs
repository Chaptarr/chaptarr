using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download;
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
        List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease,
            IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> oldestPublishDates);
        List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase);
    }

    public class DownloadDecisionMaker : IMakeDownloadDecision
    {
        private readonly IEnumerable<IDecisionEngineSpecification> _specifications;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IParsingService _parsingService;
        private readonly IBookService _bookService;
        private readonly IEditionFtsRepository _editionFtsRepository;
        private readonly IRemoteBookAggregationService _aggregationService;
        private readonly IReleaseNarratorMetadataEnricher _releaseNarratorMetadataEnricher;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public DownloadDecisionMaker(IEnumerable<IDecisionEngineSpecification> specifications,
            IParsingService parsingService,
            IBookService bookService,
            IEditionFtsRepository editionFtsRepository,
            ICustomFormatCalculationService formatService,
            IRemoteBookAggregationService aggregationService,
            IReleaseNarratorMetadataEnricher releaseNarratorMetadataEnricher,
            IConfigService configService,
            Logger logger)
        {
            _specifications = specifications;
            _parsingService = parsingService;
            _bookService = bookService;
            _editionFtsRepository = editionFtsRepository;
            _formatCalculator = formatService;
            _aggregationService = aggregationService;
            _releaseNarratorMetadataEnricher = releaseNarratorMetadataEnricher;
            _configService = configService;
            _logger = logger;
        }

        public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false) => GetRssDecision(reports, pushedRelease, null);

        public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease,
            IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> oldestPublishDates)
        {
            var decisions = GetBookDecisions(reports, pushedRelease, oldestPublishDates: oldestPublishDates).ToList();

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug(BuildDecisionSummary(pushedRelease ? "Release push" : "RSS", reports.Count, decisions));
            }

            return decisions;
        }

        public List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase)
        {
            var decisions = GetBookDecisions(reports, false, searchCriteriaBase).ToList();

            if (_logger.IsDebugEnabled)
            {
                var searchType = searchCriteriaBase.InteractiveSearch
                    ? "Interactive"
                    : searchCriteriaBase.UserInvokedSearch
                        ? "Manual"
                        : "Automatic";
                var authorName = searchCriteriaBase.Author?.Name ?? "Unknown";
                var searchedBooks = searchCriteriaBase.Books ?? new List<Book>();
                var bookTarget = searchedBooks.Count == 1
                    ? $"book='{searchedBooks[0]?.Title ?? "Unknown"}'"
                    : $"books={searchedBooks.Count}";
                var source = $"{searchType} search (author='{authorName}', {bookTarget})";

                _logger.Debug(BuildDecisionSummary(source, reports.Count, decisions));
            }

            return decisions;
        }

        private List<DownloadDecision> GetBookDecisions(List<ReleaseInfo> reports, bool pushedRelease = false, SearchCriteriaBase searchCriteria = null,
            IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> oldestPublishDates = null)
        {
            var stopwatch = Stopwatch.StartNew();
            if (reports.Any())
            {
                _logger.ProgressInfo("Processing {0} releases", reports.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            var orderedReports = reports;
            // One result batch owns the prepared catalog; nothing survives into the next search.
            var catalogPreparation = new ReleaseTitleMatchScorer.CatalogPreparation();

            var rssMappings = searchCriteria == null ? PrepareRssMappings(reports, catalogPreparation) : null;
            var historySubjects = searchCriteria == null
                ? rssMappings.Values.ToList()
                : reports.Select(report => new RemoteBook
                {
                    Release = report,
                    Author = searchCriteria.Author,
                    Books = searchCriteria.Books ?? new List<Book>()
                }).ToList();
            Exception historyPreparationError = null;
            try
            {
                if (historySubjects.Count > 0 && oldestPublishDates == null && searchCriteria?.UserInvokedSearch != true)
                {
                    oldestPublishDates = _specifications.OfType<DelaySpecification>().FirstOrDefault()?.GetOldestPendingPublishDates();
                }

                foreach (var subject in historySubjects)
                {
                    subject.PreparedPendingReleaseDates = oldestPublishDates;
                }

                _specifications.OfType<HistorySpecification>().FirstOrDefault()?.PrepareAlternativeCopySuppression(historySubjects, checkGrabHistory: searchCriteria == null);
            }
            catch (Exception ex)
            {
                historyPreparationError = ex;
                _logger.Error(ex, "Couldn't prepare batched acquisition evidence for {0} releases", reports.Count);
            }
            var historyByReport = historySubjects.GroupBy(subject => subject.Release).ToDictionary(group => group.Key, group => group.First());

            var decisions = new List<DownloadDecision>(reports.Count);
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
                _logger.Trace("Processing release '{0}' from '{1}'", report.Title, report.Indexer);

                try
                {
                    if (historyPreparationError != null)
                    {
                        if (searchCriteria != null)
                        {
                            var failed = new RemoteBook
                            {
                                Release = report, Author = searchCriteria.Author,
                                Books = searchCriteria.Books ?? new List<Book>(),
                                ParsedBookInfo = CreateParsedBookInfoForSearchDisplay(report)
                            };
                            decisions.Add(FinalizeDecision(new DownloadDecision(failed,
                                new Rejection("Unexpected error processing release", RejectionType.Permanent, false, "Error", 3)), releaseSource));
                        }

                        reportNumber++;
                        continue;
                    }

                    ParsedBookInfo parsedBookInfo = searchCriteria == null && rssMappings.TryGetValue(report, out var mappedReport)
                        ? mappedReport.ParsedBookInfo : null;
                    RemoteBook remoteBook = null;
                    var attemptedCriteriaParse = false;
                    if (searchCriteria == null && !rssMappings.ContainsKey(report))
                    {
                        reportNumber++;
                        continue;
                    }

                    historyByReport.TryGetValue(report, out var historySubject);
                    var searchDecision = TryBuildSearchCriteriaDecision(report, searchCriteria, releaseSource, catalogPreparation, historySubject);

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
                                                                                        searchCriteria.Books,
                                                                                        searchCriteria.AuthorCatalog ?? searchCriteria.Books);
                        attemptedCriteriaParse = true;
                    }

                    parsedBookInfo ??= Parser.Parser.ParseBookTitle(report.Title);

                    if (parsedBookInfo == null && searchCriteria != null && !attemptedCriteriaParse)
                    {
                        parsedBookInfo = Parser.Parser.ParseBookTitleWithSearchCriteria(report.Title,
                                                                                        searchCriteria.Author,
                                                                                        searchCriteria.Books,
                                                                                        searchCriteria.AuthorCatalog ?? searchCriteria.Books);
                    }

                    if (searchCriteria == null)
                    {
                        remoteBook = rssMappings[report];
                        parsedBookInfo = remoteBook.ParsedBookInfo;
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
                            _logger.Trace("Author/Book null for {0}, reparsing with search criteria", report.Title);
                            var parsedBookInfoWithCriteria = Parser.Parser.ParseBookTitleWithSearchCriteria(report.Title,
                                                                                                            searchCriteria.Author,
                                                                                                            searchCriteria.Books,
                                                                                                            searchCriteria.AuthorCatalog ?? searchCriteria.Books);

                            if (parsedBookInfoWithCriteria != null && parsedBookInfoWithCriteria.AuthorName.IsNotNullOrWhiteSpace())
                            {
                                remoteBook = _parsingService.Map(parsedBookInfoWithCriteria, searchCriteria);

                            }
                        }

                        remoteBook.Release = report;
                        remoteBook.ReleaseSource = releaseSource;
                        AttachPackDetection(remoteBook, report);

                        // Re-evaluate provisional title evidence with the release's category and file type.
                        if (NeedsReportQuality(remoteBook.ParsedBookInfo.Quality))
                        {
                            // For MAM releases with FileType, use direct parsing
                            var torrentInfo = report as TorrentInfo;
                            if (torrentInfo?.FileType != null && isMamIndexer)
                            {
                                _logger.Trace("MAM_FILETYPE_PARSING: Using FileType '{0}' for title '{1}'", torrentInfo.FileType, report.Title);
                                remoteBook.ParsedBookInfo.Quality = QualityParser.ParseQualityFromFileType(
                                    torrentInfo.FileType, report.Title, (int)report.IndexerFlags, report.Indexer, QualityMediaTypeHelper.GetCategoryMediaType(report));
                                _logger.Trace("MAM_FILETYPE_RESULT: Parsed quality '{0}' from FileType", remoteBook.ParsedBookInfo.Quality);
                            }
                            else
                            {
                                remoteBook.ParsedBookInfo.Quality = QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags, QualityMediaTypeHelper.GetCategoryMediaType(report));
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
                                    torrentInfo.FileType, report.Title, (int)report.IndexerFlags, report.Indexer, QualityMediaTypeHelper.GetCategoryMediaType(report));
                                _logger.Trace("MAM_FILETYPE_REPARSE_RESULT: Enhanced quality '{0}' from FileType", remoteBook.ParsedBookInfo.Quality);
                            }
                            else
                            {
                                _logger.Trace("MAM_NO_FILETYPE: No FileType available for '{0}', using standard parsing", report.Title);
                                remoteBook.ParsedBookInfo.Quality = QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags, QualityMediaTypeHelper.GetCategoryMediaType(report));
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
                            _logger.Trace("[DECISION_MAKER] Processing valid remote book with author '{0}' and {1} books",
                                remoteBook.Author?.Name ?? "NULL",
                                remoteBook.Books?.Count ?? 0);

                            _logger.Trace("[DECISION_MAKER] Author quality profile status - AudiobookProfile: {0}, EbookProfile: {1}",
                                remoteBook.Author?.AudiobookQualityProfileId.HasValue == true ? "LOADED" : "NULL",
                                remoteBook.Author?.EbookQualityProfileId.HasValue == true ? "LOADED" : "NULL");

                            _aggregationService.Augment(remoteBook);
                            PreferAllowedDetectedQuality(remoteBook);

                            remoteBook.PreparedBookHistory = historySubject?.PreparedBookHistory;
                            remoteBook.PreparedPendingReleaseDates = historySubject?.PreparedPendingReleaseDates;
                            remoteBook.PositivelyIncompatibleBookIds = historySubject?.PositivelyIncompatibleBookIds;
                            if (remoteBook.AlternativeCopies)
                            {
                                var reportDecisions = new List<DownloadDecision>(remoteBook.Books.Count);
                                foreach (var book in remoteBook.Books)
                                {
                                    var view = remoteBook.ForBooks(new[] { book });
                                    var rowDecision = ScoreAndDecide(view, searchCriteria, catalogPreparation);
                                    view.TargetCustomFormatScores[book.Id] = view.CustomFormatScore;
                                    reportDecisions.Add(FinalizeDecision(rowDecision, releaseSource));
                                }

                                decisions.AddRange(reportDecisions);
                            }
                            else
                            {
                                decision = ScoreAndDecide(remoteBook, searchCriteria, catalogPreparation);
                            }
                        }
                    }

                    if (searchCriteria != null && decision == null)
                    {
                        if (parsedBookInfo == null)
                        {
                            parsedBookInfo = new ParsedBookInfo();
                        }

                        if (NeedsReportQuality(parsedBookInfo.Quality))
                        {
                            parsedBookInfo.Quality = QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags, QualityMediaTypeHelper.GetCategoryMediaType(report));
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
                    decision = ProcessingFailure(report, searchCriteria, e);
                }

                reportNumber++;

                if (decision != null)
                {
                    decisions.Add(FinalizeDecision(decision, releaseSource));
                }
            }

            EnrichNarratorMetadataForVisibleDecisions(decisions, searchCriteria, catalogPreparation);
            _logger.Debug("Processed {0} releases in {1} ms", reports.Count, stopwatch.ElapsedMilliseconds);
            _logger.Debug("[TITLE-SIBLINGS] {0} releases, {1} score passes, {2} sibling comparisons (largest scan {3}), {4} numeric variant checks; sibling scans took {5:F2} ms",
                reports.Count, catalogPreparation.ScorePasses, catalogPreparation.SiblingsCompared,
                catalogPreparation.LargestSiblingScan, catalogPreparation.NumberVariantChecks, catalogPreparation.SiblingScanTime.TotalMilliseconds);

            return decisions;
        }

        private DownloadDecision ProcessingFailure(ReleaseInfo report, SearchCriteriaBase searchCriteria, Exception exception)
        {
            _logger.Error(exception, "Couldn't process release '{0}' from '{1}'", report.Title, report.Indexer);

            // A processing failure is not a rejection of a stored RSS candidate.
            if (searchCriteria == null)
            {
                return null;
            }

            // Keep API display data available even when parsing or scoring failed.
            var parsedBookInfo = new ParsedBookInfo
            {
                AuthorName = report.Author,
                BookTitle = report.Title,
                ReleaseTitle = report.Title,
                Quality = new QualityModel { Quality = Quality.Unknown }
            };

            try
            {
                parsedBookInfo.Quality = ParseQualityForSearchDecision(report);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to derive quality for errored release '{0}'", report.Title);
            }

            return new DownloadDecision(new RemoteBook { Release = report, ParsedBookInfo = parsedBookInfo },
                new Rejection("Unexpected error processing release", RejectionType.Permanent, false, "Error", 3));
        }

        private DownloadDecision ScoreAndDecide(RemoteBook remoteBook, SearchCriteriaBase searchCriteria, ReleaseTitleMatchScorer.CatalogPreparation catalogPreparation)
        {
            remoteBook.CustomFormats = _formatCalculator.ParseCustomFormat(remoteBook, remoteBook.Release.Size);

            _logger.Trace("[DECISION_MAKER] Getting quality profile for author '{0}' and quality '{1}'",
                remoteBook?.Author?.Name ?? "NULL",
                remoteBook?.ParsedBookInfo?.Quality?.Quality?.Name ?? "NULL");

            var qualityProfile = remoteBook.GetQualityProfile();

            if (_logger.IsTraceEnabled)
            {
                _logger.Trace("[DECISION_MAKER] Quality profile result: {0}",
                    qualityProfile != null ? $"'{qualityProfile.Name}' (ID: {qualityProfile.Id})" : "NULL");
            }

            remoteBook.CustomFormatScore = qualityProfile?.CalculateCustomFormatScore(remoteBook.CustomFormats) ?? 0;

            return GetDecisionForReport(remoteBook, searchCriteria, catalogPreparation);
        }

        private DownloadDecision FinalizeDecision(DownloadDecision decision, ReleaseSourceType releaseSource)
        {
            decision.RemoteBook.ReleaseSource = releaseSource;
            decision.RemoteBook.DownloadAllowed = decision.Approved;

            if (decision.Rejections.Any())
            {
                if (_logger.IsTraceEnabled)
                {
                    _logger.Trace("Release rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
                }
            }
            else
            {
                _logger.Trace("Release accepted");
            }

            return decision;
        }

        private DownloadDecision TryBuildSearchCriteriaDecision(ReleaseInfo report, SearchCriteriaBase searchCriteria, ReleaseSourceType releaseSource, ReleaseTitleMatchScorer.CatalogPreparation catalogPreparation, RemoteBook historySubject)
        {
            if (searchCriteria?.Author == null ||
                searchCriteria.Books?.Any() != true)
            {
                return null;
            }

            var searchedBooks = searchCriteria.Books.Where(b => b != null).ToList();
            var targetBook = searchedBooks.Count == 1 ? searchedBooks[0] : null;
            var authorCatalog = GetSearchCatalog(searchCriteria.AuthorCatalog ?? searchCriteria.Author?.Books ?? searchCriteria.Books, targetBook);
            var packDetection = ReleasePackDetector.Detect(report, targetBook);

            var searchMatch = ReleaseTitleMatchScorer.FindBestMatchForSearch(report,
                                                                             searchCriteria.Author.Name,
                                                                             searchedBooks,
                                                                             report.Author,
                                                                             authorCatalog,
                                                                             catalogPreparation);

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
                ReleaseSource = releaseSource,
                PreparedBookHistory = historySubject?.PreparedBookHistory,
                PreparedPendingReleaseDates = historySubject?.PreparedPendingReleaseDates,
                PositivelyIncompatibleBookIds = historySubject?.PositivelyIncompatibleBookIds
            };

            _aggregationService.Augment(remoteBook);
            EnsureSearchDecisionQuality(remoteBook, report);
            PreferAllowedDetectedQuality(remoteBook);

            return ScoreAndDecide(remoteBook, searchCriteria, catalogPreparation);
        }

        private Dictionary<ReleaseInfo, RemoteBook> PrepareRssMappings(List<ReleaseInfo> reports, ReleaseTitleMatchScorer.CatalogPreparation preparation)
        {
            var stopwatch = Stopwatch.StartNew();
            var mapped = new Dictionary<ReleaseInfo, RemoteBook>();
            var recallCalls = 0;
            var reportNumber = 0;
            foreach (var report in reports)
            {
                _logger.ProgressTrace("Matching release {0}/{1}", ++reportNumber, reports.Count);
                try
                {
                    var parsed = Parser.Parser.ParseBookTitle(report.Title) ?? new ParsedBookInfo();
                    parsed.AuthorName = FirstNotBlank(parsed.AuthorName, report.Author);
                    parsed.BookTitle = FirstNotBlank(parsed.BookTitle, report.Book, report.Title);
                    parsed.ReleaseTitle = FirstNotBlank(parsed.ReleaseTitle, report.Title);
                    if (NeedsReportQuality(parsed.Quality))
                    {
                        parsed.Quality = ParseQualityForSearchDecision(report);
                    }

                    var remoteBook = TryMapRssRelease(report, parsed, preparation, ref recallCalls);
                    if (remoteBook != null)
                    {
                        remoteBook.Release = report;
                        mapped[report] = remoteBook;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Couldn't map RSS release.");
                }
            }

            _logger.Debug("RSS mapping: reports={0}, mapped={1}, recallCalls={2}, elapsedMs={3}",
                reports.Count, mapped.Count, recallCalls, stopwatch.ElapsedMilliseconds);
            return mapped;
        }

        private RemoteBook TryMapRssRelease(ReleaseInfo report, ParsedBookInfo parsedBookInfo, ReleaseTitleMatchScorer.CatalogPreparation catalogPreparation, ref int recallCalls)
        {
            if (report.PendingContext != null)
            {
                var pending = report.PendingContext;
                var saved = pending.AdditionalInfo;
                var context = pending.RemoteBook ?? _parsingService.Map(parsedBookInfo, pending.AuthorId, saved.BookIds);
                var pendingMediaType = QualityMediaTypeHelper.DetectMediaType(parsedBookInfo.Quality?.Quality, report);
                var targets = (context?.Books ?? new List<Book>())
                    .Where(book => book?.AuthorId == pending.AuthorId && (!pendingMediaType.HasValue || book.MediaType == pendingMediaType.Value)).ToList();
                if (targets.Count == 0 || targets.Select(book => book.MediaType).Distinct().Count() != 1 ||
                    (!saved.AlternativeCopies && targets.Count != saved.BookIds.Where(id => id > 0).Distinct().Count()))
                {
                    return null;
                }

                context.Books = targets;
                context.AlternativeCopies = saved.AlternativeCopies;
                context.TargetEditionIds = saved.TargetEditionIds ?? new Dictionary<int, int>();
                context.TargetCustomFormatScores = saved.TargetCustomFormatScores ?? new Dictionary<int, int>();
                return context;
            }

            var sourceFields = new[]
                {
                    report.Title,
                    report.Author,
                    report.Book,
                    parsedBookInfo.AuthorName,
                    parsedBookInfo.BookTitle
                }
                .Where(value => value.IsNotNullOrWhiteSpace())
                .ToList();
            var tokens = sourceFields.SelectMany(ReleaseTitleMatchScorer.Tokenize)
                .Concat(sourceFields.SelectMany(value => StringSuperNormalizer.GetFtsQueryTerms(value, preserveFtsConnectors: false)))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (tokens.Count == 0)
            {
                _logger.Trace("RSS FTS recall skipped for release '{0}': no usable tokens in the release title or feed metadata", report.Title);
                return null;
            }

            var mediaType = QualityMediaTypeHelper.DetectMediaType(parsedBookInfo.Quality?.Quality, report);
            var mediaTypes = mediaType.HasValue
                ? new[] { mediaType.Value }
                : new[] { BookMediaType.Audiobook, BookMediaType.Ebook };

            var recalledBooks = new List<BookFtsMatch>();
            foreach (var candidateMediaType in mediaTypes)
            {
                recallCalls++;
                recalledBooks.AddRange(_editionFtsRepository.RecallBooks(null, tokens, candidateMediaType, limit: 20, monitoredOnly: true));
            }

            var recalls = recalledBooks.GroupBy(candidate => candidate.BookId)
                .Select(group => group.OrderByDescending(candidate => candidate.MatchScore).First())
                .ToList();

            if (recalls.Count == 0)
            {
                _logger.Trace("RSS FTS recall found no monitored candidates for release '{0}' from {1} token(s)", report.Title, tokens.Count);
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
                    var best = ReleaseTitleMatchScorer.FindBestMatches(
                        report, author.Name, candidates, FirstNotBlank(report.Author, parsedBookInfo.AuthorName),
                        GetSearchCatalog(catalogPreparation.GetAuthorCatalog(author.Id, _bookService), candidates[0]), catalogPreparation);
                    return new
                    {
                        Author = author,
                        Matches = best.Where(match => match.Book != null && ReleaseTitleMatchSpecification.IsAcceptedMatch(
                            match, ReleaseIdentityEvidence.Analyze(report, author, match),
                            _configService?.BookMatchingStrictness ?? BookMatchingStrictness.Balanced)).ToList()
                    };
                })
                .Where(candidate => candidate.Matches.Count > 0).ToList();
            if (matches.Count == 0)
            {
                _logger.Trace("RSS release '{0}' has no accepted author/media/work candidate", report.Title);
                return null;
            }

            var selected = matches.Count == 1 ? matches[0] : null;
            if (selected == null || selected.Matches.Skip(1).Any(match =>
                    !WorkIdMatcher.WorkProviderIdMatches(selected.Matches[0].Book, match.Book)))
            {
                _logger.Info("RSS release '{0}' has ambiguous author/media/work candidates [{1}]; no automatic decision",
                    report.Title, string.Join(",", matches.SelectMany(candidate => candidate.Matches).Select(match => match.Book.Id)));
                return null;
            }

            if (selected.Matches.Count > 1)
            {
                _logger.Info("RSS release '{0}' has {1} alternative copies [{2}] for author {3} and media {4}; evaluating each row",
                    report.Title, selected.Matches.Count, string.Join(",", selected.Matches.Select(match => match.Book.Id)),
                    selected.Author.Id, selected.Matches[0].Book.MediaType);
            }

            var selectedMatch = selected.Matches[0];
            parsedBookInfo.BookTitle = FirstNotBlank(
                selectedMatch.PrimaryTitle,
                ReleaseTitleMatchScorer.GetPrimaryBookTitle(selectedMatch.Book),
                selectedMatch.Book.Title,
                parsedBookInfo.BookTitle);
            parsedBookInfo.AuthorName = FirstNotBlank(parsedBookInfo.AuthorName, report.Author, selected.Author.Name);
            parsedBookInfo.ReleaseTitle = FirstNotBlank(parsedBookInfo.ReleaseTitle, report.Title);
            parsedBookInfo.ReleaseGroup = FirstNotBlank(parsedBookInfo.ReleaseGroup, Parser.Parser.ParseReleaseGroup(report.Title));

            _logger.Trace("RSS FTS mapped '{0}' to monitored book {1} after hydrating {2} shortlisted books",
                report.Title,
                selectedMatch.Book.Id,
                books.Count);

            return new RemoteBook
            {
                ParsedBookInfo = parsedBookInfo,
                Author = selected.Author,
                Books = selected.Matches.Select(match => match.Book).ToList(),
                AlternativeCopies = selected.Matches.Count > 1,
                TargetEditionIds = selected.Matches.ToDictionary(match => match.Book.Id,
                    match => match.Book.Editions?.FirstOrDefault(edition => edition.Monitored)?.Id ?? 0),
                MatchesByBook = selected.Matches.ToDictionary(match => match.Book.Id),
                SearchCriteriaMatch = selectedMatch
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
            remoteBook.PackDetection = ReleasePackDetector.Detect(report, targetBook) ?? ReleasePackDetection.None();
        }

        private static List<Book> GetSearchCatalog(IEnumerable<Book> books, Book targetBook)
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

            if (NeedsReportQuality(parsedBookInfo.Quality))
            {
                parsedBookInfo.Quality = ParseQualityForSearchDecision(report);
            }

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

            if (NeedsReportQuality(remoteBook.ParsedBookInfo.Quality) || IsMAMIndexer(report.Indexer))
            {
                remoteBook.ParsedBookInfo.Quality = ParseQualityForSearchDecision(report);
            }
        }

        private static bool NeedsReportQuality(QualityModel quality)
        {
            return quality == null || quality.Quality == Quality.Unknown ||
                (quality.Quality == Quality.UnknownAudio && quality.QualityDetectionSource == QualityDetectionSource.Name);
        }

        private QualityModel ParseQualityForSearchDecision(ReleaseInfo report)
        {
            var torrentInfo = report as TorrentInfo;
            if (torrentInfo?.FileType.IsNotNullOrWhiteSpace() == true && IsMAMIndexer(report.Indexer))
            {
                return QualityParser.ParseQualityFromFileType(torrentInfo.FileType, report.Title, (int)report.IndexerFlags, report.Indexer, QualityMediaTypeHelper.GetCategoryMediaType(report));
            }

            return QualityParser.ParseQuality(report.Title, null, report.Categories, report.Indexer, null, (int)report.IndexerFlags, QualityMediaTypeHelper.GetCategoryMediaType(report));
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
                if (_logger.IsTraceEnabled)
                {
                    _logger.Trace("Using detected quality '{0}' for multi-format release '{1}' instead of primary '{2}'. All detected formats: [{3}]",
                        selectedQuality.Name,
                        remoteBook.Release?.Title ?? "Unknown",
                        qualityModel.Quality?.Name ?? "Unknown",
                        qualityModel.AllDetectedFormats);
                }

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

        internal static string BuildDecisionSummary(string source, int reportCount, IReadOnlyCollection<DownloadDecision> decisions)
        {
            decisions ??= Array.Empty<DownloadDecision>();

            var accepted = decisions.Count(decision => decision.Approved);
            var permanentlyRejected = decisions.Count(decision => decision.Rejected);
            var temporarilyRejected = decisions.Count(decision => decision.TemporarilyRejected);
            var unmatched = Math.Max(0, reportCount - decisions.Count);
            var summary = $"{source} decision summary: reports={reportCount}, mapped={decisions.Count}, unmatched={unmatched}, accepted={accepted}, permanentlyRejected={permanentlyRejected}, temporarilyRejected={temporarilyRejected}";

            var topRejections = decisions
                .SelectMany(decision => decision.Rejections ?? Enumerable.Empty<Rejection>())
                .Select(rejection => rejection.Reason.IsNotNullOrWhiteSpace() ? rejection.Reason : "Unknown rejection")
                .GroupBy(reason => reason, StringComparer.Ordinal)
                .Select(group => new { Reason = group.Key, Count = group.Count() })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Reason, StringComparer.Ordinal)
                .Take(5)
                .Select(group => $"{group.Reason} ({group.Count})")
                .ToList();

            if (topRejections.Any())
            {
                summary += "; topRejections=[" + string.Join(", ", topRejections) + "]";
            }

            return summary;
        }

        private static bool IsAllowedByAuthorProfile(Author author, Quality quality)
        {
            var profile = author.GetQualityProfileForQuality(quality);

            return profile?.IsQualityAllowed(quality.Id) == true;
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

        private DownloadDecision GetDecisionForReport(RemoteBook remoteBook, SearchCriteriaBase searchCriteria = null, ReleaseTitleMatchScorer.CatalogPreparation catalogPreparation = null)
        {
            var reasons = new Rejection[0];

            foreach (var specifications in _specifications.GroupBy(v => v.Priority).OrderBy(v => v.Key))
            {
                reasons = specifications.Select(c => EvaluateSpec(c, remoteBook, searchCriteria, catalogPreparation))
                                                        .Where(c => c != null)
                                                        .ToArray();

                if (reasons.Any())
                {
                    break;
                }
            }

            return new DownloadDecision(remoteBook, reasons.ToArray());
        }

        private Rejection EvaluateSpec(IDecisionEngineSpecification spec, RemoteBook remoteBook, SearchCriteriaBase searchCriteriaBase = null, ReleaseTitleMatchScorer.CatalogPreparation catalogPreparation = null)
        {
            try
            {
                var result = spec is ReleaseTitleMatchSpecification titleMatchSpecification
                    ? titleMatchSpecification.IsSatisfiedBy(remoteBook, searchCriteriaBase, catalogPreparation)
                    : spec.IsSatisfiedBy(remoteBook, searchCriteriaBase);

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
                throw;
            }

            return null;
        }

        private static bool IsMAMIndexer(string indexerName)
        {
            return indexerName.Contains("MyAnonamouse", StringComparison.OrdinalIgnoreCase) ||
                   indexerName.Contains("MAM", StringComparison.OrdinalIgnoreCase);
        }

        private void EnrichNarratorMetadataForVisibleDecisions(List<DownloadDecision> decisions,
                                                               SearchCriteriaBase searchCriteria,
                                                               ReleaseTitleMatchScorer.CatalogPreparation catalogPreparation)
        {
            if (_releaseNarratorMetadataEnricher == null || decisions.Count == 0)
            {
                return;
            }

            // NFO evidence can change a custom-format score, not identity or other grab requirements.
            var targets = decisions.Where(decision => decision.Rejections.All(rejection =>
                rejection.Category == CustomFormatAllowedbyProfileSpecification.RejectionCategory)).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            var releases = targets
                .Select(d => d.RemoteBook?.Release)
                .Where(r => r != null)
                .ToList();

            _logger.Trace("Narrator metadata: offering {0} of {1} release(s) for lookup; {2} ineligible release(s) skipped",
                releases.Count, decisions.Count, decisions.Count - targets.Count);

            var enriched = _releaseNarratorMetadataEnricher.EnrichReleaseNarratorMetadata(releases, searchCriteria);
            if (enriched.Count == 0)
            {
                return;
            }

            _logger.Debug("Narrator metadata populated {0} of {1} offered release(s); {2} ineligible release(s) cost no lookup",
                enriched.Count, releases.Count, decisions.Count - targets.Count);

            var enrichedReleases = new HashSet<ReleaseInfo>(enriched);

            for (var i = 0; i < decisions.Count; i++)
            {
                var remoteBook = decisions[i].RemoteBook;
                if (HasProcessingError(decisions[i]) || remoteBook?.Release == null || !enrichedReleases.Contains(remoteBook.Release))
                {
                    continue;
                }

                // Changed metadata affects custom formats and specifications. Re-evaluate
                // them without repeating parsing or book mapping.
                DownloadDecision rescored;
                try
                {
                    rescored = ScoreAndDecide(remoteBook, searchCriteria, catalogPreparation);
                }
                catch (Exception ex)
                {
                    rescored = ProcessingFailure(remoteBook.Release, searchCriteria, ex);
                }

                if (rescored != null)
                {
                    decisions[i] = FinalizeDecision(rescored, remoteBook.ReleaseSource);
                }
                else
                {
                    decisions.RemoveAt(i--);
                }
            }
        }

        private static bool HasProcessingError(DownloadDecision decision) => decision.Rejections.Any(rejection => rejection.Category == "Error");
    }
}
