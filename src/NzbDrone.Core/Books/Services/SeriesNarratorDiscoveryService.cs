using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Books
{
    public class SeriesNarratorDiscoveryResult
    {
        public int SeriesId { get; set; }
        public List<NarratorInfo> CompleteNarrators { get; set; } = new List<NarratorInfo>();
        public List<NarratorInfo> PartialNarrators { get; set; } = new List<NarratorInfo>();
        public List<Series> ExistingVariants { get; set; } = new List<Series>();
        public NarratorInfo RecommendedNarrator { get; set; }
    }

    public class NarratorInfo
    {
        public string NarratorName { get; set; }
        public int BookCount { get; set; }
        public int TotalBooksInSeries { get; set; }
        public double AverageRating { get; set; }
        public bool HasCompleteSet => BookCount == TotalBooksInSeries;
        public List<string> BookTitles { get; set; } = new List<string>();
        public bool HasExistingVariant { get; set; }
    }

    public interface ISeriesNarratorDiscoveryService
    {
        Task<SeriesNarratorDiscoveryResult> DiscoverNarratorsForSeries(int seriesId);
        NarratorInfo AnalyzeNarratorForSeries(Series series, string narratorName);
    }

    public class SeriesNarratorDiscoveryService : ISeriesNarratorDiscoveryService
    {
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMultiCopySeriesService _multiCopySeriesService;
        private readonly Logger _logger;

        public SeriesNarratorDiscoveryService(
            ISeriesService seriesService,
            ISeriesBookLinkService seriesBookLinkService,
            IBookService bookService,
            IEditionService editionService,
            IMultiCopySeriesService multiCopySeriesService,
            Logger logger)
        {
            _seriesService = seriesService;
            _seriesBookLinkService = seriesBookLinkService;
            _bookService = bookService;
            _editionService = editionService;
            _multiCopySeriesService = multiCopySeriesService;
            _logger = logger ?? LogManager.GetCurrentClassLogger();
        }

        public Task<SeriesNarratorDiscoveryResult> DiscoverNarratorsForSeries(int seriesId)
        {
            var series = _seriesService.GetSeries(seriesId);
            if (series == null)
            {
                throw new ArgumentException($"Series with ID {seriesId} not found");
            }

            var result = new SeriesNarratorDiscoveryResult { SeriesId = seriesId };

            // Get all books in the series
            var seriesLinks = _seriesBookLinkService.GetLinksBySeries(seriesId);
            var bookIds = seriesLinks.Select(l => l.BookId).ToList();
            var books = _bookService.GetBooks(bookIds);

            _logger.Debug("Analyzing {0} books in series {1}", books.Count, series.Title);

            // Group books by narrator
            var narratorGroups = new Dictionary<string, NarratorInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var book in books)
            {
                // Get all physical editions of this book
                var editions = _editionService.GetEditionsByBook(book.Id)
                    .Where(e => e.Monitored)
                    .ToList();

                foreach (var edition in editions)
                {
                    if (string.IsNullOrWhiteSpace(edition.Narrator))
                    {
                        continue;
                    }

                    if (!narratorGroups.ContainsKey(edition.Narrator))
                    {
                        narratorGroups[edition.Narrator] = new NarratorInfo
                        {
                            NarratorName = edition.Narrator,
                            TotalBooksInSeries = books.Count
                        };
                    }

                    var narratorInfo = narratorGroups[edition.Narrator];
                    if (!narratorInfo.BookTitles.Contains(book.Title))
                    {
                        narratorInfo.BookCount++;
                        narratorInfo.BookTitles.Add(book.Title);
                    }

                    // Update rating
                    if (edition.Ratings?.Value > 0)
                    {
                        var currentTotal = narratorInfo.AverageRating * (narratorInfo.BookCount - 1);
                        narratorInfo.AverageRating = (currentTotal + (double)edition.Ratings.Value) / narratorInfo.BookCount;
                    }
                }
            }

            // Check for existing narrator variants
            var baseSeriesId = series.BaseSeriesId ?? series.GoodreadsSeriesId ?? series.AmazonSeriesAsin ?? series.HardcoverSeriesId ?? series.OpenLibrarySeriesId ?? series.Id.ToString();
            var existingVariants = _multiCopySeriesService.GetAllVariants(baseSeriesId, series.MediaType);
            result.ExistingVariants = existingVariants.Where(v => v.IsNarratorVariant).ToList();

            // Match narrators to narrator entities and check for existing variants
            foreach (var narratorInfo in narratorGroups.Values)
            {
                // Check if variant already exists
                narratorInfo.HasExistingVariant = existingVariants.Any(v =>
                    v.IsNarratorVariant &&
                    string.Equals(v.Narrator, narratorInfo.NarratorName, StringComparison.OrdinalIgnoreCase));

                // Categorize narrator
                if (narratorInfo.HasCompleteSet)
                {
                    result.CompleteNarrators.Add(narratorInfo);
                }
                else
                {
                    result.PartialNarrators.Add(narratorInfo);
                }
            }

            // Find recommended narrator (highest rated complete narrator without existing variant)
            result.RecommendedNarrator = result.CompleteNarrators
                .Where(n => !n.HasExistingVariant)
                .OrderByDescending(n => n.AverageRating)
                .ThenByDescending(n => n.BookCount)
                .FirstOrDefault();

            _logger.Debug("Found {0} complete narrators and {1} partial narrators for series {2}", result.CompleteNarrators.Count, result.PartialNarrators.Count, series.Title);

            return Task.FromResult(result);
        }

        public NarratorInfo AnalyzeNarratorForSeries(Series series, string narratorName)
        {
            var seriesLinks = _seriesBookLinkService.GetLinksBySeries(series.Id);
            var bookIds = seriesLinks.Select(l => l.BookId).ToList();
            var books = _bookService.GetBooks(bookIds);

            var narratorInfo = new NarratorInfo
            {
                NarratorName = narratorName,
                TotalBooksInSeries = books.Count
            };

            foreach (var book in books)
            {
                var editions = _editionService.GetEditionsByBook(book.Id)
                    .Where(e => e.Monitored &&
                           string.Equals(e.Narrator, narratorName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (editions.Any())
                {
                    narratorInfo.BookCount++;
                    narratorInfo.BookTitles.Add(book.Title);

                    var avgRating = editions.Where(e => e.Ratings?.Value > 0)
                        .Select(e => e.Ratings.Value)
                        .DefaultIfEmpty(0)
                        .Average();

                    if (avgRating > 0)
                    {
                        var currentTotal = narratorInfo.AverageRating * (narratorInfo.BookCount - 1);
                        narratorInfo.AverageRating = (currentTotal + (double)avgRating) / narratorInfo.BookCount;
                    }
                }
            }

            return narratorInfo;
        }
    }
}
