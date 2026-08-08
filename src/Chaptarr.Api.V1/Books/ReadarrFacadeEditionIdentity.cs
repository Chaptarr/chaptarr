using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.Middleware;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Validation;

namespace Chaptarr.Api.V1.Books
{
    internal static class ReadarrFacadeEditionIdentity
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static string BuildForeignEditionId(Edition edition, ReadarrFacadeContext facadeContext)
        {
            if (edition == null)
            {
                return null;
            }

            if (facadeContext?.IsGoodreads == true)
            {
                return BookEditionIdentity.GetReadarrFacadeGoodreadsEditionId(edition) ?? string.Empty;
            }

            if (facadeContext?.IsHardcover == true)
            {
                return BookEditionIdentity.GetReadarrFacadeHardcoverEditionId(edition) ?? string.Empty;
            }

            return BuildNativeForeignEditionId(edition);
        }

        private static string BuildNativeForeignEditionId(Edition edition)
        {
            var foreignEditionId = edition.ForeignEditionId?.Trim();
            if (!string.IsNullOrWhiteSpace(foreignEditionId))
            {
                if (HasCanonicalProviderIdShape(foreignEditionId))
                {
                    return foreignEditionId;
                }
            }

            var trustedForeignEditionId = BookEditionIdentity.GetTrustedForeignEditionId(edition)
                ?? TryGetTypedEditionProviderId(edition.OpenLibraryEditionId, "ol")
                ?? TryGetTypedEditionProviderId(edition.GoogleBooksEditionId, "gb");
            if (!string.IsNullOrWhiteSpace(trustedForeignEditionId))
            {
                return trustedForeignEditionId;
            }

            if (!string.IsNullOrWhiteSpace(foreignEditionId))
            {
                Logger.Warn("[NativeIdentity] Omitting bare foreign edition ID from native response. localEditionId={0} title='{1}' foreignEditionId='{2}'.",
                    edition.Id,
                    edition.Title ?? string.Empty,
                    foreignEditionId);
            }

            return string.Empty;
        }

        private static bool HasCanonicalProviderIdShape(string providerId)
        {
            var parts = providerId?.Split(new[] { ':' }, 2);
            if (parts == null || parts.Length != 2 || !ProviderIdValidator.ValidPrefixes.Contains(parts[0]))
            {
                return false;
            }

            var id = parts[1];
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim())
            {
                return false;
            }

            if (string.Equals(parts[0], "hc", StringComparison.OrdinalIgnoreCase) &&
                id.StartsWith("edition:", StringComparison.OrdinalIgnoreCase))
            {
                var hardcoverEditionId = id.Substring("edition:".Length);
                return !string.IsNullOrWhiteSpace(hardcoverEditionId) &&
                       hardcoverEditionId == hardcoverEditionId.Trim() &&
                       ProviderIdValidator.IsValidId(hardcoverEditionId);
            }

            return ProviderIdValidator.IsValidId(id);
        }

        private static string TryGetTypedEditionProviderId(string providerId, string prefix)
        {
            return ProviderIdHelper.TryNormalize(providerId, prefix, out var canonicalProviderId)
                ? canonicalProviderId
                : null;
        }

        public static List<Edition> FilterAddressableEditions(IEnumerable<Edition> editions, ReadarrFacadeContext facadeContext, string source)
        {
            var editionList = editions?.Where(e => e != null).ToList() ?? new List<Edition>();
            if (facadeContext == null)
            {
                return editionList;
            }

            var omittedCount = 0;
            var addressable = new List<Edition>();

            foreach (var edition in editionList)
            {
                if (!string.IsNullOrWhiteSpace(BuildForeignEditionId(edition, facadeContext)))
                {
                    addressable.Add(edition);
                    continue;
                }

                omittedCount++;
                Logger.Debug("[ReadarrFacade] Cannot emit edition identity in {0} dialect for localEditionId={1} title='{2}'. Omitting edition row.",
                    facadeContext.Dialect,
                    edition.Id,
                    edition.Title ?? string.Empty);
            }

            if (omittedCount > 0)
            {
                Logger.Warn("[ReadarrFacade] Omitted {0} edition row(s) without {1} identity from {2}.",
                    omittedCount,
                    facadeContext.Dialect,
                    source ?? "edition response");
            }

            return addressable;
        }
    }
}
