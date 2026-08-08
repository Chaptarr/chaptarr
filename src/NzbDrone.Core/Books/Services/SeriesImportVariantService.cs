using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface ISeriesImportVariantService
    {
        Task ProcessSeriesForAutomaticVariants(int seriesId);
        Task ProcessSeriesForAutomaticVariants(Series series);
        Task<bool> ShouldCreateVariantForNarrator(Series series, string narratorName);
    }

    public class SeriesImportVariantService : ISeriesImportVariantService,
        IHandle<SeriesRefreshCompleteEvent>
    {
        private readonly ISeriesService _seriesService;
        private readonly IBookService _bookService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly INarratorCoverageService _narratorCoverageService;
        private readonly IMultiCopySeriesService _multiCopySeriesService;
        private readonly ISeriesVariantService _seriesVariantService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public SeriesImportVariantService(
            ISeriesService seriesService,
            IBookService bookService,
            ISeriesBookLinkService seriesBookLinkService,
            INarratorCoverageService narratorCoverageService,
            IMultiCopySeriesService multiCopySeriesService,
            ISeriesVariantService seriesVariantService,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _seriesService = seriesService;
            _bookService = bookService;
            _seriesBookLinkService = seriesBookLinkService;
            _narratorCoverageService = narratorCoverageService;
            _multiCopySeriesService = multiCopySeriesService;
            _seriesVariantService = seriesVariantService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public async Task ProcessSeriesForAutomaticVariants(int seriesId)
        {
            var series = _seriesService.GetSeries(seriesId);
            if (series == null)
            {
                _logger.Warn("Series {0} not found, skipping variant processing", seriesId);
                return;
            }

            await ProcessSeriesForAutomaticVariants(series);
        }

        public async Task ProcessSeriesForAutomaticVariants(Series series)
        {
            if (series == null || series.Id <= 0)
            {
                _logger.Debug("Skipping automatic narrator variant processing for unpersisted series");
                return;
            }

            // Only process original series, not variants
            if (!series.IsOriginal)
            {
                _logger.Debug("Series {0} is already a variant, skipping", series.Title);
                return;
            }

            try
            {
                _logger.Debug("Processing series {0} for automatic narrator variants", series.Title);

                // Get all books in the series
                var seriesLinks = _seriesBookLinkService.GetLinksBySeries(series.Id);
                if (!seriesLinks.Any())
                {
                    _logger.Debug("No books in series {0}, skipping variant processing", series.Title);
                    return;
                }

                var bookIds = seriesLinks.Select(l => l.BookId).ToList();
                var books = _bookService.GetBooks(bookIds);

                // Get unique narrators from user's physical collection
                // IMPORTANT: Only use narrator data that came from metadata sources
                var userNarrators = books
                    .Where(b => b.BookFiles?.Any() == true && !b.Narrator.IsNullOrWhiteSpace())
                    .Select(b => b.Narrator)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!userNarrators.Any())
                {
                    _logger.Debug("No books with narrators in user collection for series {0}", series.Title);
                    return;
                }

                _logger.Debug("Found {0} unique narrators in user collection for series {1}: {2}", userNarrators.Count, series.Title, string.Join(", ", userNarrators));

                // Check narrator coverage from metadata sources
                var narratorCoverage = await _narratorCoverageService.GetNarratorCoverageForSeries(series);

                if (!narratorCoverage.Any())
                {
                    _logger.Debug("No narrator coverage data available for series {0} (metadata sources may be unavailable)", series.Title);
                    return;
                }

                foreach (var narratorName in userNarrators)
                {
                    await ProcessNarratorForVariant(series, narratorName, narratorCoverage, books);
                }

                // Check if all books have complete narrator coverage
                await CheckForBaseSeriesRemoval(series, books, narratorCoverage);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing series {0} for automatic variants", series.Title);
            }
        }

        public async Task<bool> ShouldCreateVariantForNarrator(Series series, string narratorName)
        {
            if (narratorName.IsNullOrWhiteSpace())
            {
                return false;
            }

            return await _narratorCoverageService.CheckNarratorHasCompleteCoverage(series, narratorName);
        }

        private async Task ProcessNarratorForVariant(
            Series series,
            string narratorName,
            Dictionary<string, NarratorCoverageInfo> narratorCoverage,
            List<Book> seriesBooks)
        {
            // Check if this narrator has complete coverage
            var coverage = narratorCoverage.Values.FirstOrDefault(c =>
                c.NarratorName.Equals(narratorName, StringComparison.OrdinalIgnoreCase));

            if (coverage == null || !coverage.HasCompleteCoverage)
            {
                _logger.Debug("Narrator {0} does not have complete coverage for series {1}", narratorName, series.Title);
                return;
            }

            // Check if variant already exists
            var existingVariants = _multiCopySeriesService.GetAllVariants(
                series.BaseSeriesId ?? series.GoodreadsSeriesId ?? series.AmazonSeriesAsin ?? series.HardcoverSeriesId ?? series.OpenLibrarySeriesId ?? series.Id.ToString(),
                series.MediaType);
            var existingVariant = existingVariants.FirstOrDefault(v =>
                v.IsNarratorVariant &&
                v.Narrator.Equals(narratorName, StringComparison.OrdinalIgnoreCase));

            if (existingVariant != null)
            {
                _logger.Debug("Variant already exists for narrator {0} in series {1}", narratorName, series.Title);

                // Update book links if needed
                await UpdateVariantBookLinks(existingVariant, seriesBooks, narratorName);
                return;
            }

            _logger.Info("Creating narrator variant for series {0} with narrator {1}", series.Title, narratorName);

            var variant = await _seriesVariantService.CreateSeriesNarratorVariant(series.Id, narratorName);

            // Link books to the new variant
            await UpdateVariantBookLinks(variant, seriesBooks, narratorName);

            _eventAggregator.PublishEvent(new SeriesVariantAutoCreatedEvent(variant, series, narratorName));
        }

        private Task UpdateVariantBookLinks(Series variant, List<Book> seriesBooks, string narratorName)
        {
            // Ensure books with this narrator are linked to the variant
            var booksWithNarrator = seriesBooks
                .Where(b => b.Narrator.Equals(narratorName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var book in booksWithNarrator)
            {
                var existingLink = _seriesBookLinkService.GetLinksBySeries(variant.Id)
                    .FirstOrDefault(l => l.BookId == book.Id);

                if (existingLink == null)
                {
                    _logger.Debug("Linking book {0} to narrator variant {1}", book.Title, variant.DisplayTitle);

                    // This would need to be implemented to update book-series links
                    // For now, the variant creation should have handled this
                }
            }

            return Task.CompletedTask;
        }

        private Task CheckForBaseSeriesRemoval(
            Series series,
            List<Book> books,
            Dictionary<string, NarratorCoverageInfo> narratorCoverage)
        {
            // If all books in the user's collection have narrators with complete coverage,
            // we might want to hide/remove the base series (future enhancement)
            // For now, we'll keep the base series visible
            var booksWithoutCompleteNarratorCoverage = books
                .Where(b => b.BookFiles?.Any() == true)
                .Where(b => !narratorCoverage.Values.Any(c =>
                    c.HasCompleteCoverage &&
                    c.NarratorName.Equals(b.Narrator, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (booksWithoutCompleteNarratorCoverage.Any())
            {
                _logger.Debug("Series {0} has {1} books without complete narrator coverage, keeping base series", series.Title, booksWithoutCompleteNarratorCoverage.Count);
            }

            return Task.CompletedTask;
        }

        public void Handle(SeriesRefreshCompleteEvent message)
        {
            if (message?.Series == null || !message.Series.IsOriginal)
            {
                return;
            }

            _logger.Debug("Processing series for automatic narrator variants after series refresh: {0}", message.Series.Title);

            try
            {
                // Fire and forget - don't block the refresh
                Task.Run(async () => await ProcessSeriesForAutomaticVariants(message.Series));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing series {0} for automatic variants", message.Series.Title);
            }
        }
    }

    public class SeriesVariantAutoCreatedEvent : IEvent
    {
        public Series Variant { get; private set; }
        public Series BaseSeries { get; private set; }
        public string NarratorName { get; private set; }

        public SeriesVariantAutoCreatedEvent(Series variant, Series baseSeries, string narratorName)
        {
            Variant = variant;
            BaseSeries = baseSeries;
            NarratorName = narratorName;
        }
    }
}
