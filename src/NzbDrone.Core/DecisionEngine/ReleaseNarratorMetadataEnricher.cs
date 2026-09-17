using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
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
        IReadOnlyCollection<ReleaseInfo> EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria);
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

        // Above this many lookups the call volume is reported at Warn instead of Debug. It
        // changes logging only; every candidate is still fetched.
        private const int LoudLookupCount = 50;

        public IReadOnlyCollection<ReleaseInfo> EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
        {
            if (_indexerFactory == null || releases == null || releases.Count == 0)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var hasPinnedTarget = searchCriteria?.Books?.Any(PreferredNarratorMatcher.HasPreferredNarratorTarget) == true;
            if (!hasPinnedTarget && !HasActiveNarratorCondition(searchCriteria))
            {
                return Array.Empty<ReleaseInfo>();
            }

            var providerCache = new Dictionary<int, INarratorMetadataProvider>();
            var populated = new List<ReleaseInfo>();
            var lookups = 0;
            var stopwatch = Stopwatch.StartNew();

            foreach (var group in releases.Where(r => r != null).GroupBy(r => r.IndexerId))
            {
                var provider = GetNarratorMetadataProvider(group.Key, providerCache);
                if (provider == null || !provider.CanProvideNarratorMetadata)
                {
                    continue;
                }

                foreach (var release in group.Where(IsLikelyAudiobook).Where(NeedsNarratorMetadata))
                {
                    lookups++;
                    provider.TryPopulateNarratorMetadata(release);

                    if (release.Narrator.IsNotNullOrWhiteSpace())
                    {
                        populated.Add(release);
                    }
                }
            }

            stopwatch.Stop();

            if (lookups > 0)
            {
                // Each lookup is one indexer API call. The caller's release set is the bound,
                // so the volume is always reported rather than silently truncated.
                _logger.Log(lookups >= LoudLookupCount ? LogLevel.Warn : LogLevel.Debug,
                    "Narrator metadata: {0} indexer lookup(s) across {1} candidate release(s) populated {2} narrator(s) in {3}ms",
                    lookups, releases.Count, populated.Count, stopwatch.ElapsedMilliseconds);
            }

            return populated;
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
