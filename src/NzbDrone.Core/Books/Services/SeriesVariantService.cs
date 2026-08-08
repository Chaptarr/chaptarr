using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface ISeriesVariantService
    {
        Task<Series> CreateSeriesNarratorVariant(int baseSeriesId, string narratorName);
        void DeleteSeriesVariant(int variantId);
        void UpdateSeriesVariantBooks(Series variant);
        List<Series> GetSeriesVariants(int baseSeriesId);
    }

    public class SeriesVariantService : ISeriesVariantService
    {
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly IMultiCopySeriesService _multiCopySeriesService;
        private readonly IBookService _bookService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public SeriesVariantService(
            ISeriesService seriesService,
            ISeriesBookLinkService seriesBookLinkService,
            IMultiCopySeriesService multiCopySeriesService,
            IBookService bookService,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _seriesService = seriesService;
            _seriesBookLinkService = seriesBookLinkService;
            _multiCopySeriesService = multiCopySeriesService;
            _bookService = bookService;
            _eventAggregator = eventAggregator;
            _logger = logger ?? LogManager.GetCurrentClassLogger();
        }

        public async Task<Series> CreateSeriesNarratorVariant(int baseSeriesId, string narratorName)
        {
            var baseSeries = _seriesService.GetSeries(baseSeriesId);
            if (baseSeries == null)
            {
                throw new ArgumentException($"Series with ID {baseSeriesId} not found");
            }

            _logger.Info("Creating narrator variant for series '{0}' with narrator '{1}'", baseSeries.Title, narratorName);

            var variant = _multiCopySeriesService.CreateNarratorVariant(baseSeries, narratorName);

            // Move narrator-matching copies into the variant (and create wanted copies where needed).
            await MoveOrCreateVariantBookLinks(baseSeries, variant, narratorName);

            // Publish event for UI update
            _eventAggregator.PublishEvent(new SeriesVariantAddedEvent(variant, baseSeries));

            var seriesProviderId = variant.GoodreadsSeriesId ?? variant.AmazonSeriesAsin ?? variant.HardcoverSeriesId ?? variant.OpenLibrarySeriesId ?? variant.Id.ToString();
            _logger.Info("Successfully created narrator variant '{0}' (ID: {1})", seriesProviderId, variant.Id);

            return variant;
        }

        public void DeleteSeriesVariant(int variantId)
        {
            var variant = _seriesService.GetSeries(variantId);
            if (variant == null)
            {
                throw new ArgumentException($"Series variant with ID {variantId} not found");
            }

            if (!variant.IsNarratorVariant)
            {
                throw new InvalidOperationException("Cannot delete non-variant series using this method");
            }

            _logger.Info("Deleting narrator variant '{0}' (ID: {1})", variant.Title, variant.Id);

            // Get all book links before deletion
            var bookLinks = _seriesBookLinkService.GetLinksBySeries(variantId);

            // Delete the series (cascade will handle book links)
            _seriesService.Delete(variantId);

            // Check if any narrator-wanted books need to be cleaned up
            CleanupOrphanedNarratorBooks(bookLinks);

            _eventAggregator.PublishEvent(new SeriesVariantDeletedEvent(variant));
        }

        public void UpdateSeriesVariantBooks(Series variant)
        {
            if (!variant.IsNarratorVariant)
            {
                return;
            }

            var baseSeriesId = string.IsNullOrWhiteSpace(variant.BaseSeriesId)
                ? null
                : _seriesService.FindById(variant.BaseSeriesId, variant.MediaType)?.Id;
            if (!baseSeriesId.HasValue)
            {
                var variantProviderId = variant.GoodreadsSeriesId ?? variant.AmazonSeriesAsin ?? variant.HardcoverSeriesId ?? variant.OpenLibrarySeriesId ?? variant.Id.ToString();
                _logger.Warn("Could not find base series for variant {0}", variantProviderId);
                return;
            }

            // Get current links for both base and variant
            var baseLinks = _seriesBookLinkService.GetLinksBySeries(baseSeriesId.Value);
            var variantLinks = _seriesBookLinkService.GetLinksBySeries(variant.Id);

            // Find new books in base series that aren't in variant
            var variantBookIds = variantLinks.Select(l => l.BookId).ToHashSet();
            var newLinks = baseLinks.Where(l => !variantBookIds.Contains(l.BookId)).ToList();

            if (newLinks.Any())
            {
                _logger.Debug("Adding {0} new book links to variant {1}", newLinks.Count, variant.Title);

                var linksToAdd = newLinks.Select(link => new SeriesBookLink
                {
                    SeriesId = variant.Id,
                    BookId = link.BookId,
                    Position = link.Position,
                    SeriesPosition = link.SeriesPosition,
                    IsPrimary = link.IsPrimary,
                    SeriesInstanceType = "narrator_variant",
                    IsInheritedLink = true
                }).ToList();

                _seriesBookLinkService.InsertMany(linksToAdd);
            }

            // Remove links that no longer exist in base series
            var baseBookIds = baseLinks.Select(l => l.BookId).ToHashSet();
            var linksToRemove = variantLinks.Where(l => l.IsInheritedLink && !baseBookIds.Contains(l.BookId)).ToList();

            if (linksToRemove.Any())
            {
                _logger.Debug("Removing {0} obsolete book links from variant {1}", linksToRemove.Count, variant.Title);
                _seriesBookLinkService.DeleteMany(linksToRemove);
            }
        }

        public List<Series> GetSeriesVariants(int baseSeriesId)
        {
            var baseSeries = _seriesService.GetSeries(baseSeriesId);
            if (baseSeries == null)
            {
                return new List<Series>();
            }

            var baseId = baseSeries.BaseSeriesId ?? baseSeries.GoodreadsSeriesId ?? baseSeries.AmazonSeriesAsin ?? baseSeries.HardcoverSeriesId ?? baseSeries.OpenLibrarySeriesId ?? baseSeries.Id.ToString();
            return _multiCopySeriesService.GetAllVariants(baseId, baseSeries.MediaType);
        }

        private Task CreateInheritedBookLinks(Series baseSeries, Series variant)
        {
            // Legacy method retained for backward compatibility; variant creation now uses MoveOrCreateVariantBookLinks.
            return Task.CompletedTask;
        }

        private async Task MoveOrCreateVariantBookLinks(Series baseSeries, Series variant, string narratorName)
        {
            var baseLinks = _seriesBookLinkService.GetLinksBySeries(baseSeries.Id);
            if (!baseLinks.Any())
            {
                _logger.Debug("Base series {0} has no links; variant {1} will have no books", baseSeries.Title, variant.DisplayTitle);
                return;
            }

            var baseBookIds = baseLinks.Select(l => l.BookId).Distinct().ToList();
            var baseBooks = _bookService.GetBooks(baseBookIds);
            var baseBookById = baseBooks.Where(b => b != null).ToDictionary(b => b.Id, b => b);

            string GetWorkKey(Book book)
            {
                if (book == null)
                {
                    return null;
                }

                var workProviderId = BookIdentity.GetStableWorkProviderIdentityTokens(book)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(workProviderId)) return workProviderId.Trim();
                var editionProviderId = BookEditionIdentity.GetCanonicalEditionProviderIds(book).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(editionProviderId)) return editionProviderId.Trim();
                return book.Id.ToString();
            }

            static (string Provider, string ProviderId) SplitProviderId(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return (null, null);
                }

                raw = raw.Trim();
                var idx = raw.IndexOf(':');
                if (idx <= 0 || idx >= raw.Length - 1)
                {
                    return (null, null);
                }

                return (raw.Substring(0, idx).Trim().ToLowerInvariant(), raw.Substring(idx + 1).Trim());
            }

            bool MatchesNarrator(Book book)
            {
                if (book == null)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(narratorName))
                {
                    return false;
                }

                var desired = narratorName.CleanNarratorName();
                var actual = (book.Narrator ?? string.Empty).CleanNarratorName();
                return !string.IsNullOrWhiteSpace(actual) && actual == desired;
            }

            var blueprint = baseLinks
                .Select(l =>
                {
                    baseBookById.TryGetValue(l.BookId, out var book);
                    return new { Link = l, Book = book };
                })
                .Where(x => x.Book != null)
                .GroupBy(x => new
                {
                    WorkKey = GetWorkKey(x.Book),
                    Position = x.Link.Position ?? string.Empty,
                    x.Link.SeriesPosition
                })
                .Select(g => g.First())
                .ToList();

            var variantLinksToInsert = new List<SeriesBookLink>();
            var baseLinksToDelete = new List<SeriesBookLink>();

            foreach (var item in blueprint)
            {
                var workKey = GetWorkKey(item.Book);
                var (provider, providerId) = SplitProviderId(workKey);

                List<Book> workCopies;
                if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(providerId))
                {
                    workCopies = _bookService.FindAllByProviderId(provider, providerId, baseSeries.MediaType);
                }
                else
                {
                    // Fallback: treat only the blueprint book as the known copy.
                    workCopies = new List<Book> { item.Book };
                }

                var narratorCopies = workCopies.Where(MatchesNarrator).ToList();
                if (!narratorCopies.Any())
                {
                    _logger.Debug("Skipping work '{0}' for narrator variant '{1}' because no existing book copy proves that narrator", item.Book.Title, narratorName);
                    continue;
                }

                foreach (var copy in narratorCopies.Where(b => b != null).DistinctBy(b => b.Id))
                {
                    variantLinksToInsert.Add(new SeriesBookLink
                    {
                        SeriesId = variant.Id,
                        BookId = copy.Id,
                        Position = item.Link.Position,
                        SeriesPosition = item.Link.SeriesPosition,
                        IsPrimary = item.Link.IsPrimary,
                        SeriesInstanceType = "narrator_variant",
                        IsInheritedLink = false
                    });

                    // Clean separation: remove these copies from the original series.
                    baseLinksToDelete.AddRange(baseLinks.Where(l => l.BookId == copy.Id));
                }
            }

            if (variantLinksToInsert.Any())
            {
                var unique = variantLinksToInsert
                    .GroupBy(l => new { l.SeriesId, l.BookId })
                    .Select(g => g.First())
                    .ToList();

                _seriesBookLinkService.InsertMany(unique);
                _logger.Debug("Created {0} narrator-variant links for {1}", unique.Count, variant.DisplayTitle);
            }

            if (baseLinksToDelete.Any())
            {
                var uniqueDeletes = baseLinksToDelete
                    .GroupBy(l => l.Id)
                    .Select(g => g.First())
                    .ToList();

                _seriesBookLinkService.DeleteMany(uniqueDeletes);
                _logger.Debug("Removed {0} claimed links from base series {1}", uniqueDeletes.Count, baseSeries.Title);
            }

            // If we created wanted copies, they need their book/edition narrator links computed later by the normal pipelines.
            await Task.CompletedTask;
        }

        private void CleanupOrphanedNarratorBooks(List<SeriesBookLink> deletedLinks)
        {
            // This would clean up any narrator-wanted book instances that are no longer needed
            // Implementation depends on how narrator-wanted books are managed
            _logger.Debug("Checking for orphaned narrator books after variant deletion");
        }
    }

    public class SeriesVariantAddedEvent : IEvent
    {
        public Series Variant { get; private set; }
        public Series BaseSeries { get; private set; }

        public SeriesVariantAddedEvent(Series variant, Series baseSeries)
        {
            Variant = variant;
            BaseSeries = baseSeries;
        }
    }

    public class SeriesVariantDeletedEvent : IEvent
    {
        public Series Variant { get; private set; }

        public SeriesVariantDeletedEvent(Series variant)
        {
            Variant = variant;
        }
    }
}
