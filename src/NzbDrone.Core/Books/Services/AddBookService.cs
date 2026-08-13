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
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Books.Calibre;

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
        private readonly IProvideBookInfo _bookInfo;
        private readonly ISearchForNewBook _bookSearch;
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

        public AddBookService(IAuthorService authorService,
                               IAuthorLibraryService authorLibraryService,
                               IBookService bookService,
                               IProvideBookInfo bookInfo,
                               ISearchForNewBook bookSearch,
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
                               Logger logger)
        {
            _authorService = authorService;
            _authorLibraryService = authorLibraryService;
            _bookService = bookService;
            _bookInfo = bookInfo;
            _bookSearch = bookSearch;
            _importListExclusionService = importListExclusionService;
            _seriesBookLinkService = seriesBookLinkService;
            _seriesService = seriesService;
            _authorInfo = authorInfo;
            _filenameBuilder = filenameBuilder;
            _monitoringService = monitoringService;
            _editionService = editionService;
            _editionSelector = editionSelector;
            _editionMetadataProfileFilter = editionMetadataProfileFilter;
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

            // Seerr/Readarr compatibility: clients may POST lookup payloads that identify the work
            // but do not carry enough edition metadata to survive Chaptarr's media-type/profile filters.
            // Hydrate from the metadata server before pruning so add-time filtering runs on real data.
            if (dbBook == null && ShouldHydrateAddPayload(book))
            {
                try
                {
                    var lookupId = ResolveAddHydrationLookupId(book);

                    if (!string.IsNullOrWhiteSpace(lookupId))
                    {
                        var requestedMediaType = book.MediaType;
                        var requestedAuthor = book.Author;
                        var requestedAddOptions = book.AddOptions;
                        var requestedAnyEditionOk = ResolveAnyEditionOkForAddPayload(book);
                        var requestedAudiobookMonitored = book.AudiobookMonitored;
                        var requestedEbookMonitored = book.EbookMonitored;
                        var requestedAdded = book.Added;

                        var candidates = _bookSearch.SearchForNewBook(lookupId, author: null) ?? new List<Book>();

                        var hydrated = candidates.FirstOrDefault(b => b != null && b.MediaType == requestedMediaType)
                                      ?? candidates.FirstOrDefault(b =>
                                          requestedEditionProviderIdsForAdd.Any() &&
                                          b?.Editions?.Any(e => requestedEditionProviderIdsForAdd.Any(id => BookEditionIdentity.EditionMatchesProviderId(e, id))) == true);

                        if (hydrated != null)
                        {
                            // Keep caller-supplied settings/intent, but take metadata fields from V5.
                            // Clients like Seerr may POST an IDs-only payload with author settings but no author metadata.
                            // Merge metadata from the hydrated author into the caller-supplied author object so we keep
                            // caller settings while taking provider IDs/names from the metadata server.
                            var hydratedAuthorMetadata = hydrated.Author;
                            if (requestedAuthor != null && hydratedAuthorMetadata != null)
                            {
                                requestedAuthor.UseMetadataFrom(hydratedAuthorMetadata);
                            }

                            hydrated.Author = requestedAuthor ?? hydratedAuthorMetadata;
                            hydrated.AuthorId = requestedAuthor?.Id ?? 0;
                            hydrated.AddOptions = requestedAddOptions;
                            hydrated.AnyEditionOk = requestedAnyEditionOk;
                            hydrated.AudiobookMonitored = requestedAudiobookMonitored;
                            hydrated.EbookMonitored = requestedEbookMonitored;
                            hydrated.MediaType = requestedMediaType;
                            hydrated.Added = requestedAdded;

                            ApplyEditionRetentionForAdd(hydrated, requestedEditionProviderIdsForAdd);

                            book = hydrated;
                            _logger.Debug("[ADD-HYDRATE] Hydrated book from metadata server for add: ProviderId={0}, MediaType={1}, Title='{2}'",
                                lookupId, requestedMediaType, hydrated.Title);
                        }
                        else
                        {
                            _logger.Warn("[ADD-HYDRATE] Unable to hydrate book from metadata server for add: ProviderId={0}, MediaType={1}", lookupId, requestedMediaType);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[ADD-HYDRATE] Failed to hydrate AddBook payload; continuing with posted payload");
                }
            }

            if (dbBook != null)
            {
                book.UseDbFieldsFrom(dbBook);

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
            }
            else
            {
                // Fail fast before hitting DB constraints. If a client posts an IDs-only payload and we can't hydrate,
                // the insert would violate NOT NULL constraints (Books.Title/Editions.Title).
                if (string.IsNullOrWhiteSpace(book.Title))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("Title",
                            "Cannot add book: missing Title. If you are using a third-party client that posts IDs-only payloads (e.g. Seerr), ensure the metadata server can resolve the provider ID and retry.")
                    });
                }

                if (book.Editions == null || !book.Editions.Any())
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("Editions",
                            "Cannot add book: no editions were supplied. Retry the add so Chaptarr can hydrate edition metadata from the metadata server.")
                    });
                }

                if (book.Editions.Any(e => e != null && string.IsNullOrWhiteSpace(e.Title)))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("Editions",
                            "Cannot add book: one or more editions are missing Title. Retry the add so Chaptarr can hydrate edition metadata from the metadata server.")
                    });
                }
            }

            // Remove any import list exclusions preventing addition
            var matchingExclusions = _importListExclusionService.FindByForeignId(ImportListExclusionBookMatcher.GetLookupIds(book));
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

            if (book.Author != null && !string.IsNullOrEmpty(book.Author.HardcoverAuthorId))
            {
                _importListExclusionService.Delete(book.Author.HardcoverAuthorId);
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
                // Author doesn't exist locally - try to import from the configured metadata server
                _logger.Info("Author not found locally for book '{0}'. Attempting import from metadata server", book.Title);

                // Log the author provider IDs we received
                _logger.Debug("Author provider IDs - HardcoverAuthorId: '{0}', GoodreadsAuthorId: '{1}'",
                    book.Author?.HardcoverAuthorId ?? "NULL",
                    book.Author?.GoodreadsAuthorId ?? "NULL");

                // Determine which provider ID to use for import
                string foreignAuthorId = null;

                if (!string.IsNullOrEmpty(book.Author.HardcoverAuthorId))
                {
                    // HardcoverAuthorId already contains the prefix (e.g., "hc:191785")
                    foreignAuthorId = book.Author.HardcoverAuthorId;
                }
                else if (!string.IsNullOrEmpty(book.Author.GoodreadsAuthorId))
                {
                    // GoodreadsAuthorId already contains the prefix (e.g., "gr:12345")
                    foreignAuthorId = book.Author.GoodreadsAuthorId;
                }
                else if (!string.IsNullOrEmpty(book.Author.AudnexusAuthorId))
                {
                    // AudnexusAuthorId already contains the prefix (e.g., "az:B000AP9A6K")
                    foreignAuthorId = book.Author.AudnexusAuthorId;
                }

                    if (!string.IsNullOrEmpty(foreignAuthorId))
                    {
                        _logger.Info("[AddBookService] Attempting author import with foreignAuthorId: '{0}'", foreignAuthorId);

                        {
                            // "None (Well, obviously just this one)" is represented in the UI as tri-state Selected (2)
                            // on the relevant media type, but the AddOptions.Monitor enum is often left as None.
                            // Treat tri-state Selected as a SpecificBook intent when adding a single book.
                            var isTriStateSelectedForMediaType =
                                (book.MediaType == BookMediaType.Audiobook && (book.Author?.AudiobookMonitorExisting ?? 0) == 2) ||
                                (book.MediaType == BookMediaType.Ebook && (book.Author?.EbookMonitorExisting ?? 0) == 2);

                            var isSpecificBookIntent =
                                book.Author?.AddOptions?.Monitor == MonitorTypes.SpecificBook ||
                                isTriStateSelectedForMediaType;

                            // Create MonitoringConfig from the book's author settings
                            var specificBookProviderIds = isSpecificBookIntent
                                ? GetAllBookProviderIds(book).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                                : null;

                            var config = new MonitoringConfig
                            {
                                IsManualAddition = true,
                                AuthorName = book.Author?.Name,
                                // Only create the media type being added; avoid failing the entire add/queue due to missing config on the other type.
                                CreateAudiobook = book.MediaType == BookMediaType.Audiobook,
                                CreateEbook = book.MediaType == BookMediaType.Ebook,
                                MonitorNewItems = book.Author.Monitored,
                                AudiobookQualityProfileId = book.Author.AudiobookQualityProfileId,
                                EbookQualityProfileId = book.Author.EbookQualityProfileId,
                                AudiobookMetadataProfileId = ResolveMetadataProfileIdForBookAdd(
                                    BookMediaType.Audiobook,
                                    book.MediaType,
                                    book.Author.AudiobookMetadataProfileId,
                                    isSpecificBookIntent),
                                EbookMetadataProfileId = ResolveMetadataProfileIdForBookAdd(
                                    BookMediaType.Ebook,
                                    book.MediaType,
                                    book.Author.EbookMetadataProfileId,
                                    isSpecificBookIntent),
                                AudiobookRootFolderPath = book.Author.AudiobookRootFolderPath,
                                EbookRootFolderPath = book.Author.EbookRootFolderPath,
                                AudiobookTags = book.Author.AudiobookTags,
                                EbookTags = book.Author.EbookTags,
                                Tags = book.Author.Tags,
                                QueueIfUnavailable = true,
                                
                                // Pass specific book context for conditional monitoring
                                MonitorMode = isSpecificBookIntent ? MonitorTypes.SpecificBook : book.Author?.AddOptions?.Monitor,
                                SpecificBookProviderIds = isSpecificBookIntent
                                    ? new HashSet<string>(specificBookProviderIds, StringComparer.OrdinalIgnoreCase)
                                    : null,
                                SpecificBookMediaType = book.MediaType,
                                // Pending import processing doesn't persist SpecificBookProviderIds; store an explicit list for later "monitor only these" behavior.
                                AudiobookBooksToMonitor = isSpecificBookIntent && book.MediaType == BookMediaType.Audiobook ? specificBookProviderIds : null,
                                EbookBooksToMonitor = isSpecificBookIntent && book.MediaType == BookMediaType.Ebook ? specificBookProviderIds : null
                            };

                            // Handle "None, except this one book" monitoring option
                            if (isSpecificBookIntent)
                            {
                                // "None, except this one book" - use Selected mode (2) for tri-state monitoring
                                config.MonitorNewItems = true;
                                if (book.MediaType == BookMediaType.Audiobook)
                                {
                                    config.AudiobookMonitorExisting = 2; // Selected mode - only specific books
                                    // Respect the user's "Monitor New Books" preference for audiobooks (if provided)
                                    config.AudiobookMonitorFuture = book.Author?.AudiobookMonitorFuture;
                                    // Don't monitor ebooks
                                    config.EbookMonitorExisting = 0; // None mode
                                    config.EbookMonitorFuture = false; // Don't monitor
                                }
                                else if (book.MediaType == BookMediaType.Ebook)
                                {
                                    config.EbookMonitorExisting = 2; // Selected mode - only specific books
                                    // Respect the user's "Monitor New Books" preference for ebooks (if provided)
                                    config.EbookMonitorFuture = book.Author?.EbookMonitorFuture;
                                    // Don't monitor audiobooks
                                    config.AudiobookMonitorExisting = 0; // None mode
                                    config.AudiobookMonitorFuture = false; // Don't monitor
                                }
                            }
                        else
                        {
                            // Apply regular monitoring settings from the book's author
                            config.AudiobookMonitorExisting = book.Author.AudiobookMonitorExisting;
                            config.AudiobookMonitorFuture = book.Author.AudiobookMonitorFuture;
                            config.EbookMonitorExisting = book.Author.EbookMonitorExisting;
                            config.EbookMonitorFuture = book.Author.EbookMonitorFuture;
                        }

                            // Use AuthorLibraryService which handles dual-instance creation correctly
                            dbAuthor = await _authorLibraryService.AddAuthorAsync(foreignAuthorId, config);
                            
                            // When the metadata server doesn't have the author yet, AddAuthorAsync queues a pending import and returns a marker with a negative Id.
                            if (dbAuthor != null && dbAuthor.Id < 0)
                            {
                                var pendingId = -dbAuthor.Id;
                                throw new ValidationException(new List<ValidationFailure>
                                {
                                    new ValidationFailure("Author",
                                        $"Author '{book.Author.Name}' isn't available yet on our metadata server. It has been queued for import (pending ID: {pendingId}) and will be imported automatically when it becomes available.")
                                });
                            }
                            
                            // After the author is added, find the specific book instance we're adding
                            var targetProviderIds = new HashSet<string>(GetAllBookProviderIds(book), StringComparer.OrdinalIgnoreCase);
                            var importedBooks = _bookService.GetBooksByAuthor(dbAuthor.Id)
                                .Where(b => GetAllBookProviderIds(b).Any(id => targetProviderIds.Contains(id)))
                                .ToList();

                            // Handle "None, except this one book" - apply monitoring to the specific book
                            if (isSpecificBookIntent && importedBooks.Any())
                            {
                                foreach (var importedBook in importedBooks)
                                {
                                    // Only monitor the specific media type the user is adding
                                    if (importedBook.MediaType == book.MediaType)
                                {
                                    if (importedBook.MediaType == BookMediaType.Audiobook)
                                    {
                                        importedBook.AudiobookMonitored = true;
                                    }
                                    else if (importedBook.MediaType == BookMediaType.Ebook)
                                    {
                                        importedBook.EbookMonitored = true;
                                    }
                                    _bookService.UpdateBook(importedBook);
                                }
                            }
                        }

                        // Find the specific instance matching the MediaType we're adding
                        var matchingBook = importedBooks.FirstOrDefault(b => b.MediaType == book.MediaType);
                        if (matchingBook != null)
                        {
                            // Book was imported with the author - use it
                            book = matchingBook;
                            book.Author = dbAuthor;
                            book.AuthorId = dbAuthor.Id;
                            
                            // Monitoring has already been handled by AuthorLibraryService.ProcessBooksForAuthor
                            // using conditional monitoring at book creation time
                            _logger.Debug("[MONITOR-DEBUG] Book '{0}' (ID: {1}) monitoring already set by ProcessBooksForAuthor",
                                book.Title, book.Id);
                            
                            return book;
                        }
                        else
                        {
                            // Book wasn't imported with author - add it now
                            book.Author = dbAuthor;
                            book.AuthorId = dbAuthor.Id;

                            // Apply tri-state monitoring at creation time
                            // 0=None, 1=All, 2=Selected (monitor physical copies only)
                            if (book.MediaType == BookMediaType.Audiobook)
                            {
                                var mode = dbAuthor.AudiobookMonitorExisting ?? 0;
                                // For Selected (2), initial add has no files yet, so leave false; it will flip on first file import
                                var monitor = mode == 1;
                                book.AudiobookMonitored = monitor;
                                book.EbookMonitored = false;
                            }
                            else if (book.MediaType == BookMediaType.Ebook)
                            {
                                var mode = dbAuthor.EbookMonitorExisting ?? 0;
                                var monitor = mode == 1;
                                book.EbookMonitored = monitor;
                                book.AudiobookMonitored = false;
                            }

                            // Legacy compatibility
                            book.Monitored = book.AudiobookMonitored || book.EbookMonitored;
                        }
                    }
                }
                else
                {
                    _logger.Error("Cannot add book '{0}' - no provider ID available for author import", book.Title);
                    throw new InvalidOperationException($"Cannot add book '{book.Title}' - no provider ID for author.");
                }
            }

                // CRITICAL: Capture monitoring intent BEFORE replacing Author object
                var monitorMode = book.Author?.AddOptions?.Monitor;
                var bookMediaType = book.MediaType;
                var isTriStateSelectedForExistingAuthorMediaType =
                    (bookMediaType == BookMediaType.Audiobook && (book.Author?.AudiobookMonitorExisting ?? 0) == 2) ||
                    (bookMediaType == BookMediaType.Ebook && (book.Author?.EbookMonitorExisting ?? 0) == 2);
                var isSpecificBookIntentForExistingAuthor =
                    monitorMode == MonitorTypes.SpecificBook ||
                    isTriStateSelectedForExistingAuthorMediaType;
                
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
                            requestedAuthorSettings.AudiobookMonitorExisting,
                            requestedAuthorSettings.AudiobookMonitorFuture,
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
                            requestedAuthorSettings.EbookMonitorExisting,
                            requestedAuthorSettings.EbookMonitorFuture,
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

                // If any monitored book was added, ensure the author is marked monitored so background sync/refresh picks it up.
                if ((book.AudiobookMonitored || book.EbookMonitored) && book.Author != null && !book.Author.Monitored)
                {
                    book.Author.Monitored = true;
                    _authorService.UpdateAuthor(book.Author);
                    _logger.Debug("Set author '{0}' to monitored because a monitored book was added", book.Author.Name);
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

            return book;
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

            var nativeFormat = mediaType == BookMediaType.Audiobook ? 2 : 3;
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
            var selectedEdition = SelectRequestedEditionForAdd(retainedEditions, requestedEditionProviderIds, book.MediaType)
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

        private int? ResolveMetadataProfileIdForBookAdd(
            BookMediaType profileMediaType,
            BookMediaType requestedMediaType,
            int? configuredProfileId,
            bool isSpecificBookIntent)
        {
            if (!isSpecificBookIntent || profileMediaType != requestedMediaType)
            {
                return configuredProfileId;
            }

            var noneProfile = _metadataProfileService.All()
                .FirstOrDefault(profile => profile.Name == MetadataProfileService.NONE_PROFILE_NAME);

            if (noneProfile == null)
            {
                _logger.Warn("Built-in metadata profile '{0}' is unavailable; specific-book add will use configured profile id {1}",
                    MetadataProfileService.NONE_PROFILE_NAME,
                    configuredProfileId);
                return configuredProfileId;
            }

            return noneProfile.Id;
        }

        private bool ShouldHydrateAddPayload(Book book)
        {
            if (book == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(book.Title) || HasMissingOrEmptyEditionMetadata(book.Editions))
            {
                return true;
            }

            return !SelectRetainedEditionsForAdd(book, book.Editions).Any();
        }

        private string ResolveAddHydrationLookupId(Book book)
        {
            if (book == null)
            {
                return null;
            }

            string FirstValidWorkId(params (string Value, string Prefix)[] ids)
            {
                foreach (var (value, prefix) in ids)
                {
                    var canonical = TryCanonicalizeWorkId(value, prefix);
                    if (!string.IsNullOrWhiteSpace(canonical))
                    {
                        return canonical;
                    }
                }

                return null;
            }

            var workId = FirstValidWorkId(
                (book.HardcoverBookId, "hc"),
                (book.GoodreadsWorkId, "gr"),
                (book.OpenLibraryWorkId, "ol"));
            if (!string.IsNullOrWhiteSpace(workId))
            {
                return workId;
            }

            foreach (var providerId in book.RemoteProviderIds ?? Enumerable.Empty<string>())
            {
                workId = TryCanonicalizeWorkId(providerId);
                if (!string.IsNullOrWhiteSpace(workId))
                {
                    return workId;
                }
            }

            return BookEditionIdentity.GetGoodreadsEditionProviderId(book, _logger, "AddBookService.AddBook.Hydrate")
                   ?? BookEditionIdentity.GetOpenLibraryEditionId(book, _logger, "AddBookService.AddBook.Hydrate")
                   ?? BookEditionIdentity.GetGoogleBooksEditionId(book, _logger, "AddBookService.AddBook.Hydrate")
                   ?? AsProviderLookupId(BookEditionIdentity.GetAudibleAsin(book, _logger, "AddBookService.AddBook.Hydrate"), "az")
                   ?? AsProviderLookupId(BookEditionIdentity.GetAsin(book, _logger, "AddBookService.AddBook.Hydrate"), "az");
        }

        private static string AsProviderLookupId(string value, string expectedPrefix)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.Canonicalize(value.Trim(), expectedPrefix);
            }
            catch
            {
                return null;
            }
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

            return BookEditionIdentity.GetEditionProviderIds(requested);
        }

        private static bool ResolveAnyEditionOkForAddPayload(Book book)
        {
            if (book == null || book.AnyEditionOk)
            {
                return true;
            }

            if (HasMissingOrEmptyEditionMetadata(book.Editions))
            {
                // Seerr's Readarr-compatible add flow round-trips the edition id Chaptarr exposed
                // from lookup, but posts only IDs and hardcodes anyEditionOk=false. That id is our
                // default monitored edition, not a user-selected pin. Treat IDs-only adds as
                // work-level so import can switch to the matched edition later. Do not key this on
                // ManualAdd; in Chaptarr it is also a local preservation marker. If Seerr starts
                // sending richer edition intent, tighten or remove this compatibility shim.
                return true;
            }

            return false;
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
