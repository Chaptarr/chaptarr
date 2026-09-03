using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public interface IRefreshSeriesService
    {
        bool RefreshSeriesInfo(int authorId, List<Series> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate);
    }

    public class RefreshSeriesService : RefreshEntityServiceBase<Series, SeriesBookLink>, IRefreshSeriesService
    {
        private readonly IBookService _bookService;
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _linkService;
        private readonly IRefreshSeriesBookLinkService _refreshLinkService;
        private readonly Logger _logger;

        public RefreshSeriesService(IBookService bookService,
                                    ISeriesService seriesService,
                                    ISeriesBookLinkService linkService,
                                    IRefreshSeriesBookLinkService refreshLinkService,
                                    Logger logger)
        : base(logger)
        {
            _bookService = bookService;
            _seriesService = seriesService;
            _linkService = linkService;
            _refreshLinkService = refreshLinkService;
            _logger = logger;
        }

        private static bool HasMatchingGoodreadsSeriesId(Series local, Series remote)
        {
            // Series identity is Goodreads-backed only. Do not fall back to Amazon or title matching.
            return local?.GoodreadsSeriesId.IsNotNullOrWhiteSpace() == true &&
                   remote?.GoodreadsSeriesId.IsNotNullOrWhiteSpace() == true &&
                   local.GoodreadsSeriesId.Equals(remote.GoodreadsSeriesId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesRemoteSeries(Series local, Series remote)
        {
            return local != null &&
                   remote != null &&
                   local.MediaType == remote.MediaType &&
                   local.IsOriginal &&
                   HasMatchingGoodreadsSeriesId(local, remote);
        }

        private static string BuildBookLookupKey(BookMediaType mediaType, string providerId)
        {
            if (providerId.IsNullOrWhiteSpace())
            {
                return null;
            }

            return $"{(int)mediaType}|{providerId.Trim()}";
        }

        private static string NormalizeSeriesBookProviderId(string providerId)
        {
            if (providerId.IsNullOrWhiteSpace())
            {
                return null;
            }

            return ProviderIdHelper.Normalize(providerId.Trim(), defaultPrefix: null);
        }

        private static void AddBookLookup(Dictionary<string, List<Book>> lookup, Book book, string providerId)
        {
            var key = BuildBookLookupKey(book.MediaType, providerId);
            if (key.IsNullOrWhiteSpace())
            {
                return;
            }

            if (!lookup.TryGetValue(key, out var books))
            {
                books = new List<Book>();
                lookup[key] = books;
            }

            if (books.All(existing => existing.Id != book.Id))
            {
                books.Add(book);
            }
        }

        private Dictionary<string, List<Book>> BuildLocalBookProviderLookup(int authorId)
        {
            var lookup = new Dictionary<string, List<Book>>(StringComparer.OrdinalIgnoreCase);
            var localBooks = _bookService.GetBooksByAuthor(authorId) ?? new List<Book>();

            foreach (var book in localBooks.Where(b => b != null))
            {
                var providerIds = BookIdentity.GetProviderIdentityTokens(book)
                    .Where(id => !id.IsNullOrWhiteSpace())
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var providerId in providerIds)
                {
                    AddBookLookup(lookup, book, providerId);
                }
            }

            _logger.Debug("[SERIES] Built local series book lookup for authorId {0}: {1} books, {2} provider keys",
                authorId,
                localBooks.Count,
                lookup.Count);

            return lookup;
        }

        private List<SeriesBookLink> BuildLocalSeriesLinks(Series remote, Dictionary<string, List<Book>> localBookLookup)
        {
            var result = new List<SeriesBookLink>();
            var seriesInstanceType = remote?.IsNarratorVariant == true ? "narrator_variant" : "original";

            if (remote?.SeriesBooks == null || !remote.SeriesBooks.Any())
            {
                return result;
            }

            foreach (var seriesBook in remote.SeriesBooks)
            {
                string normalizedForeignBookId;
                try
                {
                    normalizedForeignBookId = NormalizeSeriesBookProviderId(seriesBook.BookId);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[SERIES] Ignoring invalid series book provider ID '{0}' for series '{1}'",
                        seriesBook.BookId,
                        remote.Title);
                    continue;
                }

                var key = BuildBookLookupKey(remote.MediaType, normalizedForeignBookId);
                if (key.IsNullOrWhiteSpace() || !localBookLookup.TryGetValue(key, out var matchingBooks))
                {
                    continue;
                }

                foreach (var matchingBook in matchingBooks.Where(b => b != null).DistinctBy(b => b.Id))
                {
                    result.Add(new SeriesBookLink
                    {
                        Position = seriesBook.Position,
                        SeriesPosition = int.TryParse(seriesBook.Position, out var pos) ? pos : 0,
                        Book = matchingBook,
                        BookId = matchingBook.Id,

                        // Null means the metadata API didn't surface a primary flag for this slot;
                        // default to true so not-yet-refreshed series keep their existing membership.
                        IsPrimary = seriesBook.IsPrimary ?? true,
                        SeriesInstanceType = seriesInstanceType
                    });
                }
            }

            return result;
        }

        private List<Series> FilterRemoteSeriesToLocalBooks(int authorId, List<Series> remoteSeries)
        {
            var localBookLookup = BuildLocalBookProviderLookup(authorId);
            if (localBookLookup.Count == 0)
            {
                _logger.Debug("[SERIES] No local provider IDs available for authorId {0}; no remote series can be linked", authorId);
                return new List<Series>();
            }

            var filtered = new List<Series>();
            var linkedBookCount = 0;

            foreach (var remote in remoteSeries)
            {
                if (remote?.LinkItems?.Any() == true)
                {
                    filtered.Add(remote);
                    linkedBookCount += remote.LinkItems.Count;
                    continue;
                }

                var linkItems = BuildLocalSeriesLinks(remote, localBookLookup);
                if (!linkItems.Any())
                {
                    continue;
                }

                remote.LinkItems = linkItems;
                filtered.Add(remote);
                linkedBookCount += linkItems.Count;
            }

            _logger.Debug("[SERIES] Filtered {0} remote series to {1} series linked to local books for authorId {2} ({3} links)",
                remoteSeries.Count,
                filtered.Count,
                authorId,
                linkedBookCount);

            return filtered;
        }

        protected override RemoteData GetRemoteData(Series local, List<Series> remote, Author data)
        {
            // FIX: Handle potential duplicates by using FirstOrDefault instead of SingleOrDefault
            // This can happen when multiple series have the same provider IDs
            var matches = remote.Where(x => MatchesRemoteSeries(local, x)).ToList();

            if (matches.Count > 1)
            {
                _logger.Debug("[SERIES-MATCH] Found {0} matches for series '{1}' (GR: {2}). Using first match.",
                    matches.Count, local.Title, local.GoodreadsSeriesId);
            }

            return new RemoteData
            {
                Entity = matches.FirstOrDefault(),
                // Metadata is now integrated into Author
            };
        }

        protected override bool ShouldDelete(Series local)
        {
            return local?.IsNarratorVariant != true;
        }

        protected override bool IsMerge(Series local, Series remote)
        {
            // Detect upstream splits: only compare when BOTH sides have the provider ID.
            // Null/empty means "data not available", not "different series".
            return !string.IsNullOrEmpty(local.GoodreadsSeriesId) &&
                   !string.IsNullOrEmpty(remote.GoodreadsSeriesId) &&
                   !local.GoodreadsSeriesId.Equals(remote.GoodreadsSeriesId, StringComparison.OrdinalIgnoreCase);
        }

        protected override UpdateResult UpdateEntity(Series local, Series remote)
        {
            var remoteForUpdate = RefreshEntityCopy.CloneSeries(remote);
            remoteForUpdate.UseDbFieldsFrom(local);

            if (local.Equals(remoteForUpdate))
            {
                return UpdateResult.None;
            }

            local.UseMetadataFrom(remoteForUpdate);

            return UpdateResult.UpdateTags;
        }

        protected override Series GetEntityByForeignId(Series local)
        {
            if (!string.IsNullOrEmpty(local.GoodreadsSeriesId))
            {
                var series = _seriesService.FindById(local.GoodreadsSeriesId, local.MediaType);
                if (series != null) return series;
            }

            return null;
        }

        protected override void SaveEntity(Series local)
        {
            // Use UpdateMany to avoid firing the book edited event
            _seriesService.UpdateMany(new List<Series> { local });
        }

        protected override void DeleteEntity(Series local, bool deleteFiles)
        {
            _logger.Trace($"Removing links for series {local}");
            var children = GetLocalChildren(local, null);
            _linkService.DeleteMany(children);

            if (!_linkService.GetLinksBySeries(local.Id).Any())
            {
                _logger.Trace($"Series {local} has no links remaining, removing");
                _seriesService.Delete(local.Id);
            }
        }

        protected override List<SeriesBookLink> GetRemoteChildren(Series local, Series remote)
        {
            var result = new List<SeriesBookLink>();
            var seriesInstanceType = local?.IsNarratorVariant == true ? "narrator_variant" : "original";
            var isOriginalSeries = local?.IsNarratorVariant != true;
            var claimedBookIds = isOriginalSeries
                ? _linkService.GetClaimedBookIdsForSeriesIdentity(local.MediaType, local.GoodreadsSeriesId)
                : new HashSet<int>();

            if (remote.LinkItems != null)
            {
                _logger.Trace("[SERIES-DEBUG] Using prefiltered LinkItems for series {0}", remote.Title);
                result = remote.LinkItems;

                foreach (var link in result)
                {
                    link.SeriesInstanceType = seriesInstanceType;
                }

                if (isOriginalSeries && claimedBookIds.Count > 0)
                {
                    result = result.Where(l => l != null && !claimedBookIds.Contains(l.BookId)).ToList();
                }
            }
            // Fallback for direct callers that have not prefiltered V5 SeriesBooks.
            else if (remote.SeriesBooks != null && remote.SeriesBooks.Any())
            {
                _logger.Trace("[SERIES-DEBUG] Converting {0} SeriesBooks to SeriesBookLink objects for series {1}",
                    remote.SeriesBooks.Count, remote.Title);

                foreach (var seriesBook in remote.SeriesBooks)
                {
                    var foreignBookId = seriesBook.BookId?.Trim();
                    if (foreignBookId.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    var normalizedForeignBookId = NormalizeSeriesBookProviderId(foreignBookId);
                    var idx = normalizedForeignBookId.IndexOf(':');
                    var provider = normalizedForeignBookId.Substring(0, idx).Trim();

                    // Look up ALL local book copies by provider ID, scoped to the series media type.
                    var matchingBooks = _bookService.FindAllByProviderId(provider, normalizedForeignBookId, local.MediaType);
                    if (matchingBooks.Count == 0)
                    {
                        _logger.Trace("[SERIES-DEBUG] Could not match series book '{0}' ({1}) for series {2} ({3})",
                            seriesBook.Title, foreignBookId, remote.Title, local.MediaType);
                        continue;
                    }

                    foreach (var matchingBook in matchingBooks.DistinctBy(b => b.Id))
                    {
                        if (matchingBook == null)
                        {
                            continue;
                        }

                        // Clean separation: original series excludes any copies that are already claimed by narrator variants.
                        if (isOriginalSeries && claimedBookIds.Contains(matchingBook.Id))
                        {
                            continue;
                        }

                        var link = new SeriesBookLink
                        {
                            Position = seriesBook.Position,
                            SeriesPosition = int.TryParse(seriesBook.Position, out var pos) ? pos : 0,
                            Book = matchingBook,
                            BookId = matchingBook.Id,

                            // Null means the metadata API didn't surface a primary flag for this slot;
                            // default to true so not-yet-refreshed series keep their existing membership.
                            IsPrimary = seriesBook.IsPrimary ?? true,
                            SeriesInstanceType = seriesInstanceType
                        };

                        result.Add(link);

                        _logger.Trace("[SERIES-DEBUG] Created SeriesBookLink for book {0} at position {1}",
                            matchingBook.Id, seriesBook.Position);
                    }
                }
            }
            else
            {
                _logger.Trace("[SERIES-DEBUG] No book links found for series {0}", remote.Title);
            }

            // Defensive: dedupe remote children. Some upstream payloads contain duplicate series members
            // (or multiple provider IDs that resolve to the same local book), which would violate the
            // unique constraint on (BookId, SeriesId, SeriesInstanceType) during bulk insert.
            if (result.Count > 1)
            {
                static int GetBookId(SeriesBookLink link)
                {
                    if (link == null)
                    {
                        return 0;
                    }

                    if (link.BookId != 0)
                    {
                        return link.BookId;
                    }

                    return link.Book?.Value?.Id ?? 0;
                }

                var deduped = result
                    .Where(l => l != null)
                    .GroupBy(l => new
                    {
                        BookId = GetBookId(l),
                        InstanceType = (l.SeriesInstanceType ?? seriesInstanceType).Trim().ToLowerInvariant()
                    })
                    .Select(g =>
                    {
                        // Prefer links with a real parsed series position, then lowest position.
                        return g
                            .OrderByDescending(l => l.SeriesPosition > 0)
                            .ThenBy(l => l.SeriesPosition <= 0 ? int.MaxValue : l.SeriesPosition)
                            .First();
                    })
                    .ToList();

                if (deduped.Count != result.Count)
                {
                    _logger.Warn("[SERIES] Remote series '{0}' ({1}) contained {2} duplicate book links; deduped to {3}",
                        remote.Title,
                        local?.GoodreadsSeriesId ?? local?.AmazonSeriesAsin ?? local?.HardcoverSeriesId ?? local?.OpenLibrarySeriesId ?? local?.Id.ToString(),
                        result.Count - deduped.Count,
                        deduped.Count);
                }

                result = deduped;
            }

            return result;
        }

        protected override List<SeriesBookLink> GetLocalChildren(Series entity, List<SeriesBookLink> remoteChildren)
        {
            // Get all links for this series
            return _linkService.GetLinksBySeries(entity.Id);
        }

        protected override Tuple<SeriesBookLink, List<SeriesBookLink>> GetMatchingExistingChildren(List<SeriesBookLink> existingChildren, SeriesBookLink remote)
        {
            var existingChild = existingChildren.SingleOrDefault(x => x.BookId == remote.Book.Value.Id);
            var mergeChildren = new List<SeriesBookLink>();
            return Tuple.Create(existingChild, mergeChildren);
        }

        protected override void PrepareNewChild(SeriesBookLink child, Series entity)
        {
            child.Series = entity;
            child.SeriesId = entity.Id;

            // Ensure BookId is set from the Book object
            if (child.Book?.Value != null)
            {
                child.BookId = child.Book.Value.Id;
            }

            _logger.Trace("[SERIES-DEBUG] PrepareNewChild: SeriesId={0}, BookId={1}, Position={2}",
                child.SeriesId, child.BookId, child.Position);
        }

        protected override void PrepareExistingChild(SeriesBookLink local, SeriesBookLink remote, Series entity)
        {
            local.Series = entity;
            local.SeriesId = entity.Id;
        }

        protected override bool AreChildrenUpToDate(SeriesBookLink local, SeriesBookLink remote)
        {
            if (local == null || remote == null)
            {
                return false;
            }

            var remoteForCompare = RefreshEntityCopy.CloneSeriesBookLink(remote);
            remoteForCompare.UseDbFieldsFrom(local);
            return local.Equals(remoteForCompare);
        }

        protected override SeriesBookLink CreateChildForAdd(SeriesBookLink remoteChild, Series entity)
        {
            return RefreshEntityCopy.CloneSeriesBookLink(remoteChild);
        }

        protected override void AddChildren(List<SeriesBookLink> children)
        {
            _logger.Trace("[SERIES-DEBUG] AddChildren called with {0} SeriesBookLink records to insert", children.Count);

            if (children.Any())
            {
                foreach (var link in children.Take(5))
                {
                    _logger.Trace("[SERIES-DEBUG] Inserting link: SeriesId={0}, BookId={1}, Position={2}",
                        link.SeriesId, link.BookId, link.Position);
                }

                _linkService.InsertMany(children);
                _logger.Trace("[SERIES-DEBUG] Successfully inserted {0} SeriesBookLink records", children.Count);
            }
        }

        protected override bool RefreshChildren(SortedChildren localChildren, List<SeriesBookLink> remoteChildren, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
        {
            return _refreshLinkService.RefreshSeriesBookLinkInfo(localChildren.Added, localChildren.Updated, localChildren.Merged, localChildren.Deleted, localChildren.UpToDate, remoteChildren, forceUpdateFileTags);
        }

        public bool RefreshSeriesInfo(int authorId, List<Series> remoteSeries, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
        {
            remoteSeries ??= new List<Series>();

            _logger.Trace("[SERIES-DEBUG] RefreshSeriesInfo called for authorId {0} with {1} remote series",
                authorId, remoteSeries.Count);

            // We only persist Goodreads-backed series. Amazon-only series are considered invalid and are not saved.
            var goodreadsBackedRemoteSeries = remoteSeries
                .Where(s => s != null && !s.GoodreadsSeriesId.IsNullOrWhiteSpace())
                .ToList();

            if (goodreadsBackedRemoteSeries.Count != remoteSeries.Count)
            {
                _logger.Debug("[SERIES] Filtered {0} non-Goodreads remote series for authorId {1}",
                    remoteSeries.Count - goodreadsBackedRemoteSeries.Count, authorId);
            }

            // Guardrail: if upstream returned zero Goodreads-backed series, do NOT prune existing Goodreads-backed series.
            // This avoids nuking user data on transient upstream issues.
            // We still allow the migration (and future refresh passes) to clean up any invalid local rows.
            var remoteMediaTypes = goodreadsBackedRemoteSeries
                .Select(s => s.MediaType)
                .Distinct()
                .ToHashSet();

            if (remoteMediaTypes.Count == 0)
            {
                _logger.Debug("[SERIES] No Goodreads-backed series returned for authorId {0}; skipping refresh", authorId);
                return false;
            }

            // Defensive: prevent duplicate inserts if the metadata payload contains duplicates.
            var providerBackedRemoteSeries = goodreadsBackedRemoteSeries
                .GroupBy(s => $"{(int)s.MediaType}|{s.GoodreadsSeriesId?.Trim()}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (providerBackedRemoteSeries.Count != goodreadsBackedRemoteSeries.Count)
            {
                _logger.Warn("[SERIES] Metadata payload contained {0} duplicate Goodreads series entries for authorId {1}; ignoring duplicates",
                    goodreadsBackedRemoteSeries.Count - providerBackedRemoteSeries.Count, authorId);
            }

            providerBackedRemoteSeries = FilterRemoteSeriesToLocalBooks(authorId, providerBackedRemoteSeries);
            if (providerBackedRemoteSeries.Count == 0)
            {
                _logger.Debug("[SERIES] No Goodreads-backed series could be linked to local books for authorId {0}; skipping refresh", authorId);
                return false;
            }

            var updated = false;

            // Discover existing series via:
            // 1) author-linked series (SeriesBookLink -> Book -> AuthorId)
            // 2) provider ID lookup (so linkless-but-persisted series can still be repaired)
            var existingByAuthor = _seriesService.GetByAuthorId(authorId);

            var existingBySeries = providerBackedRemoteSeries
                .GroupBy(s => s.MediaType)
                .SelectMany(g => _seriesService.FindById(
                    g.Select(s => s.GoodreadsSeriesId)
                        .Where(id => !id.IsNullOrWhiteSpace())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    g.Key))
                .ToList();

            var existing = existingByAuthor
                .Concat(existingBySeries)
                .Where(s => s != null && s.IsOriginal)
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .ToList();

            var toAdd = providerBackedRemoteSeries
                .Where(remote => !existing.Any(local => MatchesRemoteSeries(local, remote)))
                .ToList();

            if (toAdd.Any())
            {
                _seriesService.InsertMany(toAdd);
            }

            // Refresh ALL existing + newly added series through the base-class path.
            // - Matched originals get metadata + link reconciliation
            // - Unmatched narrator variants are preserved via ShouldDelete=false
            // - Legacy provider-id-less originals (if any slip through) are deleted via ShouldDelete=true
            var all = existing
                .Concat(toAdd)
                .Where(s => s != null && s.IsOriginal && remoteMediaTypes.Contains(s.MediaType))
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .ToList();

            foreach (var item in all)
            {
                updated |= RefreshEntityInfo(item, providerBackedRemoteSeries, remoteData, forceBookRefresh, forceUpdateFileTags, lastUpdate);
            }

            return updated;
        }
    }
}
