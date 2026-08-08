using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Api.V1.ProviderIds;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
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
        protected readonly IAuthorService _authorService;
        protected readonly IEditionService _editionService;
	        protected readonly IAddBookService _addBookService;
	        private readonly IEditionSelector _editionSelector;
	        protected readonly IManageCommandQueue _commandQueueManager;
	        protected readonly IMetadataProfileService _metadataProfileService;
	        protected readonly IQualityProfileService _qualityProfileService;
	        protected readonly IRootFolderService _rootFolderService;
	        private readonly IMediaFileService _mediaFileService;
	        private readonly IEventAggregator _eventAggregator;
            private readonly IProviderAliasService _providerAliasService;
	        private readonly Logger _logger;
	        private readonly object _importStateLock = new object();
	        private readonly HashSet<int> _activeImportCommands = new HashSet<int>();

	        public BookController(IAuthorService authorService,
	                          IBookService bookService,
		                          IAddBookService addBookService,
	                          IEditionService editionService,
	                          IEditionSelector editionSelector,
	                          ISeriesBookLinkService seriesBookLinkService,
	                          IAuthorStatisticsService authorStatisticsService,
                          IMediaFileService mediaFileService,
                          IMapCoversToLocal coverMapper,
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
                          IProviderAliasService providerAliasService = null)

        : base(bookService, seriesBookLinkService, authorStatisticsService, coverMapper, upgradableSpecification, signalRBroadcaster)
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
            _eventAggregator = eventAggregator;
            _logger = logger;
            _providerAliasService = providerAliasService;

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
	            PostValidator.RuleFor(s => s.Author.AudiobookMonitorExisting)
	                         .Must(v => !v.HasValue || v.Value is 0 or 1 or 2)
	                         .When(s => s.Author != null)
	                         .WithMessage("AudiobookMonitorExisting must be 0 (None), 1 (All), or 2 (Selected)");
	            PostValidator.RuleFor(s => s.Author.EbookMonitorExisting)
	                         .Must(v => !v.HasValue || v.Value is 0 or 1 or 2)
	                         .When(s => s.Author != null)
	                         .WithMessage("EbookMonitorExisting must be 0 (None), 1 (All), or 2 (Selected)");
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

	        [HttpPost("import")]
	        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
	        public async Task<ActionResult> ImportBook([FromBody] BookImportResource importResource)
	        {
            try
            {
                _logger.Debug("[V1-BOOK-IMPORT] Starting import with foreignBookId: {0}, foreignAuthorId: {1}, mediaType: {2}",
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

                MonitorTypes monitorMode;
                var monitorExisting = importResource.AuthorMonitoring?.MonitorExisting ?? "None";
                switch (monitorExisting.ToLowerInvariant())
                {
                    case "all":
                        monitorMode = MonitorTypes.All;
                        break;
                    case "select":
                        monitorMode = MonitorTypes.SpecificBook;
                        break;
                    case "none":
                    default:
                        monitorMode = MonitorTypes.None;
                        break;
                }

                if (editionId != "0" && !string.Equals(editionProvider, bookProvider, StringComparison.OrdinalIgnoreCase))
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
                    Monitored = true,
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
                    Monitored = monitorMode == MonitorTypes.All || importResource.AuthorMonitoring?.MonitorFuture == true,
                    AddOptions = new AddAuthorOptions
                    {
                        Monitor = monitorMode,
                        Monitored = monitorMode == MonitorTypes.All || importResource.AuthorMonitoring?.MonitorFuture == true,
                        SearchForMissingBooks = importResource.AuthorMonitoring?.SearchForMissing == true
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
	                }

                if (bookMediaType == BookMediaType.Audiobook)
                {
                    author.AudiobookRootFolderPath = importResource.RootFolder;
                    author.AudiobookQualityProfileId = importResource.QualityProfileId;
                    author.AudiobookMetadataProfileId = importResource.MetadataProfileId;
                    author.AudiobookMonitorExisting = monitorMode == MonitorTypes.All ? 1 :
                                                      monitorMode == MonitorTypes.SpecificBook ? 2 : 0;
                    author.AudiobookMonitorFuture = importResource.AuthorMonitoring?.MonitorFuture;
                }
                else
                {
                    author.EbookRootFolderPath = importResource.RootFolder;
                    author.EbookQualityProfileId = importResource.QualityProfileId;
                    author.EbookMetadataProfileId = importResource.MetadataProfileId;
                    author.EbookMonitorExisting = monitorMode == MonitorTypes.All ? 1 :
                                                  monitorMode == MonitorTypes.SpecificBook ? 2 : 0;
                    author.EbookMonitorFuture = importResource.AuthorMonitoring?.MonitorFuture;
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

                if (importResource.AuthorMonitoring?.SearchForMissing == true)
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
                _logger.Error(ex, "[V1-BOOK-IMPORT] Unexpected error during import");
                throw new ValidationException(new[]
                {
                    new ValidationFailure("Import", $"Failed to import book: {ex.Message}")
                });
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
            [FromQuery] string include = null)
        {
            _logger.Debug("[API-PERFORMANCE] GetBooks called: authorId={0}, bookIds.Count={1}, bookId='{2}', titleSlug='{3}', mediaType='{4}', monitored={5}",
                authorId, bookIds?.Count ?? 0, bookId, titleSlug, mediaType, monitored);

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
                var statsByBookId = BuildBookStatisticsById(_authorStatisticsService.AuthorStatistics(normalizedMediaType));

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

                        _logger.Debug("[API-DEBUG] Monitored view for {0} (monitored OR has files): {1} books", mediaType ?? "general", filteredBooks.Count);
                    }
                    else
                    {
                        _logger.Debug("[API-DEBUG] All view (all books): {0} books", filteredBooks.Count);
                    }
                }

                return MapToResource(filteredBooks,
                    IncludeRequested(include, "author"),
                    statsByBookId,
                    includeOverview: IncludeRequested(include, "overview"),
                    includeLinks: IncludeRequested(include, "links"));
            }

            if (authorId.HasValue)
            {
                _logger.Debug("[API-PERFORMANCE] Taking AUTHOR-SPECIFIC PATH for authorId={0}, mediaType='{1}'", authorId.Value, mediaType);

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
                        _logger.Debug("[API-DEBUG] Monitored view for {0} (monitored OR has files): {1} books", mediaType ?? "general", filteredBooks.Count);
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

            var bookList = _bookService.GetBooks(bookIds);

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
                    _logger.Debug("[BOOK-SLUG-ROUTE] titleSlug='{0}' belonged to sibling media type. Resolved to {1} {2} row(s) by provider identity.",
                        routeToken,
                        matches.Count,
                        scopedMediaType.Value);
                }
            }

            if (!matches.Any())
            {
                _logger.Debug("[BOOK-SLUG-ROUTE] No book found for titleSlug='{0}', mediaType='{1}'", routeToken, mediaType ?? "any");
                return null;
            }

            if (matches.Count > 1)
            {
                _logger.Warn("[BOOK-SLUG-ROUTE] titleSlug='{0}', mediaType='{1}' matched {2} local books. Choosing the monitored/lowest-id row deterministically: {3}",
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

        [HttpGet("{bookId:int}/siblings")]
        public ActionResult<BookSiblingDeleteInfoResource> GetBookSiblings(int bookId)
        {
            var book = _bookService.GetBook(bookId);
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

        [HttpGet("{bookId:int}")]
        public ActionResult<BookResource> GetBookById(int bookId)
        {
            var book = _bookService.GetBook(bookId);
            if (book == null)
            {
                return NotFound();
            }
            return MapToResource(book, true);
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
            _logger.Debug("[API-INFINITE-SCROLL] GetBookBuckets called: includeUnmonitored={0}, sortKey={1}, sortDirection={2}, mediaType={3}, downloaded={4}, monitored={5}, missing={6}, wanted={7}",
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
            [FromQuery] int pageSize = 200,
            [FromQuery] string sortKey = "cleanTitle",
            [FromQuery] string sortDirection = "ASC",
            [FromQuery] bool includeUnmonitored = false,
            [FromQuery] string mediaType = null,
            [FromQuery] bool? downloaded = null,
            [FromQuery] bool? monitored = null,
            [FromQuery] bool? missing = null,
            [FromQuery] bool? wanted = null,
            [FromQuery] string include = null)
        {
            _logger.Debug("[API-INFINITE-SCROLL] GetBooksPaged called: offset={0}, pageSize={1}, sortKey={2}, sortDirection={3}, includeUnmonitored={4}, mediaType={5}, downloaded={6}, monitored={7}, missing={8}, wanted={9}",
                offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, monitored, missing, wanted);
            var normalizedMediaType = MediaTypeParameterParser.NormalizeOptional(mediaType);

            // Validate parameters
            if (pageSize <= 0 || pageSize > 1000)
            {
                return BadRequest("Page size must be between 1 and 1000");
            }

            if (offset < 0)
            {
                return BadRequest("Offset must be non-negative");
            }

            var pagedBooks = _bookService.GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, normalizedMediaType, downloaded, monitored, missing, wanted);
            var statsByBookId = BuildBookStatisticsById(_authorStatisticsService.AuthorStatistics(normalizedMediaType));

            // Convert Book entities to BookResource
            var pagedResource = new PagedBookApiResource
            {
                Records = MapToResource(pagedBooks.Records,
                    IncludeRequested(include, "author"),
                    statsByBookId,
                    includeOverview: IncludeRequested(include, "overview"),
                    includeLinks: IncludeRequested(include, "links")),
                TotalCount = pagedBooks.TotalCount,
                Offset = pagedBooks.Offset,
                PageSize = pagedBooks.PageSize
            };

            _logger.Debug("[API-INFINITE-SCROLL] Paged response: {0} records, offset={1}, totalCount={2}",
                pagedResource.Records.Count, pagedResource.Offset, pagedResource.TotalCount);

            return Ok(pagedResource);
        }

        [HttpGet("{id:int}/overview")]
        public object Overview(int id)
        {
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
			        public async Task<ActionResult<BookResource>> AddBook([FromQuery] string mediaType, [FromBody] BookResource bookResource)
			        {
			            try
			            {
		                if (_logger.IsDebugEnabled)
		                {
		                    _logger.Debug("[AddBook] Request received");
		                    _logger.Debug("[AddBook] Title: {0}", bookResource?.Title ?? "NULL");
		                    _logger.Debug("[AddBook] MediaType (body): {0}", bookResource?.MediaType ?? "NULL");
		                    _logger.Debug("[AddBook] MediaType (query): {0}", mediaType ?? "NULL");
		                    _logger.Debug("[AddBook] ForeignBookId: {0}", bookResource?.ForeignBookId ?? "NULL");
		                    _logger.Debug("[AddBook] HardcoverBookId: {0}", bookResource?.HardcoverBookId ?? "NULL");
		                    _logger.Debug("[AddBook] GoodreadsBookId: {0}", bookResource?.GoodreadsBookId ?? "NULL");
		                    _logger.Debug("[AddBook] Author.Id: {0}", bookResource?.Author?.Id ?? 0);
		                    _logger.Debug("[AddBook] Author.AuthorName: {0}", bookResource?.Author?.AuthorName ?? "NULL");
		                    _logger.Debug("[AddBook] Author.AudiobookMonitorExisting: {0}", bookResource?.Author?.AudiobookMonitorExisting ?? 0);
		                    _logger.Debug("[AddBook] Author.EbookMonitorExisting: {0}", bookResource?.Author?.EbookMonitorExisting ?? 0);
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
	                    // Seerr compatibility: when mediaType is omitted, attempt to monitor/add BOTH audiobook and ebook variants.
	                    // We reuse the same payload and invoke AddBook twice with the appropriate mediaType, but only for
	                    // media types that are actually configured (or already configured on the existing author).
		                    _logger.Debug("[AddBook] No mediaType provided; defaulting to BOTH audiobook and ebook");

	                    NormalizeReadarrSingleFields(bookResource, wantAudiobook: true, wantEbook: true);
	                    var existingAuthor = TryGetExistingAuthor(bookResource?.Author);

	                    var canCreateAudiobook = HasConfiguredMediaSettings(bookResource?.Author, BookMediaType.Audiobook);
	                    var canCreateEbook = HasConfiguredMediaSettings(bookResource?.Author, BookMediaType.Ebook);

	                    var hasExistingAudiobook = HasConfiguredMediaSettings(existingAuthor, BookMediaType.Audiobook);
	                    var hasExistingEbook = HasConfiguredMediaSettings(existingAuthor, BookMediaType.Ebook);

	                    var shouldAddAudiobook = canCreateAudiobook || hasExistingAudiobook;
	                    var shouldAddEbook = canCreateEbook || hasExistingEbook;

	                    if (!shouldAddAudiobook && !shouldAddEbook)
	                    {
	                        return BadRequest("No media type is configured (audiobook/ebook). Configure at least one root folder + quality profile for this instance.");
	                    }

	                    var originalMediaType = bookResource?.MediaType;

	                    NzbDrone.Core.Books.Book firstAdded = null;
	                    var didFirstRefresh = false;

			                    if (shouldAddAudiobook)
			                    {
			                        bookResource.MediaType = "audiobook";
			                        var model = bookResource.ToModel(facadeContext);
			                        if (IsMissingUpstreamProviderBookId(model))
			                        {
			                            _logger.Warn("[AddBlockedMissingForeignBookId] Blocking AddBook (audiobook) due to missing upstream provider book/work ID. Title='{0}' ForeignBookId='{1}' HardcoverBookId='{2}' GoodreadsBookId='{3}' GoodreadsWorkId='{4}' OpenLibraryWorkId='{5}' GoogleBooksId='{6}' ASIN='{7}' AudibleASIN='{8}'",
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

				                        firstAdded = await _addBookService.AddBook(model, doRefresh: true);
				                        EnsureBookCover(firstAdded.Id, "create");
				                        didFirstRefresh = true;
			                    }

			                    if (shouldAddEbook)
			                    {
			                        bookResource.MediaType = "ebook";
			                        var model = bookResource.ToModel(facadeContext);
			                        if (IsMissingUpstreamProviderBookId(model))
			                        {
			                            _logger.Warn("[AddBlockedMissingForeignBookId] Blocking AddBook (ebook) due to missing upstream provider book/work ID. Title='{0}' ForeignBookId='{1}' HardcoverBookId='{2}' GoodreadsBookId='{3}' GoodreadsWorkId='{4}' OpenLibraryWorkId='{5}' GoogleBooksId='{6}' ASIN='{7}' AudibleASIN='{8}'",
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

				                        var ebook = await _addBookService.AddBook(model, doRefresh: !didFirstRefresh);
				                        EnsureBookCover(ebook.Id, "create");
				                        firstAdded ??= ebook;
			                    }

	                    bookResource.MediaType = originalMediaType;

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
			                    _logger.Warn("[AddBlockedMissingForeignBookId] Blocking AddBook due to missing upstream provider book/work ID. Title='{0}' ForeignBookId='{1}' HardcoverBookId='{2}' GoodreadsBookId='{3}' GoodreadsWorkId='{4}' OpenLibraryWorkId='{5}' GoogleBooksId='{6}' ASIN='{7}' AudibleASIN='{8}'",
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

			                var book = await _addBookService.AddBook(modelToAdd, doRefresh: true);
			                EnsureBookCover(book.Id, "create");

			                return Created(book.Id);
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

        private ActionResult<BookResource> UpdateBookInternal(BookResource bookResource, bool pinExplicitEditionChange)
        {
            var facadeContext = HttpContext.GetReadarrFacadeContext();
            var prefixFailures = GetNativePrefixFailures(bookResource, facadeContext);
            if (prefixFailures.Any())
            {
                throw new ValidationException(prefixFailures);
            }

            var book = _bookService.GetBook(bookResource.Id);
            var wasMonitoredForFacadeMediaType = facadeContext != null &&
                                                 book.IsMonitoredForMediaType(facadeContext.MediaType);
            var beforeMonitoredEditionId = book?.Editions?.SingleOrDefault(e => e != null && e.Monitored)?.Id;

            var model = bookResource.ToModel(book, facadeContext);
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
                : model?.Editions?.SingleOrDefault(e => e != null && e.Monitored)?.Id;

            if (ShouldApplyFacadeSpecificBookMonitoring(
                    facadeContext,
                    model.MediaType,
                    wasMonitoredForFacadeMediaType,
                    bookResource.Monitored))
            {
                // A facade request changes only its media side. The generic update would overwrite
                // the other side; submitted edition selection is persisted independently below.
                _authorService.PromoteMediaTypeMonitoringToSelected(model.AuthorId, facadeContext.MediaType);
                _bookService.SetMonitoredForMediaType(new[] { model.Id }, facadeContext.MediaType, true);
            }
            else
            {
                _bookService.UpdateBook(model);
            }

            _editionService.UpdateMany(model.Editions);

            if (beforeMonitoredEditionId != afterMonitoredEditionId)
            {
                EnsureBookCover(model.Id, "editionSwitch");
            }

            BroadcastResourceChange(ModelAction.Updated, model.Id);

            return Accepted(model.Id);
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
        public void DeleteBook(int id, bool deleteFiles = false, bool addImportListExclusion = false, bool applyToBothFormats = false)
        {
            _bookService.DeleteBook(id, deleteFiles, addImportListExclusion, applyToBothFormats);
        }

        [HttpPost("{id}/downloadmedia")]
        public IActionResult DownloadBookMedia(int id, bool forceDownload = false)
        {
            var book = _bookService.GetBook(id);

            if (book == null)
            {
                return NotFound();
            }

            _commandQueueManager.Push(new DownloadBookMediaCommand(id, forceDownload));

            return Accepted();
        }

        [HttpPut("monitor")]
        public IActionResult SetBooksMonitored([FromBody] BooksMonitoredResource resource)
        {
            // Use the new simplified approach - no mediaType needed
            _bookService.SetMonitored(resource.BookIds, resource.Monitored);

            return Accepted(MapToResource(_bookService.GetBooks(resource.BookIds), false));
        }

        [HttpPost("{id}/editions/wanted")]
        public ActionResult<BookResource> AddWantedEdition(int id, [FromBody] AddWantedEditionRequest request)
        {
            if (request == null || request.EditionId <= 0)
            {
                return BadRequest("EditionId is required");
            }

	            try
	            {
	                var wantedBook = _bookService.AddWantedEdition(id, request.EditionId);
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
            // Always broadcast a fully loaded book resource (editions, files, covers).
            // In Chaptarr, Books no longer use lazy loading for Editions, so the Book instance on the event
            // may not contain Editions and can cause the displayed title/cover to "change" on monitor toggles.
            BroadcastResourceChange(ModelAction.Updated, message.Book.Id);
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
