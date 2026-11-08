using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books
{
    public interface IAddBookService
    {
        Task<Book> AddBook(Book book, bool doRefresh = true);
        Task<List<Book>> AddBooks(List<Book> books, bool doRefresh = true);
    }

    public class AddBookService : IAddBookService
    {
        private readonly IAuthorService _authorService;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IBookService _bookService;
        private readonly IBookMonitoredService _bookMonitoredService;
        private readonly IProvideBookInfo _bookInfo;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly ISeriesService _seriesService;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IBuildFileNames _filenameBuilder;
        private readonly IMonitoringService _monitoringService;
        private readonly Logger _logger;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly IEditionService _editionService;
        private readonly IEditionSelector _editionSelector;
        private readonly IEditionMetadataProfileFilter _editionMetadataProfileFilter;
        private readonly IManageCommandQueue _commandQueueManager;

        public AddBookService(IAuthorService authorService,
                               IAuthorLibraryService authorLibraryService,
                               IBookService bookService,
                               IBookMonitoredService bookMonitoredService,
                               IProvideBookInfo bookInfo,
                               IImportListExclusionService importListExclusionService,
                               ISeriesBookLinkService seriesBookLinkService,
                               ISeriesService seriesService,
                               IProvideAuthorInfo authorInfo,
                               IBuildFileNames filenameBuilder,
                               IMonitoringService monitoringService,
                               IEditionService editionService,
                               IEditionSelector editionSelector,
                               IEditionMetadataProfileFilter editionMetadataProfileFilter,
                               IMetadataProfileService metadataProfileService,
                               Logger logger,
                               IManageCommandQueue commandQueueManager)
        {
            _authorService = authorService;
            _authorLibraryService = authorLibraryService;
            _bookService = bookService;
            _bookMonitoredService = bookMonitoredService;
            _bookInfo = bookInfo;
            _importListExclusionService = importListExclusionService;
            _seriesBookLinkService = seriesBookLinkService;
            _seriesService = seriesService;
            _authorInfo = authorInfo;
            _filenameBuilder = filenameBuilder;
            _monitoringService = monitoringService;
            _editionService = editionService;
            _editionSelector = editionSelector;
            _editionMetadataProfileFilter = editionMetadataProfileFilter;
            _commandQueueManager = commandQueueManager;
            _metadataProfileService = metadataProfileService;
            _logger = logger;
        }

        public async Task<Book> AddBook(Book book, bool doRefresh = true)
        {
            _logger.Debug("Adding book {0} with provider IDs - HC: {1}, GR-work: {2}, edition: {3}",
                book.Title,
                book.HardcoverBookId,
                book.GoodreadsWorkId,
                BookEditionIdentity.GetGoodreadsEditionProviderId(book, _logger, "AddBookService.AddBook"));
            
            // DEBUG: Log monitoring mode
                _logger.Debug("[SPECIFIC-BOOK-DEBUG] AddBook called for '{0}' with Monitor mode: {1}",
                    book.Title, book.Author?.AddOptions?.Monitor);
                _logger.Debug("[SPECIFIC-BOOK-DEBUG] MediaType: {0}, Author: {1}",
                    book.MediaType, book.Author?.Name ?? "NULL");

            // we allow adding extra editions, so check if the book already exists
            Book dbBook = null;
            var requestedEditionProviderIdsForAdd = GetRequestedEditionProviderIdsFromPayload(book?.Editions);

            // Canonicalize inbound provider IDs so legacy search/add payloads cannot recreate malformed rows.
                if (!string.IsNullOrEmpty(book.HardcoverBookId))
                {
                    dbBook = _bookService.FindByProviderId("hc", ProviderIdHelper.Canonicalize(book.HardcoverBookId, "hc"), book.MediaType);
                }

                if (dbBook == null && !string.IsNullOrEmpty(book.GoodreadsWorkId))
                {
                    dbBook = _bookService.FindByProviderId("gr", ProviderIdHelper.Canonicalize(book.GoodreadsWorkId, "gr"), book.MediaType);
                }

                if (dbBook == null && BookEditionIdentity.GetGoodreadsEditionProviderId(book, _logger, "AddBookService.AddBook.Lookup") is string goodreadsEditionId &&
                    !string.IsNullOrEmpty(goodreadsEditionId))
                {
                    dbBook = _bookService.FindByProviderId("gr", goodreadsEditionId, book.MediaType);
                }

                if (dbBook == null && !string.IsNullOrEmpty(book.OpenLibraryWorkId))
                {
                    dbBook = _bookService.FindByProviderId("ol", ProviderIdHelper.Canonicalize(book.OpenLibraryWorkId, "ol"), book.MediaType);
                }

            if (dbBook == null && BookEditionIdentity.GetGoogleBooksEditionId(book, _logger, "AddBookService.AddBook.Lookup") is string googleBooksEditionId &&
                !string.IsNullOrEmpty(googleBooksEditionId))
            {
                dbBook = _bookService.FindByProviderId("gb", googleBooksEditionId, book.MediaType);
            }

            if (dbBook == null)
            {
                return await RequestBookThroughAuthorCatalog(book);
            }

            if (GetRequestedNarration(book) != null)
            {
                return CompleteRequestedNarration(book, dbBook) ?? await RequestBookThroughAuthorCatalog(book);
            }

            if (GetInitialBookPolicy(book).HasValue || book.Author?.AddOptions?.Monitor == MonitorTypes.None || book.AddOptions?.MonitorBook == false)
            {
                var existing = FindExistingAuthorForAdd(book);
                var configured = existing?.HasConfiguredMediaSettings(book.MediaType) == true;
                if (!configured)
                {
                    return await RequestBookThroughAuthorCatalog(book);
                }

                return CompleteScopedBookRequest(dbBook, book.AddOptions?.SearchForNewBook == true,
                    book.AddOptions?.MonitorBook ?? true);
            }

            var monitorRequested = book.AddOptions?.MonitorBook ?? book.IsMonitored();
            var searchRequested = book.AddOptions?.SearchForNewBook == true;
            book.UseDbFieldsFrom(dbBook);
            var storedEditions = dbBook.Editions ?? _editionService.GetEditionsByBook(dbBook.Id);
            if (!EditionPinPolicy.CanAutomationSelectEdition(dbBook, storedEditions))
            {
                book.Editions = storedEditions;
            }

            book.AddOptions ??= new AddBookOptions();
            if (searchRequested) book.AddOptions.SearchForNewBook = false;

            // Some clients post IDs-only payloads even when the book already exists locally.
            // Ensure we never Upsert NULL Title/Edition titles.
            book.Title ??= dbBook.Title;

            if (HasMissingOrEmptyEditionMetadata(book.Editions))
            {
                book.Editions = _editionService.GetEditionsByBook(dbBook.Id) ?? new List<Edition>();

                if (book.Editions.Any())
                {
                    var selected = SelectPreferredEditionForAdd(book, book.Editions, requestedEditionProviderIdsForAdd);
                    var selectedIndex = GetSelectedRetainedEditionIndex(book.Editions, selected);
                    for (var i = 0; i < book.Editions.Count; i++)
                    {
                        book.Editions[i].Monitored = i == selectedIndex;
                    }
                }
            }

            // Note it's a manual addition so it's not deleted on next refresh
            book.AddOptions.AddType = BookAddType.Manual;
            if (book.Added == default(DateTime) || book.Added < new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            {
                book.Added = DateTime.UtcNow;
            }

            // ManualAdd on editions is respected as-is from the caller.
            // API clients (Seer) send manualAdd: false for book-level adds.
            // The Chaptarr UI sends manualAdd: true only when the user pins a specific edition/narrator.

            // Try to find author by provider IDs
            Author dbAuthor = FindExistingAuthorForAdd(book);

            if (dbAuthor == null)
            {
                return await RequestBookThroughAuthorCatalog(book);
            }

                // CRITICAL: Capture monitoring intent BEFORE replacing Author object
                var monitorMode = book.Author?.AddOptions?.Monitor;
                var bookMediaType = book.MediaType;
                var isSpecificBookIntentForExistingAuthor =
                    monitorMode == MonitorTypes.SpecificBook;
                var isAllBooksIntentForExistingAuthor =
                    monitorMode == MonitorTypes.All;
                
                _logger.Debug("[SPECIFIC-BOOK-FIX] Captured monitoring intent BEFORE replacing Author: Monitor={0}, MediaType={1}",
                    monitorMode, bookMediaType);

                // If the author already exists, progressively fill missing per-media-type settings (without overwriting).
                // This is important for Seerr/Readarr clients that send a single profile/rootFolderPath.
                var requestedAuthorSettings = book.Author;
                if (requestedAuthorSettings != null && dbAuthor != null)
                {
                    // Fill audiobook settings (including its root folder path) if missing.
                    if (requestedAuthorSettings.AudiobookQualityProfileId.HasValue &&
                        !string.IsNullOrWhiteSpace(requestedAuthorSettings.AudiobookRootFolderPath))
                    {
                        dbAuthor = _authorService.UpdateAuthorProgressiveSettings(
                            dbAuthor,
                            requestedAuthorSettings.AudiobookQualityProfileId,
                            requestedAuthorSettings.AudiobookMetadataProfileId,
                            requestedAuthorSettings.AudiobookMonitored,
                            requestedAuthorSettings.AudiobookMonitorNewItems,
                            null,
                            null,
                            null,
                            null,
                            requestedAuthorSettings.AudiobookRootFolderPath);
                    }

                    // Fill ebook settings (including its root folder path) if missing.
                    if (requestedAuthorSettings.EbookQualityProfileId.HasValue &&
                        !string.IsNullOrWhiteSpace(requestedAuthorSettings.EbookRootFolderPath))
                    {
                        dbAuthor = _authorService.UpdateAuthorProgressiveSettings(
                            dbAuthor,
                            null,
                            null,
                            null,
                            null,
                            requestedAuthorSettings.EbookQualityProfileId,
                            requestedAuthorSettings.EbookMetadataProfileId,
                            requestedAuthorSettings.EbookMonitored,
                            requestedAuthorSettings.EbookMonitorNewItems,
                            requestedAuthorSettings.EbookRootFolderPath);
                    }
                }

                book.Author = dbAuthor;
                book.AuthorId = dbAuthor.Id;

                // Per requirements: a missing per-media-type metadata profile means that media type is disabled.
                // Block manual book adds for disabled media types (UI/add/import list parity).
                if (bookMediaType == BookMediaType.Audiobook)
                {
                    var profileId = dbAuthor?.AudiobookMetadataProfileId;
                    if (!profileId.HasValue || profileId.Value <= 0 || !_metadataProfileService.Exists(profileId.Value))
                    {
                        throw new ValidationException(new[]
                        {
                            new ValidationFailure("AudiobookMetadataProfileId",
                                "Audiobook metadata profile is not set for this author (audiobooks are disabled). Set an audiobook metadata profile to add audiobook books.")
                        });
                    }
                }
                else if (bookMediaType == BookMediaType.Ebook)
                {
                    var profileId = dbAuthor?.EbookMetadataProfileId;
                    if (!profileId.HasValue || profileId.Value <= 0 || !_metadataProfileService.Exists(profileId.Value))
                    {
                        throw new ValidationException(new[]
                        {
                            new ValidationFailure("EbookMetadataProfileId",
                                "Ebook metadata profile is not set for this author (ebooks are disabled). Set an ebook metadata profile to add ebook books.")
                        });
                    }
                }
                
                if (monitorRequested || searchRequested ||
                    isSpecificBookIntentForExistingAuthor ||
                    isAllBooksIntentForExistingAuthor)
                {
                    book.Author = dbAuthor;
                    book.AuthorId = dbAuthor.Id;
                    book.SetMonitored(true);
                    book.Monitored = true;
                }

                // Handle "None, except this one book" when author already exists
                // Using CAPTURED monitorMode instead of book.Author.AddOptions which is now NULL
                    if (isSpecificBookIntentForExistingAuthor)
                    {
                    _logger.Debug("[SPECIFIC-BOOK-FIX] Setting monitoring for specific book '{0}' of media type {1} (existing author)",
                        book.Title, bookMediaType);
                
                if (bookMediaType == BookMediaType.Audiobook)
                {
                    book.AudiobookMonitored = true;
                    book.EbookMonitored = false;
                }
                else if (bookMediaType == BookMediaType.Ebook)
                {
                    book.EbookMonitored = true;
                    book.AudiobookMonitored = false;
                }
            }
            else
            {
                _logger.Debug("[SPECIFIC-BOOK-FIX] Not SpecificBook mode ({0}), using default monitoring", monitorMode);
                    // Use default monitoring from the existing author
                    // Books inherit monitoring from author settings
                }
                
                // Keep legacy Monitored field consistent
                book.Monitored = book.AudiobookMonitored || book.EbookMonitored;
            
            ApplyEditionRetentionForAdd(book, requestedEditionProviderIdsForAdd);

            // Persist the book to the database (editions will be available in DB now)
            _bookService.AddBook(book, doRefresh);
            RemoveMatchingImportListExclusions(book);

            if (isAllBooksIntentForExistingAuthor)
            {
                _bookMonitoredService.SetBookMonitoredStatus(dbAuthor, new MonitoringOptions
                {
                    Monitor = MonitorTypes.All,
                    MediaType = bookMediaType
                });
            }

            // Fallback: if this book was not imported with the author, ensure a best edition is monitored
            try
            {
                EnsureAutoSelectedEdition(book);
            }
            catch (ValidationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[EDITION-SELECT] Failed to auto-select monitored edition for '{0}'", book.Title);
            }

            // Handle series links if they exist
            if (book.SeriesLinks != null && book.SeriesLinks.Any())
            {
                _logger.Trace("[SERIES-DEBUG] Processing {0} series links for book '{1}'", book.SeriesLinks.Count, book.Title);

                // Debug: Log what's in each SeriesLink
                for (int i = 0; i < book.SeriesLinks.Count; i++)
                {
                    var link = book.SeriesLinks[i];
                    if (link.Series?.Value != null)
                    {
                        var series = link.Series.Value;
                        _logger.Trace("[SERIES-DEBUG] SeriesLink[{0}]: Title='{1}', DatabaseId={2}, HardcoverId='{3}', GoodreadsId='{4}', Position={5}",
                            i, series.Title, series.Id, series.HardcoverSeriesId, series.GoodreadsSeriesId, link.Position);
                    }
                    else
                    {
                        _logger.Trace("[SERIES-DEBUG] SeriesLink[{0}]: Series.Value is NULL", i);
                    }
                }

                var linksToInsert = new List<SeriesBookLink>();

                foreach (var link in book.SeriesLinks)
                {
                    if (link.Series?.Value != null)
                    {
                        var series = link.Series.Value;

                        // Use database ID directly - no need for complex provider ID lookups
                        if (series.Id > 0)
                        {
                            _logger.Trace("[SERIES-DEBUG] Using database ID {0} for series '{1}'", series.Id, series.Title);

                            link.BookId = book.Id;
                            link.SeriesId = series.Id;
                            linksToInsert.Add(link);
                        }
                        else
                        {
                            _logger.Trace("[SERIES-DEBUG] Series '{0}' has invalid database ID: {1}, skipping link", series.Title, series.Id);
                        }
                    }
                    else
                    {
                        _logger.Trace("[SERIES-DEBUG] SeriesLink has null Series.Value for book '{0}', skipping", book.Title);
                    }
                }

                if (linksToInsert.Any())
                {
                    _logger.Trace("[SERIES-DEBUG] Inserting {0} series-book links for '{1}'", linksToInsert.Count, book.Title);

                    // Debug: Log each link being inserted
                    foreach (var linkToInsert in linksToInsert)
                    {
                        _logger.Trace("[SERIES-DEBUG] Creating SeriesBookLink: BookId={0} -> SeriesId={1}, Position={2}",
                            linkToInsert.BookId, linkToInsert.SeriesId, linkToInsert.Position);
                    }

                    _seriesBookLinkService.InsertMany(linksToInsert);
                    _logger.Trace("[SERIES-DEBUG] Successfully inserted {0} SeriesBookLink records", linksToInsert.Count);
                }
                else
                {
                    _logger.Trace("[SERIES-DEBUG] NO SeriesBookLinks to insert for book '{0}' - linksToInsert was empty", book.Title);
                }
            }

            SearchRequestedBook(book, searchRequested);
            return book;
        }

        private async Task<Book> RequestBookThroughAuthorCatalog(Book requestedBook)
        {
            var workProviderId = ResolveRequestedWorkProviderId(requestedBook, out var metadata);
            if (string.IsNullOrWhiteSpace(workProviderId))
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("ForeignBookId", "Cannot add book: the selected result does not contain a stable work-level provider ID.")
                });
            }

            var authorProviderId = GetAuthorProviderId(requestedBook.Author);
            if (metadata == null)
            {
                try
                {
                    metadata = _bookInfo.GetWorkInfo(workProviderId, requestedBook.MediaType,
                        AuthorIdentity.NormalizeWorkLookupAuthorHint(workProviderId, authorProviderId));
                }
                catch (BookNotFoundException)
                {
                    // Not-ready metadata retains the existing durable author-catalog request.
                }
                catch (BookInfoException ex)
                {
                    _logger.Warn(ex, "Primary metadata for requested work {0} is unavailable; retaining the author-catalog request", workProviderId);
                }
            }

            var primary = metadata == null ? null : BookIdentity.GetRequestedWorkPrimaryAuthor(metadata, workProviderId, requestedBook.MediaType);
            if (metadata != null && primary == null)
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("ForeignBookId", "Cannot add book: metadata does not establish the requested work and media type's primary author.")
                });
            }

            authorProviderId = primary == null ? authorProviderId : AuthorIdentity.GetPreferredProviderId(primary);
            if (string.IsNullOrWhiteSpace(authorProviderId))
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("Author", "Cannot add book: the selected result does not contain an author provider ID.")
                });
            }

            var mediaType = requestedBook.MediaType;
            var searchRequested = requestedBook.AddOptions?.SearchForNewBook == true;
            var requestedIds = new List<string> { workProviderId };

            // An explicit add reverses a matching import-list exclusion before the
            // authoritative author blob is normalized. Waiting until after import
            // would let the exclusion remove the requested work and strand the
            // pending request forever.
            RemoveMatchingImportListExclusions(requestedBook, primary);

            var config = BuildRequestedBookMonitoringConfig(requestedBook, requestedIds, searchRequested);
            if (primary != null && (!string.IsNullOrWhiteSpace(primary.Name) ||
                !AuthorIdentity.MatchesByProviderIdIntersection(requestedBook.Author, primary)))
                config.AuthorName = primary.Name;
            var author = await _authorLibraryService.AddAuthorAsync(authorProviderId, config);
            if (author?.Id < 0)
            {
                throw new PendingBookRequestException(-author.Id);
            }

            if (author == null || author.Id <= 0)
            {
                throw new InvalidOperationException($"Unable to import author {authorProviderId} for requested work {workProviderId}.");
            }

            var localBook = ResolveLocalRequestedBook(author.Id, workProviderId, mediaType);
            if (localBook == null)
            {
                // AddAuthorAsync queues this shape when the authoritative author blob does not yet
                // contain the requested work. Reaching here means that contract was broken.
                throw new InvalidOperationException($"Requested work {workProviderId} was not found after importing author {authorProviderId}.");
            }

            if (GetRequestedNarration(requestedBook) != null)
            {
                return CompleteRequestedNarration(requestedBook, localBook)
                    ?? throw new ValidationException(new[] { new ValidationFailure("Edition", "The requested narration is missing from the authoritative audiobook catalog.") });
            }

            if (GetInitialBookPolicy(requestedBook).HasValue || requestedBook.Author?.AddOptions?.Monitor == MonitorTypes.None || requestedBook.AddOptions?.MonitorBook == false)
            {
                return CompleteScopedBookRequest(localBook, searchRequested,
                    requestedBook.AddOptions?.MonitorBook ?? true);
            }

            localBook.Author = author;
            localBook.SetMonitored(true);
            localBook.Monitored = true;
            _bookService.MonitorRequestedBooks(new[] { localBook.Id }, mediaType == BookMediaType.Ebook ? "ebook" : "audiobook");

            if (searchRequested)
            {
                localBook.AddOptions ??= new AddBookOptions();
                localBook.AddOptions.AddType = BookAddType.Manual;
                localBook.AddOptions.SearchForNewBook = false;
                _bookService.SetAddOptions(new[] { localBook });
                SearchRequestedBook(localBook, true);
            }

            RemoveMatchingImportListExclusions(localBook);

            return localBook;
        }

        private void RemoveMatchingImportListExclusions(Book book, Author author = null)
        {
            var lookupIds = ImportListExclusionBookMatcher.GetLookupIds(book);
            // Provider IDs can have one author exclusion per media side, so include all known aliases in the plural lookup.
            lookupIds.AddRange(AuthorIdentity.GetProviderIdentityTokenList(author ?? book.Author));

            var matchingExclusions = _importListExclusionService.FindByForeignId(
                lookupIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            var oppositeMediaType = ImportListExclusionBookMatcher.GetOppositeMediaType(book.MediaType);

            foreach (var exclusion in matchingExclusions)
            {
                if (exclusion.MediaType == book.MediaType)
                {
                    _importListExclusionService.Delete(exclusion.Id);
                    continue;
                }

                if (!exclusion.MediaType.HasValue)
                {
                    exclusion.MediaType = oppositeMediaType;
                    _importListExclusionService.Update(exclusion);
                }
            }
        }

        private MonitoringConfig BuildRequestedBookMonitoringConfig(Book book, List<string> requestedIds, bool searchRequested)
        {
            var author = book.Author ?? new Author();
            var mediaType = book.MediaType;
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            var initialPolicy = GetInitialBookPolicy(book);
            var initialMonitorMode = initialPolicy ?? author.AddOptions?.Monitor ?? MonitorTypes.SpecificBook;
            var monitorRequested = book.AddOptions?.MonitorBook ?? true;

            return new MonitoringConfig
            {
                IsManualAddition = true,
                AudiobookNarrations = GetRequestedNarration(book) is Edition narration
                    ? new List<RequestedNarration> { new() { WorkProviderId = requestedIds[0],
                        EditionProviderId = BookEditionIdentity.GetTrustedForeignEditionId(narration),
                        NarratorNames = BookEditionIdentity.GetNarratorNames(narration), RequiresEdition = true, Search = searchRequested } }
                    : null,
                AuthorName = author.Name,
                CreateAudiobook = isAudiobook,
                CreateEbook = !isAudiobook,
                AudiobookMonitored = isAudiobook ? author.AudiobookMonitored ?? true : author.AudiobookMonitored,
                AudiobookMonitorNewItems = isAudiobook ? author.AudiobookMonitorNewItems ?? NewItemMonitorTypes.None : author.AudiobookMonitorNewItems,
                AudiobookMonitorExistingMode = isAudiobook && requestedIds?.Any() == true
                    ? initialMonitorMode
                    : null,
                EbookMonitored = !isAudiobook ? author.EbookMonitored ?? true : author.EbookMonitored,
                EbookMonitorNewItems = !isAudiobook ? author.EbookMonitorNewItems ?? NewItemMonitorTypes.None : author.EbookMonitorNewItems,
                EbookMonitorExistingMode = !isAudiobook && requestedIds?.Any() == true
                    ? initialMonitorMode
                    : null,
                AudiobookQualityProfileId = author.AudiobookQualityProfileId,
                EbookQualityProfileId = author.EbookQualityProfileId,
                AudiobookMetadataProfileId = author.AudiobookMetadataProfileId,
                EbookMetadataProfileId = author.EbookMetadataProfileId,
                AudiobookRootFolderPath = author.AudiobookRootFolderPath,
                EbookRootFolderPath = author.EbookRootFolderPath,
                AudiobookTags = author.AudiobookTags,
                EbookTags = author.EbookTags,
                Tags = author.Tags,
                QueueIfUnavailable = true,
                MonitorMode = author.AddOptions?.Monitor ?? MonitorTypes.SpecificBook,
                SpecificBookProviderIds = new HashSet<string>(requestedIds, StringComparer.OrdinalIgnoreCase),
                SpecificBookMediaType = mediaType,
                AudiobookBooksToAdd = isAudiobook && !monitorRequested ? requestedIds : null,
                EbookBooksToAdd = !isAudiobook && !monitorRequested ? requestedIds : null,
                AudiobookBooksToMonitor = isAudiobook && monitorRequested ? requestedIds : null,
                EbookBooksToMonitor = !isAudiobook && monitorRequested ? requestedIds : null,
                AudiobookBooksToSearch = isAudiobook && searchRequested ? requestedIds : null,
                EbookBooksToSearch = !isAudiobook && searchRequested ? requestedIds : null
            };
        }

        private static Edition GetRequestedNarration(Book book) => book.MediaType == BookMediaType.Audiobook
            ? book.Editions?.SingleOrDefault(edition => edition.Monitored && edition.ManualAdd && BookEditionIdentity.IsNarratedAudiobookEdition(edition))
            : null;

        private Book CompleteRequestedNarration(Book request, Book source)
        {
            var requested = GetRequestedNarration(request);
            var editionProviderId = BookEditionIdentity.GetTrustedForeignEditionId(requested);
            var narratorNames = BookEditionIdentity.GetNarratorNames(requested);
            // Narration intent can be fulfilled by another catalog edition of the same work.
            // Load every compatible pocket together before the shared writer applies pin/copy policy.
            var workLookups = BookIdentity.GetStableWorkProviderIdentityTokens(source)
                .Select(id => (Scope: "work", Provider: id.Substring(0, id.IndexOf(':')), Id: id));
            var bookIds = _bookService.FindBookIdsByProviderIds(workLookups).Values
                .SelectMany(ids => ids).Append(source.Id).Distinct();
            var matches = BookEditionIdentity.OrderRequestedNarrationEditions(_bookService.GetBooksForLookup(bookIds)
                .Where(book => book.AuthorId == source.AuthorId && book.MediaType == source.MediaType &&
                    (book.Id == source.Id || BookIdentity.MatchesByStableWorkProviderId(book, source)))
                .SelectMany(book => book.Editions ?? Enumerable.Empty<Edition>())
                .Where(edition => BookEditionIdentity.IsNarratedAudiobookEdition(edition) &&
                    BookEditionIdentity.MatchesRequestedNarration(edition, editionProviderId, narratorNames))
                .DistinctBy(edition => edition.Id)).ToList();
            if (matches.Count == 0)
            {
                return null;
            }

            if (matches.Count > 1 && !matches.All(edition => BookEditionIdentity.HasSameNarration(edition, requested)))
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("Edition", "The requested narration is missing or ambiguous in the authoritative audiobook catalog.")
                });
            }

            var target = _bookService.AddWantedEditions(new List<Edition> { matches[0] })[matches[0].Id];
            return CompleteScopedBookRequest(target, request.AddOptions?.SearchForNewBook == true,
                request.AddOptions?.MonitorBook ?? true);
        }

        private static MonitorTypes? GetInitialBookPolicy(Book book)
        {
            return book.MediaType == BookMediaType.Audiobook
                ? book.Author?.AddOptions?.AudiobookMonitor
                : book.Author?.AddOptions?.EbookMonitor;
        }

        private Book CompleteScopedBookRequest(Book book, bool searchRequested, bool monitorRequested)
        {
            var mediaType = book.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
            // A request selects its matching sibling rows when format sync is enabled.
            // The independent author-side gate remains unchanged.
            if (monitorRequested)
            {
                _bookService.MonitorRequestedBooks(new[] { book.Id }, mediaType);
            }
            else
            {
                _bookService.SetMonitored(new[] { book.Id }, false);
            }

            var author = _authorService.GetAuthor(book.AuthorId);

            // Request preservation survives the initial scan; it does not pin an edition.
            book.AddOptions ??= new AddBookOptions();
            book.AddOptions.AddType = BookAddType.Manual;
            book.AddOptions.MonitorBook = monitorRequested;
            if (searchRequested) book.AddOptions.SearchForNewBook = false;
            _bookService.SetAddOptions(new[] { book });
            if (author?.AddOptions != null)
            {
                _commandQueueManager.Push(RefreshAuthorCommand.InitialScan(author));
            }

            SearchRequestedBook(book, searchRequested);

            var saved = _bookService.GetBook(book.Id);
            RemoveMatchingImportListExclusions(saved);
            return saved;
        }

        private void SearchRequestedBook(Book book, bool searchRequested)
        {
            if (searchRequested)
            {
                book.Author ??= _authorService.GetAuthor(book.AuthorId);
                _commandQueueManager.Push(new BookSearchCommand(new List<int> { book.Id }), trigger: BookSearchCommand.SearchOnAddTrigger(book.Author, book.MediaType));
            }
        }

        private Book ResolveLocalRequestedBook(int authorId, string workProviderId, BookMediaType mediaType)
        {
            var separator = workProviderId.IndexOf(':');
            if (separator <= 0)
            {
                return null;
            }

            var matches = _bookService.FindAllByWorkProviderId(
                    workProviderId.Substring(0, separator),
                    ProviderIdHelper.StripPrefix(workProviderId),
                    mediaType)
                .Where(book => book.AuthorId == authorId)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            return BookIdentity.SelectRequestedBook(matches);
        }

        private string ResolveRequestedWorkProviderId(Book book, out Tuple<string, Book, List<Author>> metadata)
        {
            metadata = null;
            foreach (var providerId in BookEditionIdentity.GetCanonicalWorkProviderIds(book)
                         .Concat(book?.RemoteProviderIds ?? Enumerable.Empty<string>()))
            {
                var normalized = TryCanonicalizeWorkId(providerId);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            var editionProviderId = BookEditionIdentity.GetCanonicalEditionProviderIds(book, _logger, "AddBookService.ResolveRequestedWork")
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(editionProviderId))
            {
                return null;
            }

            metadata = _bookInfo.GetEditionInfo(editionProviderId, book.MediaType);
            var resolvedBook = metadata?.Item2;
            if (resolvedBook?.MediaType != book.MediaType || resolvedBook.Editions?.Any(edition =>
                    BookEditionIdentity.EditionMatchesProviderId(edition, editionProviderId)) != true)
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("Edition", "Cannot add book: metadata does not establish the requested edition and media type.")
                });
            }
            return BookEditionIdentity.GetCanonicalWorkProviderIds(resolvedBook)
                .Concat(resolvedBook?.RemoteProviderIds ?? Enumerable.Empty<string>())
                .Select(providerId => TryCanonicalizeWorkId(providerId))
                .FirstOrDefault(providerId => !string.IsNullOrWhiteSpace(providerId));
        }

        private static string GetAuthorProviderId(Author author)
        {
            if (author == null)
            {
                return null;
            }

            foreach (var (providerId, prefix) in new[]
                     {
                         (author.HardcoverAuthorId, "hc"),
                         (author.GoodreadsAuthorId, "gr"),
                         (author.OpenLibraryAuthorId, "ol"),
                         (author.AudnexusAuthorId, "az")
                     })
            {
                if (string.IsNullOrWhiteSpace(providerId))
                {
                    continue;
                }

                try
                {
                    return ProviderIdHelper.Canonicalize(providerId, prefix);
                }
                catch
                {
                    // Try the next provider identity carried by the search result.
                }
            }

            return null;
        }

        private void EnsureAutoSelectedEdition(Book book)
        {
            if (book == null)
            {
                return;
            }

            var dbEditions = _editionService.GetEditionsByBook(book.Id) ?? new List<Edition>();
            var hasMonitored = dbEditions.Any(e => e.Monitored);
            var anyEditionRequested = (book.Editions == null || !book.Editions.Any() || book.Editions.All(e => string.IsNullOrWhiteSpace(e.ForeignEditionId))) || book.AnyEditionOk;

            if (hasMonitored || !anyEditionRequested)
            {
                return;
            }

            var author = book.Author ?? _authorService.GetAuthor(book.AuthorId);
            var metadataProfile = ResolveMetadataProfile(author, book.MediaType);
            var filteredEditions = _editionMetadataProfileFilter.Apply(dbEditions, metadataProfile);
            var retainedSelection = _editionSelector.SelectRetainedEditions(
                book.MediaType,
                filteredEditions);

            var candidateEditions = retainedSelection?.RetainedEditions?.ToList() ?? filteredEditions;
            var best = _editionSelector.SelectBestEdition(candidateEditions, book.MediaType);
            if (best != null)
            {
                _logger.Debug("[EDITION-SELECT] Auto-selected edition '{0}' (ID:{1}) for '{2}' (mediaType={3})", best.Title, best.Id, book.Title, book.MediaType);
                _editionService.SetMonitored(best, false);

                if (best.ForeignEditionId.IsNotNullOrWhiteSpace() &&
                    !string.Equals(book.ForeignEditionId, best.ForeignEditionId, StringComparison.OrdinalIgnoreCase))
                {
                    book.ForeignEditionId = best.ForeignEditionId;
                    _bookService.UpdateBook(book);
                }

                return;
            }

            var mediaTypeLabel = book.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
            throw new ValidationException(new List<ValidationFailure>
            {
                new ValidationFailure("Edition", $"No {mediaTypeLabel} edition available for this book")
            });
        }

        private MetadataProfile ResolveMetadataProfile(Author author, BookMediaType mediaType)
        {
            int? profileId = mediaType switch
            {
                BookMediaType.Audiobook => author?.AudiobookMetadataProfileId,
                BookMediaType.Ebook => author?.EbookMetadataProfileId,
                _ => null
            };

            if (!profileId.HasValue || !_metadataProfileService.Exists(profileId.Value))
            {
                return null;
            }

            return _metadataProfileService.Get(profileId.Value);
        }

        private Edition SelectPreferredEditionForAdd(Book book, IEnumerable<Edition> editions, IReadOnlyCollection<string> requestedEditionProviderIds)
        {
            var mediaType = book?.MediaType ?? BookMediaType.Audiobook;
            var candidateEditions = SelectRetainedEditionsForAdd(book, editions);

            if (!candidateEditions.Any())
            {
                return null;
            }

            return SelectRequestedEditionForAdd(candidateEditions, requestedEditionProviderIds, mediaType)
                   ?? _editionSelector.SelectBestEdition(candidateEditions, mediaType);
        }

        private List<Edition> SelectRetainedEditionsForAdd(Book book, IEnumerable<Edition> editions)
        {
            var editionList = (editions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null)
                .ToList();
            var mediaType = book?.MediaType ?? BookMediaType.Audiobook;

            if (!editionList.Any())
            {
                return new List<Edition>();
            }

            var author = ResolveAuthorForAddFiltering(book, mediaType);
            var metadataProfile = ResolveMetadataProfile(author, mediaType);
            var filteredEditions = _editionMetadataProfileFilter.Apply(editionList, metadataProfile);

            var retainedSelection = _editionSelector.SelectRetainedEditions(
                mediaType,
                filteredEditions);

            return retainedSelection?.RetainedEditions?.Where(e => e != null).ToList() ?? filteredEditions;
        }

        private Author ResolveAuthorForAddFiltering(Book book, BookMediaType mediaType)
        {
            var author = book?.Author;
            if (ResolveMetadataProfile(author, mediaType) != null)
            {
                return author;
            }

            return FindExistingAuthorForAdd(book) ?? author;
        }

        private Author FindExistingAuthorForAdd(Book book)
        {
            Author dbAuthor = null;
            var author = book?.Author;

            if (author?.Id > 0)
            {
                try
                {
                    dbAuthor = _authorService.GetAuthor(author.Id);
                }
                catch (ModelNotFoundException)
                {
                    // Fall back to provider IDs below.
                }
            }

            if (dbAuthor == null && (book?.AuthorId ?? 0) > 0 && book.AuthorId != (author?.Id ?? 0))
            {
                try
                {
                    dbAuthor = _authorService.GetAuthor(book.AuthorId);
                }
                catch (ModelNotFoundException)
                {
                    // Fall back to provider IDs below.
                }
            }

            if (author == null)
            {
                return dbAuthor;
            }

            if (dbAuthor == null && !string.IsNullOrEmpty(author.HardcoverAuthorId))
            {
                dbAuthor = _authorService.FindByProviderId("hc", author.HardcoverAuthorId);
            }

            if (dbAuthor == null && !string.IsNullOrEmpty(author.GoodreadsAuthorId))
            {
                dbAuthor = _authorService.FindByProviderId("gr", author.GoodreadsAuthorId);
            }

            if (dbAuthor == null && !string.IsNullOrEmpty(author.OpenLibraryAuthorId))
            {
                dbAuthor = _authorService.FindByProviderId("ol", author.OpenLibraryAuthorId);
            }

            if (dbAuthor == null && !string.IsNullOrEmpty(author.GoogleBooksAuthorId))
            {
                dbAuthor = _authorService.FindByProviderId("gb", author.GoogleBooksAuthorId);
            }

            if (dbAuthor == null && !string.IsNullOrEmpty(author.AudnexusAuthorId))
            {
                dbAuthor = _authorService.FindByProviderId("az", ProviderIdHelper.Normalize(author.AudnexusAuthorId, "az"));
            }

            return dbAuthor;
        }

        private Edition SelectRequestedEditionForAdd(List<Edition> retainedEditions, IReadOnlyCollection<string> requestedEditionProviderIds, BookMediaType mediaType)
        {
            if (requestedEditionProviderIds == null || !requestedEditionProviderIds.Any())
            {
                return null;
            }

            var requested = retainedEditions.FirstOrDefault(e =>
                requestedEditionProviderIds.Any(id => BookEditionIdentity.EditionMatchesProviderId(e, id)));

            if (requested == null)
            {
                return null;
            }

            var nativeFormat = EditionReadingFormat.NativeFor(mediaType);
            if (requested.ReadingFormatId == nativeFormat || requested.ManualAdd)
            {
                return requested;
            }

            if (retainedEditions.Any(e => e.ReadingFormatId == nativeFormat))
            {
                _logger.Debug("Requested add edition matched retained edition '{0}' but was not honored because it is non-native for {1} and a native edition survived retention. Requested IDs: {2}",
                    requested.ForeignEditionId ?? requested.Id.ToString(),
                    mediaType,
                    string.Join(", ", requestedEditionProviderIds));
                return null;
            }

            return requested;
        }

        private void ApplyEditionRetentionForAdd(Book book, IReadOnlyCollection<string> requestedEditionProviderIds)
        {
            if (book?.Editions == null || !book.Editions.Any())
            {
                return;
            }

            var retainedEditions = SelectRetainedEditionsForAdd(book, book.Editions);
            var selectedEdition = EditionPinPolicy.GetProtectedEditions(book, retainedEditions).FirstOrDefault()
                                  ?? SelectRequestedEditionForAdd(retainedEditions, requestedEditionProviderIds, book.MediaType)
                                  ?? _editionSelector.SelectBestEdition(retainedEditions, book.MediaType);

            if (!retainedEditions.Any() || selectedEdition == null)
            {
                var mediaTypeLabel = book.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("Edition", $"No {mediaTypeLabel} edition survived metadata profile filtering for this book.")
                });
            }

            var selectedIndex = GetSelectedRetainedEditionIndex(retainedEditions, selectedEdition);
            if (selectedIndex < 0)
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("Edition", "Selected edition was not retained for this book.")
                });
            }

            for (var i = 0; i < retainedEditions.Count; i++)
            {
                var edition = retainedEditions[i];
                edition.Monitored = i == selectedIndex;
                edition.ManualAdd = edition.ManualAdd && edition.Monitored;
            }

            book.Editions = retainedEditions;
            if (selectedEdition.ForeignEditionId.IsNotNullOrWhiteSpace())
            {
                book.ForeignEditionId = selectedEdition.ForeignEditionId;
            }
        }

        private static int GetSelectedRetainedEditionIndex(IReadOnlyList<Edition> retainedEditions, Edition selectedEdition)
        {
            if (retainedEditions == null || selectedEdition == null)
            {
                return -1;
            }

            for (var i = 0; i < retainedEditions.Count; i++)
            {
                if (ReferenceEquals(retainedEditions[i], selectedEdition))
                {
                    return i;
                }
            }

            if (selectedEdition.Id > 0)
            {
                for (var i = 0; i < retainedEditions.Count; i++)
                {
                    if (retainedEditions[i]?.Id == selectedEdition.Id)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string TryCanonicalizeWorkId(string providerId, string expectedPrefix = null)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            var prefixes = !string.IsNullOrWhiteSpace(expectedPrefix)
                ? new[] { expectedPrefix }
                : new[] { "hc", "gr", "ol" };

            foreach (var prefix in prefixes)
            {
                try
                {
                    var canonical = ProviderIdHelper.Canonicalize(providerId.Trim(), prefix);
                    var raw = ProviderIdHelper.StripPrefix(canonical);
                    if (prefix == "hc" && (!raw.All(char.IsDigit) || raw.Contains(":")))
                    {
                        return null;
                    }

                    return canonical;
                }
                catch
                {
                    // Try the next work-level provider prefix.
                }
            }

            return null;
        }

        private IReadOnlyCollection<string> GetRequestedEditionProviderIdsFromPayload(IEnumerable<Edition> editions)
        {
            var requested = editions?
                .Where(e => e != null && e.Monitored)
                .OrderBy(e => e.Id)
                .FirstOrDefault();

            if (BookEditionIdentity.IsExplicitEditionProviderId(requested?.ForeignEditionId) &&
                !ProviderIdHelper.TryNormalize(BookEditionIdentity.NormalizeEditionLookupId(requested.ForeignEditionId), null, out _))
            {
                return Array.Empty<string>();
            }

            return BookEditionIdentity.GetEditionProviderIds(requested);
        }

        private static bool HasMissingOrEmptyEditionMetadata(IEnumerable<Edition> editions)
        {
            if (editions == null)
            {
                return true;
            }

            var materialized = editions.ToList();
            if (!materialized.Any())
            {
                return true;
            }

            return materialized.Any(e =>
                e == null ||
                string.IsNullOrWhiteSpace(e.Title) ||
                !e.ReadingFormatId.HasValue);
        }

        public async Task<List<Book>> AddBooks(List<Book> books, bool doRefresh = true)
        {
            var added = DateTime.UtcNow;
            var addedBooks = new List<Book>();

            foreach (var a in books)
            {
                a.Added = added;
                try
                {
                    addedBooks.Add(await AddBook(a, doRefresh));
                }
                catch (Exception ex)
                {
                    // Could be a bad id from an import list
                    var bookIdentifier = a.HardcoverBookId ??
                                         a.GoodreadsWorkId ??
                                         BookEditionIdentity.GetCanonicalEditionProviderIds(a, _logger, "AddBookService.AddBooks").FirstOrDefault() ??
                                         a.OpenLibraryWorkId ??
                                         a.Id.ToString() ??
                                         "Unknown";
                    _logger.Error(ex, "Failed to import id: {0} - {1}", bookIdentifier, a.Title);
                }
            }

            return addedBooks;
        }
            private string GetBookProviderId(Book book)
            {
                return BookEditionIdentity.GetCanonicalWorkProviderIds(book).FirstOrDefault()
                    ?? BookEditionIdentity.GetCanonicalEditionProviderIds(book, _logger, "AddBookService.GetBookProviderId").FirstOrDefault();
            }

            private IEnumerable<string> GetAllBookProviderIds(Book book)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var providerId in BookEditionIdentity.GetCanonicalWorkProviderIds(book)
                                                             .Concat(BookEditionIdentity.GetCanonicalEditionProviderIds(book, _logger, "AddBookService.GetAllBookProviderIds"))
                                                             .Concat(book?.RemoteProviderIds ?? Enumerable.Empty<string>()))
                {
                    if (!string.IsNullOrWhiteSpace(providerId) && seen.Add(providerId.Trim()))
                    {
                        yield return providerId.Trim();
                    }
                }
            }
    }
}
