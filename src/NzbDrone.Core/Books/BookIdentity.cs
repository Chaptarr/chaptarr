using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    internal sealed class BookReferenceComparer : IEqualityComparer<Book>
    {
        public static readonly BookReferenceComparer Instance = new();

        public bool Equals(Book x, Book y) => ReferenceEquals(x, y);
        public int GetHashCode(Book obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
    }

    public enum WorkFirstMatchDisposition
    {
        None,
        StableWork,
        EditionUnique,
        EditionAmbiguous
    }

    public sealed class WorkFirstMatchResult
    {
        public WorkFirstMatchResult(WorkFirstMatchDisposition disposition, IEnumerable<Book> matches)
        {
            Disposition = disposition;
            Matches = (matches ?? Enumerable.Empty<Book>())
                .Where(book => book != null)
                .Distinct(BookReferenceComparer.Instance)
                .ToList();
        }

        public WorkFirstMatchDisposition Disposition { get; }
        public IReadOnlyList<Book> Matches { get; }
    }

    public static class BookIdentity
    {
        public static bool MatchesByProviderIdIntersection(Book local, Book remote)
        {
            if (local == null || remote == null)
            {
                return false;
            }

            var localWorkTokens = GetStableWorkProviderIdentityTokens(local);
            var remoteWorkTokens = GetStableWorkProviderIdentityTokens(remote);

            if (localWorkTokens.Count > 0 || remoteWorkTokens.Count > 0)
            {
                return localWorkTokens.Count > 0 &&
                       remoteWorkTokens.Count > 0 &&
                       localWorkTokens.Overlaps(remoteWorkTokens);
            }

            if (local.MediaType != remote.MediaType)
            {
                return false;
            }

            var localEditionTokens = GetEditionProviderIdentityTokens(local);
            var remoteEditionTokens = GetEditionProviderIdentityTokens(remote);

            return localEditionTokens.Count > 0 &&
                   remoteEditionTokens.Count > 0 &&
                   localEditionTokens.Overlaps(remoteEditionTokens);
        }

        public static bool MatchesByStableWorkProviderId(Book local, Book remote)
        {
            var localTokens = GetStableWorkProviderIdentityTokens(local);
            if (localTokens.Count == 0)
            {
                return false;
            }

            var remoteTokens = GetStableWorkProviderIdentityTokens(remote);
            if (remoteTokens.Count == 0)
            {
                return false;
            }

            return localTokens.Overlaps(remoteTokens);
        }

        public static List<Book> FindWorkFirstMatches(IEnumerable<Book> candidates, Book remote)
        {
            var result = FindWorkFirstMatchResult(candidates, remote);
            return result.Disposition == WorkFirstMatchDisposition.EditionAmbiguous
                ? new List<Book>()
                : result.Matches.ToList();
        }

        public static WorkFirstMatchResult FindWorkFirstMatchResult(IEnumerable<Book> candidates, Book remote)
        {
            var candidateList = (candidates ?? Enumerable.Empty<Book>())
                .Where(book => book != null)
                .ToList();

            if (remote == null || candidateList.Count == 0)
            {
                return new WorkFirstMatchResult(WorkFirstMatchDisposition.None, null);
            }

            var workMatches = candidateList
                .Where(candidate => MatchesByStableWorkProviderId(candidate, remote))
                .ToList();

            if (workMatches.Any())
            {
                return new WorkFirstMatchResult(WorkFirstMatchDisposition.StableWork, workMatches);
            }

            // Edition IDs can stabilize edition-only pockets, but must never bridge into a
            // row that already carries work identity. A shared ASIN is edition identity, not
            // work identity.
            if (GetStableWorkProviderIdentityTokens(remote).Count > 0)
            {
                return new WorkFirstMatchResult(WorkFirstMatchDisposition.None, null);
            }

            var remoteEditionTokens = GetEditionProviderIdentityTokens(remote);
            if (remoteEditionTokens.Count == 0)
            {
                return new WorkFirstMatchResult(WorkFirstMatchDisposition.None, null);
            }

            var uniqueMatches = new HashSet<Book>(BookReferenceComparer.Instance);
            var ambiguousMatches = new HashSet<Book>(BookReferenceComparer.Instance);
            foreach (var token in remoteEditionTokens)
            {
                var tokenMatches = candidateList
                    .Where(candidate => GetStableWorkProviderIdentityTokens(candidate).Count == 0)
                    .Where(candidate => GetEditionProviderIdentityTokens(candidate).Contains(token))
                    .ToList();

                if (tokenMatches.Count == 1)
                {
                    uniqueMatches.Add(tokenMatches[0]);
                }
                else if (tokenMatches.Count > 1)
                {
                    ambiguousMatches.UnionWith(tokenMatches);
                }
            }

            if (ambiguousMatches.Count > 0 || uniqueMatches.Count > 1)
            {
                ambiguousMatches.UnionWith(uniqueMatches);
                return new WorkFirstMatchResult(WorkFirstMatchDisposition.EditionAmbiguous, ambiguousMatches);
            }

            return uniqueMatches.Count == 1
                ? new WorkFirstMatchResult(WorkFirstMatchDisposition.EditionUnique, uniqueMatches)
                : new WorkFirstMatchResult(WorkFirstMatchDisposition.None, null);
        }

        public static HashSet<string> GetStableWorkProviderIdentityTokens(Book book)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (book == null)
            {
                return tokens;
            }

            AddStableWork(tokens, book.HardcoverBookId, "hc");
            AddStableWork(tokens, book.GoodreadsWorkId, "gr");
            AddStableWork(tokens, book.OpenLibraryWorkId, "ol");

            foreach (var remoteProviderId in book.RemoteProviderIds ?? Enumerable.Empty<string>())
            {
                AddStableWork(tokens, remoteProviderId, null);
            }

            return tokens;
        }

        public static HashSet<string> GetEditionProviderIdentityTokens(Book book)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (book == null)
            {
                return tokens;
            }

            foreach (var editionProviderId in BookEditionIdentity.GetCanonicalEditionProviderIds(book))
            {
                Add(tokens, editionProviderId);
            }

            return tokens;
        }

        public static HashSet<string> GetProviderIdentityTokens(Book book)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (book == null)
            {
                return tokens;
            }

            // Provider IDs only (no titles, no local/internal IDs). If any ID overlaps, it's the same work.
            Add(tokens, book.ForeignEditionId);
            foreach (var workProviderId in BookEditionIdentity.GetCanonicalWorkProviderIds(book))
            {
                Add(tokens, workProviderId);
            }

            foreach (var editionProviderId in BookEditionIdentity.GetCanonicalEditionProviderIds(book))
            {
                Add(tokens, editionProviderId);
            }

            // Optional upstream provider sets (work-level) for robust matching when a work gains new provider IDs.
            if (book.RemoteProviderIds != null)
            {
                foreach (var remoteId in book.RemoteProviderIds)
                {
                    Add(tokens, remoteId);
                }
            }

            return tokens;
        }

        private static void AddStableWork(HashSet<string> tokens, string rawId, string defaultPrefix)
        {
            if (tokens == null || string.IsNullOrWhiteSpace(rawId))
            {
                return;
            }

            string normalized;
            try
            {
                normalized = ProviderIdHelper.Canonicalize(rawId.Trim(), defaultPrefix);
            }
            catch
            {
                return;
            }

            if (normalized == null)
            {
                return;
            }

            var colon = normalized.IndexOf(':');
            if (colon <= 0 || colon >= normalized.Length - 1)
            {
                return;
            }

            var prefix = normalized.Substring(0, colon);
            var id = normalized.Substring(colon + 1);
            if (id.Length == 0 || id.Contains(":"))
            {
                return;
            }

            if (prefix.Equals("hc", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("gr", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("ol", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(prefix.ToLowerInvariant() + ":" + id);
            }
        }

        private static void Add(HashSet<string> tokens, string rawId)
        {
            var normalized = NormalizeProviderId(rawId);
            if (normalized == null)
            {
                return;
            }
            tokens.Add(normalized);
        }

        private static string NormalizeProviderId(string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return null;
            }

            rawId = rawId.Trim();

            // Reject internal/local identifiers outright.
            if (rawId.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ||
                rawId.StartsWith("work:", StringComparison.OrdinalIgnoreCase) ||
                rawId.StartsWith("internal:", StringComparison.OrdinalIgnoreCase) ||
                rawId.StartsWith("golden:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // If the ID is not prefixed, we can't safely treat it as a provider ID here (except Amazon handled separately).
            var colon = rawId.IndexOf(':');
            if (colon <= 0 || colon >= rawId.Length-1)
            {
                return null;
            }

            // Normalize prefix casing and whitespace.
            var prefix = rawId.Substring(0, colon).Trim().ToLowerInvariant();
            var id = rawId.Substring(colon + 1).Trim();
            if (id.Length == 0)
            {
                return null;
            }

            // Only accept canonical provider IDs (az/gr/hc/ol/gb). We do not use ISBN or other identifiers
            // to handshake local books to remote works.
            if (!ProviderIdHelper.IsCanonicalPrefix(prefix))
            {
                return null;
            }

            return prefix + ":" + id;
        }
    }
}
