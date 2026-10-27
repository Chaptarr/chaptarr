using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

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
        private readonly ICustomFormatService _customFormatService;

        public ReleaseNarratorMetadataEnricher(Logger logger, IIndexerFactory indexerFactory, ICustomFormatService customFormatService)
        {
            _logger = logger;
            _indexerFactory = indexerFactory;
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

            var books = searchCriteria?.Books?.Where(book => book?.MediaType == BookMediaType.Audiobook).ToList();
            if (books == null || books.Count == 0)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var targets = books.Select(book => PreferredNarratorMatcher.BuildTarget(book)).Where(target => target != null).ToList();
            var scoredIds = searchCriteria.Author?.GetQualityProfileForMediaType(BookMediaType.Audiobook)?.FormatItems?
                .Where(item => item.Score != 0 && item.Format != null).Select(item => item.Format.Id).ToHashSet();
            var conditions = scoredIds?.Count > 0
                ? _customFormatService.All().Where(format => scoredIds.Contains(format.Id) && format.AppliesToMediaType(BookMediaType.Audiobook))
                    .SelectMany(format => format.Specifications).Where(condition =>
                        condition is NarratorSpecification or NarratorNamesSpecification or AudioProductionSpecification).ToList()
                : new List<ICustomFormatSpecification>();
            if (targets.Count == 0 && conditions.Count == 0)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var populated = new List<ReleaseInfo>();
            var lookups = 0;
            var stopwatch = Stopwatch.StartNew();

            foreach (var group in releases.Where(r => r != null).GroupBy(r => r.IndexerId))
            {
                var provider = GetNarratorMetadataProvider(group.Key);
                if (provider == null || !provider.CanProvideNarratorMetadata)
                {
                    continue;
                }

                foreach (var release in group)
                {
                    try
                    {
                        if (IsLikelyAudiobook(release) && NeedsMetadata(release, searchCriteria.Author, books, targets, conditions))
                        {
                            lookups++;
                            if (provider.TryPopulateNarratorMetadata(release))
                            {
                                populated.Add(release);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Optional narrator metadata failed for release '{0}' from indexer '{1}' ({2}); keeping existing search evidence",
                            release.Title, release.Indexer, release.IndexerId);
                    }
                }
            }

            stopwatch.Stop();

            if (lookups > 0)
            {
                // The caller's release set bounds lookup attempts. Cached results may avoid HTTP;
                // endpoint fallback may need more than one request per lookup.
                _logger.Log(lookups >= LoudLookupCount ? LogLevel.Warn : LogLevel.Debug,
                    "Narrator metadata: {0} indexer lookup(s) across {1} candidate release(s) updated {2} release(s) in {3}ms",
                    lookups, releases.Count, populated.Count, stopwatch.ElapsedMilliseconds);
            }

            return populated;
        }

        private INarratorMetadataProvider GetNarratorMetadataProvider(int indexerId)
        {
            if (indexerId <= 0)
            {
                return null;
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

            return provider;
        }

        private static bool NeedsMetadata(ReleaseInfo release, Author author, List<Book> books,
            IReadOnlyCollection<PreferredNarratorMatcher.PreferredNarratorTarget> targets,
            IReadOnlyCollection<ICustomFormatSpecification> conditions)
        {
            if (release.HasNfo == false)
            {
                return false;
            }

            if (conditions.Count > 0)
            {
                var input = CustomFormatCalculationService.CreateReleaseInput(new RemoteBook { Release = release, Author = author, Books = books });
                if (conditions.Any(condition => condition is AudioProductionSpecification) &&
                    !AudioProductionDetector.IsDramatizedOrFullCast(input.IsGraphicAudio, input.AudioProductionType, input.AudioProductionFields))
                {
                    return true;
                }

                // A negated condition can match because evidence is absent. Only positive
                // evidence settles that condition without an additional lookup.
                if (input.Narrator.IsNullOrWhiteSpace() && conditions.Any(condition =>
                    (condition is NarratorSpecification or NarratorNamesSpecification) && condition.IsSatisfiedBy(input) == condition.Negate))
                {
                    return true;
                }
            }

            return targets.Count > 0 && NeedsNarratorMetadata(release, targets);
        }

        private static bool NeedsNarratorMetadata(ReleaseInfo release, IReadOnlyCollection<PreferredNarratorMatcher.PreferredNarratorTarget> targets)
        {
            if (!string.IsNullOrWhiteSpace(release.Narrator))
            {
                return false;
            }

            return !targets
                .Where(target => !target.HasUnresolvedNames || target.Names.Count == 1)
                .Any(target => target.Names.All(name =>
                    ReleaseNarratorEvidenceExtractor.ContainsKnownNarratorName(new[] { name }, release.Title) ||
                    release.NarratorEvidenceFields?.Any(field =>
                        ReleaseNarratorEvidenceExtractor.ContainsKnownNarratorName(new[] { name }, field)) == true));
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
    }
}
