using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    public static class MatchingContextPresets
    {
        public static MatchingContext ForScanLocal(bool allowPathFallback = true)
        {
            return new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = !allowPathFallback,
                PerFileMatching = true
            };
        }

        public static MatchingContext ForScanV5(bool allowPathFallback = true)
        {
            return new MatchingContext
            {
                AllowV5Identification = true,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = !allowPathFallback,
                PerFileMatching = true
            };
        }

        public static MatchingContext ForManualPreview(bool allowPathFallback = true)
        {
            return new MatchingContext
            {
                AllowV5Identification = true,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = !allowPathFallback,
                PerFileMatching = true,
                AllowGroupedV5Suggestions = true
            };
        }

        public static MatchingContext ForScanScopedRematch()
        {
            return new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = true
            };
        }

        public static MatchingContext ForDownloaded(bool allowV5Identification, IEnumerable<int> targetBookIds = null, bool allowPathFallback = false)
        {
            var context = new MatchingContext
            {
                AllowV5Identification = allowV5Identification,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = !allowPathFallback,
                PerFileMatching = true,
                TargetBookIds = NormalizeTargetBookIds(targetBookIds)
            };

            return context;
        }

        public static MatchingContext ForAuthorReady()
        {
            return new MatchingContext
            {
                AllowV5Identification = true,
                AllowAuthorImport = true,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = false
            };
        }

        public static MatchingContext ForDirectDefault(bool allowPathFallback = true)
        {
            return new MatchingContext
            {
                AllowV5Identification = true,
                AllowAuthorImport = true,
                DeferUnmatchedToAuthorReady = true,
                AllowUnscopedFallback = true,
                DisablePathFallback = !allowPathFallback,
                PerFileMatching = false
            };
        }

        private static List<int> NormalizeTargetBookIds(IEnumerable<int> targetBookIds)
        {
            var ids = targetBookIds?
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            return ids?.Count > 0 ? ids : null;
        }
    }
}
