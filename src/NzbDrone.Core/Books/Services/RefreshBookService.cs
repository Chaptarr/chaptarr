using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public interface IRefreshBookService
    {
        bool RefreshBookInfo(Book book, List<Book> remoteBooks, Author remoteData, bool forceUpdateFileTags);
        bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate);
    }

    public class RefreshBookService : RefreshEntityServiceBase<Book, Edition>,
        IRefreshBookService,
        IExecute<RefreshBookCommand>,
        IExecute<BulkRefreshBookCommand>
    {
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IEditionService _editionService;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IProvideBookInfo _bookInfo;
        private readonly IRefreshEditionService _refreshEditionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ICheckIfBookShouldBeRefreshed _checkIfBookShouldBeRefreshed;
        private readonly IEditionSelector _editionSelector;
        private readonly IEditionMetadataProfileFilter _editionMetadataProfileFilter;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly Logger _logger;
        private EditionRefreshMatchingIndex _editionRefreshMatchingIndex;

        // Cache for book metadata during refresh
        private readonly Dictionary<string, Author> _bookMetadataCache = new Dictionary<string, Author>();

        public RefreshBookService(IBookService bookService,
                                  IAuthorService authorService,
                                  IRootFolderService rootFolderService,
                                          IEditionService editionService,
                                  IProvideAuthorInfo authorInfo,
                                  IProvideBookInfo bookInfo,
                                  IRefreshEditionService refreshEditionService,
                                  IMediaFileService mediaFileService,
                                  IHistoryService historyService,
                                  IEventAggregator eventAggregator,
                                  ICheckIfBookShouldBeRefreshed checkIfBookShouldBeRefreshed,
                                  IEditionSelector editionSelector,
                                  IEditionMetadataProfileFilter editionMetadataProfileFilter,
                                  IMapCoversToLocal mediaCoverService,
                                  Logger logger)
        : base(logger)
        {
            _bookService = bookService;
            _authorService = authorService;
            _rootFolderService = rootFolderService;
            _editionService = editionService;
            _authorInfo = authorInfo;
            _bookInfo = bookInfo;
            _refreshEditionService = refreshEditionService;
            _mediaFileService = mediaFileService;
            _historyService = historyService;
            _eventAggregator = eventAggregator;
            _checkIfBookShouldBeRefreshed = checkIfBookShouldBeRefreshed;
            _editionSelector = editionSelector;
            _editionMetadataProfileFilter = editionMetadataProfileFilter;
            _mediaCoverService = mediaCoverService;
            _logger = logger;
        }

        private string GetPrimaryProviderKey(Book b)
        {
            if (b == null)
            {
                return null;
            }

            // Provider IDs only. Do not fall back to ISBN/ISBN10/ISBN13 for remote resolution.
            // ISBNs are not stable identifiers for mapping a local book/work to the metadata server.
            foreach (var candidate in GetPrimaryProviderKeyCandidates(b))
            {
                if (ProviderIdHelper.TryNormalize(candidate, defaultPrefix: null, out var normalized))
                {
                    return normalized;
                }

                if (candidate.IsNotNullOrWhiteSpace())
                {
                    _logger.Debug("Ignoring malformed provider lookup key '{0}' for book {1} (Id: {2})", candidate, b.Title, b.Id);
                }
            }

            return null;
        }

        private IEnumerable<string> GetPrimaryProviderKeyCandidates(Book b)
        {
            foreach (var id in BookIdentity.GetStableWorkProviderIdentityTokens(b))
            {
                yield return id;
            }

            // Edition-level ids are only a fallback for books that do not have a stable work id.
            foreach (var id in TryGetProviderIds(() => BookEditionIdentity.GetCanonicalEditionProviderIds(b, _logger, "RefreshBookService.GetPrimaryProviderKey"), b, "edition"))
            {
                yield return id;
            }
        }

        private IEnumerable<string> TryGetProviderIds(Func<List<string>> getProviderIds, Book book, string scope)
        {
            try
            {
                return getProviderIds() ?? new List<string>();
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warn(ex, "Ignoring malformed {0} provider IDs for book {1} (Id: {2})", scope, book?.Title, book?.Id);
                return new List<string>();
            }
        }

        private bool HasUsableRemoteEditions(Book local, Book remote, string source)
        {
            if (remote?.Editions == null || !remote.Editions.Any())
            {
                _logger.Error("[REMOTE-BOOK-DATA] Skipping remote book '{0}' for local book '{1}' (Id: {2}) because {3} returned no editions. ProviderId={4}",
                    remote?.Title ?? local?.Title ?? "Unknown",
                    local?.Title ?? "Unknown",
                    local?.Id,
                    source,
                    GetPrimaryProviderKey(local) ?? "unknown");

                return false;
            }

            var retentionSelection = BuildRetentionSelection(local, remote, remote.Editions.ToList());
            if (retentionSelection.RetainedEditions != null && retentionSelection.RetainedEditions.Any())
            {
                return true;
            }

            _logger.Warn("[REMOTE-BOOK-DATA] Skipping remote book '{0}' for local book '{1}' (Id: {2}) because {3} left no retained editions after metadata-profile and media-type filtering. ProviderId={4}",
                remote?.Title ?? local?.Title ?? "Unknown",
                local?.Title ?? "Unknown",
                local?.Id,
                source,
                GetPrimaryProviderKey(local) ?? "unknown");

            return false;
        }

        private Author GetSkyhookData(Book book)
        {
            string bookIdentifier = null;

            try
            {
                bookIdentifier = GetPrimaryProviderKey(book);
                var cacheKey = $"{bookIdentifier}:{book.MediaType}";

                if (string.IsNullOrEmpty(bookIdentifier))
                {
                    _logger.Warn("No provider ID available for book {0} (Id: {1}), skipping remote refresh (ISBNs are not used for lookup)", book.Title, book.Id);
                    return null;
                }

                // Check cache first
                if (_bookMetadataCache.ContainsKey(cacheKey))
                {
                    _logger.Debug("Using cached metadata for book: {0}", bookIdentifier);
                    return _bookMetadataCache[cacheKey];
                }

                Tuple<string, Book, List<Author>> tuple;
                if (!string.IsNullOrWhiteSpace(book.GoodreadsWorkId))
                {
                    var workProviderId = ProviderIdHelper.Canonicalize(book.GoodreadsWorkId, "gr");
                    tuple = _bookInfo.GetWorkInfo(workProviderId, book.MediaType, AuthorIdentity.GetWorkLookupAuthorHintForProviderId(book.Author, workProviderId));
                }
                else if (BookEditionIdentity.GetGoodreadsEditionProviderId(book, _logger, "RefreshBookService.GetSkyhookData") is string goodreadsEditionId &&
                         !string.IsNullOrWhiteSpace(goodreadsEditionId))
                {
                    tuple = _bookInfo.GetEditionInfo(goodreadsEditionId, book.MediaType);
                }
                else
                {
                    tuple = _bookInfo.GetBookInfo(bookIdentifier, book.MediaType, AuthorIdentity.GetWorkLookupAuthorHintForProviderId(book.Author, bookIdentifier));
                }

                if (tuple == null || tuple.Item2 == null)
                {
                    _logger.Warn("BookInfo returned no book metadata for {0} (Id: {1})", bookIdentifier, book.Id);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(tuple.Item1))
                {
                    _logger.Warn("BookInfo returned no author key for {0} (Id: {1}), skipping remote refresh", bookIdentifier, book.Id);
                    return null;
                }

                Author author;
                try
                {
                    author = _authorInfo.GetAuthorInfo(tuple.Item1);
                }
                catch (AuthorNotFoundException ex)
                {
                    _logger.Warn(ex, "Could not resolve author metadata for book {0} (Id: {1}, AuthorKey: {2})", bookIdentifier, book.Id, tuple.Item1);
                    return null;
                }
                catch (InvalidProviderIdException ex)
                {
                    _logger.Warn(ex, "Invalid author identifier returned for book {0} (Id: {1}, AuthorKey: {2})", bookIdentifier, book.Id, tuple.Item1);
                    return null;
                }

                var newbook = tuple.Item2;

                // Debug logging to understand edition issue
                _logger.Debug("GetBookInfo returned book {0} with {1} editions",
                    newbook.Title,
                    newbook.Editions?.Count ?? 0);

                newbook.Author = author;
                newbook.AuthorId = author.Id;
                newbook.MediaType = book.MediaType;

                if (!HasUsableRemoteEditions(book, newbook, "direct metadata lookup"))
                {
                    return null;
                }

                // Cache the metadata
                _bookMetadataCache[cacheKey] = author;

                author.Books = new List<Book> { newbook };
                return author;
            }
            catch (BookNotFoundException)
            {
                _logger.Warn("Could not find book metadata for {0} (Id: {1})", bookIdentifier ?? "unknown", book?.Id);
            }
            catch (BadRequestException ex)
            {
                // Treat provider lookup errors as non-fatal during refresh.
                _logger.Warn(ex, "Bad request looking up book metadata for {0} (Id: {1})", bookIdentifier ?? "unknown", book?.Id);
            }
            catch (InvalidProviderIdException ex)
            {
                _logger.Warn(ex, "Invalid provider ID looking up book metadata for {0} (Id: {1})", bookIdentifier ?? "unknown", book?.Id);
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warn(ex, "Malformed provider ID looking up book metadata for {0} (Id: {1})", bookIdentifier ?? "unknown", book?.Id);
            }

            return null;
        }

        protected override RemoteData GetRemoteData(Book local, List<Book> remote, Author data)
        {
            var result = new RemoteData();
            var hasAuthorScopedRemoteSnapshot = remote != null;

            // Find matching book by any provider ID.
            // With dual-instance architecture, remote can contain both audiobook and ebook instances that share provider IDs,
            // so prefer matching by MediaType to avoid cross-contaminating metadata/editions between instances.
            var matchingBooks = BookIdentity.FindWorkFirstMatches(
                remote?.Where(x => x.MediaType == local.MediaType),
                local);

            var book = matchingBooks.FirstOrDefault();

            if (book != null && !HasUsableRemoteEditions(local, book, "author payload"))
            {
                book = null;
            }

            if (book == null && !hasAuthorScopedRemoteSnapshot)
            {
                data = GetSkyhookData(local);
                if (data != null && data.Books != null)
                {
                    var skyhookMatches = BookIdentity.FindWorkFirstMatches(
                        data.Books?.Where(x => x.MediaType == local.MediaType),
                        local);

                    book = skyhookMatches.FirstOrDefault();
                }
            }
            else if (book == null)
            {
                _logger.Debug("[REMOTE-BOOK-DATA] Direct metadata fallback suppressed for local book '{0}' (Id: {1}) because an author-scoped remote snapshot was supplied. The author blob after metadata-profile/native-format pruning is authoritative.",
                    local?.Title ?? "Unknown",
                    local?.Id);
            }

            if (book == null && ShouldDelete(local))
            {
                return result;
            }

            result.Entity = book;
            return result;
        }

        protected override void EnsureNewParent(Book local, Book remote)
        {
            // Make sure the appropriate author exists (it could be that an book changes parent)
            // The authorMetadata entry will be in the db but make sure a corresponding author is too
            // so that the book doesn't just disappear.

            // Skip parent validation for multi-copy books or when remote is null
            if (remote == null || remote.Author == null)
            {
                _logger.Debug("Skipping parent validation - remote book or metadata is null (likely a multi-copy book)");
                return;
            }

            // TODO filter by metadata id before hitting database
            var authorIdentifier = remote.Author.HardcoverAuthorId ??
                                   remote.Author.GoodreadsAuthorId ??
                                   remote.Author.OpenLibraryAuthorId ??
                                   remote.Author.GoogleBooksAuthorId;

            _logger.Trace($"Ensuring parent author exists [{authorIdentifier}]");

            Author newAuthor = null;
            if (!string.IsNullOrEmpty(remote.Author.HardcoverAuthorId))
            {
                newAuthor = _authorService.FindByProviderId("hc", remote.Author.HardcoverAuthorId);
            }
            else if (!string.IsNullOrEmpty(remote.Author.GoodreadsAuthorId))
            {
                newAuthor = _authorService.FindByProviderId("gr", remote.Author.GoodreadsAuthorId);
            }
            else if (!string.IsNullOrEmpty(remote.Author.OpenLibraryAuthorId))
            {
                newAuthor = _authorService.FindByProviderId("ol", remote.Author.OpenLibraryAuthorId);
            }
            else if (!string.IsNullOrEmpty(remote.Author.GoogleBooksAuthorId))
            {
                newAuthor = _authorService.FindByProviderId("gb", remote.Author.GoogleBooksAuthorId);
            }

            if (newAuthor == null)
            {
                // Fallback: try all upstream provider IDs (handles golden author ID merges/splits).
                if (remote.Author.RemoteProviderIds != null)
                {
                    foreach (var remoteId in remote.Author.RemoteProviderIds)
                    {
                        if (string.IsNullOrWhiteSpace(remoteId))
                        {
                            continue;
                        }

                        var colonIdx = remoteId.IndexOf(':');
                        if (colonIdx <= 0 || colonIdx >= remoteId.Length - 1)
                        {
                            continue;
                        }

                        var prefix = remoteId.Substring(0, colonIdx);
                        var id = remoteId.Substring(colonIdx + 1);
                        newAuthor = _authorService.FindByProviderId(prefix, id);
                        if (newAuthor != null)
                        {
                            break;
                        }
                    }
                }

                if (newAuthor != null)
                {
                    return;
                }

                // VIOLATION OF ARCHITECTURE: RefreshBookService should NOT create authors
                // "ALL ROADS LEAD TO AUTHORS" - Books cannot create their parent authors
                // If the author doesn't exist, log a warning but don't create it
                _logger.Warn("Book {0} references author {1} that doesn't exist in the database. " +
                           "This book's author association may be incorrect. " +
                           "Authors must be added through the proper author import flow.",
                           local.Title, authorIdentifier);

                // TODO: This needs proper handling - perhaps mark the book as having an invalid author reference
                // For now, we'll continue with the refresh but the author association may be broken
            }
        }

        protected override bool ShouldDelete(Book local)
        {
            // Readarr-style pruning:
            // Delete books that disappear from metadata (or are filtered out by metadata profiles)
            // unless they are pinned by user intent (manual add, wanted narrator, strict/manual edition)
            // or have physical files.
            if (HasLocalBookInstancePreservationMarker(local))
            {
                return false;
            }

            return !HasKnownFiles(local);
        }

        private static bool HasLocalBookInstancePreservationMarker(Book book)
        {
            if (book == null)
            {
                return true;
            }

            if (book.AddOptions?.AddType == BookAddType.Manual)
            {
                return true;
            }


            if (!book.AnyEditionOk)
            {
                return true;
            }

            return book.Editions?.Any(edition => edition?.ManualAdd == true) == true;
        }

        private bool HasKnownFiles(Book book)
        {
            if (book == null)
            {
                return false;
            }

            if (book.BookFiles != null)
            {
                return book.BookFiles.Any();
            }

            return book.Id > 0 && _mediaFileService.GetFilesByBook(book.Id).Any();
        }

        private int GetKnownFileCount(Edition edition)
        {
            if (edition == null || edition.Id <= 0)
            {
                return 0;
            }

            if (edition.BookFiles != null)
            {
                return edition.BookFiles.Count;
            }

            try
            {
                return _mediaFileService.GetFilesByEdition(edition.Id)?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        protected override void LogProgress(Book local)
        {
            if (BulkAuthorRefreshProgressContext.Current != null)
            {
                var commandId = ProgressMessageContext.CommandModel?.Id;

                if (commandId.HasValue)
                {
                    _eventAggregator.PublishEvent(new BulkAuthorBookProgressEvent(commandId.Value, $"Checking Info for {local.Title}"));
                }

                return;
            }

            _logger.ProgressInfo("Checking Info for {0}", local.Title);
        }

        protected override bool IsMerge(Book local, Book remote)
        {
            if (BookIdentity.MatchesByStableWorkProviderId(local, remote))
            {
                return false;
            }

            // Detect upstream splits: a provider ID that BOTH sides have now points to
            // different entities.  A null/empty remote ID means "data not available from
            // the API" — NOT "different book".  Only compare when both sides are populated.
            return (!string.IsNullOrEmpty(local.HardcoverBookId) && !string.IsNullOrEmpty(remote.HardcoverBookId) && local.HardcoverBookId != remote.HardcoverBookId) ||
                   (!string.IsNullOrEmpty(local.GoodreadsWorkId) && !string.IsNullOrEmpty(remote.GoodreadsWorkId) && local.GoodreadsWorkId != remote.GoodreadsWorkId) ||
                   (!string.IsNullOrEmpty(local.OpenLibraryWorkId) && !string.IsNullOrEmpty(remote.OpenLibraryWorkId) && local.OpenLibraryWorkId != remote.OpenLibraryWorkId) ||
                   (BookEditionIdentity.GetGoogleBooksEditionId(local, _logger, "RefreshBookService.IsMerge.local") is string localGoogleBooksId &&
                    BookEditionIdentity.GetGoogleBooksEditionId(remote, _logger, "RefreshBookService.IsMerge.remote") is string remoteGoogleBooksId &&
                    !string.Equals(localGoogleBooksId, remoteGoogleBooksId, StringComparison.OrdinalIgnoreCase));
        }

        protected override UpdateResult UpdateEntity(Book local, Book remote)
        {
            UpdateResult result;

            LogRemovedProviderIdentity(local, remote);

            var remoteAuthor = remote?.Author;
            var remoteAuthorId = remoteAuthor != null && remoteAuthor.Id > 0 ? remoteAuthor.Id : remote?.AuthorId ?? 0;

            var remoteForUpdate = RefreshEntityCopy.CloneBook(remote, includeEditions: false);
            remoteForUpdate.UseDbFieldsFrom(local);

            // If local release date is null and remote doesn't have one either,
            // future refreshes can fill it if the configured metadata server adds it later.
            if (local.ReleaseDate == null && remoteForUpdate.ReleaseDate == null)
            {
                _logger.Debug("Book {0} has null release date, will be updated if found in future refreshes", local.Title);
            }

            if (local.Title != (remoteForUpdate.Title ?? "Unknown") ||
                IsMerge(local, remoteForUpdate) ||
                (local.Author != null && remoteForUpdate.Author != null &&
                 (local.Author.HardcoverAuthorId != remoteForUpdate.Author.HardcoverAuthorId ||
                  local.Author.GoodreadsAuthorId != remoteForUpdate.Author.GoodreadsAuthorId ||
                  local.Author.OpenLibraryAuthorId != remoteForUpdate.Author.OpenLibraryAuthorId ||
                  local.Author.GoogleBooksAuthorId != remoteForUpdate.Author.GoogleBooksAuthorId)))
            {
                result = UpdateResult.UpdateTags;
            }
            else if (!local.Equals(remoteForUpdate))
            {
                _logger.Trace("Book [{0}][{1}] metadata changed during refresh", local.Id, local.Title);

                result = UpdateResult.Standard;
            }
            else
            {
                result = UpdateResult.None;
            }

            // Force update and fetch covers if images have changed so that we can write them into tags
            // if (remote.Images.Any() && !local.Images.SequenceEqual(remote.Images))
            // {
            //     _mediaCoverService.EnsureBookCovers(remote);
            //     result = UpdateResult.UpdateTags;
            // }

            // IMPORTANT: Only apply remote metadata when we have detected a real change.
            // Refresh runs frequently and BookInfoProxy synthesizes volatile fields (e.g. LastUpdated);
            // applying metadata unconditionally causes pointless churn and breaks idempotence.
            if (result != UpdateResult.None)
            {
                local.UseMetadataFrom(remoteForUpdate);
            }
            else
            {
                local.RemoteProviderIds = CloneRemoteProviderIds(remoteForUpdate.RemoteProviderIds);
            }

            if (remoteAuthorId > 0 && local.AuthorId != remoteAuthorId)
            {
                if (remoteAuthor != null && remoteAuthor.Id == remoteAuthorId)
                {
                    _bookService.ReassignAuthor(local, remoteAuthor);
                }
                else
                {
                    _bookService.ReassignAuthor(local, remoteAuthorId);
                }
            }

            local.LastInfoSync = DateTime.UtcNow;

            return result;
        }

        private void LogRemovedProviderIdentity(Book local, Book remote)
        {
            var localProviderIds = BookIdentity.GetStableWorkProviderIdentityTokens(local);
            if (localProviderIds.Count == 0)
            {
                return;
            }

            var remoteProviderIds = BookIdentity.GetStableWorkProviderIdentityTokens(remote);
            var removedProviderIds = localProviderIds
                .Except(remoteProviderIds, StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (removedProviderIds.Count == 0)
            {
                return;
            }

            _logger.Warn(
                "[PROVIDER-ID-DRIFT] Server blueprint removed work provider IDs [{0}] from book '{1}' (local BookId={2}); replacing local identity with current server IDs [{3}]. Fix unexpected identity drift upstream.",
                string.Join(", ", removedProviderIds),
                local?.Title ?? remote?.Title ?? "Unknown",
                local?.Id ?? 0,
                string.Join(", ", remoteProviderIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)));
        }

        private static HashSet<string> CloneRemoteProviderIds(IEnumerable<string> source)
        {
            var values = source?
                .Where(id => !id.IsNullOrWhiteSpace())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return values?.Count > 0 ? values : null;
        }

        private static Edition SelectMonitoredEdition(IEnumerable<Edition> editions)
        {
            return editions?
                .Where(e => e != null && e.Monitored)
                .OrderBy(e => e.Id)
                .FirstOrDefault();
        }

        protected override UpdateResult MergeEntity(Book local, Book target, Book remote)
        {
            _logger.Warn($"Book {local} was merged with {remote} because the original was a duplicate.");

            // Update book ids for trackfiles
            var files = local.BookFiles ?? _mediaFileService.GetFilesByBook(local.Id);
            var targetEditions = target.Editions ?? _editionService.GetEditionsByBook(target.Id);
            if (targetEditions == null || targetEditions.Count == 0)
            {
                _logger.Warn("Unable to reassign files from book {0} (ID: {1}) to merge target {2} (ID: {3}) because the target has no editions.",
                    local.Title, local.Id, target.Title, target.Id);
            }
            else
            {
                var targetEditionLookup = targetEditions
                    .Where(e => !string.IsNullOrWhiteSpace(e.ForeignEditionId))
                    .GroupBy(e => e.ForeignEditionId)
                    .ToDictionary(g => g.Key, g => SelectMonitoredEdition(g) ?? g.OrderBy(e => e.Id).First());

                var fallbackEditionId = (SelectMonitoredEdition(targetEditions)?.Id) ?? targetEditions[0].Id;

                files.ForEach(file =>
                {
                    var foreignEditionId = file.Edition?.ForeignEditionId;
                    if (!string.IsNullOrWhiteSpace(foreignEditionId) && targetEditionLookup.TryGetValue(foreignEditionId, out var matchingEdition))
                    {
                        file.EditionId = matchingEdition.Id;
                    }
                    else
                    {
                        file.EditionId = fallbackEditionId;
                    }
                });

                _mediaFileService.Update(files);
            }

            // Update book ids for history
            var items = _historyService.GetByBook(local.Id, null);
            items.ForEach(x => x.BookId = target.Id);
            _historyService.UpdateMany(items);

            _bookService.DeleteMany(new List<Book> { local });

            return UpdateResult.UpdateTags;
        }

        protected override Book GetEntityByForeignId(Book local)
        {
            // Try to find by any provider ID
            if (!string.IsNullOrEmpty(local.HardcoverBookId))
            {
                var book = _bookService.FindByProviderId("hc", local.HardcoverBookId, local.MediaType);
                if (book != null) return book;
            }

            if (BookEditionIdentity.GetGoodreadsEditionProviderId(local, _logger, "RefreshBookService.GetEntityByForeignId") is string goodreadsEditionId &&
                !string.IsNullOrEmpty(goodreadsEditionId))
            {
                var book = _bookService.FindByProviderId("gr", goodreadsEditionId, local.MediaType);
                if (book != null) return book;
            }

            if (!string.IsNullOrEmpty(local.OpenLibraryWorkId))
            {
                var book = _bookService.FindByProviderId("ol", local.OpenLibraryWorkId, local.MediaType);
                if (book != null) return book;
            }

            if (BookEditionIdentity.GetGoogleBooksEditionId(local, _logger, "RefreshBookService.GetEntityByForeignId") is string googleBooksEditionId &&
                !string.IsNullOrEmpty(googleBooksEditionId))
            {
                var book = _bookService.FindByProviderId("gb", googleBooksEditionId, local.MediaType);
                if (book != null) return book;
            }

            return null;
        }

        protected override void SaveEntity(Book local)
        {
            // Use UpdateMany to avoid firing the book edited event
            _bookService.UpdateMany(new List<Book> { local });
        }

        protected override void DeleteEntity(Book local, bool deleteFiles)
        {
            _bookService.DeleteBook(local.Id, deleteFiles);
        }

        protected override List<Edition> GetRemoteChildren(Book local, Book remote)
        {
            _logger.Debug("GetRemoteChildren called for book {0} (Id: {1})",
                local?.Title ?? "Unknown",
                local?.Id.ToString() ?? "Unknown");

            if (remote == null)
            {
                _logger.Warn("Remote book is null for {0}", local?.Title ?? "Unknown");
                return new List<Edition>();
            }

            _logger.Debug("Remote book {0} has {1} editions",
                remote.Title ?? "Unknown",
                remote.Editions?.Count ?? 0);

            if (remote.Editions == null || !remote.Editions.Any())
            {
                _logger.Warn("No editions found for remote book {0}", local?.Title ?? "Unknown");
                return new List<Edition>();
            }

            // For books with multiple physical copies, each copy manages its own edition
            // This is handled by having different LocalBookIds for each copy

            var retentionSelection = BuildRetentionSelection(local, remote, remote.Editions.ToList());
            var editionsToImport = retentionSelection.RetainedEditions?.ToList() ?? new List<Edition>();

            var audiobookEditions = editionsToImport.Where(e => e?.ReadingFormatId == 2).ToList();
            var audiobookCount = audiobookEditions.Count;
            var otherCount = editionsToImport.Count - audiobookCount;

            _logger.Debug("Importing {0} editions for book {1}: {2} audiobook editions, {3} other editions",
                editionsToImport.Count, local.Title, audiobookCount, otherCount);

            if (audiobookCount > 0)
            {
                _logger.Debug("Audiobook narrators: {0}",
                    string.Join(", ", audiobookEditions
                        .Where(e => e.NarratorNames != null && e.NarratorNames.Any())
                        .SelectMany(e => e.NarratorNames)
                        .Distinct()));
            }

            return editionsToImport;
        }

        private EditionRetentionSelection BuildRetentionSelection(Book local, Book remote, List<Edition> remoteEditions)
        {
            if (local == null || remoteEditions == null || remoteEditions.Count == 0)
            {
                return new EditionRetentionSelection(new List<Edition>(), new List<string>());
            }

            var profile = ResolveMetadataProfile(local);
            var protectedForeignEditionIds = GetProtectedForeignEditionIds(local);
            var filteredRemoteEditions = _editionMetadataProfileFilter?.Apply(remoteEditions, profile, protectedForeignEditionIds) ?? remoteEditions;

            var selection = _editionSelector.SelectRetainedEditions(
                local.MediaType,
                filteredRemoteEditions.ToList());

            if (protectedForeignEditionIds.Count > 0)
            {
                var retained = selection.RetainedEditions?.ToList() ?? new List<Edition>();
                var retainedKeys = new HashSet<string>(
                    retained.Select(EditionSelector.GetRetentionDedupeKey).Where(key => key.IsNotNullOrWhiteSpace()),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var protectedEdition in filteredRemoteEditions.Where(e => e != null && protectedForeignEditionIds.Contains(e.ForeignEditionId ?? string.Empty)))
                {
                    var key = EditionSelector.GetRetentionDedupeKey(protectedEdition);
                    if (key.IsNullOrWhiteSpace() || retainedKeys.Add(key))
                    {
                        retained.Add(protectedEdition);
                    }
                }

                selection = new EditionRetentionSelection(retained, selection.Warnings ?? new List<string>());
            }

            if (selection.Warnings != null && selection.Warnings.Count > 0)
            {
                _logger.Debug("[RETENTION] Book '{0}' warnings: {1}", local.Title, string.Join(", ", selection.Warnings));
            }

            return selection;
        }

        private MetadataProfile ResolveMetadataProfile(Book local)
        {
            if (local == null)
            {
                return null;
            }

            var authorId = local.AuthorId > 0 ? local.AuthorId : (local.Author?.Id ?? 0);
            if (authorId <= 0)
            {
                return null;
            }

            var author = local.Author;
            MetadataProfile profile = local.MediaType == BookMediaType.Ebook
                ? author?.EbookMetadataProfile?.Value
                : author?.AudiobookMetadataProfile?.Value;

            if (profile == null)
            {
                try
                {
                    author = _authorService.GetAuthor(authorId);
                }
                catch
                {
                    author = null;
                }

                profile = local.MediaType == BookMediaType.Ebook
                    ? author?.EbookMetadataProfile?.Value
                    : author?.AudiobookMetadataProfile?.Value;
            }

            return profile;
        }

        private IReadOnlySet<string> GetProtectedForeignEditionIds(Book local)
        {
            var protectedEditionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (local == null || local.Id <= 0)
            {
                return protectedEditionIds;
            }

            try
            {
                var localEditions = local.Editions ?? _editionService.GetEditionsForRefresh(local.Id) ?? new List<Edition>();
                foreach (var edition in localEditions.Where(e => e != null && e.ManualAdd && e.ForeignEditionId.IsNotNullOrWhiteSpace()))
                {
                    protectedEditionIds.Add(edition.ForeignEditionId);
                }

                var localFiles = local.BookFiles ?? _mediaFileService.GetFilesByBook(local.Id) ?? new List<BookFile>();
                var localEditionById = localEditions
                    .Where(e => e != null && e.Id > 0 && e.ForeignEditionId.IsNotNullOrWhiteSpace())
                    .GroupBy(e => e.Id)
                    .ToDictionary(g => g.Key, g => g.First(), EqualityComparer<int>.Default);

                foreach (var file in localFiles)
                {
                    var foreignEditionId = file?.Edition?.ForeignEditionId;
                    if (foreignEditionId.IsNotNullOrWhiteSpace())
                    {
                        protectedEditionIds.Add(foreignEditionId);
                        continue;
                    }

                    if (file?.EditionId > 0 &&
                        localEditionById.TryGetValue(file.EditionId, out var edition) &&
                        edition?.ForeignEditionId.IsNotNullOrWhiteSpace() == true)
                    {
                        protectedEditionIds.Add(edition.ForeignEditionId);
                    }
                }
            }
            catch
            {
                // Best-effort only: if protection lookup fails, still continue with unprotected retention selection.
            }

            return protectedEditionIds;
        }

        protected override List<Edition> GetLocalChildren(Book entity, List<Edition> remoteChildren)
        {
            if (entity?.Editions != null)
            {
                foreach (var edition in entity.Editions.Where(edition => edition != null))
                {
                    edition.Book = entity;
                }

                return entity.Editions;
            }

            return _editionService.GetEditionsForRefresh(entity.Id);
        }

        private EditionRefreshMatchingIndex GetEditionRefreshMatchingIndex(List<Edition> existingChildren)
        {
            if (_editionRefreshMatchingIndex?.Source == existingChildren)
            {
                return _editionRefreshMatchingIndex;
            }

            _editionRefreshMatchingIndex = EditionRefreshMatchingIndex.Build(existingChildren, _logger);
            return _editionRefreshMatchingIndex;
        }

        private sealed class EditionRefreshMatchingIndex
        {
            private readonly Dictionary<string, List<Edition>> _editionsByForeignId;
            private readonly Dictionary<int, int> _sourceOrderByEditionId;
            private readonly HashSet<int> _activeEditionIds;

            private EditionRefreshMatchingIndex(
                List<Edition> source,
                Dictionary<string, List<Edition>> editionsByForeignId,
                Dictionary<int, int> sourceOrderByEditionId,
                HashSet<int> activeEditionIds)
            {
                Source = source;
                _editionsByForeignId = editionsByForeignId;
                _sourceOrderByEditionId = sourceOrderByEditionId;
                _activeEditionIds = activeEditionIds;
            }

            public List<Edition> Source { get; }

            public static EditionRefreshMatchingIndex Build(List<Edition> existingChildren, Logger logger)
            {
                var source = existingChildren ?? new List<Edition>();
                var editionsByForeignId = new Dictionary<string, List<Edition>>(StringComparer.Ordinal);
                var sourceOrderByEditionId = new Dictionary<int, int>();
                var activeEditionIds = new HashSet<int>();

                for (var i = 0; i < source.Count; i++)
                {
                    var edition = source[i];
                    if (edition == null || edition.Id <= 0)
                    {
                        continue;
                    }

                    activeEditionIds.Add(edition.Id);
                    sourceOrderByEditionId[edition.Id] = i;

                    if (string.IsNullOrEmpty(edition.ForeignEditionId))
                    {
                        continue;
                    }

                    if (!editionsByForeignId.TryGetValue(edition.ForeignEditionId, out var editions))
                    {
                        editions = new List<Edition>();
                        editionsByForeignId[edition.ForeignEditionId] = editions;
                    }

                    editions.Add(edition);
                }

                logger?.Debug("[AUTHOR-REFRESH-TIMING] Built edition lookup for {0} local editions with {1} foreign edition ids",
                    source.Count,
                    editionsByForeignId.Count);

                return new EditionRefreshMatchingIndex(source, editionsByForeignId, sourceOrderByEditionId, activeEditionIds);
            }

            public List<Edition> GetCandidates(Edition remote)
            {
                if (string.IsNullOrEmpty(remote?.ForeignEditionId) ||
                    !_editionsByForeignId.TryGetValue(remote.ForeignEditionId, out var editions))
                {
                    return null;
                }

                return editions
                    .Where(edition => edition != null && edition.Id > 0 && _activeEditionIds.Contains(edition.Id))
                    .OrderBy(edition => _sourceOrderByEditionId.TryGetValue(edition.Id, out var order) ? order : int.MaxValue)
                    .ToList();
            }

            public void Consume(Edition existingChild, IEnumerable<Edition> mergedChildren)
            {
                if (existingChild?.Id > 0)
                {
                    _activeEditionIds.Remove(existingChild.Id);
                }

                foreach (var child in mergedChildren ?? Enumerable.Empty<Edition>())
                {
                    if (child?.Id > 0)
                    {
                        _activeEditionIds.Remove(child.Id);
                    }
                }
            }
        }

        protected override Tuple<Edition, List<Edition>> GetMatchingExistingChildren(List<Edition> existingChildren, Edition remote)
        {
            // ForeignEditionId is expected to be unique, but older DBs (or prior bugs) can contain duplicates.
            // Do not let a duplicate crash an author refresh; pick the best candidate and merge the others into it.
            var matchingIndex = GetEditionRefreshMatchingIndex(existingChildren);
            var matches = matchingIndex.GetCandidates(remote) ??
                          existingChildren
                              .Where(x => x.ForeignEditionId == remote.ForeignEditionId)
                              .ToList();

            if (matches.Count == 0)
            {
                return Tuple.Create<Edition, List<Edition>>(null, new List<Edition>());
            }

            if (matches.Count == 1)
            {
                var singleMatchMerge = new List<Edition>();
                matchingIndex.Consume(matches[0], singleMatchMerge);
                return Tuple.Create(matches[0], singleMatchMerge);
            }

            var bestMatch = matches
                // Preserve user intent first: if any duplicate is pinned, keep that row as canonical.
                .OrderByDescending(e => e.ManualAdd ? 1 : 0)
                // Next prefer the row that is already file-attached to minimize churn.
                .ThenByDescending(GetKnownFileCount)
                .ThenByDescending(e => e.Monitored ? 1 : 0)
                .ThenBy(e => e.Id)
                .First();

            _logger.Warn("Multiple local editions share ForeignEditionId '{0}' (matches: {1}) for book {2} (ID: {3}). Using edition ID {4}.",
                remote.ForeignEditionId, matches.Count, bestMatch.Book?.Title ?? "Unknown", bestMatch.BookId, bestMatch.Id);

            // Merge duplicates into the canonical row so the DB converges (files/history/etc are re-parented).
            var merge = matches
                .Where(e => e != null && e.Id > 0 && e.Id != bestMatch.Id)
                .ToList();

            matchingIndex.Consume(bestMatch, merge);
            return Tuple.Create(bestMatch, merge);
        }

        protected override void PrepareNewChild(Edition child, Book entity)
        {
            child.BookId = entity.Id;
            child.Book = entity;
        }

        protected override void PrepareExistingChild(Edition local, Edition remote, Book entity)
        {
            local.BookId = entity.Id;
            local.Book = entity;
        }

        protected override bool AreChildrenUpToDate(Edition local, Edition remote)
        {
            var remoteForCompare = RefreshEntityCopy.CloneEdition(remote);
            remoteForCompare.UseDbFieldsFrom(local);
            return local.Equals(remoteForCompare);
        }

        protected override Edition CreateChildForAdd(Edition remoteChild, Book entity)
        {
            return RefreshEntityCopy.CloneEdition(remoteChild);
        }

        protected override void AddChildren(List<Edition> children)
        {
            // hack - add the chilren in refresh children so we can control monitored status
        }

        private void MonitorSingleEdition(SortedChildren children)
        {
            if (children?.All == null || !children.All.Any())
            {
                return;
            }

            // Persist monitored-flag changes for editions that are currently UpToDate (refresh would otherwise skip them).
            var beforeMonitoredById = children.UpToDate?
                .Where(e => e != null && e.Id > 0)
                .GroupBy(e => e.Id)
                .ToDictionary(g => g.Key, g => g.First().Monitored)
                ?? new Dictionary<int, bool>();

            var all = children.All.Where(e => e != null).ToList();

            // Centralized selection + invariant repair: exactly one monitored edition per book.
            // Build a stable file-count map once (avoids selection drift and reduces repeated queries).
            var fileCountsByEditionId = all
                .Where(e => e.Id > 0)
                .Select(e => e.Id)
                .Distinct()
                .ToDictionary(id => id, id => GetKnownFileCount(all.First(e => e.Id == id)));

            var mediaType = all
                .Select(e => e.Book?.MediaType)
                .FirstOrDefault(mt => mt.HasValue);

            _editionSelector.EnsureSingleMonitoredEdition(all, fileCountsByEditionId, mediaType);

            PersistMonitoredChanges(children, beforeMonitoredById);
            Debug.Assert(all.Count(e => e.Monitored) == 1, "exactly one edition monitored");
        }

        private static void PersistMonitoredChanges(SortedChildren children, Dictionary<int, bool> beforeMonitoredById)
        {
            if (children?.UpToDate == null || !children.UpToDate.Any() || beforeMonitoredById == null || beforeMonitoredById.Count == 0)
            {
                return;
            }

            var toUpdate = children.UpToDate
                .Where(e => e != null && e.Id > 0 && beforeMonitoredById.TryGetValue(e.Id, out var before) && e.Monitored != before)
                .ToList();

            if (!toUpdate.Any())
            {
                return;
            }

            children.UpToDate = children.UpToDate.Except(toUpdate).ToList();
            children.Updated.AddRange(toUpdate);
        }

        protected override bool RefreshChildren(SortedChildren localChildren, List<Edition> remoteChildren, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
        {
            // SAFETY CHECK: Before deleting editions, check if they have files
            if (localChildren.Deleted.Any())
            {
                foreach (var edition in localChildren.Deleted.ToList())
                {
                    var hasFiles = GetKnownFileCount(edition) > 0;
                    var isPinned = edition.ManualAdd;
                    if (hasFiles || isPinned)
                    {
                        _logger.Warn("Edition {0} (ID: {1}) is protected ({2}) - preserving instead of deleting",
                            edition.Title,
                            edition.Id,
                            hasFiles ? "has files" : "ManualAdd");
                        localChildren.Deleted.Remove(edition);
                        // Preserved editions are local-only (or pinned) and should not be churned on every refresh.
                        // If monitoring selection changes, MonitorSingleEdition will move them to Updated.
                        localChildren.UpToDate.Add(edition);
                    }
                }
            }

            // make sure only one of the releases ends up monitored (after preserving any file-attached editions)
            MonitorSingleEdition(localChildren);

            localChildren.All.ForEach(x => _logger.Trace($"release: {x} monitored: {x.Monitored}"));

            if (localChildren.Added.Any())
            {
                var stopwatch = Stopwatch.StartNew();
                _editionService.InsertMany(localChildren.Added);
                stopwatch.Stop();
                
                _logger.Debug("[DB-TIMING] Inserted {0} editions in {1}ms (avg {2}ms/edition)",
                    localChildren.Added.Count, stopwatch.ElapsedMilliseconds,
                    stopwatch.ElapsedMilliseconds / localChildren.Added.Count);
            }

            return _refreshEditionService.RefreshEditionInfo(localChildren.Added, localChildren.Updated, localChildren.Merged, localChildren.Deleted, localChildren.UpToDate, remoteChildren, forceUpdateFileTags);
        }

        protected override void PublishEntityUpdatedEvent(Book entity)
        {
            // Fetch fresh from DB so all lazy loads are available
            _eventAggregator.PublishEvent(new BookUpdatedEvent(_bookService.GetBook(entity.Id)));
        }

        public bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
        {
            var updated = false;
            _bookMetadataCache.Clear();

            // Defensive: the caller can accidentally include the same DB row multiple times (e.g. duplicate
            // matching during author refresh). De-dupe by database ID to avoid double-processing / double-deletes.
            if (books != null && books.Count > 1)
            {
                var seen = new HashSet<int>();
                var deduped = new List<Book>(books.Count);
                foreach (var book in books)
                {
                    if (book == null)
                    {
                        continue;
                    }

                    if (book.Id <= 0)
                    {
                        // Unsaved/temporary entries should not be collapsed.
                        deduped.Add(book);
                        continue;
                    }

                    if (seen.Add(book.Id))
                    {
                        deduped.Add(book);
                    }
                }

                books = deduped;
            }

            // Group books by provider ID to ensure all books with same provider ID update together
            var groups = books
                .GroupBy(b => GetPrimaryProviderKey(b) ?? $"local:{b.Id}")
                .ToList();

            _logger.Debug("Refreshing {0} books in {1} provider groups", books.Count, groups.Count);

            foreach (var group in groups)
            {
                var groupKey = group.Key;
                _logger.Debug("Processing provider group {0} with {1} books", groupKey, group.Count());

                // Skip entire group if none of the books need refresh and force is off
                var anyNeedsRefresh = forceBookRefresh || group.Any(b => _checkIfBookShouldBeRefreshed.ShouldRefresh(b));
                if (!anyNeedsRefresh)
                {
                    foreach (var b in group)
                    {
                        _logger.Debug("Skipping refresh of book: {0}", b.Title);
                    }
                    continue;
                }

                // Use the first book in the group as representative for fetching remote data
                var representative = group.First();
                
                // Refresh every book in the group using the same remote data
                // This ensures audiobook and ebook with same provider ID update together
                foreach (var book in group)
                {
                    _logger.Debug("Refreshing book {0} (ID: {1}) in group {2}", book.Title, book.Id, groupKey);
                    updated |= RefreshEntityInfo(book, remoteBooks, remoteData, true, forceUpdateFileTags, lastUpdate);
                }
            }

            return updated;
        }

        public bool RefreshBookInfo(Book book, List<Book> remoteBooks, Author remoteData, bool forceUpdateFileTags)
        {
            return RefreshEntityInfo(book, remoteBooks, remoteData, true, forceUpdateFileTags, null);
        }

        public bool RefreshBookInfo(Book book)
        {
            var data = GetSkyhookData(book);

            if (data == null)
            {
                _logger.Warn("Failed to get metadata for book {0} (Id: {1}), skipping refresh", book.Title, book.Id);
                return false;
            }

            // CRITICAL FIX: When refreshing a single book, GetSkyhookData returns an Author object
            // with a single book that has editions populated. We need to pass THIS book
            // (which has editions) as the remote book, not the entire author book list.
            var remoteBook = BookIdentity.FindWorkFirstMatches(data.Books?.Where(b => b.MediaType == book.MediaType), book).FirstOrDefault();

            if (remoteBook != null)
            {
                return RefreshBookInfo(book, new List<Book> { remoteBook }, data, false);
            }
            else
            {
                _logger.Warn("Remote book not found in author data for Id: {0}", book.Id);
                return RefreshBookInfo(book, data.Books, data, false);
            }
        }

        public void Execute(BulkRefreshBookCommand message)
        {
            var books = _bookService.GetBooks(message.BookIds);

            foreach (var book in books)
            {
                RefreshBookInfo(book);
            }
        }

        public void Execute(RefreshBookCommand message)
        {
            if (message.BookId.HasValue)
            {
                var book = _bookService.GetBook(message.BookId.Value);

                RefreshBookInfo(book);
            }
        }
    }
}
