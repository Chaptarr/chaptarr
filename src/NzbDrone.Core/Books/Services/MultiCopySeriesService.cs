using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Books
{
    public interface IMultiCopySeriesService
    {
        string GenerateSeriesVariantId(string baseSeriesId, string narratorName);
        Series CreateNarratorVariant(Series baseSeries, string narratorName);
        List<Series> GetAllVariants(string baseSeriesId, BookMediaType? mediaType = null);
        Series GetOrCreateNarratorVariant(Series baseSeries, string narratorName);
        int GetNextVariantNumber(string baseSeriesId, BookMediaType? mediaType = null);
    }

    public class MultiCopySeriesService : IMultiCopySeriesService
    {
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly Logger _logger;

        // Regex to match narrator variant IDs like "series_id_n1", "series_id_n2", etc.
        private static readonly Regex NarratorVariantIdPattern = new Regex(@"^(.+)_n(\d+)$", RegexOptions.Compiled);

        public MultiCopySeriesService(ISeriesService seriesService, ISeriesBookLinkService seriesBookLinkService, Logger logger)
        {
            _seriesService = seriesService;
            _seriesBookLinkService = seriesBookLinkService;
            _logger = logger ?? LogManager.GetCurrentClassLogger();
        }

        public string GenerateSeriesVariantId(string baseSeriesId, string narratorName)
        {
            var baseId = ExtractBaseSeriesId(baseSeriesId);
            var nextNumber = GetNextVariantNumber(baseId);

            return $"{baseId}_n{nextNumber}";
        }

        public Series CreateNarratorVariant(Series baseSeries, string narratorName)
        {
            var baseSeriesId = baseSeries.BaseSeriesId ?? baseSeries.GoodreadsSeriesId ?? baseSeries.AmazonSeriesAsin ?? baseSeries.HardcoverSeriesId ?? baseSeries.OpenLibrarySeriesId ?? baseSeries.Id.ToString();
            var baseId = ExtractBaseSeriesId(baseSeriesId);
            var nextVariantNumber = GetNextVariantNumber(baseId, baseSeries.MediaType);
            var variantId = $"{baseId}_n{nextVariantNumber}";

            var variant = new Series
            {
                // Set provider IDs based on base series
                GoodreadsSeriesId = baseSeries.GoodreadsSeriesId,
                AmazonSeriesAsin = baseSeries.AmazonSeriesAsin,
                HardcoverSeriesId = baseSeries.HardcoverSeriesId,
                OpenLibrarySeriesId = baseSeries.OpenLibrarySeriesId,
                // LocalSeriesId removed - using database IDs directly
                BaseSeriesId = baseSeriesId,
                InstanceNumber = nextVariantNumber,
                Title = baseSeries.Title,
                TitleSlug = baseSeries.TitleSlug + "-" + narratorName.ToLowerInvariant().Replace(" ", "-"),
                Description = baseSeries.Description,
                Numbered = baseSeries.Numbered,
                WorkCount = baseSeries.WorkCount,
                PrimaryWorkCount = baseSeries.PrimaryWorkCount,
                Narrator = narratorName,
                PreferredNarratorId = null,
                MediaType = baseSeries.MediaType
            };

            _logger.Debug("Creating narrator variant {0} for series {1} with narrator {2}", variantId, baseSeries.Title, narratorName);

            return _seriesService.AddSeries(variant);
        }

        public List<Series> GetAllVariants(string baseSeriesId, BookMediaType? mediaType = null)
        {
            var baseId = ExtractBaseSeriesId(baseSeriesId);

            // Get all series where BaseSeriesId matches
            var allVariants = _seriesService.GetAllSeries()
                .Where(s => s.BaseSeriesId == baseId ||
                           s.GoodreadsSeriesId == baseId ||
                           s.AmazonSeriesAsin == baseId ||
                           s.HardcoverSeriesId == baseId ||
                           s.OpenLibrarySeriesId == baseId ||
                           s.Id.ToString() == baseId)
                .Where(s => !mediaType.HasValue || s.MediaType == mediaType.Value)
                .ToList();

            return allVariants;
        }

        public Series GetOrCreateNarratorVariant(Series baseSeries, string narratorName)
        {
            var baseSeriesId = baseSeries.BaseSeriesId ?? baseSeries.GoodreadsSeriesId ?? baseSeries.AmazonSeriesAsin ?? baseSeries.HardcoverSeriesId ?? baseSeries.OpenLibrarySeriesId ?? baseSeries.Id.ToString();
            var existingVariants = GetAllVariants(baseSeriesId, baseSeries.MediaType);

            // Check if a variant with this narrator already exists
            var normalizedNarratorName = (narratorName ?? string.Empty).CleanNarratorName();
            var existingVariant = existingVariants.FirstOrDefault(s =>
                s.IsNarratorVariant &&
                !string.IsNullOrWhiteSpace(normalizedNarratorName) &&
                (s.Narrator ?? string.Empty).CleanNarratorName() == normalizedNarratorName);

            if (existingVariant != null)
            {
                var variantProviderId = existingVariant.GoodreadsSeriesId ?? existingVariant.AmazonSeriesAsin ?? existingVariant.HardcoverSeriesId ?? existingVariant.OpenLibrarySeriesId ?? existingVariant.Id.ToString();
                _logger.Debug("Found existing narrator variant {0} for series {1}", variantProviderId, baseSeries.Title);
                return existingVariant;
            }

            return CreateNarratorVariant(baseSeries, narratorName);
        }

        public int GetNextVariantNumber(string baseSeriesId, BookMediaType? mediaType = null)
        {
            var baseId = ExtractBaseSeriesId(baseSeriesId);
            var existingVariants = GetAllVariants(baseId, mediaType);

            if (!existingVariants.Any(v => v.IsNarratorVariant))
            {
                return 1;
            }

            var maxNumber = existingVariants
                .Where(v => v.IsNarratorVariant)
                .Max(v => v.InstanceNumber);

            return maxNumber + 1;
        }

        private string ExtractBaseSeriesId(string seriesId)
        {
            // If it matches narrator variant pattern, extract base ID
            var match = NarratorVariantIdPattern.Match(seriesId);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Otherwise return as-is
            return seriesId;
        }
    }
}
