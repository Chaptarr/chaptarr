using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.DecisionEngine;

namespace Chaptarr.Api.V1.Indexers
{
    public class InteractiveReleaseMapper
    {
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
        private readonly ICached<DownloadDecision> _downloadDecisionCache;
        private readonly Logger _logger;

        public InteractiveReleaseMapper(ICacheManager cacheManager, Logger logger)
        {
            _downloadDecisionCache = cacheManager.GetCache<DownloadDecision>(typeof(ReleaseController), "downloadDecisions");
            _logger = logger;
        }

        public ReleaseResource MapDecision(DownloadDecision decision, int weight, string searchId = null, int? bookId = null)
        {
            var resource = decision.ToResource(weight);
            if (searchId != null)
            {
                resource.SearchId = searchId;
                resource.SearchedBookId = bookId;
                resource.DownloadUrl = null;
                resource.MagnetUrl = null;
            }

            // Grouped searches own their decisions until thirty minutes after the batch ends.
            if (searchId == null)
            {
                resource.ExpiresAt = DateTime.UtcNow + CacheLifetime;
                _downloadDecisionCache.Set(GetCacheKey(resource), decision, CacheLifetime);
            }
            return resource;
        }

        public (List<ReleaseResource> results, List<ReleaseResource> hiddenResults, FilterSummary filterSummary) MapInteractiveDecisions(IEnumerable<DownloadDecision> decisions, bool bypassFilters = false, Func<DownloadDecision, int, ReleaseResource> mapDecision = null)
        {
            mapDecision ??= (decision, weight) => MapDecision(decision, weight);
            var result = new List<ReleaseResource>();
            var hiddenResults = new List<ReleaseResource>();
            var allDecisions = decisions.ToList(); // Convert to list for multiple enumeration
            var totalDecisions = allDecisions.Count;
            var filteredCount = 0;
            var hardFilteredCount = 0;
            var softFilteredCount = 0;
            var durationsFound = 0;
            var narratorsFound = 0;
            var graphicAudioFound = 0;

            foreach (var downloadDecision in allDecisions)
            {
                // Count MAM metadata for trace logging
                var releaseInfo = downloadDecision.RemoteBook.Release;
                if (releaseInfo != null)
                {
                    if (!string.IsNullOrWhiteSpace(releaseInfo.Duration))
                    {
                        durationsFound++;
                    }

                    if (!string.IsNullOrWhiteSpace(releaseInfo.Narrator))
                    {
                        narratorsFound++;
                    }

                    if (releaseInfo.IsGraphicAudio)
                    {
                        graphicAudioFound++;
                    }
                }

                // Enhanced filtering logic with bypass support
                var shouldFilter = InteractiveSearchVisibility.Evaluate(downloadDecision);
                if (shouldFilter.ShouldFilter)
                {
                    filteredCount++;
                    if (shouldFilter.IsHardFilter)
                    {
                        hardFilteredCount++;
                    }
                    else
                    {
                        softFilteredCount++;
                    }

                    _logger.Debug("FILTER_DEBUG_REJECTION: Title='{0}', Rejections=[{1}], Hard={2}, Soft={3}, HasIsHardFilter={4}, HasIsSoftFilter={5}, BypassMode={6}",
                        downloadDecision.RemoteBook.Release.Title,
                        string.Join(", ", downloadDecision.Rejections.Select(r => $"'{r.Reason}'")),
                        shouldFilter.IsHardFilter,
                        downloadDecision.Rejections.All(r => r.IsSoftFilter),
                        downloadDecision.Rejections.Any(r => r.IsHardFilter),
                        downloadDecision.Rejections.Any(r => r.IsSoftFilter),
                        bypassFilters);

                    hiddenResults.Add(mapDecision(downloadDecision, hiddenResults.Count));
                    continue;
                }

                var release = mapDecision(downloadDecision, result.Count);

                result.Add(release);
            }

            foreach (var release in result.Concat(hiddenResults))
            {
                release.IsPreferredChoice = false;
            }

            var preferred = result.FirstOrDefault(release => release.Approved && release.DownloadAllowed &&
                (!release.SearchedBookId.HasValue || release.BookId == release.SearchedBookId));
            if (preferred != null)
            {
                preferred.IsPreferredChoice = true;
            }

            // Create filter summary
            var filterSummary = CreateFilterSummary(allDecisions, result, hardFilteredCount, softFilteredCount, bypassFilters);

            // Enhanced logging with filter breakdown
            _logger.Trace("MAM_SEARCH_SUMMARY: TOTAL={0}, FILTERED={1} (HARD={2}, SOFT={3}), DISPLAYED={4}, DURATIONS_FOUND={5}, NARRATORS_FOUND={6}, GRAPHIC_AUDIO={7}, BYPASS_MODE={8}", totalDecisions, filteredCount, hardFilteredCount, softFilteredCount, result.Count, durationsFound, narratorsFound, graphicAudioFound, bypassFilters);

            if (bypassFilters && softFilteredCount > 0)
            {
                _logger.Debug("FILTER_BYPASS: {0} soft-filtered release(s) are available in the hidden results and can be grabbed deliberately (visible list: {1})", softFilteredCount, result.Count);
            }

            if (durationsFound == 0 && totalDecisions > 0)
            {
                _logger.Trace("MAM_DURATION_WARNING: No durations found in {0} results - check pattern matching", totalDecisions);
            }

            return (result, hiddenResults, filterSummary);
        }

        internal static FilterSummary CreateFilterSummary(IEnumerable<DownloadDecision> allDecisions, List<ReleaseResource> displayedResults, int hardFilteredCount, int softFilteredCount, bool bypassFilters)
        {
            var decisions = allDecisions.ToList();
            var hiddenDecisions = decisions
                .Where(decision => InteractiveSearchVisibility.Evaluate(decision).ShouldFilter)
                .ToList();
            var totalDecisions = decisions.Count;
            var filteredCount = hardFilteredCount + softFilteredCount;

            // Analyze filter categories and create breakdown
            var filterBreakdown = new Dictionary<string, int>();
            var filteredCategories = new List<string>();
            var filterWarnings = new List<string>();

            foreach (var decision in hiddenDecisions)
            {
                foreach (var rejection in decision.Rejections)
                {
                    var category = rejection.Category ?? "General";
                    if (!filterBreakdown.ContainsKey(category))
                    {
                        filterBreakdown[category] = 0;
                        filteredCategories.Add(category);
                    }

                    filterBreakdown[category]++;
                }
            }

            if (filteredCount > 0)
            {
                filterWarnings.Add($"{filteredCount} results hidden because author/book identity or release contents could not be safely resolved");
            }

            // Create summary text
            var summaryText = "";
            if (filteredCount > 0)
            {
                if (bypassFilters)
                {
                    summaryText = $"Showing {displayedResults.Count} of {totalDecisions} results (bypass mode enabled, {hardFilteredCount} hard filtered)";
                }
                else
                {
                    summaryText = $"Showing {displayedResults.Count} of {totalDecisions} results ({filteredCount} filtered)";
                }
            }
            else
            {
                summaryText = $"Showing all {displayedResults.Count} results";
            }

            return new FilterSummary
            {
                TotalResults = totalDecisions,
                FilteredCount = filteredCount,
                DisplayedCount = displayedResults.Count,
                HardFilteredCount = hardFilteredCount,
                SoftFilteredCount = softFilteredCount,
                HasSoftFilters = softFilteredCount > 0,
                HasHardFilters = hardFilteredCount > 0,
                BypassMode = bypassFilters,
                FilteredCategories = filteredCategories.Distinct().ToList(),
                FilterBreakdown = filterBreakdown,
                SummaryText = summaryText,
                FilterWarnings = filterWarnings
            };
        }

        internal static string GetCacheKey(ReleaseResource resource)
        {
            return resource.SearchId == null
                ? string.Concat(resource.IndexerId, "_", resource.Guid)
                : $"search:{Guid.Parse(resource.SearchId):N}:book:{resource.SearchedBookId}:indexer:{resource.IndexerId}:{resource.Guid}";
        }

    }
}
