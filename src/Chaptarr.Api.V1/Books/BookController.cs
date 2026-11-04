using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Api.V1.ProviderIds;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;
using NzbDrone.Common.Instrumentation;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController]
	    public class BookController : BookControllerWithSignalR,
	        IHandle<BookGrabbedEvent>,
	        IHandle<BookEditedEvent>,
	        IHandle<BookUpdatedEvent>,
	        IHandle<BookDeletedEvent>,
	        IHandle<BookImportedEvent>,
	        IHandle<ImportStageProgressEvent>,
	        IHandle<CommandExecutedEvent>,
	        IHandle<TrackImportedEvent>,
	        IHandle<BookFileDeletedEvent>
	    {
        private const int MaxBookshelfAuthorIds = 100;

        protected readonly IAuthorService _authorService;
        protected readonly IEditionService _editionService;
	        protected readonly IAddBookService _addBookService;
	        private readonly IEditionSelector _editionSelector;
	        protected readonly IManageCommandQueue _commandQueueManager;
	        protected readonly IMetadataProfileService _metadataProfileService;
	        protected readonly IQualityProfileService _qualityProfileService;
	        protected readonly IRootFolderService _rootFolderService;
	        private readonly IMediaFileService _mediaFileService;
	        private readonly IMediaCoverProxy _mediaCoverProxy;
	        private readonly IEventAggregator _eventAggregator;
            private readonly IProviderAliasService _providerAliasService;
            private readonly IBookshelfLegacyIdentityMapService _bookshelfLegacyIdentityMapService;
	        private readonly Logger _logger;
	        private readonly object _importStateLock = new object();
	        private readonly HashSet<int> _activeImportCommands = new HashSet<int>();
            private readonly object _bookEditBroadcastLock = new object();
            private readonly HashSet<int> _pendingBookEditIds = new HashSet<int>();
            private readonly Debouncer _bookEditBroadcastDebouncer;

	        public BookController(IAuthorService authorService,
	                          IBookService bookService,
		                          IAddBookService addBookService,
	                          IEditionService editionService,
	                          IEditionSelector editionSelector,
	                          ISeriesBookLinkService seriesBookLinkService,
	                          IAuthorStatisticsService authorStatisticsService,
                          IMediaFileService mediaFileService,
                          IMapCoversToLocal coverMapper,
                          IMediaCoverProxy mediaCoverProxy,
                          IUpgradableSpecification upgradableSpecification,
	                          IBroadcastSignalRMessage signalRBroadcaster,
	                          IManageCommandQueue commandQueueManager,
	                          IEventAggregator eventAggregator,
	                          IMetadataProfileService metadataProfileService,
	                          IQualityProfileService qualityProfileService,
	                          IRootFolderService rootFolderService,
                          QualityProfileExistsValidator qualityProfileExistsValidator,
                          MetadataProfileExistsValidator metadataProfileExistsValidator,
                          Logger logger,
                          IProviderAliasService providerAliasService = null,
                          IBookshelfLegacyIdentityMapService bookshelfLegacyIdentityMapService = null)

        : base(bookService, seriesBookLinkService, authorStatisticsService, coverMapper, upgradableSpecification, signalRBroadcaster, bookshelfLegacyIdentityMapService, editionService)
	        {
	            _authorService = authorService;
	            _editionService = editionService;
	            _editionSelector = editionSelector;
	            _addBookService = addBookService;
	            _commandQueueManager = commandQueueManager;
	            _metadataProfileService = metadataProfileService;
            _qualityProfileService = qualityProfileService;
            _rootFolderService = rootFolderService;
            _mediaFileService = mediaFileService;
            _mediaCoverProxy = mediaCoverProxy;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _providerAliasService = providerAliasService;
            _bookshelfLegacyIdentityMapService = bookshelfLegacyIdentityMapService;
            _bookEditBroadcastDebouncer = new Debouncer(FlushPendingBookEdits, TimeSpan.FromMilliseconds(500), executeRestartsTimer: true);

            PostValidator.RuleFor(s => s.Author).Must(author =>
            {
                if (author == null)
                {
                    return false;
                }

                if (author.AudiobookQualityProfileId.HasValue || author.EbookQualityProfileId.HasValue || author.QualityProfileId.HasValue)
                {
                    return true;
                }

                // Existing authors may be posted without full profile details (e.g. from search results).
                // Allow the add to proceed so the stored author profiles can be used.
                if (author.Id > 0)
                {
                    if (_authorService.GetAuthor(author.Id) != null)
                    {
                        return true;
                    }
                }

                // If we have a provider foreign id and it matches a local author, allow it.
                // This keeps third-party clients compatible when posting minimal author objects.
                if (!author.ForeignAuthorId.IsNullOrWhiteSpace())
                {
                    var foreignId = author.ForeignAuthorId.Trim();
                    var idx = foreignId.IndexOf(':');
                    string provider = null;
                    var providerId = foreignId;

                    if (idx > 0)
                    {
                        provider = foreignId.Substring(0, idx);
                        providerId = foreignId.Substring(idx + 1).Trim();
                    }
                    else if (long.TryParse(foreignId, out _))
                    {
                        provider = "hc";
                    }

                    if (!provider.IsNullOrWhiteSpace() && !providerId.IsNullOrWhiteSpace() &&
                        ProviderAmbiguityHelper.FindAuthorProviderMatches(_authorService, _providerAliasService, provider, providerId, _logger).Any())
                    {
                        return true;
                    }
                }

                return false;
            })
		                         .WithMessage("At least one quality profile must be selected");
	            PostValidator.RuleFor(s => s.Author.MetadataProfileId).SetValidator(metadataProfileExistsValidator);
	            PostValidator.RuleFor(s => s.Author.AudiobookQualityProfileId)
	                         .SetValidator(qualityProfileExistsValidator)
	                         .When(s => s.Author?.AudiobookQualityProfileId.HasValue == true && s.Author.AudiobookQualityProfileId.Value > 0);
	            PostValidator.RuleFor(s => s.Author.EbookQualityProfileId)
	                         .SetValidator(qualityProfileExistsValidator)
	                         .When(s => s.Author?.EbookQualityProfileId.HasValue == true && s.Author.EbookQualityProfileId.Value > 0);
	            PostValidator.RuleFor(s => s.Author.AudiobookMetadataProfileId)
	                         .SetValidator(metadataProfileExistsValidator)
	                         .When(s => s.Author?.AudiobookMetadataProfileId.HasValue == true && s.Author.AudiobookMetadataProfileId.Value > 0);
	            PostValidator.RuleFor(s => s.Author.EbookMetadataProfileId)
	                         .SetValidator(metadataProfileExistsValidator)
	                         .When(s => s.Author?.EbookMetadataProfileId.HasValue == true && s.Author.EbookMetadataProfileId.Value > 0);
            PostValidator.RuleFor(s => s.Author.AudiobookRootFolderPath).IsValidPath()
		                         .When(s => s.Author.Path.IsNullOrWhiteSpace() && !s.Author.AudiobookRootFolderPath.IsNullOrWhiteSpace());
            PostValidator.RuleFor(s => s.Author.EbookRootFolderPath).IsValidPath()
	                         .When(s => s.Author.Path.IsNullOrWhiteSpace() && !s.Author.EbookRootFolderPath.IsNullOrWhiteSpace());
            PostValidator.RuleFor(s => s.Author.RootFolderPath).IsValidPath()
                             .When(s => s.Author != null && s.Author.Path.IsNullOrWhiteSpace() && !s.Author.RootFolderPath.IsNullOrWhiteSpace());
	        }

        private ActionResult GetProviderAmbiguityResult(ProviderAmbiguityResource ambiguity)
        {
            return ambiguity == null ? null : StatusCode(ProviderAmbiguityHelper.StatusCode, ambiguity);
        }

        private ActionResult GetAuthorProviderAmbiguityResult(string prefixedProviderId, string field, string operation)
        {
            return GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetAuthorAmbiguity(
                _authorService,
                _providerAliasService,
                prefixedProviderId,
                field,
                _logger,
                operation));
        }

        private ActionResult GetAuthorProviderAmbiguityResult(string provider, string providerId, string field, string operation)
        {
            return GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetAuthorAmbiguity(
                _authorService,
                _providerAliasService,
                provider,
                providerId,
                field,
                _logger,
                operation));
        }

        private ActionResult GetBookProviderAmbiguityResult(Book book, string field)
        {
            return GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetBookAmbiguity(
                _bookService,
                book,
                field,
                _logger,
                "adding book"));
        }

        private ActionResult GetBookProviderAmbiguityResult(string provider, string providerId, BookMediaType mediaType, string field)
        {
            return GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetBookAmbiguity(
                _bookService,
                provider,
                providerId,
                mediaType,
                field,
                _logger,
                "importing book"));
        }

        private bool IsImportActive()
	        {
	            lock (_importStateLock)
	            {
	                return _activeImportCommands.Count > 0;
	            }
	        }

        private void QueueBookEditBroadcast(int bookId)
        {
            if (bookId <= 0)
            {
                return;
            }

            var firstInBurst = false;
            lock (_bookEditBroadcastLock)
            {
                firstInBurst = _pendingBookEditIds.Count == 0;
                _pendingBookEditIds.Add(bookId);
            }

            // Preserve an immediate row update for ordinary single-book edits. If more
            // edits arrive in the same burst, the debounced flush replaces hundreds of
            // fully-loaded row broadcasts with one collection sync.
            if (firstInBurst)
            {
                BroadcastResourceChange(ModelAction.Updated, bookId);
            }

            _bookEditBroadcastDebouncer.Execute();
        }

        internal void FlushPendingBookEdits()
        {
            try
            {
                int pendingCount;
                lock (_bookEditBroadcastLock)
                {
                    pendingCount = _pendingBookEditIds.Count;
                    _pendingBookEditIds.Clear();
                }

                if (pendingCount > 1)
                {
                    BroadcastResourceChange(ModelAction.Sync);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[UI-BROADCAST] Failed to flush pending Book updates");
            }
        }

	        [HttpPost("import")]
	        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
	        public async Task<ActionResult> ImportBook([FromBody] BookImportResource importResource)
	        {
            try
            {
                _logger.DebugSafe("[V1-BOOK-IMPORT] Starting import with foreignBookId: {0}, foreignAuthorId: {1}, mediaType: {2}",
                    importResource.ForeignBookId, importResource.ForeignAuthorId, importResource.MediaType);

                if (string.IsNullOrWhiteSpace(importResource.ForeignBookId) || string.IsNullOrWhiteSpace(importResource.ForeignAuthorId))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("ForeignIds", "foreignBookId and foreignAuthorId are required")
                    });
                }

                var facadeContext = HttpContext.GetReadarrFacadeContext();
                var prefixFailures = new List<ValidationFailure>();
                if (ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(importResource.ForeignBookId, facadeContext))
                {
                    prefixFailures.Add(new ValidationFailure("ForeignBookId", ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("foreignBookId")));
                }

                if (ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(importResource.ForeignAuthorId, facadeContext))
                {
                    prefixFailures.Add(new ValidationFailure("ForeignAuthorId", ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("foreignAuthorId")));
                }

                if (prefixFailures.Any())
                {
                    throw new ValidationException(prefixFailures);
                }

                var foreignBookId = ReadarrFacadeProviderIdTranslator.NormalizeBareProviderId(importResource.ForeignBookId, facadeContext);
                var foreignAuthorId = ReadarrFacadeProviderIdTranslator.NormalizeBareProviderId(importResource.ForeignAuthorId, facadeContext);

	                string bookProvider, bookId, authorProvider, authorId, editionProvider, editionId;
	                var bookValid = ProviderIdValidator.TryNormalize(foreignBookId, out _, out bookProvider, out bookId, out var bookErrorMessage);
	                var authorValid = ProviderIdValidator.TryNormalize(foreignAuthorId, out _, out authorProvider, out authorId, out var authorErrorMessage);
	                if (!bookValid || !authorValid)
	                {
	                    var details = new List<string>();
	                    if (!bookValid) details.Add($"foreignBookId: {bookErrorMessage}");
	                    if (!authorValid) details.Add($"foreignAuthorId: {authorErrorMessage}");

	                    throw new ValidationException(new[]
	                    {
	                        new ValidationFailure("ForeignIds", string.Join(" ", details))
	                    });
	                }

                var foreignEditionId = importResource.ForeignEditionId?.Trim().Trim('{', '}');
                if (!string.Equals(foreignEditionId, "0", StringComparison.OrdinalIgnoreCase) &&
                    ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(foreignEditionId, facadeContext))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("ForeignEditionId", ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("foreignEditionId"))
                    });
                }

                if (string.IsNullOrWhiteSpace(foreignEditionId) ||
                    foreignEditionId.Equals("0", StringComparison.OrdinalIgnoreCase))
                {
                    editionProvider = bookProvider;
                    editionId = "0";
                }
	                else
	                {
	                    var editionParts = foreignEditionId.Split(new[] { ':' }, 2);
	                    if (editionParts.Length == 1)
	                    {
	                        editionProvider = bookProvider;
	                        editionId = editionParts[0].Trim();
                    }
                    else if (editionParts.Length == 2)
                    {
                        editionProvider = editionParts[0].Trim().ToLowerInvariant();
                        editionId = editionParts[1].Trim();
                    }
                    else
                    {
                        throw new ValidationException(new[]
                        {
                            new ValidationFailure("ForeignEditionId", "Invalid edition ID format. Expected 'provider:id' or '0'")
                        });
                    }
	                }

	                if (editionId != "0" &&
	                    (!ProviderIdValidator.ValidPrefixes.Contains(editionProvider) || !ProviderIdValidator.IsValidId(editionId)))
	                {
	                    throw new ValidationException(new[]
	                    {
	                        new ValidationFailure("ForeignEditionId", $"Invalid edition ID. Expected {ProviderIdValidator.ValidPrefixesDisplay} with an alphanumeric id.")
	                    });
	                }

                var bookMediaType = MediaTypeParameterParser.ParseRequired(importResource.MediaType);

                if (bookProvider == "rnr")
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("ForeignBookId", "RanobeDB release IDs identify editions. Use an rn: work ID for foreignBookId.")
                    });
                }

                if (bookProvider == "rn" && bookMediaType != BookMediaType.Ebook)
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("MediaType", "RanobeDB metadata is available for eBooks only.")
                    });
                }

                var authorAmbiguity = GetAuthorProviderAmbiguityResult(authorProvider, authorId, "foreignAuthorId", "importing book");
                if (authorAmbiguity != null)
                {
                    return authorAmbiguity;
                }

                var bookAmbiguity = GetBookProviderAmbiguityResult(bookProvider, bookId, bookMediaType, "foreignBookId");
                if (bookAmbiguity != null)
                {
                    return bookAmbiguity;
                }

                if (editionId != "0")
                {
                    var editionAmbiguity = GetBookProviderAmbiguityResult(editionProvider, editionId, bookMediaType, "foreignEditionId");
                    if (editionAmbiguity != null)
                    {
                        return editionAmbiguity;
                    }
                }

                var authorMonitoring = importResource.AuthorMonitoring;
                var monitoring = AuthorController.ResolveImportMonitoring(new AuthorImportResource
                {
                    Monitor = authorMonitoring?.Monitor,
                    MonitorExisting = authorMonitoring?.MonitorExisting,
                    MonitorFuture = authorMonitoring?.MonitorFuture,
                    AudiobookMonitored = authorMonitoring?.AudiobookMonitored,
                    AudiobookMonitorNewItems = authorMonitoring?.AudiobookMonitorNewItems,
                    AudiobookMonitorExistingMode = authorMonitoring?.AudiobookMonitorExistingMode,
                    EbookMonitored = authorMonitoring?.EbookMonitored,
                    EbookMonitorNewItems = authorMonitoring?.EbookMonitorNewItems,
                    EbookMonitorExistingMode = authorMonitoring?.EbookMonitorExistingMode
                }, bookMediaType, legacySelectTargetsSpecificBook: true);
                var monitorMode = monitoring.MonitorExistingMode ?? MonitorTypes.SpecificBook;
                var authorMonitored = monitoring.Monitored;
                var monitorNewItems = monitoring.MonitorNewItems;
                var monitorCurrentBook = monitorMode != MonitorTypes.None;

                if (editionId != "0" &&
                    !string.Equals(editionProvider, bookProvider, StringComparison.OrdinalIgnoreCase) &&
                    !(bookProvider == "rn" && editionProvider == "rnr"))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("ForeignEditionId",
                            "Edition provider must match book provider when a specific edition is provided.")
                    });
                }

                var bookToAdd = new Book
                {
                    Title = "Pending Import",
                    MediaType = bookMediaType,
                    Monitored = monitorCurrentBook,
                    AudiobookMonitored = bookMediaType == BookMediaType.Audiobook && monitorCurrentBook,
                    EbookMonitored = bookMediaType == BookMediaType.Ebook && monitorCurrentBook,
                    // This compatibility/import endpoint is automation-facing. A supplied Edition is an
                    // initial preference, not the explicit human preservation pin used by the id-route editor.
                    AnyEditionOk = true,
                    Editions = new List<Edition>()
                };

	                switch (bookProvider.ToLowerInvariant())
	                {
	                    case "hc":
	                        bookToAdd.HardcoverBookId = bookId;
	                        break;
	                    case "gr":
	                        bookToAdd.GoodreadsWorkId = bookId;
	                        break;
	                    case "ol":
	                        bookToAdd.OpenLibraryWorkId = bookId;
	                        break;
	                    case "rn":
	                        bookToAdd.RanobeDbBookId = ProviderIdHelper.WithPrefix("rn", bookId);
	                        break;
	                    case "gb":
	                        bookToAdd.Editions.Add(new Edition
	                        {
	                            GoogleBooksEditionId = ProviderIdHelper.StripPrefix(bookId),
	                            Monitored = true
	                        });
	                        break;
	                    case "az":
	                        var normalizedAsin = bookId?.Trim().ToUpperInvariant();
	                        var edition = new Edition
	                        {
	                            Asin = normalizedAsin,
	                            AudibleASIN = normalizedAsin,
	                            Monitored = true
	                        };
	                        bookToAdd.Editions.Add(edition);
	                        if (bookMediaType == BookMediaType.Audiobook)
	                        {
	                            bookToAdd.ForeignEditionId = $"az:{normalizedAsin}";
	                        }
	                        else
	                        {
	                            bookToAdd.ForeignEditionId = $"az:{normalizedAsin}";
	                        }
	                        break;
	                }

                var author = new NzbDrone.Core.Books.Author
                {
                    Name = "Pending Import",
                    Monitored = authorMonitored == true,
                    AudiobookMonitored = bookMediaType == BookMediaType.Audiobook ? authorMonitored : null,
                    AudiobookMonitorNewItems = bookMediaType == BookMediaType.Audiobook ? monitorNewItems : null,
                    EbookMonitored = bookMediaType == BookMediaType.Ebook ? authorMonitored : null,
                    EbookMonitorNewItems = bookMediaType == BookMediaType.Ebook ? monitorNewItems : null,
                    AddOptions = new AddAuthorOptions
                    {
                        Monitor = monitorMode,
                        Monitored = authorMonitored == true,
                        SearchForMissingBooks = authorMonitoring?.SearchForMissing == true
                    }
                };

	                switch (authorProvider.ToLowerInvariant())
	                {
	                    case "hc":
	                        author.HardcoverAuthorId = authorId;
	                        break;
	                    case "gr":
	                        author.GoodreadsAuthorId = authorId;
	                        break;
	                    case "ol":
	                        author.OpenLibraryAuthorId = authorId;
	                        break;
	                    case "gb":
	                        author.GoogleBooksAuthorId = authorId;
	                        break;
	                    case "az":
	                        author.AudnexusAuthorId = $"az:{authorId?.Trim().ToUpperInvariant()}";
	                        break;
	                    case "rn":
	                        author.RanobeDbAuthorId = ProviderIdHelper.WithPrefix("rn", authorId);
	                        break;
	                }

                if (bookMediaType == BookMediaType.Audiobook)
                {
                    author.AudiobookRootFolderPath = importResource.RootFolder;
                    author.AudiobookQualityProfileId = importResource.QualityProfileId;
                    author.AudiobookMetadataProfileId = importResource.MetadataProfileId;
                }
                else
                {
                    author.EbookRootFolderPath = importResource.RootFolder;
                    author.EbookQualityProfileId = importResource.QualityProfileId;
                    author.EbookMetadataProfileId = importResource.MetadataProfileId;
                }

                bookToAdd.Author = author;

                if (editionId != "0")
                {
                    var edition = new Edition
                    {
                        ForeignEditionId = $"{editionProvider}:{editionId}",
                        Monitored = true
                    };
                    bookToAdd.Editions = new List<Edition> { edition };
                }

                EditionPinPolicy.MarkSelectionAsAutomatic(bookToAdd, bookToAdd.Editions);

                _logger.Debug("[V1-BOOK-IMPORT] Calling AddBookService with monitor mode: {0}, mediaType: {1}",
                    monitorMode, bookMediaType);

                var addedBook = await _addBookService.AddBook(bookToAdd);
                EnsureBookCover(addedBook.Id, "import");

                if (authorMonitoring?.SearchForMissing == true)
                {
                    _commandQueueManager.Push(new BookSearchCommand
                    {
                        BookIds = new List<int> { addedBook.Id }
                    });
                }

                _logger.Info("[V1-BOOK-IMPORT] Successfully added book with ID: {0}", addedBook.Id);

                return Created("", new { id = addedBook.Id });
            }
            catch (ValidationException ex)
            {
                _logger.Error(ex, "[V1-BOOK-IMPORT] Validation error");
                throw;
            }
            catch (Exception ex)
            {
                // Not a validation problem: let the error pipeline answer with a server error so callers
                // can tell a crash from a rejected request.
                _logger.Error(ex, "[V1-BOOK-IMPORT] Unexpected error during import");
                throw;
            }
        }

        [HttpGet]
        public List<BookResource> GetBooks([FromQuery] int? authorId,
            [FromQuery] List<int> bookIds,
            [FromQuery] string bookId,
            [FromQuery] string titleSlug = null,
            [FromQuery] bool includeAllAuthorBooks = false,
            [FromQuery] string mediaType = null,
            [FromQuery] bool? monitored = null,
            [FromQuery] string include = null,
            [FromQuery] int? offset = null,
            [FromQuery] int? limit = null)
        {
            _logger.DebugSafe("[API-PERFORMANCE] GetBooks called: authorId={0}, bookIds.Count={1}, bookId='{2}', titleSlug='{3}', mediaType='{4}', monitored={5}",
                authorId, bookIds?.Count ?? 0, bookId, titleSlug, mediaType, monitored);

            mediaType ??= HttpContext?.GetReadarrFacadeContext()?.MediaType;

            bookIds ??= new List<int>();

            if (!authorId.HasValue && !bookIds.Any() && bookId.IsNullOrWhiteSpace() && titleSlug.IsNullOrWhiteSpace())
            {
                _logger.Warn("[API-PERFORMANCE] Taking general book path without an author/book filter.");

                var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
                var normalizedMediaType = parsedMediaType.HasValue ? MediaTypeParameterParser.ToApiValue(parsedMediaType.Value) : null;

                // Load lean display/sync books: monitored edition only, no book files.
                var allBooks = _bookService.GetBooksForDisplay(authorId: null, mediaType: normalizedMediaType);
                var authors = _authorService.GetAllAuthors().ToDictionary(x => x.Id);

                var validBooks = new List<Book>(allBooks.Count);
                foreach (var book in allBooks)
                {
                    if (authors.TryGetValue(book.AuthorId, out var author))
                    {
                        book.Author = author;
                        validBooks.Add(book);
                    }
                    else
                    {
                        _logger.Warn("Book '{0}' (ID: {1}) references missing author ID: {2}",
                            book.Title, book.Id, book.AuthorId);
                    }
                }

                // General /api/v1/book is a sync/index endpoint. Metadata profile pruning is enforced
                // at hydrate/refresh time; doing it again here would force all-edition loading.
                var filteredBooks = validBooks;
                var statsByBookId = BuildBookStatisticsById(GetAuthorStatistics(normalizedMediaType));

                // Apply monitored filter if requested.
                // Monitored view: show monitored books plus physical copies with files.
                // Unmonitored view: show all lean books.
                if (monitored.HasValue)
                {
                    if (monitored.Value)
                    {
                        if (parsedMediaType == BookMediaType.Audiobook)
                        {
                            filteredBooks = filteredBooks.Where(b => b.AudiobookMonitored || HasFiles(b, statsByBookId)).ToList();
                        }
                        else if (parsedMediaType == BookMediaType.Ebook)
                        {
                            filteredBooks = filteredBooks.Where(b => b.EbookMonitored || HasFiles(b, statsByBookId)).ToList();
                        }
                        else
                        {
                            filteredBooks = filteredBooks.Where(b => b.IsMonitored() || HasFiles(b, statsByBookId)).ToList();
                        }

                        _logger.DebugSafe("[API-DEBUG] Monitored view for {0} (monitored OR has files): {1} books", mediaType ?? "general", filteredBooks.Count);
                    }
                    else
                    {
                        _logger.Debug("[API-DEBUG] All view (all books): {0} books", filteredBooks.Count);
                    }
                }

                if (BookListPaging.IsRequested(offset, limit))
                {
                    Response.Headers["X-Total-Count"] = filteredBooks.Count.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                    filteredBooks = BookListPaging.Slice(filteredBooks, offset, limit, b => b.Id);
                }

                return MapToResource(filteredBooks,
                    IncludeRequested(include, "author"),
                    statsByBookId,
                    includeOverview: IncludeRequested(include, "overview"),
                    includeLinks: HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true ||
                        IncludeRequested(include, "links"));
            }

            if (authorId.HasValue)
            {
                _logger.DebugSafe("[API-PERFORMANCE] Taking AUTHOR-SPECIFIC PATH for authorId={0}, mediaType='{1}'", authorId.Value, mediaType);

                var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
                var normalizedMediaType = parsedMediaType.HasValue ? MediaTypeParameterParser.ToApiValue(parsedMediaType.Value) : null;

                // Use the new display logic that handles multiple instances with media type filtering
                var displayBooks = _bookService.GetBooksForDisplay(authorId, normalizedMediaType);

                var author = _authorService.GetAuthor(authorId.Value);
                var editions = _editionService.GetEditionsByAuthor(authorId.Value)
                    .GroupBy(x => x.BookId)
                    .ToDictionary(x => x.Key, y => y.ToList());

                foreach (var book in displayBooks)
                {
                    book.Author = author;
                }

                var filteredBooks = displayBooks;

                _logger.Debug("[API-DEBUG] GetBooksForDisplay returned {0} books", displayBooks.Count);

                // Log detailed book info before monitoring filter
                _logger.Debug("[API-DEBUG] Before monitoring filter - monitored param: {0}", monitored);
                foreach (var book in filteredBooks.Take(5)) // Log first 5 books
                {
                    _logger.Debug("[API-DEBUG] Book '{0}' (ID: {1}) - AudiobookMonitored: {2}, EbookMonitored: {3}, MediaType: {4}",
                        book.Title, book.Id, book.AudiobookMonitored, book.EbookMonitored, book.MediaType);
                }

                // Apply monitored filter if requested
                // Monitored view: Show monitored books + all physical copies (books with files)
                // Unmonitored view: show all local book rows.
                if (monitored.HasValue)
                {
                    if (monitored.Value)
                    {
                        // Monitored toggle ON: Show monitored books OR books with files
                        // Check media-type specific monitoring based on current context
                        if (parsedMediaType == BookMediaType.Audiobook)
                        {
                            filteredBooks = filteredBooks.Where(b => b.AudiobookMonitored || b.Editions?.Any(e => e.BookFiles?.Any() == true) == true).ToList();
                        }
                        else if (parsedMediaType == BookMediaType.Ebook)
                        {
                            filteredBooks = filteredBooks.Where(b => b.EbookMonitored || b.Editions?.Any(e => e.BookFiles?.Any() == true) == true).ToList();
                        }
                        else
                        {
                            // No mediaType specified: check either media-type monitoring
                            filteredBooks = filteredBooks.Where(b => b.IsMonitored() || b.Editions?.Any(e => e.BookFiles?.Any() == true) == true).ToList();
                        }
                        _logger.DebugSafe("[API-DEBUG] Monitored view for {0} (monitored OR has files): {1} books", mediaType ?? "general", filteredBooks.Count);
                    }
                    else
                    {
                        // All toggle OFF: Show ALL books (no additional filtering)
                        _logger.Debug("[API-DEBUG] All view (all books): {0} books", filteredBooks.Count);
                    }
                }

                var resources = MapToResource(filteredBooks, false);

                // Ensure narrator search can work client-side even when the book-level Narrator display is gated.
                // Use the full edition set for each book, so unmonitored editions
                // still contribute to AvailableNarrators.
                try
                {
                    foreach (var resource in resources)
                    {
                        if (resource == null)
                        {
                            continue;
                        }

                        if (!editions.TryGetValue(resource.Id, out var bookEditions) || bookEditions == null)
                        {
                            continue;
                        }

                        var narrators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var edition in bookEditions)
                        {
                            if (edition == null)
                            {
                                continue;
                            }

                            if (edition.NarratorNames != null)
                            {
                                foreach (var name in edition.NarratorNames)
                                {
                                    if (!string.IsNullOrWhiteSpace(name))
                                    {
                                        narrators.Add(name.Trim());
                                    }
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(edition.Narrator))
                            {
                                narrators.Add(edition.Narrator.Trim());
                            }
                        }

                        resource.AvailableNarrators = narrators.Count > 0
                            ? narrators.OrderBy(x => x).ToList()
                            : new List<string>();
                    }
                }
                catch
                {
                    // Best-effort only; the endpoint should still succeed if narrator aggregation fails.
                }

                return resources;
            }

            if (titleSlug.IsNotNullOrWhiteSpace())
            {
                var titleSlugBooks = ResolveTitleSlugQuery(titleSlug, mediaType);

                if (monitored.HasValue && monitored.Value)
                {
                    titleSlugBooks = titleSlugBooks
                        .Where(b => b.IsMonitored() || b.Editions?.Any(e => e.BookFiles?.Any() == true) == true)
                        .ToList();
                }

                return MapToResource(titleSlugBooks, false);
            }

            if (bookId.IsNotNullOrWhiteSpace())
            {
                var book = ResolveBookRouteToken(bookId, mediaType);

                if (book == null)
                {
                    return MapToResource(new List<Book>(), false);
                }

                if (includeAllAuthorBooks)
                {
                    var authorBooks = _bookService.GetBooksByAuthor(book.AuthorId);

                    // Apply monitored filter if requested
                    if (monitored.HasValue)
                    {
                        if (monitored.Value)
                        {
                            // Monitored toggle ON: Show monitored books OR books with files
                            // For monitored filter, check book files via editions
                            authorBooks = authorBooks.Where(b => b.IsMonitored() || b.Editions?.Any(e => e.BookFiles?.Any() == true) == true).ToList();
                        }
                        // Note: if monitored is false, we show ALL books (no additional filtering)
                    }

                    return MapToResource(authorBooks, false);
                }
                else
                {
                    var singleBookList = new List<Book> { book };

                    // Apply monitored filter if requested
                    if (monitored.HasValue && monitored.Value)
                    {
                        // Monitored toggle ON: Show only if monitored OR has files
                        // Check if book has files via editions
                        if (!book.IsMonitored() && !(book.Editions?.Any(e => e.BookFiles?.Any() == true) == true))
                        {
                            singleBookList = new List<Book>(); // Empty list if book doesn't match filter
                        }
                    }
                    // Note: if monitored is false, we show the book regardless

                    return MapToResource(singleBookList, false);
                }
            }

            var requestedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
            var resolvedBookIds = ResolveBookIdsForApi(bookIds, mediaType);
            var bookList = _bookService.GetBooks(resolvedBookIds);

            if (requestedMediaType.HasValue)
            {
                bookList = bookList.Where(book => book.MediaType == requestedMediaType.Value).ToList();
            }

            // Apply monitored filter if requested
            if (monitored.HasValue && monitored.Value)
            {
                // Monitored toggle ON: Show monitored books OR books with files
                // For monitored filter, check book files via editions
                bookList = bookList.Where(b => b.IsMonitored() || b.Editions?.Any(e => e.BookFiles?.Any() == true) == true).ToList();
            }
            // Note: if monitored is false, we show ALL books (no additional filtering)

            return MapToResource(bookList, false);
        }

        private List<Book> ResolveTitleSlugQuery(string titleSlug, string mediaType)
        {
            var facadeContext = HttpContext.GetReadarrFacadeContext();
            var scopedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);

            if (facadeContext != null && titleSlug.IsNotNullOrWhiteSpace() && titleSlug.All(char.IsDigit))
            {
                var provider = facadeContext.IsGoodreads ? "gr" : "hc";
                var providerId = provider + ":" + titleSlug.Trim();
                var matches = scopedMediaType.HasValue
                    ? _bookService.FindAllByProviderId(provider, providerId, scopedMediaType.Value)
                    : new[] { BookMediaType.Audiobook, BookMediaType.Ebook }
                        .SelectMany(type => _bookService.FindAllByProviderId(provider, providerId, type) ?? new List<Book>())
                        .ToList();

                if (matches?.Any() == true)
                {
                    return matches
                        .Where(book => book != null)
                        .GroupBy(book => book.Id)
                        .Select(group => _bookService.GetBook(group.Key))
                        .Where(book => book != null)
                        .OrderBy(book => book.Id)
                        .ToList();
                }
            }

            var routeMatch = ResolveBookRouteToken(titleSlug, mediaType);
            return routeMatch == null ? new List<Book>() : new List<Book> { routeMatch };
        }

        private Book ResolveBookRouteToken(string routeToken, string mediaType)
        {
            if (routeToken.IsNullOrWhiteSpace())
            {
                return null;
            }

            // Chaptarr-native route/API callers use the local numeric row id.
            if (int.TryParse(routeToken, out var bookIdInt))
            {
                var legacyMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
                if (legacyMediaType.HasValue)
                {
                    var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(_bookshelfLegacyIdentityMapService, new[] { bookIdInt }, legacyMediaType);
                    return _bookService.GetBooks(resolvedBookIds)
                        .Where(book => book.MediaType == legacyMediaType.Value)
                        .OrderBy(book => book.Id)
                        .FirstOrDefault();
                }

                if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true &&
                    _bookshelfLegacyIdentityMapService != null)
                {
                    var legacyBookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(new[] { bookIdInt });
                    if (legacyBookIds.Count > 0)
                    {
                        var legacyBooks = _bookService.GetBooks(legacyBookIds)
                            .Where(book => book != null)
                            .ToList();
                        var primaryBookId = SelectLegacyPrimaryBookId(legacyBooks);
                        return primaryBookId > 0 ? _bookService.GetBook(primaryBookId) : null;
                    }
                }

                return _bookService.GetBook(bookIdInt);
            }

            var scopedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);

            // Readarr-compatible browser links use /book/{titleSlug}. Seerr builds those links
            // from the titleSlug returned by POST /api/v1/book, so resolve slugs here without
            // changing the canonical Chaptarr route shape.
            var allBooks = _bookService.GetAllBooks();
            var slugMatches = allBooks
                .Where(book => book != null &&
                               string.Equals(book.TitleSlug, routeToken, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var matches = slugMatches;
            if (scopedMediaType.HasValue)
            {
                matches = matches
                    .Where(book => book.MediaType == scopedMediaType.Value)
                    .ToList();
            }

            if (!matches.Any() && scopedMediaType.HasValue && slugMatches.Any())
            {
                // Seerr stores one titleSlug per Readarr instance, but Chaptarr stores separate
                // audiobook/ebook rows. If the stored slug belongs to the sibling format, resolve
                // through provider identity rather than inventing a string/title match.
                matches = allBooks
                    .Where(candidate => candidate != null &&
                                        candidate.MediaType == scopedMediaType.Value &&
                                        slugMatches.Any(slugMatch => WorkIdMatcher.WorkProviderIdMatches(slugMatch, candidate)))
                    .ToList();

                if (matches.Any())
                {
                    _logger.DebugSafe("[BOOK-SLUG-ROUTE] titleSlug='{0}' belonged to sibling media type. Resolved to {1} {2} row(s) by provider identity.",
                        routeToken,
                        matches.Count,
                        scopedMediaType.Value);
                }
            }

            if (!matches.Any())
            {
                _logger.DebugSafe("[BOOK-SLUG-ROUTE] No book found for titleSlug='{0}', mediaType='{1}'", routeToken, mediaType ?? "any");
                return null;
            }

            if (matches.Count > 1)
            {
                _logger.WarnSafe("[BOOK-SLUG-ROUTE] titleSlug='{0}', mediaType='{1}' matched {2} local books. Choosing the monitored/lowest-id row deterministically: {3}",
                    routeToken,
                    mediaType ?? "any",
                    matches.Count,
                    string.Join(", ", matches.Select(book => $"{book.Id}:{book.MediaType}:{book.Title}")));
            }

            var selected = matches
                .OrderByDescending(book => IsMonitoredForRouteScope(book, scopedMediaType))
                .ThenBy(book => book.Id)
                .FirstOrDefault();

            return selected == null ? null : _bookService.GetBook(selected.Id);
        }

        private List<int> ResolveBookIdsForApi(IEnumerable<int> sourceIds, string mediaType = null)
        {
            var ids = sourceIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            var effectiveMediaType = mediaType ?? HttpContext?.GetReadarrFacadeContext()?.MediaType;
            return HttpContext.ResolveBookshelfLegacyBookIds(
                _bookshelfLegacyIdentityMapService,
                ids,
                MediaTypeParameterParser.ParseOptional(effectiveMediaType));
        }

        private int SelectLegacyPrimaryBookId(IReadOnlyCollection<Book> books)
        {
            if (books == null || books.Count == 0)
            {
                return 0;
            }

            var monitoredBookIds = _editionService.GetEditionsByBook(books.Select(book => book.Id))
                .Where(edition => edition?.Monitored == true)
                .Select(edition => edition.BookId)
                .ToHashSet();

            return SelectLegacyPrimaryBookId(books, monitoredBookIds);
        }

        private static int SelectLegacyPrimaryBookId(IReadOnlyCollection<Book> books, IReadOnlySet<int> monitoredBookIds)
        {
            var monitored = books.Where(book => monitoredBookIds.Contains(book.Id)).ToList();
            if (monitored.Count > 0)
            {
                return monitored.OrderBy(book => book.MediaType).ThenBy(book => book.Id).First().Id;
            }

            return books
                .OrderByDescending(book => book.HasFiles)
                .ThenBy(book => book.MediaType)
                .ThenBy(book => book.Id)
                .First()
                .Id;
        }

        private Book ResolveBookForApi(int sourceId, string mediaType = null)
        {
            var resolvedIds = ResolveBookIdsForApi(new[] { sourceId }, mediaType);
            return resolvedIds.Count == 0 ? null : _bookService.GetBook(resolvedIds[0]);
        }

        private static bool IsMonitoredForRouteScope(Book book, BookMediaType? mediaType)
        {
            if (book == null)
            {
                return false;
            }

            if (mediaType == BookMediaType.Audiobook)
            {
                return book.AudiobookMonitored;
            }

            if (mediaType == BookMediaType.Ebook)
            {
                return book.EbookMonitored;
            }

            return book.IsMonitored();
        }

        [HttpGet("{bookId:int}/related-formats")]
        public ActionResult<BookRelatedFormatsResource> GetRelatedFormats(int bookId)
        {
            var book = ResolveBookForApi(bookId);
            if (book == null)
            {
                return NotFound();
            }

            var relatedFormats = book.AuthorId > 0
                ? _bookService.GetBooksByAuthor(book.AuthorId)
                    .Where(candidate =>
                        candidate != null &&
                        candidate.Id != book.Id &&
                        candidate.MediaType != book.MediaType &&
                        WorkIdMatcher.WorkProviderIdMatches(book, candidate))
                    .OrderBy(candidate => candidate.MediaType)
                    .ThenBy(candidate => candidate.Id)
                    .Select(candidate => new BookRelatedFormatResource
                    {
                        BookId = candidate.Id,
                        MediaType = ToMediaTypeName(candidate.MediaType),
                        Title = candidate.Title,
                        Narrator = candidate.Narrator,
                        Disambiguation = candidate.PublicationYear?.ToString() ?? candidate.Publisher
                    })
                    .ToList()
                : new List<BookRelatedFormatResource>();

            return new BookRelatedFormatsResource { Formats = relatedFormats };
        }

        [HttpGet("{bookId:int}/siblings")]
        public ActionResult<BookSiblingDeleteInfoResource> GetBookSiblings(int bookId)
        {
            var book = ResolveBookForApi(bookId);
            if (book == null)
            {
                return NotFound();
            }

            if (book.AuthorId <= 0)
            {
                var currentFiles = _mediaFileService?.GetFilesByBooks(new List<int> { book.Id }) ?? new List<NzbDrone.Core.MediaFiles.BookFile>();

                return new BookSiblingDeleteInfoResource
                {
                    SiblingMediaType = ToMediaTypeName(GetOppositeMediaType(book.MediaType)),
                    CurrentBook = BuildBookDeleteDetail(book, currentFiles)
                };
            }

            var siblings = _bookService.GetBooksByAuthor(book.AuthorId)
                .Where(candidate =>
                    candidate != null &&
                    candidate.Id != book.Id &&
                    WorkIdMatcher.WorkProviderIdMatches(book, candidate))
                .OrderBy(candidate => candidate.Id)
                .ToList();

            var matchedBooks = new[] { book }.Concat(siblings).ToList();
            var matchedBookIds = matchedBooks.Select(candidate => candidate.Id).ToList();
            var filesByBookId = (_mediaFileService?.GetFilesByBooks(matchedBookIds) ?? new List<NzbDrone.Core.MediaFiles.BookFile>())
                .Where(file => file?.Edition != null)
                .GroupBy(file => file.Edition.BookId)
                .ToDictionary(group => group.Key, group => group.OrderBy(file => file.Path).ToList());

            var currentBookDetail = BuildBookDeleteDetail(book, filesByBookId.GetValueOrDefault(book.Id) ?? new List<NzbDrone.Core.MediaFiles.BookFile>());
            var siblingDetails = siblings
                .Select(sibling => BuildBookDeleteDetail(sibling, filesByBookId.GetValueOrDefault(sibling.Id) ?? new List<NzbDrone.Core.MediaFiles.BookFile>()))
                .ToList();
            var statsResource = BuildDeleteStatistics(siblingDetails);

            return new BookSiblingDeleteInfoResource
            {
                SiblingMediaType = GetMatchedSiblingMediaTypeName(book, siblings),
                BookIds = siblings.Select(candidate => candidate.Id).ToList(),
                CurrentBook = currentBookDetail,
                Siblings = siblingDetails,
                Statistics = statsResource,
                AudiobookCount = matchedBooks.Count(candidate => candidate.MediaType == BookMediaType.Audiobook),
                EbookCount = matchedBooks.Count(candidate => candidate.MediaType == BookMediaType.Ebook)
            };
        }

        private static BookSiblingStatisticsResource BuildDeleteStatistics(IEnumerable<BookSiblingDetailResource> details)
        {
            var files = details?
                .SelectMany(detail => detail.Files ?? new List<BookSiblingFileResource>())
                .ToList() ?? new List<BookSiblingFileResource>();

            return new BookSiblingStatisticsResource
            {
                BookFileCount = files.Count,
                SizeOnDisk = files.Sum(file => file.Size)
            };
        }

        private static BookSiblingDetailResource BuildBookDeleteDetail(Book book, List<NzbDrone.Core.MediaFiles.BookFile> files)
        {
            return new BookSiblingDetailResource
            {
                BookId = book.Id,
                MediaType = ToMediaTypeName(book.MediaType),
                Title = book.Title,
                Files = (files ?? new List<NzbDrone.Core.MediaFiles.BookFile>())
                    .Select(file => new BookSiblingFileResource
                    {
                        Path = file.Path,
                        Size = file.Size
                    })
                    .ToList()
            };
        }

        private static string GetMatchedSiblingMediaTypeName(Book book, List<Book> siblings)
        {
            var siblingTypes = siblings?
                .Select(candidate => candidate.MediaType)
                .Distinct()
                .ToList() ?? new List<BookMediaType>();

            if (siblingTypes.Count == 1)
            {
                return ToMediaTypeName(siblingTypes[0]);
            }

            return siblingTypes.Count > 1 ? "mixed" : ToMediaTypeName(GetOppositeMediaType(book.MediaType));
        }

        private static string ToMediaTypeName(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
        }

        private static BookMediaType GetOppositeMediaType(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Ebook ? BookMediaType.Audiobook : BookMediaType.Ebook;
        }

        [RestGetById]
        [Produces("application/json")]
        public override ActionResult<BookResource> GetResourceByIdWithErrorHandler(int id)
        {
            try
            {
                var mediaType = Request.Query["mediaType"].FirstOrDefault() ?? HttpContext?.GetReadarrFacadeContext()?.MediaType;
                var book = ResolveBookRouteToken(id.ToString(global::System.Globalization.CultureInfo.InvariantCulture), mediaType);
                if (book == null)
                {
                    return NotFound();
                }

                return MapToResource(book, true);
            }
            catch (ModelNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Every known edition of a book with its narration details, for clients that let a person
        /// choose a performance. Unknown details stay unknown.
        /// </summary>
        [HttpGet("{id:int}/editions/catalog")]
        public ActionResult<List<EditionCatalogResource>> GetEditionCatalog(int id, [FromQuery] string mediaType = null)
        {
            var book = ResolveBookForApi(id, mediaType);
            if (book == null)
            {
                return NotFound();
            }

            id = book.Id;

            var editions = _editionService.GetEditionsByBook(id);
            _mediaCoverProxy.ProxyRemoteUrls(editions.SelectMany(edition => edition.Images ?? new List<MediaCover>()));

            var ownedEditionIds = (_mediaFileService.GetFilesByBook(id) ?? new List<NzbDrone.Core.MediaFiles.BookFile>())
                .Select(file => file.EditionId)
                .ToHashSet();
            var facadeContext = HttpContext?.GetReadarrFacadeContext();

            var catalog = editions
                .OrderByDescending(edition => edition.Monitored)
                .ThenBy(edition => edition.Title)
                .Select(edition => edition.ToCatalogResource(ownedEditionIds.Contains(edition.Id), facadeContext))
                .ToList();
            return catalog;
        }

        [HttpGet("buckets")]
        public ActionResult<NzbDrone.Core.Books.BookBucketResource> GetBookBuckets(
            [FromQuery] bool includeUnmonitored = true,
            [FromQuery] string sortKey = "title",
            [FromQuery] string sortDirection = "ASC",
            [FromQuery] string mediaType = null,
            [FromQuery] bool? downloaded = null,
            [FromQuery] bool? monitored = null,
            [FromQuery] bool? missing = null,
            [FromQuery] bool? wanted = null)
        {
            _logger.DebugSafe("[API-INFINITE-SCROLL] GetBookBuckets called: includeUnmonitored={0}, sortKey={1}, sortDirection={2}, mediaType={3}, downloaded={4}, monitored={5}, missing={6}, wanted={7}",
                includeUnmonitored, sortKey, sortDirection, mediaType, downloaded, monitored, missing, wanted);
            var normalizedMediaType = MediaTypeParameterParser.NormalizeOptional(mediaType);

            var buckets = _bookService.GetBookBuckets(sortKey, sortDirection, includeUnmonitored, normalizedMediaType, downloaded, monitored, missing, wanted);

            _logger.Debug("[API-INFINITE-SCROLL] Buckets returned: {0} total books, {1} buckets",
                buckets.TotalCount, buckets.Buckets?.Count ?? 0);

            return Ok(buckets);
        }

        [HttpGet("ids")]
        public ActionResult<List<int>> GetBookIds(
            [FromQuery] bool includeUnmonitored = true,
            [FromQuery] string mediaType = null,
            [FromQuery] bool? downloaded = null,
            [FromQuery] bool? monitored = null,
            [FromQuery] bool? missing = null,
            [FromQuery] bool? wanted = null)
        {
            var normalizedMediaType = MediaTypeParameterParser.NormalizeOptional(mediaType);
            return Ok(_bookService.GetBookIds(includeUnmonitored, normalizedMediaType, downloaded, monitored, missing, wanted));
        }

        [HttpGet("paged")]
        public ActionResult<PagedBookApiResource> GetBooksPaged(
            [FromQuery] int offset = 0,
            [FromQuery] int pageSize = 50,
            [FromQuery] string sortKey = "bookshelfTitle",
            [FromQuery] string sortDirection = "ASC",
            [FromQuery] bool includeUnmonitored = false,
            [FromQuery] string mediaType = null,
            [FromQuery] bool? downloaded = null,
            [FromQuery] bool? monitored = null,
            [FromQuery] bool? missing = null,
            [FromQuery] bool? wanted = null,
            [FromQuery] string include = null)
        {
            _logger.DebugSafe("[API-INFINITE-SCROLL] GetBooksPaged called: offset={0}, pageSize={1}, sortKey={2}, sortDirection={3}, includeUnmonitored={4}, mediaType={5}, downloaded={6}, monitored={7}, missing={8}, wanted={9}",
                offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, monitored, missing, wanted);
            var normalizedMediaType = MediaTypeParameterParser.NormalizeOptional(mediaType);

            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                // BookshelfNG clamps these legacy pagination values instead of rejecting them.
                var legacyOffset = Math.Max(0, offset);
                var legacyPageSize = Math.Clamp(pageSize, 1, 200);
                var allBooks = _bookService.GetAllBooks()
                    .Where(book => book != null)
                    .ToList();
                var legacyMappings = _bookshelfLegacyIdentityMapService?.GetMappingsByTargetIds("Book", allBooks.Select(book => book.Id))
                    .GroupBy(mapping => mapping.TargetId)
                    .ToDictionary(group => group.Key, group => group.First())
                    ?? new Dictionary<int, BookshelfLegacyIdentityMapEntry>();
                var monitoredEditionBookIds = _editionService.GetEditionsByBook(allBooks.Select(book => book.Id))
                    .Where(edition => edition?.Monitored == true)
                    .Select(edition => edition.BookId)
                    .ToHashSet();

                var legacyGroups = allBooks
                    .GroupBy(book => legacyMappings.TryGetValue(book.Id, out var mapping)
                        ? (IsLegacy: true, Id: mapping.SourceId)
                        : (IsLegacy: false, Id: book.Id))
                    .Select(group =>
                    {
                        var rows = group.ToList();
                        var primaryBookId = SelectLegacyPrimaryBookId(rows, monitoredEditionBookIds);
                        return new
                        {
                            LegacyId = group.Key.Id,
                            Rows = rows,
                            Primary = rows.FirstOrDefault(book => book.Id == primaryBookId)
                        };
                    })
                    .Where(group => group.Primary != null)
                    .OrderBy(group => group.Primary.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(group => group.LegacyId)
                    .ToList();

                var pageGroups = legacyGroups.Skip(legacyOffset).Take(legacyPageSize).ToList();
                var pageRows = pageGroups.SelectMany(group => group.Rows).ToList();
                var legacyStatsByBookId = BuildBookStatisticsById(GetAuthorStatistics());
                var legacyPagedResource = new PagedBookApiResource
                {
                    Records = MapToResource(
                        pageRows,
                        string.IsNullOrWhiteSpace(include) || IncludeRequested(include, "author"),
                        legacyStatsByBookId,
                        includeOverview: IncludeRequested(include, "overview"),
                        includeLinks: string.IsNullOrWhiteSpace(include) || IncludeRequested(include, "links")),
                    TotalCount = legacyGroups.Count,
                    Offset = legacyOffset,
                    PageSize = legacyPageSize
                };

                return Ok(legacyPagedResource);
            }

            // Native ChaptarrNG paging reports invalid values explicitly.
            if (pageSize <= 0 || pageSize > 1000)
            {
                return BadRequest("Page size must be between 1 and 1000");
            }

            if (offset < 0)
            {
                return BadRequest("Offset must be non-negative");
            }

            var pagedBooks = _bookService.GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, normalizedMediaType, downloaded, monitored, missing, wanted);
            var statsByBookId = BuildBookStatisticsById(GetAuthorStatistics(normalizedMediaType));

            // Convert Book entities to BookResource
            var pagedResource = new PagedBookApiResource
            {
                Records = MapToResource(pagedBooks.Records,
                    string.IsNullOrWhiteSpace(include) || IncludeRequested(include, "author"),
                    statsByBookId,
                    includeOverview: IncludeRequested(include, "overview"),
                    includeLinks: string.IsNullOrWhiteSpace(include) || IncludeRequested(include, "links")),
                TotalCount = pagedBooks.TotalCount,
                Offset = pagedBooks.Offset,
                PageSize = pagedBooks.PageSize
            };

            _logger.Debug("[API-INFINITE-SCROLL] Paged response: {0} records, offset={1}, totalCount={2}",
                pagedResource.Records.Count, pagedResource.Offset, pagedResource.TotalCount);

            return Ok(pagedResource);
        }

        [HttpGet("shelf/authors")]
        public ActionResult<List<BookshelfAuthorCountResource>> GetBookshelfAuthors([FromQuery] string mediaType)
        {
            var includeBothFormats = string.Equals(mediaType?.Trim(), "both", StringComparison.OrdinalIgnoreCase);
            var counts = includeBothFormats
                ? new[] { BookMediaType.Audiobook, BookMediaType.Ebook }
                    .SelectMany(_bookService.GetBookshelfAuthorCounts)
                    .GroupBy(count => count.AuthorId)
                    .Select(group => new AuthorBookCount
                    {
                        AuthorId = group.Key,
                        BookCount = group.Sum(count => count.BookCount)
                    })
                    .ToList()
                : _bookService.GetBookshelfAuthorCounts(MediaTypeParameterParser.ParseRequired(mediaType));

            return counts.Select(count => new BookshelfAuthorCountResource
            {
                AuthorId = count.AuthorId,
                BookCount = count.BookCount
            }).ToList();
        }

        [HttpGet("shelf")]
        public ActionResult<List<BookResource>> GetBookshelfBooks([FromQuery] List<int> authorIds, [FromQuery] string mediaType)
        {
            var includeBothFormats = string.Equals(mediaType?.Trim(), "both", StringComparison.OrdinalIgnoreCase);
            var parsedMediaType = includeBothFormats
                ? (BookMediaType?)null
                : MediaTypeParameterParser.ParseRequired(mediaType);
            var distinctAuthorIds = (authorIds ?? new List<int>()).Distinct().ToList();

            if (distinctAuthorIds.Count == 0 || distinctAuthorIds.Count > MaxBookshelfAuthorIds)
            {
                return BadRequest($"authorIds must contain between 1 and {MaxBookshelfAuthorIds} author IDs");
            }

            var books = includeBothFormats
                ? _bookService.GetBooksForBookshelf(distinctAuthorIds, BookMediaType.Audiobook)
                    .Concat(_bookService.GetBooksForBookshelf(distinctAuthorIds, BookMediaType.Ebook))
                    .ToList()
                : _bookService.GetBooksForBookshelf(distinctAuthorIds, parsedMediaType.Value);
            if (books.Count == 0)
            {
                return new List<BookResource>();
            }

            // The resource mapper reads each book's author; load them once instead of lazily per book.
            var authorsById = _authorService.GetAuthors(distinctAuthorIds).ToDictionary(author => author.Id);
            foreach (var book in books)
            {
                if (authorsById.TryGetValue(book.AuthorId, out var author))
                {
                    book.Author = author;
                }
            }

            // Statistics for just these authors, so a page of shelf rows doesn't compute them for the whole library.
            var authorStatistics = includeBothFormats
                ? _authorStatisticsService.AuthorStatistics(distinctAuthorIds, "audiobook")
                    .Concat(_authorStatisticsService.AuthorStatistics(distinctAuthorIds, "ebook"))
                    .ToList()
                : _authorStatisticsService.AuthorStatistics(
                    distinctAuthorIds,
                    MediaTypeParameterParser.ToApiValue(parsedMediaType.Value));
            var statsByBookId = BuildBookStatisticsById(authorStatistics);

            return MapToResource(books,
                includeAuthor: false,
                statsByBookId,
                includeOverview: false,
                includeLinks: false);
        }

        [HttpGet("{id:int}/overview")]
        public object Overview(int id)
        {
            var book = ResolveBookForApi(id);
            if (book == null)
            {
                return NotFound();
            }

            id = book.Id;
            var monitoredEditions = _editionService.GetEditionsByBook(id).Where(x => x.Monitored).ToList();

            if (!monitoredEditions.Any())
            {
                // No monitored editions, return empty overview
                return new
                {
                    id,
                    overview = string.Empty
                };
            }

            // Handle edge case of multiple monitored editions (should be fixed by housekeeping)
            // For now, prefer audio format editions
            var audioFormats = new[] { "Audible Audio", "Audiobook", "Audio CD", "MP3 CD", "Audio Cassette" };
            var audioEdition = monitoredEditions.FirstOrDefault(e =>
                e.Format != null && audioFormats.Any(af => e.Format.Contains(af, StringComparison.OrdinalIgnoreCase)));

            var selectedEdition = audioEdition ?? monitoredEditions.First();

            return new
            {
                id,
                overview = selectedEdition.Overview ?? string.Empty
            };
        }

				        [RestPostById]
				        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
				        [ProducesResponseType(typeof(PendingBookRequestResource), StatusCodes.Status202Accepted)]
				        [ProducesResponseType(typeof(BookResource), StatusCodes.Status201Created)]
			        public async Task<ActionResult<BookResource>> AddBook([FromQuery] string mediaType, [FromBody] BookResource bookResource)
			        {
			            try
			            {
		                if (_logger.IsDebugEnabled)
		                {
		                    _logger.Debug("[AddBook] Request received");
		                    _logger.DebugSafe("[AddBook] Title: {0}", bookResource?.Title ?? "NULL");
		                    _logger.DebugSafe("[AddBook] MediaType (body): {0}", bookResource?.MediaType ?? "NULL");
		                    _logger.DebugSafe("[AddBook] MediaType (query): {0}", mediaType ?? "NULL");
		                    _logger.DebugSafe("[AddBook] ForeignBookId: {0}", bookResource?.ForeignBookId ?? "NULL");
		                    _logger.DebugSafe("[AddBook] HardcoverBookId: {0}", bookResource?.HardcoverBookId ?? "NULL");
		                    _logger.DebugSafe("[AddBook] GoodreadsBookId: {0}", bookResource?.GoodreadsBookId ?? "NULL");
		                    _logger.Debug("[AddBook] Author.Id: {0}", bookResource?.Author?.Id ?? 0);
			                    _logger.DebugSafe("[AddBook] Author.AuthorName: {0}", bookResource?.Author?.AuthorName ?? "NULL");
			                    _logger.Debug("[AddBook] Author.AudiobookMonitorExisting: {0}", bookResource?.Author?.AudiobookMonitorExisting ?? 0);
			                    _logger.Debug("[AddBook] Author.EbookMonitorExisting: {0}", bookResource?.Author?.EbookMonitorExisting ?? 0);
			                    _logger.Debug("[AddBook] Author.AudiobookMonitored: {0}", bookResource?.Author?.AudiobookMonitored);
			                    _logger.Debug("[AddBook] Author.EbookMonitored: {0}", bookResource?.Author?.EbookMonitored);
		                    _logger.Debug("[AddBook] Editions count: {0}", bookResource?.Editions?.Count ?? 0);
		                }

			                var requestedMediaType = !string.IsNullOrWhiteSpace(mediaType) ? mediaType : bookResource?.MediaType;
                            var facadeContext = HttpContext.GetReadarrFacadeContext();
                            var prefixFailures = GetNativePrefixFailures(bookResource, facadeContext);
                            if (prefixFailures.Any())
                            {
                                throw new ValidationException(prefixFailures);
                            }

			                if (string.IsNullOrWhiteSpace(requestedMediaType))
			                {
                    // Modern unscoped clients may request both format catalogs. A migrated
                    // BookshelfNG client instead maps its one selected edition to one format.
		                    _logger.Debug("[AddBook] No mediaType provided; defaulting to BOTH audiobook and ebook");

                    var legacyMediaType = facadeContext?.IsBookshelfCompatibility == true
                        ? ResolveBookshelfLegacyAddMediaType(bookResource)
                        : (BookMediaType?)null;
                    NormalizeReadarrSingleFields(
                        bookResource,
                        wantAudiobook: legacyMediaType == BookMediaType.Audiobook || !legacyMediaType.HasValue,
                        wantEbook: legacyMediaType == BookMediaType.Ebook || !legacyMediaType.HasValue);
	                    var existingAuthor = TryGetExistingAuthor(bookResource?.Author);

	                    var canCreateAudiobook = HasConfiguredMediaSettings(bookResource?.Author, BookMediaType.Audiobook);
	                    var canCreateEbook = HasConfiguredMediaSettings(bookResource?.Author, BookMediaType.Ebook);

	                    var hasExistingAudiobook = HasConfiguredMediaSettings(existingAuthor, BookMediaType.Audiobook);
	                    var hasExistingEbook = HasConfiguredMediaSettings(existingAuthor, BookMediaType.Ebook);

	                    var shouldAddAudiobook = canCreateAudiobook || hasExistingAudiobook;
	                    var shouldAddEbook = canCreateEbook || hasExistingEbook;

                    if (legacyMediaType == BookMediaType.Audiobook)
                    {
                        shouldAddEbook = false;
                    }
                    else if (legacyMediaType == BookMediaType.Ebook)
                    {
                        shouldAddAudiobook = false;
                    }

	                    if (!shouldAddAudiobook && !shouldAddEbook)
	                    {
	                        return BadRequest("No media type is configured (audiobook/ebook). Configure at least one root folder + quality profile for this instance.");
	                    }

	                    var originalMediaType = bookResource?.MediaType;

			                    NzbDrone.Core.Books.Book firstAdded = null;
			                    var didFirstRefresh = false;
			                    int? pendingId = null;

			                    if (shouldAddAudiobook)
			                    {
			                        bookResource.MediaType = "audiobook";
			                        var model = bookResource.ToModel(facadeContext);
			                        if (IsMissingUpstreamProviderBookId(model))
			                        {
			                            _logger.WarnSafe("[AddBlockedMissingForeignBookId] Blocking AddBook (audiobook) due to missing upstream provider book/work ID. Title='{0}' ForeignBookId='{1}' HardcoverBookId='{2}' GoodreadsBookId='{3}' GoodreadsWorkId='{4}' OpenLibraryWorkId='{5}' GoogleBooksId='{6}' ASIN='{7}' AudibleASIN='{8}'",
			                                bookResource?.Title ?? "NULL",
			                                bookResource?.ForeignBookId ?? "NULL",
			                                bookResource?.HardcoverBookId ?? "NULL",
			                                bookResource?.GoodreadsBookId ?? "NULL",
			                                bookResource?.GoodreadsWorkId ?? "NULL",
			                                bookResource?.OpenLibraryWorkId ?? "NULL",
			                                bookResource?.GoogleBooksId ?? "NULL",
			                                model?.ASIN ?? "NULL",
			                                model?.AudibleASIN ?? "NULL");

			                            return BadRequest("Cannot add book: missing upstream provider book/work ID (Hardcover/Goodreads/OpenLibrary/GoogleBooks).");
			                        }

					                        try
					                        {
                                            firstAdded = await AddBookWithQueuePolicy(
                                                model,
                                                doRefresh: true,
                                                queueIfUnavailable: facadeContext?.IsBookshelfCompatibility != true);
					                            EnsureBookCover(firstAdded.Id, "create");
					                            didFirstRefresh = true;
					                        }
					                        catch (PendingBookRequestException ex)
					                        {
					                            pendingId = ex.PendingId;
					                        }
			                    }

			                    if (shouldAddEbook)
			                    {
			                        bookResource.MediaType = "ebook";
			                        var model = bookResource.ToModel(facadeContext);
			                        if (IsMissingUpstreamProviderBookId(model))
			                        {
			                            _logger.WarnSafe("[AddBlockedMissingForeignBookId] Blocking AddBook (ebook) due to missing upstream provider book/work ID. Title='{0}' ForeignBookId='{1}' HardcoverBookId='{2}' GoodreadsBookId='{3}' GoodreadsWorkId='{4}' OpenLibraryWorkId='{5}' GoogleBooksId='{6}' ASIN='{7}' AudibleASIN='{8}'",
			                                bookResource?.Title ?? "NULL",
			                                bookResource?.ForeignBookId ?? "NULL",
			                                bookResource?.HardcoverBookId ?? "NULL",
			                                bookResource?.GoodreadsBookId ?? "NULL",
			                                bookResource?.GoodreadsWorkId ?? "NULL",
			                                bookResource?.OpenLibraryWorkId ?? "NULL",
			                                bookResource?.GoogleBooksId ?? "NULL",
			                                model?.ASIN ?? "NULL",
			                                model?.AudibleASIN ?? "NULL");

			                            return BadRequest("Cannot add book: missing upstream provider book/work ID (Hardcover/Goodreads/OpenLibrary/GoogleBooks).");
			                        }

					                        try
					                        {
                                            var ebook = await AddBookWithQueuePolicy(
                                                model,
                                                doRefresh: !didFirstRefresh,
                                                queueIfUnavailable: facadeContext?.IsBookshelfCompatibility != true);
					                            EnsureBookCover(ebook.Id, "create");
					                            firstAdded ??= ebook;
					                        }
					                        catch (PendingBookRequestException ex)
					                        {
					                            pendingId ??= ex.PendingId;
					                        }
				                    }

	                    bookResource.MediaType = originalMediaType;

			                    if (pendingId.HasValue)
			                    {
			                        return Accepted(new PendingBookRequestResource
			                        {
			                            PendingId = pendingId.Value,
			                            Message = PendingBookRequestException.UserMessage
			                        });
			                    }

			                    return Created(firstAdded.Id);
	                }

	                if (!string.Equals(requestedMediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
	                    !string.Equals(requestedMediaType, "ebook", StringComparison.OrdinalIgnoreCase))
	                {
	                    return BadRequest($"Invalid mediaType parameter: '{requestedMediaType}'. Valid values are: audiobook, ebook");
	                }

	                var addBookAuthorAmbiguity = GetAuthorProviderAmbiguityResult(ReadarrFacadeProviderIdTranslator.NormalizeBareProviderId(bookResource?.Author?.ForeignAuthorId, facadeContext), "author.foreignAuthorId", "adding book");
	                if (addBookAuthorAmbiguity != null)
	                {
	                    return addBookAuthorAmbiguity;
	                }

	                NormalizeReadarrSingleFields(
	                    bookResource,
	                    wantAudiobook: string.Equals(requestedMediaType, "audiobook", StringComparison.OrdinalIgnoreCase),
	                    wantEbook: string.Equals(requestedMediaType, "ebook", StringComparison.OrdinalIgnoreCase));

			                // Apply query override (if provided) so the model maps to the correct BookMediaType.
			                bookResource.MediaType = requestedMediaType;

			                var modelToAdd = bookResource.ToModel(facadeContext);
			                if (IsMissingUpstreamProviderBookId(modelToAdd))
			                {
			                    _logger.WarnSafe("[AddBlockedMissingForeignBookId] Blocking AddBook due to missing upstream provider book/work ID. Title='{0}' ForeignBookId='{1}' HardcoverBookId='{2}' GoodreadsBookId='{3}' GoodreadsWorkId='{4}' OpenLibraryWorkId='{5}' GoogleBooksId='{6}' ASIN='{7}' AudibleASIN='{8}'",
			                        bookResource?.Title ?? "NULL",
			                        bookResource?.ForeignBookId ?? "NULL",
			                        bookResource?.HardcoverBookId ?? "NULL",
			                        bookResource?.GoodreadsBookId ?? "NULL",
			                        bookResource?.GoodreadsWorkId ?? "NULL",
			                        bookResource?.OpenLibraryWorkId ?? "NULL",
			                        bookResource?.GoogleBooksId ?? "NULL",
			                        modelToAdd?.ASIN ?? "NULL",
			                        modelToAdd?.AudibleASIN ?? "NULL");

			                    return BadRequest("Cannot add book: missing upstream provider book/work ID (Hardcover/Goodreads/OpenLibrary/GoogleBooks).");
			                }

			                var addAmbiguity = GetBookProviderAmbiguityResult(modelToAdd, "book");
			                if (addAmbiguity != null)
			                {
			                    return addAmbiguity;
			                }

                var book = await AddBookWithQueuePolicy(
                    modelToAdd,
                    doRefresh: true,
                    queueIfUnavailable: facadeContext?.IsBookshelfCompatibility != true);
			                EnsureBookCover(book.Id, "create");

			                return Created(book.Id);
				        }
			            catch (PendingBookRequestException ex)
			            {
			                return Accepted(new PendingBookRequestResource
			                {
			                    PendingId = ex.PendingId,
			                    Message = PendingBookRequestException.UserMessage
			                });
			            }
			            catch (AmbiguousLocalBookMatchException ex)
			            {
			                var ambiguity = ProviderAmbiguityHelper.GetBookAmbiguity(
			                    ex.Candidates,
			                    ex.WorkProviderId,
			                    ex.MediaType,
			                    "workProviderId",
			                    _logger,
			                    "adding book");
			                return StatusCode(ProviderAmbiguityHelper.StatusCode, ambiguity);
			            }
			            catch (ValidationException ex)
	            {
	                _logger.Error(ex, "[AddBook] Validation error");
	                foreach (var error in ex.Errors)
	                {
	                    _logger.Error("[AddBook] Validation error: {0} - {1}", error.PropertyName, error.ErrorMessage);
	                }
	                throw;
	            }
	            catch (Exception ex)
	            {
	                _logger.Error(ex, "[AddBook] Unexpected error");
	                throw;
		            }
		        }

        private static bool IsMissingUpstreamProviderBookId(NzbDrone.Core.Books.Book book)
        {
	            if (book == null)
	            {
	                return true;
	            }

	            return string.IsNullOrWhiteSpace(book.HardcoverBookId) &&
	                   string.IsNullOrWhiteSpace(book.GoodreadsWorkId) &&
	                   string.IsNullOrWhiteSpace(book.OpenLibraryWorkId) &&
                   !BookEditionIdentity.GetCanonicalEditionProviderIds(book).Any();
		        }

            private static List<ValidationFailure> GetNativePrefixFailures(BookResource bookResource, ReadarrFacadeContext facadeContext)
            {
                var failures = new List<ValidationFailure>();
                if (bookResource == null || facadeContext != null)
                {
                    return failures;
                }

                AddPrefixFailureIfBare(failures, "foreignBookId", bookResource.ForeignBookId);
                AddPrefixFailureIfBare(failures, "author.foreignAuthorId", bookResource.Author?.ForeignAuthorId);
                AddPrefixFailureIfBare(failures, "foreignEditionId", bookResource.ForeignEditionId, allowZero: true);

                foreach (var edition in bookResource.Editions ?? Enumerable.Empty<EditionResource>())
                {
                    AddPrefixFailureIfBare(failures, "editions.foreignEditionId", edition?.ForeignEditionId, allowZero: true);
                }

                return failures;
            }

            private static void AddPrefixFailureIfBare(List<ValidationFailure> failures, string field, string providerId, bool allowZero = false)
            {
                if (allowZero && string.Equals(providerId?.Trim(), "0", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(providerId, facadeContext: null))
                {
                    failures.Add(new ValidationFailure(field, ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage(field)));
                }
            }

			        private void EnsureBookCover(int bookId, string reason)
			        {
			            if (bookId <= 0)
			            {
			                return;
			            }

			            try
			            {
			                var book = _bookService.GetBook(bookId);
			                _logger.Debug("[CoverTrigger] reason={0} bookId={1}", reason ?? "unknown", bookId);
			                _coverMapper.EnsureBookCovers(book);
			                _eventAggregator?.PublishEvent(new MediaCoversUpdatedEvent(book));
			            }
			            catch (Exception ex)
			            {
			                _logger.Debug(ex, "Failed to ensure book cover for BookId={0} (reason={1})", bookId, reason ?? "unknown");
			            }
			        }

			        private NzbDrone.Core.Books.Author TryGetExistingAuthor(global::Chaptarr.Api.V1.Author.AuthorResource authorResource)
			        {
		            if (authorResource == null)
		            {
		                return null;
		            }

		            if (authorResource.Id > 0)
		            {
		                try
		                {
		                    return _authorService.GetAuthor(authorResource.Id);
		                }
		                catch
		                {
		                    // ignore
		                }
		            }

		            var foreignAuthorId = authorResource.ForeignAuthorId?.Trim();
		            if (string.IsNullOrWhiteSpace(foreignAuthorId))
		            {
		                return null;
		            }

		            var idx = foreignAuthorId.IndexOf(':');
		            if (idx <= 0)
		            {
		                return null;
		            }

		            var prefix = foreignAuthorId.Substring(0, idx).Trim().ToLowerInvariant();
		            var id = foreignAuthorId.Substring(idx + 1).Trim();
		            if (string.IsNullOrWhiteSpace(id))
		            {
		                return null;
		            }

		            // Canonical provider prefixes only. Any long-form/unknown prefix is a contract violation
		            // and should be fixed upstream/client-side rather than tolerated here.
		            var provider = prefix switch
		            {
		                "hc" => "hc",
		                "gr" => "gr",
		                "ol" => "ol",
		                "gb" => "gb",
		                "az" => "az",
		                _ => null
		            };

		            if (provider == null)
		            {
		                return null;
		            }

		            var matches = ProviderAmbiguityHelper.FindAuthorProviderMatches(_authorService, _providerAliasService, provider, id, _logger);
		            return matches.Count == 1 ? matches[0] : null;
		        }

		        private static bool HasConfiguredMediaSettings(NzbDrone.Core.Books.Author author, BookMediaType mediaType)
		        {
		            if (author == null)
		            {
		                return false;
		            }

		            return mediaType == BookMediaType.Audiobook
		                ? author.AudiobookQualityProfileId.HasValue && !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath)
		                : author.EbookQualityProfileId.HasValue && !string.IsNullOrWhiteSpace(author.EbookRootFolderPath);
		        }

        private static BookMediaType ResolveBookshelfLegacyAddMediaType(BookResource bookResource)
        {
            var editions = bookResource?.Editions ?? new List<EditionResource>();
            var selectedEditions = editions.Where(edition => edition?.Monitored == true).ToList();
            if (selectedEditions.Count != 1)
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure(
                        "Editions",
                        "BookshelfNG book additions must select exactly one edition; its format determines the catalog entry to add.")
                });
            }

            // BookshelfNG represented a work once and carried its selected format on
            // the monitored edition. Use that same choice when projecting it into the
            // separate Chaptarr audiobook and ebook catalogs.
            return selectedEditions[0].IsEbook ? BookMediaType.Ebook : BookMediaType.Audiobook;
        }

        private Task<NzbDrone.Core.Books.Book> AddBookWithQueuePolicy(
            NzbDrone.Core.Books.Book book,
            bool doRefresh,
            bool queueIfUnavailable)
        {
            if (_addBookService is IAddBookServiceWithQueueControl queueControlledService)
            {
                return queueControlledService.AddBook(book, doRefresh, queueIfUnavailable);
            }

            return _addBookService.AddBook(book, doRefresh);
        }

	        private static bool HasConfiguredMediaSettings(global::Chaptarr.Api.V1.Author.AuthorResource author, BookMediaType mediaType)
	        {
	            if (author == null)
	            {
	                return false;
		            }

	            return mediaType == BookMediaType.Audiobook
	                ? author.AudiobookQualityProfileId.HasValue && !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath)
	                : author.EbookQualityProfileId.HasValue && !string.IsNullOrWhiteSpace(author.EbookRootFolderPath);
	        }

        private static bool HasFiles(Book book, IReadOnlyDictionary<int, BookStatistics> statsByBookId)
        {
            if (book == null)
            {
                return false;
	            }

	            return book.HasFiles ||
                   (statsByBookId != null &&
                    statsByBookId.TryGetValue(book.Id, out var stats) &&
                    stats?.BookFileCount > 0);
        }

        private static bool IncludeRequested(string include, string token)
        {
            if (string.IsNullOrWhiteSpace(include) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            return include
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Any(x => x.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                          x.Equals(token, StringComparison.OrdinalIgnoreCase));
        }

		        private static bool HasAnyMediaSettings(global::Chaptarr.Api.V1.Author.AuthorResource author, BookMediaType mediaType)
	        {
	            if (author == null)
	            {
	                return false;
	            }

	            return mediaType == BookMediaType.Audiobook
	                ? author.AudiobookQualityProfileId.HasValue ||
	                  author.AudiobookMetadataProfileId.HasValue ||
	                  !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath)
	                : author.EbookQualityProfileId.HasValue ||
	                  author.EbookMetadataProfileId.HasValue ||
	                  !string.IsNullOrWhiteSpace(author.EbookRootFolderPath);
	        }

	        private static MediaTypeSettings GetMediaSettings(RootFolder rootFolder, BookMediaType mediaType)
	        {
	            return mediaType == BookMediaType.Audiobook
	                ? rootFolder?.GetAudiobookSettings()
	                : rootFolder?.GetEbookSettings();
	        }

	        private static bool ShouldFillSiblingFromMixedRoot(global::Chaptarr.Api.V1.Author.AuthorResource author, RootFolder requestedRootFolder, BookMediaType siblingMediaType)
	        {
	            if (requestedRootFolder?.FolderType != FolderType.Mixed ||
	                requestedRootFolder.DefaultSyncMonitoredAcrossFormats != true ||
	                HasAnyMediaSettings(author, siblingMediaType))
	            {
	                return false;
	            }

	            return GetMediaSettings(requestedRootFolder, siblingMediaType)?.QualityProfileId.HasValue == true;
	        }

	        private void NormalizeReadarrSingleFields(BookResource bookResource, bool wantAudiobook, bool wantEbook)
	        {
	            var author = bookResource?.Author;
	            if (author == null)
		            {
		                return;
		            }

		            var rootFolders = _rootFolderService.All() ?? new List<RootFolder>();

		            RootFolder rootFolderByPath = null;
		            if (!string.IsNullOrWhiteSpace(author.RootFolderPath))
		            {
		                rootFolderByPath = _rootFolderService.GetBestRootFolder(author.RootFolderPath, rootFolders);
		            }

		            // If a client sends a single qualityProfileId, map it to the correct media type by profile type.
		            if (author.QualityProfileId.HasValue &&
		                !author.AudiobookQualityProfileId.HasValue &&
		                !author.EbookQualityProfileId.HasValue)
		            {
		                try
		                {
		                    var profile = _qualityProfileService.Get(author.QualityProfileId.Value);
		                    if (profile.ProfileType == ProfileType.Audiobook)
		                    {
		                        author.AudiobookQualityProfileId = author.QualityProfileId;
		                    }
		                    else if (profile.ProfileType == ProfileType.Ebook)
		                    {
		                        author.EbookQualityProfileId = author.QualityProfileId;
		                    }

		                    // Avoid storing a type-specific profile ID in the legacy single field.
		                    author.QualityProfileId = null;
		                }
		                catch
		                {
		                    // ignore and let downstream validation/reporting handle
		                }
		            }

		            // Same idea for metadataProfileId.
		            if (author.MetadataProfileId.HasValue &&
		                !author.AudiobookMetadataProfileId.HasValue &&
		                !author.EbookMetadataProfileId.HasValue)
		            {
		                try
		                {
		                    var profile = _metadataProfileService.Get(author.MetadataProfileId.Value);
		                    if (profile.ProfileType == MetadataProfileType.Audiobook)
		                    {
		                        author.AudiobookMetadataProfileId = author.MetadataProfileId;
		                        author.MetadataProfileId = null;
		                    }
		                    else if (profile.ProfileType == MetadataProfileType.Ebook)
		                    {
		                        author.EbookMetadataProfileId = author.MetadataProfileId;
		                        author.MetadataProfileId = null;
		                    }
		                    // If General, keep it as a legacy fallback.
		                }
		                catch
		                {
		                    // ignore and let downstream validation/reporting handle
		                }
		            }

		            RootFolder audiobookRootFolder = null;
		            if (!string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath))
		            {
		                audiobookRootFolder = _rootFolderService.GetBestRootFolder(author.AudiobookRootFolderPath, rootFolders);
		            }
		            if (audiobookRootFolder == null && rootFolderByPath?.GetAudiobookSettings() != null)
		            {
		                audiobookRootFolder = rootFolderByPath;
		            }
		            audiobookRootFolder ??= rootFolders.FirstOrDefault(rf => rf.GetAudiobookSettings()?.QualityProfileId.HasValue == true);

		            RootFolder ebookRootFolder = null;
		            if (!string.IsNullOrWhiteSpace(author.EbookRootFolderPath))
		            {
		                ebookRootFolder = _rootFolderService.GetBestRootFolder(author.EbookRootFolderPath, rootFolders);
		            }
		            if (ebookRootFolder == null && rootFolderByPath?.GetEbookSettings() != null)
		            {
		                ebookRootFolder = rootFolderByPath;
	            }
	            ebookRootFolder ??= rootFolders.FirstOrDefault(rf => rf.GetEbookSettings()?.QualityProfileId.HasValue == true);

	            var requestedAudiobookRootFolder = !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath)
	                ? _rootFolderService.GetBestRootFolder(author.AudiobookRootFolderPath, rootFolders)
	                : rootFolderByPath;
	            var requestedEbookRootFolder = !string.IsNullOrWhiteSpace(author.EbookRootFolderPath)
	                ? _rootFolderService.GetBestRootFolder(author.EbookRootFolderPath, rootFolders)
	                : rootFolderByPath;

	            var fillAudiobookSettings = wantAudiobook;
	            var fillEbookSettings = wantEbook;

	            if (wantAudiobook &&
	                !wantEbook &&
	                ShouldFillSiblingFromMixedRoot(author, requestedAudiobookRootFolder, BookMediaType.Ebook))
	            {
	                ebookRootFolder = requestedAudiobookRootFolder;
	                fillEbookSettings = true;
	            }

	            if (wantEbook &&
	                !wantAudiobook &&
	                ShouldFillSiblingFromMixedRoot(author, requestedEbookRootFolder, BookMediaType.Audiobook))
	            {
	                audiobookRootFolder = requestedEbookRootFolder;
	                fillAudiobookSettings = true;
	            }

	            // Fill missing per-type profile IDs from the root folder defaults (when requested).
	            if (fillAudiobookSettings)
	            {
	                var settings = audiobookRootFolder?.GetAudiobookSettings();
	                if (!author.AudiobookQualityProfileId.HasValue && settings?.QualityProfileId.HasValue == true)
	                {
		                    author.AudiobookQualityProfileId = settings.QualityProfileId;
		                }
		                if (!author.AudiobookMetadataProfileId.HasValue && settings?.MetadataProfileId.HasValue == true)
		                {
		                    author.AudiobookMetadataProfileId = settings.MetadataProfileId;
	                }
	            }

	            if (fillEbookSettings)
	            {
	                var settings = ebookRootFolder?.GetEbookSettings();
	                if (!author.EbookQualityProfileId.HasValue && settings?.QualityProfileId.HasValue == true)
	                {
		                    author.EbookQualityProfileId = settings.QualityProfileId;
		                }
		                if (!author.EbookMetadataProfileId.HasValue && settings?.MetadataProfileId.HasValue == true)
		                {
		                    author.EbookMetadataProfileId = settings.MetadataProfileId;
		                }
		            }

		            // Only set per-type root folders when that media type is actually configured.
		            if (author.AudiobookQualityProfileId.HasValue)
		            {
		                if (string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath))
		                {
		                    if (!string.IsNullOrWhiteSpace(author.RootFolderPath) && audiobookRootFolder == rootFolderByPath)
		                    {
		                        author.AudiobookRootFolderPath = author.RootFolderPath;
		                    }
		                    else
		                    {
		                        author.AudiobookRootFolderPath = audiobookRootFolder?.Path;
		                    }
		                }
		            }
		            else
		            {
		                author.AudiobookRootFolderPath = null;
		                author.AudiobookMetadataProfileId = null;
		            }

		            if (author.EbookQualityProfileId.HasValue)
		            {
		                if (string.IsNullOrWhiteSpace(author.EbookRootFolderPath))
		                {
		                    if (!string.IsNullOrWhiteSpace(author.RootFolderPath) && ebookRootFolder == rootFolderByPath)
		                    {
		                        author.EbookRootFolderPath = author.RootFolderPath;
		                    }
		                    else
		                    {
		                        author.EbookRootFolderPath = ebookRootFolder?.Path;
		                    }
		                }
		            }
		            else
		            {
		                author.EbookRootFolderPath = null;
		                author.EbookMetadataProfileId = null;
		            }

		            // The legacy Readarr/Seerr single fields are input shims only. At this point they have
		            // been resolved into media-specific settings; leaving RootFolderPath populated lets
		            // AuthorResource.ToModel() expand an ebook-only root back into audiobook state.
		            author.RootFolderPath = null;
		            author.QualityProfileId = null;
		            author.MetadataProfileId = null;
		        }

		        // Readarr/Seerr compatibility: Readarr updates books via PUT /api/v1/book (no id in route)
        // with the book id included in the request body.
        [HttpPut]
        public ActionResult<BookResource> UpdateBookByBody([FromBody] BookResource bookResource)
        {
            return UpdateBookInternal(bookResource, pinExplicitEditionChange: false);
        }

        [RestPutById]
        public ActionResult<BookResource> UpdateBook([FromBody] BookResource bookResource)
        {
            return UpdateBookInternal(bookResource, pinExplicitEditionChange: true);
        }

        private void RestoreProxiedCoverUrls(List<Edition> submitted, List<Edition> stored)
        {
            if (submitted == null)
            {
                return;
            }

            foreach (var edition in submitted.Where(e => e?.Images != null))
            {
                var storedImages = stored?.FirstOrDefault(e => e != null && e.Id == edition.Id)?.Images;
                var unresolved = new List<NzbDrone.Core.MediaCover.MediaCover>();

                foreach (var image in edition.Images.Where(i => i != null && _mediaCoverProxy.IsProxyUrl(i.Url)))
                {
                    if (_mediaCoverProxy.TryResolveProxyUrl(image.Url, out var restored))
                    {
                        image.Url = restored;
                        continue;
                    }

                    var storedUrl = storedImages?.FirstOrDefault(i => i != null && i.CoverType == image.CoverType)?.Url;

                    if (storedUrl.IsNotNullOrWhiteSpace())
                    {
                        image.Url = storedUrl;
                        continue;
                    }

                    unresolved.Add(image);
                }

                if (unresolved.Count > 0)
                {
                    _logger.Debug("Dropping {0} cover(s) on edition {1} whose proxy urls could no longer be resolved", unresolved.Count, edition.Id);
                    edition.Images = edition.Images.Except(unresolved).ToList();
                }
            }
        }

        private ActionResult<BookResource> UpdateBookInternal(BookResource bookResource, bool pinExplicitEditionChange)
        {
            var facadeContext = HttpContext.GetReadarrFacadeContext();
            var sourceBookId = bookResource.Id;
            var prefixFailures = GetNativePrefixFailures(bookResource, facadeContext);
            if (prefixFailures.Any())
            {
                throw new ValidationException(prefixFailures);
            }

            var updateMediaType = MediaTypeParameterParser.ParseOptional(facadeContext?.MediaType ?? bookResource.MediaType);
            if (facadeContext?.IsBookshelfCompatibility == true)
            {
                updateMediaType = ResolveBookshelfLegacyUpdateMediaType(bookResource, sourceBookId);
            }

            var resolvedBookIds = ResolveBookIdsForApi(
                new[] { sourceBookId },
                updateMediaType.HasValue ? MediaTypeParameterParser.ToApiValue(updateMediaType.Value) : null);
            if (resolvedBookIds.Count == 0)
            {
                return NotFound();
            }

            var targetBookId = resolvedBookIds.Count == 1
                ? resolvedBookIds[0]
                : SelectLegacyPrimaryBookId(_bookService.GetBooks(resolvedBookIds));
            if (targetBookId <= 0)
            {
                return NotFound();
            }

            bookResource.Id = targetBookId;
            var book = _bookService.GetBook(bookResource.Id);
            if (book == null)
            {
                return NotFound();
            }

            bookResource.MediaType ??= MediaTypeParameterParser.ToApiValue(book.MediaType);
            if (facadeContext?.IsBookshelfCompatibility == true)
            {
                ResolveBookshelfLegacyEditionIds(bookResource.Editions, book);
            }

            var wasMonitoredForFacadeMediaType = facadeContext != null &&
                                                 book.IsMonitoredForMediaType(facadeContext.MediaType);
            var beforeMonitoredEditionId = GetSingleMonitoredEditionId(book?.Editions);

            var model = bookResource.ToModel(book, facadeContext);
            model.Editions = (model.Editions ?? Enumerable.Empty<Edition>())
                .Where(edition => edition != null && edition.IsEbook == (model.MediaType == BookMediaType.Ebook))
                .ToList();
            foreach (var edition in model.Editions)
            {
                edition.BookId = model.Id;
            }
            RestoreProxiedCoverUrls(model?.Editions, book?.Editions);
            var shouldPinExplicitEditionSelection = ShouldPinExplicitEditionSelection(pinExplicitEditionChange, beforeMonitoredEditionId, model?.Editions);
            var shouldSkipEditionRepair = ShouldSkipEditionRepairForPartialFacadeCompat(pinExplicitEditionChange, facadeContext, beforeMonitoredEditionId, book?.Editions, model?.Editions);

            // Preserve a single submitted edition selection. Only clean up impossible
            // zero/multiple monitored states before persisting.
            if (!shouldSkipEditionRepair)
            {
                _editionSelector.EnsureSingleMonitoredEdition(model.Editions, mediaType: model.MediaType);
            }
            if (shouldPinExplicitEditionSelection)
            {
                model.AnyEditionOk = false;
            }
            var afterMonitoredEditionId = shouldSkipEditionRepair
                ? beforeMonitoredEditionId
                : GetSingleMonitoredEditionId(model?.Editions);

            if (ShouldApplyFacadeSpecificBookMonitoring(
                    facadeContext,
                    model.MediaType,
                    wasMonitoredForFacadeMediaType,
                    bookResource.Monitored))
            {
                // A facade request changes only its media side. The generic update would overwrite
                // the other side; submitted edition selection is persisted independently below.
                _authorService.EnsureMediaTypeMonitoring(model.AuthorId, facadeContext.MediaType);
                _bookService.SetMonitoredForMediaType(new[] { model.Id }, facadeContext.MediaType, true);
            }
            else
            {
                _bookService.UpdateBook(model);
            }

            _editionService.UpdateMany(model.Editions);

            if (facadeContext?.IsBookshelfCompatibility == true &&
                !bookResource.AudiobookMonitored.HasValue &&
                !bookResource.EbookMonitored.HasValue)
            {
                var sourceWorkIds = _bookshelfLegacyIdentityMapService?.ResolveBookIds(new[] { sourceBookId })
                    ?? new List<int> { targetBookId };
                _bookService.SetMonitored(sourceWorkIds, bookResource.Monitored);
            }

            if (beforeMonitoredEditionId != afterMonitoredEditionId)
            {
                EnsureBookCover(model.Id, "editionSwitch");
            }

            BroadcastResourceChange(ModelAction.Updated, model.Id);

            if (facadeContext?.IsBookshelfCompatibility == true)
            {
                var legacyResource = MapToResource(_bookService.GetBook(model.Id), true);
                return AcceptedAtAction(
                    nameof(GetResourceByIdWithErrorHandler),
                    new { id = sourceBookId },
                    legacyResource);
            }

            return Accepted(model.Id);
        }

        // A book can reach an update with more than one monitored edition (e.g. an author add
        // pre-selected one and a request added another); the update repairs that state, so
        // reading the previous selection must not throw. Null unless exactly one is monitored.
        internal static int? GetSingleMonitoredEditionId(IEnumerable<Edition> editions)
        {
            var monitored = editions?.Where(e => e != null && e.Monitored).Take(2).ToList();
            return monitored?.Count == 1 ? monitored[0].Id : null;
        }

        private BookMediaType? ResolveBookshelfLegacyUpdateMediaType(BookResource resource, int sourceBookId)
        {
            var selectedEditions = resource?.Editions?.Where(edition => edition?.Monitored == true).ToList()
                ?? new List<EditionResource>();
            if (selectedEditions.Count == 1)
            {
                return selectedEditions[0].IsEbook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            }

            var targetBookIds = _bookshelfLegacyIdentityMapService?.ResolveBookIds(new[] { sourceBookId })
                ?? new List<int> { sourceBookId };
            var targetBooks = _bookService.GetBooks(targetBookIds)
                .Where(book => book != null)
                .ToList();
            if (targetBooks.Count == 0)
            {
                return null;
            }

            var primaryBookId = SelectLegacyPrimaryBookId(targetBooks);
            return targetBooks.FirstOrDefault(book => book.Id == primaryBookId)?.MediaType;
        }

        private void ResolveBookshelfLegacyEditionIds(IEnumerable<EditionResource> editions, Book targetBook)
        {
            var sourceEditions = editions?.Where(edition => edition != null && edition.Id > 0).ToList();
            if (sourceEditions == null || sourceEditions.Count == 0 || _bookshelfLegacyIdentityMapService == null)
            {
                return;
            }

            var mappings = _bookshelfLegacyIdentityMapService.GetMappings("Edition", sourceEditions.Select(edition => edition.Id));
            foreach (var edition in sourceEditions)
            {
                var mapping = mappings.FirstOrDefault(candidate =>
                    candidate.SourceId == edition.Id &&
                    (!candidate.MediaType.HasValue || candidate.MediaType.Value == (int)targetBook.MediaType));
                if (mapping != null)
                {
                    edition.Id = mapping.TargetId;
                }

                edition.BookId = targetBook.Id;
            }
        }

        internal static bool ShouldApplyFacadeSpecificBookMonitoring(
            ReadarrFacadeContext facadeContext,
            BookMediaType bookMediaType,
            bool wasMonitored,
            bool requestedMonitored)
        {
            if (facadeContext == null || wasMonitored || !requestedMonitored)
            {
                return false;
            }

            return (bookMediaType == BookMediaType.Audiobook &&
                    string.Equals(facadeContext.MediaType, "audiobook", StringComparison.OrdinalIgnoreCase)) ||
                   (bookMediaType == BookMediaType.Ebook &&
                    string.Equals(facadeContext.MediaType, "ebook", StringComparison.OrdinalIgnoreCase));
        }

        internal static bool ShouldPinExplicitEditionSelection(bool pinExplicitEditionChange, int? beforeMonitoredEditionId, IEnumerable<Edition> submittedEditions)
        {
            return pinExplicitEditionChange && IsExplicitEditionSelectionChange(beforeMonitoredEditionId, submittedEditions);
        }

        internal static bool ShouldSkipEditionRepairForPartialFacadeCompat(bool pinExplicitEditionChange, ReadarrFacadeContext facadeContext, int? beforeMonitoredEditionId, IEnumerable<Edition> storedEditions, IEnumerable<Edition> submittedEditions)
        {
            if (pinExplicitEditionChange || facadeContext == null || !beforeMonitoredEditionId.HasValue)
            {
                return false;
            }

            var storedEditionIds = new HashSet<int>((storedEditions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null && e.Id > 0)
                .Select(e => e.Id));
            var submitted = (submittedEditions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null)
                .ToList();

            if (storedEditionIds.Count == 0 || submitted.Count == 0)
            {
                return false;
            }

            var submittedEditionIds = new HashSet<int>(submitted
                .Where(e => e.Id > 0)
                .Select(e => e.Id));

            return submittedEditionIds.Count > 0 &&
                   submittedEditionIds.All(storedEditionIds.Contains) &&
                   submittedEditionIds.Count < storedEditionIds.Count &&
                   !submittedEditionIds.Contains(beforeMonitoredEditionId.Value) &&
                   !submitted.Any(e => e.Monitored);
        }

        internal static bool IsExplicitEditionSelectionChange(int? beforeMonitoredEditionId, IEnumerable<Edition> submittedEditions)
        {
            var submittedMonitoredEditionIds = submittedEditions?
                .Where(e => e != null && e.Monitored)
                .Select(e => e.Id)
                .OrderBy(id => id)
                .ToList() ?? new List<int>();

            return submittedMonitoredEditionIds.Count == 1 &&
                   (!beforeMonitoredEditionId.HasValue || beforeMonitoredEditionId.Value != submittedMonitoredEditionIds[0]);
        }

        [RestDeleteById]
        public void DeleteBook(int id, bool deleteFiles = false, bool addImportListExclusion = false, bool applyToBothFormats = false, string mediaType = null)
        {
            var resolvedBookIds = ResolveBookIdsForApi(new[] { id }, mediaType);
            if (applyToBothFormats && resolvedBookIds.Count > 0)
            {
                _bookService.DeleteBook(resolvedBookIds[0], deleteFiles, addImportListExclusion, applyToBothFormats: true);
                return;
            }

            foreach (var resolvedBookId in resolvedBookIds)
            {
                _bookService.DeleteBook(resolvedBookId, deleteFiles, addImportListExclusion);
            }
        }

        [HttpPost("{id}/downloadmedia")]
        public IActionResult DownloadBookMedia(int id, bool forceDownload = false, string mediaType = null)
        {
            var bookIds = ResolveBookIdsForApi(new[] { id }, mediaType);

            if (bookIds.Count == 0)
            {
                return NotFound();
            }

            _commandQueueManager.Push(new DownloadBookMediaCommand(bookIds[0], forceDownload)
            {
                BookIds = bookIds
            });

            return Accepted();
        }

        [HttpPut("monitor")]
        public IActionResult SetBooksMonitored([FromBody] BooksMonitoredResource resource, [FromQuery] string mediaType = null)
        {
            // Native Chaptarr requests use target row IDs. Only the persisted legacy
            // compatibility context interprets source IDs from the old BookshelfNG address.
            var effectiveMediaType = mediaType ?? HttpContext?.GetReadarrFacadeContext()?.MediaType;
            var sourceBookIds = resource?.BookIds ?? new List<int>();
            var bookIds = HttpContext.ResolveBookshelfLegacyBookIds(
                _bookshelfLegacyIdentityMapService,
                sourceBookIds,
                MediaTypeParameterParser.ParseOptional(effectiveMediaType));
            _bookService.SetMonitored(bookIds, resource?.Monitored ?? false);

            return Accepted(MapToResource(_bookService.GetBooks(bookIds), false));
        }

        [HttpPost("{id}/editions/wanted")]
        public ActionResult<BookResource> AddWantedEdition(int id, [FromBody] AddWantedEditionRequest request, string mediaType = null)
        {
            if (request == null || request.EditionId <= 0)
            {
                return BadRequest("EditionId is required");
            }

	            try
	            {
	                var book = ResolveBookForApi(id, mediaType);
	                if (book == null)
	                {
	                    return NotFound();
	                }

	                var wantedBook = _bookService.AddWantedEdition(book.Id, request.EditionId, request.AsNewVariant);
	                EnsureBookCover(wantedBook.Id, "wantedEdition");

	                if (request.SearchForNewBook)
	                {
	                    _commandQueueManager.Push(new BookSearchCommand(new List<int> { wantedBook.Id }));
	                }

	                // Ensure the UI updates immediately without requiring a full page refresh.
	                // SignalR's book handler treats "updated" as upsert (adds if missing).
	                BroadcastResourceChange(ModelAction.Updated, wantedBook.Id);

	                return MapToResource(_bookService.GetBook(wantedBook.Id), true);
	            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [NonAction]
        public void Handle(BookGrabbedEvent message)
        {
            foreach (var book in message.Book.GetBooksMatchingReleaseMediaType())
            {
                var resource = book.ToResource();
                resource.Grabbed = true;

                BroadcastResourceChange(ModelAction.Updated, resource);
            }
        }

        [NonAction]
        public void Handle(BookEditedEvent message)
        {
            QueueBookEditBroadcast(message.Book.Id);
        }

        [NonAction]
        public void Handle(BookUpdatedEvent message)
        {
            // See Handle(BookEditedEvent): broadcast a fully loaded resource.
            BroadcastResourceChange(ModelAction.Updated, message.Book.Id);
        }

        [NonAction]
        public void Handle(BookDeletedEvent message)
        {
            BroadcastResourceChange(ModelAction.Deleted, message.Book.ToResource());
        }

	        [NonAction]
	        public void Handle(BookImportedEvent message)
	        {
	            if (IsImportActive())
	            {
	                return;
	            }

                var bookId = message.Book?.Id
                    ?? message.ImportedBooks?
                        .Select(file => file?.Edition?.BookId ?? file?.Edition?.Book?.Id ?? 0)
                        .FirstOrDefault(id => id > 0)
                    ?? 0;

                if (bookId > 0)
                {
                    BroadcastResourceChange(ModelAction.Updated, bookId);
                }
	        }

        [NonAction]
        public void Handle(TrackImportedEvent message)
        {
            var bookId = message.BookInfo?.Book?.Id
                ?? message.ImportedBook?.Edition?.BookId
                ?? message.ImportedBook?.Edition?.Book?.Id
                ?? 0;

            if (bookId > 0)
            {
                BroadcastResourceChange(ModelAction.Updated, bookId);
            }
        }

	        [NonAction]
	        public void Handle(BookFileDeletedEvent message)
	        {
	            if (IsImportActive())
	            {
	                return;
	            }

	            if (message.Reason == DeleteMediaFileReason.Upgrade)
	            {
	                return;
	            }

            BroadcastResourceChange(ModelAction.Updated, MapToResource(message.BookFile.Edition.Book, true));
        }

	        [NonAction]
	        public void Handle(ImportStageProgressEvent message)
	        {
	            if (!message.CommandId.HasValue)
	            {
	                return;
	            }

	            lock (_importStateLock)
	            {
	                if (message.Stage == ImportStage.ImportComplete)
	                {
	                    _activeImportCommands.Remove(message.CommandId.Value);
	                }
	                else
	                {
	                    _activeImportCommands.Add(message.CommandId.Value);
	                }
	            }

		            if (message.Stage == ImportStage.ImportComplete)
		            {
		                // Avoid expensive per-file broadcasts during import; resync once when the import finishes.
		                BroadcastResourceChange(ModelAction.Sync);
		            }
		        }

	        [NonAction]
	        public void Handle(CommandExecutedEvent message)
	        {
	            var commandId = message?.Command?.Id ?? 0;
	            if (commandId <= 0)
	            {
	                return;
	            }

	            var shouldSync = false;
	            lock (_importStateLock)
	            {
	                if (_activeImportCommands.Remove(commandId))
	                {
	                    shouldSync = _activeImportCommands.Count == 0;
	                }
	            }

	            if (shouldSync)
	            {
	                BroadcastResourceChange(ModelAction.Sync);
	            }
	        }
	    }
	}
