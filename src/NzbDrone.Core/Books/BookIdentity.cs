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
        internal static Author GetRequestedWorkPrimaryAuthor(Tuple<string, Book, List<Author>> metadata, string workProviderId, BookMediaType mediaType)
        {
            var book = metadata?.Item2;
            if (book?.MediaType != mediaType ||
                !ProviderIdHelper.TryNormalize(workProviderId, null, out var requestedWorkId) ||
                !GetStableWorkProviderIdentityTokens(book).Contains(requestedWorkId))
            {
                return null;
            }

            return GetLookupPrimaryAuthor(metadata);
        }

        internal static Author GetLookupPrimaryAuthor(Tuple<string, Book, List<Author>> metadata)
        {
            if (!ProviderIdHelper.TryNormalize(metadata?.Item1, null, out var primaryProviderId)) return null;
            // The lookup's selected persona owns this work; contributor order cannot
            // replace that identity or override a conflicting returned book owner.
            var bookAuthorIds = AuthorIdentity.GetProviderIdentityTokens(metadata.Item2?.Author);
            if (bookAuthorIds.Count > 0)
            {
                return bookAuthorIds.Contains(primaryProviderId) ? metadata.Item2.Author : null;
            }

            var selected = metadata.Item3?.Where(author => AuthorIdentity.GetProviderIdentityTokens(author).Contains(primaryProviderId)).ToList();
            return selected?.Count == 1 ? selected[0] : null;
        }

        public static Book SelectRequestedBook(IEnumerable<Book> books)
        {
            return books.Where(book => book.Id > 0)
                .OrderByDescending(book => book.IsMonitored())
                .ThenBy(book => book.Id)
                .FirstOrDefault();
        }

        internal static bool HasUserPreservationMarker(Book book)
        {
            return book == null ||
                book.AddOptions?.AddType == BookAddType.Manual ||
                !book.AnyEditionOk ||
                book.Editions?.Any(edition => edition?.ManualAdd == true) == true;
        }

        // Decide from the loaded snapshot, before any edition or file ownership changes.
        internal static bool CanAbsorbDuplicate(Book book, bool hasFiles) =>
            !hasFiles && !HasLocalCopyMarker(book);

        internal static bool HasLocalCopyMarker(Book book) =>
            HasUserPreservationMarker(book) || !string.IsNullOrWhiteSpace(book.UnitKeyHash);

        internal static Book FindMatchingRemotePocket(IEnumerable<Book> candidates, Book local)
        {
            var matches = FindWorkFirstMatches(candidates?.Where(book => book.MediaType == local.MediaType), local);
            return matches.Count > 1
                ? matches.OrderByDescending(candidate => GetPocketMatchRank(candidate, local)).First()
                : matches.FirstOrDefault();
        }

        // Fingerprint = media type, stable work IDs and the complete edition-ID set.
        // Missing edition identity cannot establish an identical server pocket.
        internal static string BuildIdenticalPocketFingerprint(Book book)
        {
            if (book?.Editions == null || book.Editions.Count == 0)
            {
                return null;
            }

            var editionIds = new List<string>(book.Editions.Count);
            foreach (var edition in book.Editions)
            {
                if (edition == null || string.IsNullOrWhiteSpace(edition.ForeignEditionId))
                {
                    return null;
                }

                editionIds.Add(edition.ForeignEditionId.Trim().ToLowerInvariant());
            }

            editionIds.Sort(StringComparer.Ordinal);
            var workTokens = GetStableWorkProviderIdentityTokens(book)
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Select(token => token.Trim().ToLowerInvariant())
                .OrderBy(token => token, StringComparer.Ordinal);

            return $"{book.MediaType}::W[{string.Join(",", workTokens)}]::E[{string.Join(",", editionIds)}]";
        }

        internal static int GetPocketMatchRank(Book candidate, Book book)
        {
            // Only disambiguate candidates already matched by provider identity. The server's
            // pocket key survives edition updates; the existing fingerprint separates same-key pockets.
            var sameKey = ProviderIdHelper.TryNormalize(candidate.BaseBookId, null, out var candidateId) &&
                ProviderIdHelper.TryNormalize(book.BaseBookId, null, out var bookId) &&
                string.Equals(candidateId, bookId, StringComparison.OrdinalIgnoreCase);
            var fingerprint = BuildIdenticalPocketFingerprint(book);
            var identical = fingerprint != null && fingerprint == BuildIdenticalPocketFingerprint(candidate);
            return (sameKey ? 2 : 0) + (identical ? 1 : 0);
        }

        public static bool MatchesByProviderIdIntersection(Book local, Book remote)
        {
            if (local == null || remote == null)
            {
                return false;
            }

            var localWorkTokens = GetStableWorkProviderIdentityTokens(local);
            var remoteWorkTokens = GetStableWorkProviderIdentityTokens(remote);

            return MatchesByProviderIdIntersection(local, remote, localWorkTokens, remoteWorkTokens);
        }

        internal static bool MatchesByProviderIdIntersection(Book local, Book remote,
            HashSet<string> localWorkTokens, HashSet<string> remoteWorkTokens)
        {
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
