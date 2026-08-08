using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public class NarratorCoverageInfo
    {
        public string NarratorName { get; set; }
        public int TotalBooksInSeries { get; set; }
        public int BooksNarratedBySpeaker { get; set; }
        public bool HasCompleteCoverage => TotalBooksInSeries > 0 && TotalBooksInSeries == BooksNarratedBySpeaker;
        public List<string> CoveredBookIds { get; set; } = new List<string>();
        public List<string> MissingBookIds { get; set; } = new List<string>();
    }

    public interface INarratorCoverageService
    {
        Task<Dictionary<string, NarratorCoverageInfo>> GetNarratorCoverageForSeries(Series series);
        Task<bool> CheckNarratorHasCompleteCoverage(Series series, string narratorName);
        Task<List<string>> GetNarratorsWithCompleteCoverage(Series series);
    }

    public class NarratorCoverageService : INarratorCoverageService
    {
        private readonly IProvideBookInfo _bookInfo;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IBookService _bookService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly ICached<Dictionary<string, NarratorCoverageInfo>> _cache;
        private readonly Logger _logger;

        public NarratorCoverageService(
            IProvideBookInfo bookInfo,
            IProvideAuthorInfo authorInfo,
            IBookService bookService,
            ISeriesBookLinkService seriesBookLinkService,
            ICacheManager cacheManager,
            Logger logger)
        {
            _bookInfo = bookInfo;
            _authorInfo = authorInfo;
            _bookService = bookService;
            _seriesBookLinkService = seriesBookLinkService;
            _cache = cacheManager.GetCache<Dictionary<string, NarratorCoverageInfo>>(GetType(), "coverage");
            _logger = logger;
        }

        public Task<Dictionary<string, NarratorCoverageInfo>> GetNarratorCoverageForSeries(Series series)
        {
            var seriesProviderId = series.GoodreadsSeriesId ?? series.AmazonSeriesAsin ?? series.HardcoverSeriesId ?? series.OpenLibrarySeriesId ?? series.Id.ToString();
            var cacheKey = $"series_{seriesProviderId}";

            var result = _cache.Get(cacheKey, () => CalculateNarratorCoverage(series).GetAwaiter().GetResult(), TimeSpan.FromHours(1));
            return Task.FromResult(result);
        }

        public async Task<bool> CheckNarratorHasCompleteCoverage(Series series, string narratorName)
        {
            if (narratorName.IsNullOrWhiteSpace())
            {
                return false;
            }

            var coverage = await GetNarratorCoverageForSeries(series);

            // Try exact match first
            if (coverage.TryGetValue(narratorName, out var info))
            {
                return info.HasCompleteCoverage;
            }

            // Try case-insensitive match
            var match = coverage.FirstOrDefault(kv =>
                kv.Key.Equals(narratorName, StringComparison.OrdinalIgnoreCase));

            return match.Value?.HasCompleteCoverage ?? false;
        }

        public async Task<List<string>> GetNarratorsWithCompleteCoverage(Series series)
        {
            var coverage = await GetNarratorCoverageForSeries(series);

            return coverage
                .Where(kv => kv.Value.HasCompleteCoverage)
                .Select(kv => kv.Key)
                .ToList();
        }

        private async Task<Dictionary<string, NarratorCoverageInfo>> CalculateNarratorCoverage(Series series)
        {
            var result = new Dictionary<string, NarratorCoverageInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Get all books in the series from metadata
                var seriesLinks = _seriesBookLinkService.GetLinksBySeries(series.Id);
                var bookIds = seriesLinks.Select(l => l.BookId).ToList();
                var localBooks = _bookService.GetBooks(bookIds);

                // Get total book count for the series
                var totalBooksInSeries = localBooks.Count;

                if (totalBooksInSeries == 0)
                {
                    _logger.Debug("No books found for series {0}, cannot calculate narrator coverage", series.Title);
                    return result;
                }

                _logger.Debug("Calculating narrator coverage for series {0} with {1} books", series.Title, totalBooksInSeries);

                // For each book, get narrator information from metadata
                foreach (var book in localBooks)
                {
                    try
                    {
                        // Try to get narrator info from metadata sources
                        var bookMetadata = await GetBookMetadata(book);

                        if (bookMetadata?.Narrators != null)
                        {
                            foreach (var narrator in bookMetadata.Narrators)
                            {
                                if (narrator.IsNullOrWhiteSpace())
                                {
                                    continue;
                                }

                                if (!result.ContainsKey(narrator))
                                {
                                    result[narrator] = new NarratorCoverageInfo
                                    {
                                        NarratorName = narrator,
                                        TotalBooksInSeries = totalBooksInSeries
                                    };

                                }

                                result[narrator].BooksNarratedBySpeaker++;
                                var bookProviderId = GetBookProviderId(book);
                                if (bookProviderId != null)
                                {
                                    result[narrator].CoveredBookIds.Add(bookProviderId);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to get narrator info for book {0}", book.Title);
                    }
                }

                // Calculate missing books for each narrator
                foreach (var coverage in result.Values)
                {
                    coverage.MissingBookIds = localBooks
                        .Where(b =>
                        {
                            var bookProviderId = GetBookProviderId(b);
                            if (bookProviderId == null)
                            {
                                return false;
                            }
                            return !coverage.CoveredBookIds.Contains(bookProviderId);
                        })
                        .Select(GetBookProviderId)
                        .Where(id => id != null)
                        .ToList();
                }

                _logger.Debug("Found {0} narrators for series {1}: {2}",
                    result.Count,
                    series.Title,
                    string.Join(", ", result.Select(r => $"{r.Key} ({r.Value.BooksNarratedBySpeaker}/{totalBooksInSeries})")));

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to calculate narrator coverage for series {0}", series.Title);
                return result;
            }
        }

        private Task<BookMetadata> GetBookMetadata(Book book)
        {
            // IMPORTANT: Only use narrator info from trusted metadata sources
            // Never use ID3 tags or local file metadata for narrator information
            try
            {
                // Try to get book info from metadata providers
                var bookProviderId = GetBookProviderId(book);
                if (string.IsNullOrEmpty(bookProviderId))
                {
                    _logger.Warn("No provider ID available for book {0}", book.Title);
                    return Task.FromResult<BookMetadata>(null);
                }
                Tuple<string, Book, List<Author>> bookInfoTuple;
                if (!string.IsNullOrWhiteSpace(book.GoodreadsWorkId))
                {
                    var workProviderId = ProviderIdHelper.Canonicalize(book.GoodreadsWorkId, "gr");
                    bookInfoTuple = _bookInfo.GetWorkInfo(workProviderId, book.MediaType, AuthorIdentity.GetWorkLookupAuthorHintForProviderId(book.Author, workProviderId));
                }
                else if (BookEditionIdentity.GetGoodreadsEditionProviderId(book) is string goodreadsEditionId &&
                         !string.IsNullOrWhiteSpace(goodreadsEditionId))
                {
                    bookInfoTuple = _bookInfo.GetEditionInfo(goodreadsEditionId, book.MediaType);
                }
                else
                {
                    bookInfoTuple = _bookInfo.GetBookInfo(bookProviderId, book.MediaType, AuthorIdentity.GetWorkLookupAuthorHintForProviderId(book.Author, bookProviderId));
                }

                if (bookInfoTuple?.Item2?.Editions != null)
                {
                    // Extract narrators from metadata provider response
                    var narrators = bookInfoTuple.Item2.Editions
                        .Where(e => !e.Narrator.IsNullOrWhiteSpace())
                        .Select(e => e.Narrator)
                        .Distinct()
                        .ToList();

                    _logger.Debug("Found {0} narrators from metadata for book {1}: {2}", narrators.Count, book.Title, string.Join(", ", narrators));

                    return Task.FromResult(new BookMetadata { Narrators = narrators });
                }

                _logger.Debug("No metadata available for book {0}, skipping narrator coverage", book.Title);
                return Task.FromResult(new BookMetadata { Narrators = new List<string>() });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Metadata provider not available for book {0}, skipping narrator coverage", book.Title);
                return Task.FromResult(new BookMetadata { Narrators = new List<string>() });
            }
        }

        private static string GetBookProviderId(Book book)
        {
            if (book == null)
            {
                return null;
            }

            // Provider IDs only. Do not fall back to local database IDs or ISBNs.
            return BookEditionIdentity.GetCanonicalWorkProviderIds(book).FirstOrDefault()
                   ?? book.RemoteProviderIds?.FirstOrDefault(id => id.IsNotNullOrWhiteSpace())
                   ?? BookEditionIdentity.GetCanonicalEditionProviderIds(book).FirstOrDefault();
        }

        private class BookMetadata
        {
            public List<string> Narrators { get; set; }
        }
    }
}
