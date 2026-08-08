using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IReleaseNarratorMetadataEnricher
    {
        void EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria);
    }

    public class ReleaseNarratorMetadataEnricher : IReleaseNarratorMetadataEnricher
    {
        private readonly Logger _logger;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly ICustomFormatService _customFormatService;

        public ReleaseNarratorMetadataEnricher(Logger logger,
                                               IIndexerFactory indexerFactory,
                                               IQualityProfileService qualityProfileService,
                                               ICustomFormatService customFormatService)
        {
            _logger = logger;
            _indexerFactory = indexerFactory;
            _qualityProfileService = qualityProfileService;
            _customFormatService = customFormatService;
        }

        public void EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
        {
            if (_indexerFactory == null || releases == null || releases.Count == 0)
            {
                return;
            }

            var hasPinnedTarget = searchCriteria?.Books?.Any(PreferredNarratorMatcher.HasPreferredNarratorTarget) == true;
            if (!hasPinnedTarget && !HasActiveNarratorCondition(searchCriteria))
            {
                return;
            }

            const int maxLookupsPerIndexer = 8;
            const int maxLookupsTotal = 25;

            var providerCache = new Dictionary<int, INarratorMetadataProvider>();
            var totalLookups = 0;

            foreach (var group in releases.Where(r => r != null).GroupBy(r => r.IndexerId))
            {
                if (totalLookups >= maxLookupsTotal)
                {
                    break;
                }

                var provider = GetNarratorMetadataProvider(group.Key, providerCache);
                if (provider == null || !provider.CanProvideNarratorMetadata)
                {
                    continue;
                }

                var indexerLookups = 0;
                foreach (var release in group.Where(IsLikelyAudiobook))
                {
                    if (totalLookups >= maxLookupsTotal || indexerLookups >= maxLookupsPerIndexer)
                    {
                        break;
                    }

                    if (!NeedsNarratorMetadata(release))
                    {
                        continue;
                    }

                    indexerLookups++;
                    totalLookups++;
                    provider.TryPopulateNarratorMetadata(release);
                }
            }

            if (totalLookups > 0)
            {
                _logger.Debug("Fetched narrator metadata for {0} release(s) before custom format scoring", totalLookups);
            }
        }

        private INarratorMetadataProvider GetNarratorMetadataProvider(int indexerId, Dictionary<int, INarratorMetadataProvider> cache)
        {
            if (indexerId <= 0)
            {
                return null;
            }

            if (cache.TryGetValue(indexerId, out var cached))
            {
                return cached;
            }

            INarratorMetadataProvider provider = null;

            try
            {
                var definition = _indexerFactory.Find(indexerId);
                if (definition != null)
                {
                    provider = _indexerFactory.GetInstance(definition) as INarratorMetadataProvider;
                }
            }
            catch (System.Exception ex)
            {
                _logger.Trace(ex, "Narrator metadata enrichment: failed to resolve indexer provider for id {0}", indexerId);
            }

            cache[indexerId] = provider;
            return provider;
        }

        private static bool NeedsNarratorMetadata(ReleaseInfo release)
        {
            if (release == null)
            {
                return false;
            }

            if (release.HasNfo.HasValue && release.HasNfo.Value == false)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(release.Narrator))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(PreferredNarratorMatcher.ExtractNarratorFromFields(new[] { release.Title }));
        }

        private static bool IsLikelyAudiobook(ReleaseInfo release)
        {
            var title = release?.Title ?? string.Empty;

            if (Regex.IsMatch(title, @"\b(epub|mobi|azw3?|pdf|djvu|cbz|cbr|fb2|ebook)\b", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(title, @"\b(audiobook|audible|mp3|m4b|m4a|flac|aac|opus|ogg|wav)\b", RegexOptions.IgnoreCase))
            {
                return false;
            }

            return true;
        }

        private bool HasActiveNarratorCondition(SearchCriteriaBase searchCriteria)
        {
            var profileId = searchCriteria?.Author?.AudiobookQualityProfileId;
            if (!profileId.HasValue || profileId.Value <= 0 || _qualityProfileService == null || _customFormatService == null)
            {
                return false;
            }

            try
            {
                var profile = _qualityProfileService.Get(profileId.Value);
                var activeFormatIds = (profile?.FormatItems ?? new List<ProfileFormatItem>())
                    .Where(item => item.Score != 0 && item.Format?.Id > 0)
                    .Select(item => item.Format.Id)
                    .ToHashSet();

                if (activeFormatIds.Count == 0)
                {
                    return false;
                }

                return _customFormatService.All()
                    .Where(format => activeFormatIds.Contains(format.Id))
                    .Any(format => format.Specifications?.Any(specification => specification is NarratorSpecification or NarratorNamesSpecification) == true);
            }
            catch (System.Exception ex)
            {
                _logger.Trace(ex, "Narrator metadata enrichment: failed to resolve audiobook profile {0}", profileId.Value);
                return false;
            }
        }
    }
}
