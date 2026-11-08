using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.CustomFormats
{
    internal static class PreferredNarratorMatcher
    {
        public static PreferredNarratorMatchResult Evaluate(CustomFormatInput input)
        {
            var targetNames = DistinctNarratorNames(input?.PreferredNarratorNames);

            if (targetNames.Count == 0)
            {
                return PreferredNarratorMatchResult.Empty;
            }

            var authorName = input?.Author?.Name;
            var nonAuthorTargets = targetNames
                .Where(target => string.IsNullOrWhiteSpace(authorName) || !IsNarratorNameMatch(target, authorName))
                .ToList();

            var releaseEvidence = ReleaseNarratorEvidenceExtractor.Extract(input);

            var matchedTargets = MatchTargets(targetNames, DistinctNarratorNames(releaseEvidence.Names));
            var overlapCount = matchedTargets.Count;
            var nonAuthorOverlapCount = matchedTargets.Count(target => nonAuthorTargets.Contains(target, StringComparer.OrdinalIgnoreCase));
            var hasAnchor = nonAuthorTargets.Count == 0 ? overlapCount > 0 : nonAuthorOverlapCount > 0;
            var any = hasAnchor;
            var majority = any && targetNames.Count >= 3 && overlapCount * 2 > targetNames.Count;
            var complete = any &&
                           targetNames.Count >= 2 &&
                           !input.PreferredNarratorHasUnresolvedNames &&
                           overlapCount == targetNames.Count;

            return new PreferredNarratorMatchResult
            {
                HasTarget = true,
                TargetCount = targetNames.Count,
                OverlapCount = overlapCount,
                NonAuthorTargetCount = nonAuthorTargets.Count,
                NonAuthorOverlapCount = nonAuthorOverlapCount,
                HasUnresolvedTargetNames = input.PreferredNarratorHasUnresolvedNames,
                Any = any,
                Majority = majority,
                Complete = complete
            };
        }

        public static bool IsMatch(CustomFormatInput input)
        {
            return Evaluate(input).Any;
        }

        public static bool HasPreferredNarratorTarget(CustomFormatInput input)
        {
            return input?.PreferredNarratorNames?.Any(name => !string.IsNullOrWhiteSpace(name)) == true;
        }

        public static void ApplyTarget(CustomFormatInput input, Book book, Edition explicitEdition = null)
        {
            ApplyTarget(input, BuildTarget(book, explicitEdition));
        }

        public static void ApplyTarget(CustomFormatInput input, PreferredNarratorTarget target)
        {
            if (input?.MediaType != BookMediaType.Audiobook || target == null)
            {
                return;
            }

            input.PreferredNarratorNames = target.Names;
            input.PreferredNarratorHasUnresolvedNames = target.HasUnresolvedNames;
        }

        public static PreferredNarratorTarget BuildTarget(Book book, Edition explicitEdition = null)
        {
            var edition = GetPreferredNarratorEdition(book, explicitEdition);
            if (!EditionReadingFormat.IsAudiobook(edition?.ReadingFormatId))
            {
                return null;
            }

            var extracted = ReleaseNarratorEvidenceExtractor.ExtractNames(edition.NarratorNames, edition.Narrator);
            if (extracted.Names.Count == 0)
            {
                return null;
            }

            return new PreferredNarratorTarget
            {
                Names = extracted.Names,
                HasUnresolvedNames = extracted.TotalCountHint > extracted.Names.Count
            };
        }

        internal static bool IsNarratorNameMatch(string releaseName, string targetName)
        {
            return NarratorNameMatcher.IsMatch(releaseName, targetName);
        }

        private static Edition GetPreferredNarratorEdition(Book book, Edition explicitEdition = null)
        {
            if (explicitEdition?.ManualAdd == true && EditionReadingFormat.IsAudiobook(explicitEdition?.ReadingFormatId))
            {
                return explicitEdition;
            }

            var editions = book?.Editions?
                .Where(edition => EditionReadingFormat.IsAudiobook(edition?.ReadingFormatId))
                .ToList() ?? new List<Edition>();

            var manualEdition = editions
                .OrderByDescending(edition => edition.ManualAdd)
                .ThenByDescending(edition => edition.Monitored)
                .ThenBy(edition => edition.Id)
                .FirstOrDefault(edition => edition.ManualAdd);

            if (manualEdition != null)
            {
                return manualEdition;
            }

            if (book?.AnyEditionOk == false)
            {
                var monitoredEdition = editions
                    .Where(edition => edition.Monitored)
                    .OrderBy(edition => edition.Id)
                    .FirstOrDefault();

                return monitoredEdition ?? (EditionReadingFormat.IsAudiobook(explicitEdition?.ReadingFormatId) ? explicitEdition : null);
            }

            return null;
        }

        private static List<string> MatchTargets(IReadOnlyCollection<string> targetNames, IReadOnlyCollection<string> releaseNames)
        {
            if (targetNames == null || releaseNames == null || targetNames.Count == 0 || releaseNames.Count == 0)
            {
                return new List<string>();
            }

            var availableReleases = releaseNames.ToList();
            var matchedTargets = new List<string>();

            foreach (var target in targetNames.OrderByDescending(name => UnicodeComparisonNormalizer.WordSpans(UnicodeComparisonNormalizer.NormalizeWords(name)).Count()))
            {
                var release = availableReleases.FirstOrDefault(candidate => IsNarratorNameMatch(candidate, target));
                if (release == null)
                {
                    continue;
                }

                matchedTargets.Add(target);
                availableReleases.Remove(release);
            }

            return matchedTargets;
        }

        private static List<string> DistinctNarratorNames(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(
                    UnicodeComparisonNormalizer.NormalizeWords,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        internal sealed class PreferredNarratorTarget
        {
            public List<string> Names { get; init; } = new List<string>();
            public bool HasUnresolvedNames { get; init; }
        }
    }

    internal sealed class PreferredNarratorMatchResult
    {
        public static PreferredNarratorMatchResult Empty { get; } = new PreferredNarratorMatchResult();

        public bool HasTarget { get; init; }
        public int TargetCount { get; init; }
        public int OverlapCount { get; init; }
        public int NonAuthorTargetCount { get; init; }
        public int NonAuthorOverlapCount { get; init; }
        public bool HasUnresolvedTargetNames { get; init; }
        public bool Any { get; init; }
        public bool Majority { get; init; }
        public bool Complete { get; init; }
    }
}
