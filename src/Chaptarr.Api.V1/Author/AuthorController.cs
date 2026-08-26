using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Api.V1.ProviderIds;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;
using NLog;
using Newtonsoft.Json;
using SystemTextJsonIgnoreAttribute = System.Text.Json.Serialization.JsonIgnoreAttribute;
using SystemTextJsonIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Chaptarr.Api.V1.Author
{
    public class SetPrimaryPhotoRequest
    {
        public string PhotoUrl { get; set; }
        public int? PhotoId { get; set; }
    }

    public class LoadImageRequest
    {
        public string ImageUrl { get; set; }
    }

    public class LoadImageResponseResource
    {
        public string Status { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public string LocalPath { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public string ErrorCode { get; set; }

        public string Message { get; set; }
    }
    [V1ApiController]
	    public class AuthorController : RestControllerWithSignalR<AuthorResource, NzbDrone.Core.Books.Author>,
	                                IHandle<BookImportedEvent>,
	                                IHandle<BookEditedEvent>,
	                                IHandle<BookFileDeletedEvent>,
	                                IHandle<BookFileAddedEvent>,
	                                IHandle<BookFileUpdatedEvent>,
	                                IHandle<BookFilesAddedEvent>,
	                                IHandle<ImportStageProgressEvent>,
	                                IHandle<CommandExecutedEvent>,
	                                IHandle<AuthorAddedEvent>,
	                                IHandle<AuthorUpdatedEvent>,
	                                IHandle<AuthorEditedEvent>,
	                                IHandle<AuthorDeletedEvent>,
	                                IHandle<AuthorRenamedEvent>,
                                IHandle<MediaCoversUpdatedEvent>,
                                IHandle<AuthorRefreshCompleteEvent>
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly ISeriesService _seriesService;
        // DEPRECATED-IDENTIFICATION: IAddAuthorService removed - use IAuthorLibraryService instead
        // private readonly IAddAuthorService _addAuthorService;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IAuthorStatisticsService _authorStatisticsService;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IRootFolderService _rootFolderService;
        private readonly IProviderAliasService _providerAliasService;
        private readonly IEventAggregator _eventAggregator;
	        private readonly IAppFolderInfo _appFolderInfo;
	        private readonly IBuildFileNames _fileNameBuilder;
	        private readonly Logger _logger;
	        private readonly object _importStateLock = new object();
	        private readonly HashSet<int> _activeImportCommands = new HashSet<int>();
            private readonly ConcurrentDictionary<int, byte> _pendingAuthorUpdates = new ConcurrentDictionary<int, byte>();
            private readonly Debouncer _authorUpdateDebouncer;

	        public AuthorController(IBroadcastSignalRMessage signalRBroadcaster,
	                            IAuthorService authorService,
	                            IBookService bookService,
                            ISeriesService seriesService,
                            IAuthorLibraryService authorLibraryService,
                            IAuthorStatisticsService authorStatisticsService,
                            IMapCoversToLocal coverMapper,
                            IManageCommandQueue commandQueueManager,
                            IRootFolderService rootFolderService,
                            IEventAggregator eventAggregator,
                            IAppFolderInfo appFolderInfo,
                            IBuildFileNames fileNameBuilder,
                            Logger logger,
                            RecycleBinValidator recycleBinValidator,
                            RootFolderValidator rootFolderValidator,
                            MappedNetworkDriveValidator mappedNetworkDriveValidator,
                            AuthorPathValidator authorPathValidator,
                            AuthorExistsValidator authorExistsValidator,
                            AuthorAncestorValidator authorAncestorValidator,
                            SystemFolderValidator systemFolderValidator,
                            QualityProfileExistsValidator qualityProfileExistsValidator,
                            MetadataProfileExistsValidator metadataProfileExistsValidator,
                            AuthorFolderAsRootFolderValidator authorFolderAsRootFolderValidator,
                            IProviderAliasService providerAliasService = null)
            : base(signalRBroadcaster)
        {
            _authorService = authorService;
            _bookService = bookService;
            _seriesService = seriesService;
            _authorLibraryService = authorLibraryService;
            _authorStatisticsService = authorStatisticsService;

            _coverMapper = coverMapper;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
            _rootFolderService = rootFolderService;
            _providerAliasService = providerAliasService;
            _eventAggregator = eventAggregator;
            _appFolderInfo = appFolderInfo;
            _fileNameBuilder = fileNameBuilder;

            _authorUpdateDebouncer = new Debouncer(FlushPendingAuthorUpdates, TimeSpan.FromMilliseconds(500), executeRestartsTimer: true);

            // MetadataProfileId is deprecated and can be null
            // Only validate if it's provided (for backward compatibility)
            SharedValidator.RuleFor(s => s.MetadataProfileId)
                           .Must(id => !id.HasValue || id.Value > 0)
                           .WithMessage("MetadataProfileId must be greater than 0 if provided")
                           .When(s => s.MetadataProfileId.HasValue);

            SharedValidator.RuleFor(s => s.Path)
                           .Cascade(CascadeMode.Stop)
                           .IsValidPath()
                           .SetValidator(rootFolderValidator)
                           .SetValidator(mappedNetworkDriveValidator)
                           .SetValidator(authorPathValidator)
                           .SetValidator(authorAncestorValidator)
                           .SetValidator(recycleBinValidator)
                           .SetValidator(systemFolderValidator)
                           .When(s => !s.Path.IsNullOrWhiteSpace());

            SharedValidator.RuleFor(s => s).Must(s => s.AudiobookQualityProfileId.HasValue || s.EbookQualityProfileId.HasValue)
                           .WithMessage("At least one quality profile must be selected");
            
            // Only validate MetadataProfileId existence if it's provided and > 0
	            SharedValidator.RuleFor(s => s.MetadataProfileId)
	                           .SetValidator(metadataProfileExistsValidator)
	                           .When(s => s.MetadataProfileId.HasValue && s.MetadataProfileId.Value > 0);

	            SharedValidator.RuleFor(s => s.AudiobookQualityProfileId)
	                           .SetValidator(qualityProfileExistsValidator)
	                           .When(s => s.AudiobookQualityProfileId.HasValue && s.AudiobookQualityProfileId.Value > 0);
	            SharedValidator.RuleFor(s => s.EbookQualityProfileId)
	                           .SetValidator(qualityProfileExistsValidator)
	                           .When(s => s.EbookQualityProfileId.HasValue && s.EbookQualityProfileId.Value > 0);
	            SharedValidator.RuleFor(s => s.AudiobookMetadataProfileId)
	                           .SetValidator(metadataProfileExistsValidator)
	                           .When(s => s.AudiobookMetadataProfileId.HasValue && s.AudiobookMetadataProfileId.Value > 0);
	            SharedValidator.RuleFor(s => s.EbookMetadataProfileId)
	                           .SetValidator(metadataProfileExistsValidator)
	                           .When(s => s.EbookMetadataProfileId.HasValue && s.EbookMetadataProfileId.Value > 0);
	            SharedValidator.RuleFor(s => s.AudiobookMonitorExisting)
	                           .Must(v => !v.HasValue || v.Value is 0 or 1 or 2)
	                           .WithMessage("AudiobookMonitorExisting must be 0 (None), 1 (All), or 2 (Selected)");
	            SharedValidator.RuleFor(s => s.EbookMonitorExisting)
	                           .Must(v => !v.HasValue || v.Value is 0 or 1 or 2)
	                           .WithMessage("EbookMonitorExisting must be 0 (None), 1 (All), or 2 (Selected)");

	            PostValidator.RuleFor(s => s.Path).IsValidPath().When(s => s.AudiobookRootFolderPath.IsNullOrWhiteSpace() && s.EbookRootFolderPath.IsNullOrWhiteSpace());
            PostValidator.RuleFor(s => s.AudiobookRootFolderPath)
                         .IsValidPath()
                         .SetValidator(authorFolderAsRootFolderValidator)
                         .When(s => s.Path.IsNullOrWhiteSpace() && !s.AudiobookRootFolderPath.IsNullOrWhiteSpace());
            PostValidator.RuleFor(s => s.EbookRootFolderPath)
                         .IsValidPath()
                         .SetValidator(authorFolderAsRootFolderValidator)
                         .When(s => s.Path.IsNullOrWhiteSpace() && !s.EbookRootFolderPath.IsNullOrWhiteSpace());
            PostValidator.RuleFor(s => s.AuthorName).NotEmpty();
            PostValidator.RuleFor(s => s.ForeignAuthorId).NotEmpty().SetValidator(authorExistsValidator);

	            PutValidator.RuleFor(s => s.Path).IsValidPath();
	        }

	        private bool IsImportActive()
	        {
	            lock (_importStateLock)
	            {
	                return _activeImportCommands.Count > 0 || ImportSessionProgressTracker.IsImportActive;
	            }
	        }

            private void QueueAuthorUpdate(int authorId)
            {
                if (authorId <= 0)
                {
                    return;
                }

                _pendingAuthorUpdates.TryAdd(authorId, 0);
                if (!IsImportActive())
                {
                    _authorUpdateDebouncer.Execute();
                }
            }

            private void FlushPendingAuthorUpdates()
            {
                if (IsImportActive())
                {
                    return;
                }

                try
                {
                    var authorIds = _pendingAuthorUpdates.Keys.ToArray();
                    foreach (var id in authorIds)
                    {
                        if (!_pendingAuthorUpdates.TryRemove(id, out _))
                        {
                            continue;
                        }

                        var author = _authorService.GetAuthor(id);
                        if (author == null)
                        {
                            continue;
                        }

                        _authorStatisticsService.InvalidateAuthorCache(id);
                        BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(author));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[UI-BROADCAST] Failed to flush pending Author updates");
                }
            }

	        protected override AuthorResource GetResourceById(int id)
	        {
	            var author = _authorService.GetAuthor(id);
	            return GetAuthorResource(author);
	        }

        [HttpGet("{authorId:int}")]
        public ActionResult<AuthorResource> GetAuthorById(int authorId)
        {

            var author = _authorService.GetAuthor(authorId);
            if (author == null)
            {
                return NotFound();
            }
            return GetAuthorResource(author);
        }

        private AuthorResource GetAuthorResource(NzbDrone.Core.Books.Author author)
        {
            if (author == null)
            {
                return null;
            }

            // Ensure author has all relationships loaded
            if (author.Books == null)
            {
                author.Books = _bookService.GetBooksByAuthor(author.Id);
            }

            if (author.Series == null)
            {
                author.Series = _seriesService.GetByAuthorId(author.Id);
            }

            var resource = author.ToResource(HttpContext.GetReadarrFacadeContext());
            MapCoversToLocal(resource);
            FetchAndLinkAuthorStatistics(resource, HttpContext.GetReadarrFacadeContext()?.MediaType);
            LinkNextPreviousBooks(resource);

            LinkRootFolderPath(new[] { author }, resource);

            // Attach per-media-type statistics for live updates (used by Authors list without full refetch)
            try
            {
                var audioStats = _authorStatisticsService.AuthorStatistics(author.Id, "audiobook");
                var ebookStats = _authorStatisticsService.AuthorStatistics(author.Id, "ebook");
                resource.AudiobookStatistics = audioStats.ToResource();
                resource.EbookStatistics = ebookStats.ToResource();
            }
            catch { /* Non-fatal: fall back to aggregate stats only */ }

            return resource;
        }

        [HttpGet]
        public List<AuthorResource> AllAuthors([FromQuery] string mediaType = null)
        {
            var normalizedMediaType = MediaTypeParameterParser.NormalizeOptional(mediaType);
            var authorStats = normalizedMediaType == null
                ? _authorStatisticsService.AuthorStatistics() 
                : _authorStatisticsService.AuthorStatistics(normalizedMediaType);
                
            var authors = _authorService.GetAllAuthors();
            var authorResources = authors.ToResource(HttpContext.GetReadarrFacadeContext());

            MapCoversToLocal(authorResources.ToArray());
            LinkNextPreviousBooks(authorResources.ToArray());
            LinkAuthorStatistics(authorResources, authorStats.ToDictionary(x => x.AuthorId));
            LinkRootFolderPath(authors, authorResources.ToArray());

            return authorResources;
        }


        private static void ApplyFacadeAuthorSingleFields(AuthorResource authorResource, ReadarrFacadeContext facadeContext)
        {
            if (authorResource == null || facadeContext == null)
            {
                return;
            }

            if (facadeContext.MediaType == "audiobook")
            {
                authorResource.AudiobookQualityProfileId ??= authorResource.QualityProfileId;
                authorResource.AudiobookRootFolderPath ??= authorResource.RootFolderPath;
                authorResource.AudiobookTags ??= authorResource.Tags;
                authorResource.AudiobookMonitorExisting ??= authorResource.Monitored == true ? 1 : 0;
                authorResource.AudiobookMonitorFuture ??= authorResource.Monitored ?? false;
            }
            else if (facadeContext.MediaType == "ebook")
            {
                authorResource.EbookQualityProfileId ??= authorResource.QualityProfileId;
                authorResource.EbookRootFolderPath ??= authorResource.RootFolderPath;
                authorResource.EbookTags ??= authorResource.Tags;
                authorResource.EbookMonitorExisting ??= authorResource.Monitored == true ? 1 : 0;
                authorResource.EbookMonitorFuture ??= authorResource.Monitored ?? false;
            }
        }

        private ActionResult GetProviderAmbiguityResult(ProviderAmbiguityResource ambiguity)
        {
            return ambiguity == null ? null : StatusCode(ProviderAmbiguityHelper.StatusCode, ambiguity);
        }

        [RestPostById]
        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
        public async Task<ActionResult<AuthorResource>> AddAuthor([FromBody] AuthorResource authorResource, [FromQuery] bool queueIfUnavailable = true)
        {
            var facadeContext = HttpContext.GetReadarrFacadeContext();
            ApplyFacadeAuthorSingleFields(authorResource, facadeContext);
            if (ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(authorResource.ForeignAuthorId, facadeContext))
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("ForeignAuthorId", ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("foreignAuthorId"))
                });
            }

            var foreignAuthorId = ReadarrFacadeProviderIdTranslator.NormalizeBareProviderId(authorResource.ForeignAuthorId, facadeContext);
            if (!ProviderIdValidator.TryNormalize(foreignAuthorId, out var normalizedForeignAuthorId, out var authorProvider, out var authorId, out var errorMessage))
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("ForeignAuthorId", errorMessage)
                });
            }

            var ambiguity = GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetAuthorAmbiguity(
                _authorService,
                _providerAliasService,
                authorProvider,
                authorId,
                "foreignAuthorId",
                _logger,
                "adding author"));
            if (ambiguity != null)
            {
                return ambiguity;
            }

            var config = new MonitoringConfig
            {
                MonitorNewItems = authorResource.Monitored ?? false,
                IsManualAddition = true,
                CreateAudiobook = !string.IsNullOrWhiteSpace(authorResource.AudiobookRootFolderPath),
                CreateEbook = !string.IsNullOrWhiteSpace(authorResource.EbookRootFolderPath),
                AudiobookMonitorExisting = authorResource.AudiobookMonitorExisting,
                AudiobookMonitorFuture = authorResource.AudiobookMonitorFuture,
                EbookMonitorExisting = authorResource.EbookMonitorExisting,
                EbookMonitorFuture = authorResource.EbookMonitorFuture,
                AudiobookQualityProfileId = authorResource.AudiobookQualityProfileId,
                EbookQualityProfileId = authorResource.EbookQualityProfileId,
                AudiobookMetadataProfileId = authorResource.AudiobookMetadataProfileId,
                EbookMetadataProfileId = authorResource.EbookMetadataProfileId,
                AudiobookRootFolderPath = authorResource.AudiobookRootFolderPath,
                EbookRootFolderPath = authorResource.EbookRootFolderPath,
                QueueIfUnavailable = queueIfUnavailable,
                Tags = authorResource.Tags,
                SearchForMissingBooks = authorResource.AddOptions?.SearchForMissingBooks,
                RequestedBy = "ApiV1AuthorAdd",
                AuthorName = authorResource.AuthorName
            };

            NzbDrone.Core.Books.Author author;
            try
            {
                author = await _authorLibraryService.AddAuthorAsync(normalizedForeignAuthorId, config);
            }
            catch (AuthorNotFoundException)
            {
                return NotFound(new ApiErrorResource
                {
                    Message = "The author isn't available yet on the metadata server."
                });
            }

            // Pending import: Author not available in golden payloads yet; queued for retry.
            // AddAuthorAsync returns a negative ID marker: -pendingId
            if (author.Id < 0)
            {
                var pendingId = -author.Id;

                return Accepted(new
                {
                    pendingId,
                    message = "The author isn't available yet on the metadata server. Chaptarr has queued the import and will automatically add them when they become available (you can visit the chaptarrbot channel in our discord to ask for updates)."
                });
            }

            return Created(author.Id);
        }

        [HttpPost("import")]
        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
        public async Task<ActionResult<AuthorResource>> ImportAuthor([FromBody] AuthorImportResource importResource)
        {
            try
            {
                _logger.Debug("[V1-AUTHOR-IMPORT] Starting import with foreignAuthorId: {0}, mediaType: {1}",
                    importResource.ForeignAuthorId, importResource.MediaType);

	                var facadeContext = HttpContext.GetReadarrFacadeContext();
                if (ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(importResource.ForeignAuthorId, facadeContext))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("ForeignAuthorId", ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("foreignAuthorId"))
                    });
                }

                var foreignAuthorId = ReadarrFacadeProviderIdTranslator.NormalizeBareProviderId(importResource.ForeignAuthorId, facadeContext);
                if (!ProviderIdValidator.TryNormalize(foreignAuthorId, out var normalizedForeignAuthorId, out var authorProvider, out var authorId, out var errorMessage))
                {
                    throw new ValidationException(new[]
	                    {
	                        new ValidationFailure("ForeignAuthorId", errorMessage)
	                    });
	                }

                BookMediaType bookMediaType;
                if (string.Equals(importResource.MediaType, "audiobook", StringComparison.OrdinalIgnoreCase))
                {
                    bookMediaType = BookMediaType.Audiobook;
                }
                else if (string.Equals(importResource.MediaType, "ebook", StringComparison.OrdinalIgnoreCase))
                {
                    bookMediaType = BookMediaType.Ebook;
                }
                else
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("MediaType", "Invalid mediaType. Expected 'audiobook' or 'ebook'.")
                    });
                }

                RootFolder selectedRootFolder = null;
                var requestedRootFolderPath = importResource.RootFolder?.Trim();

                static bool IsCompatibleRootFolder(RootFolder rootFolder, BookMediaType mediaType)
                {
                    if (rootFolder == null)
                    {
                        return false;
                    }

                    if (rootFolder.FolderType == FolderType.Mixed)
                    {
                        return true;
                    }

                    return mediaType == BookMediaType.Audiobook
                        ? rootFolder.FolderType == FolderType.Audiobook
                        : rootFolder.FolderType == FolderType.Ebook;
                }

                var allRootFolders = _rootFolderService.All();
                if (!string.IsNullOrWhiteSpace(requestedRootFolderPath))
                {
                    selectedRootFolder = allRootFolders.FirstOrDefault(r => r.Path.PathEquals(requestedRootFolderPath));
                    if (selectedRootFolder == null)
                    {
                        throw new ValidationException(new[]
                        {
                            new ValidationFailure("RootFolder", "Selected root folder is not configured. Add it in Settings → Media Management.")
                        });
                    }

                    if (!IsCompatibleRootFolder(selectedRootFolder, bookMediaType))
                    {
                        var expected = bookMediaType == BookMediaType.Audiobook ? "audiobooks" : "ebooks";
                        throw new ValidationException(new[]
                        {
                            new ValidationFailure("RootFolder", $"Selected root folder is not configured for {expected}.")
                        });
                    }
                }
                else
                {
                    // Missing/empty root folder from UI: fall back to a compatible configured root folder.
                    selectedRootFolder = bookMediaType == BookMediaType.Audiobook
                        ? allRootFolders.FirstOrDefault(r => r.FolderType == FolderType.Audiobook) ?? allRootFolders.FirstOrDefault(r => r.FolderType == FolderType.Mixed)
                        : allRootFolders.FirstOrDefault(r => r.FolderType == FolderType.Ebook) ?? allRootFolders.FirstOrDefault(r => r.FolderType == FolderType.Mixed);

                    if (selectedRootFolder == null || !IsCompatibleRootFolder(selectedRootFolder, bookMediaType))
                    {
                        var expected = bookMediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
                        throw new ValidationException(new[]
                        {
                            new ValidationFailure("RootFolder", $"No {expected} root folders are configured. Add one in Settings → Media Management.")
                        });
                    }
                }

                var selectedRootFolderPath = selectedRootFolder?.Path;

                var monitorExisting = importResource.MonitorExisting ?? string.Empty;
                var monitorExistingMode = monitorExisting.Trim().ToLowerInvariant() switch
                {
                    "all" => 1,
                    "select" => 2,
                    "none" => 0,
                    _ => 0
                };

                var monitorAll = monitorExistingMode == 1;
                var shouldSearchForMissingBooks = importResource.SearchForMissingBooks ?? monitorAll;

	                var importAmbiguity = GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetAuthorAmbiguity(
                    _authorService,
                    _providerAliasService,
                    authorProvider,
                    authorId,
                    "foreignAuthorId",
                    _logger,
                    "importing author"));
                if (importAmbiguity != null)
                {
                    return importAmbiguity;
                }

                var existingAuthorMatches = ProviderAmbiguityHelper.FindAuthorProviderMatches(_authorService, _providerAliasService, authorProvider, authorId, _logger);
                var existingAuthor = existingAuthorMatches.Count == 1 ? existingAuthorMatches[0] : null;

                if (existingAuthor != null)
                {
                    _logger.Debug("[V1-AUTHOR-IMPORT] Author already exists with ID: {0}, updating settings", existingAuthor.Id);
                    string hydrationWarning = null;

                    if (bookMediaType == BookMediaType.Audiobook)
                    {
                        existingAuthor.AudiobookRootFolderPath = selectedRootFolderPath;
                        existingAuthor.AudiobookQualityProfileId = importResource.QualityProfileId;
                        existingAuthor.AudiobookMetadataProfileId = importResource.MetadataProfileId;
                        existingAuthor.AudiobookMonitorExisting = monitorExistingMode;
                        existingAuthor.AudiobookMonitorFuture = importResource.MonitorFuture;

                        if (importResource.ManualFlag)
                        {
                            existingAuthor.AudiobookSettingsManuallyOverridden = true;
                        }
                    }
                    else
                    {
                        existingAuthor.EbookRootFolderPath = selectedRootFolderPath;
                        existingAuthor.EbookQualityProfileId = importResource.QualityProfileId;
                        existingAuthor.EbookMetadataProfileId = importResource.MetadataProfileId;
                        existingAuthor.EbookMonitorExisting = monitorExistingMode;
                        existingAuthor.EbookMonitorFuture = importResource.MonitorFuture;

                        if (importResource.ManualFlag)
                        {
                            existingAuthor.EbookSettingsManuallyOverridden = true;
                        }
                    }

                    if (monitorExistingMode > 0 || importResource.MonitorFuture)
                    {
                        existingAuthor.Monitored = true;
                    }

                    existingAuthor = _authorService.UpdateAuthor(existingAuthor);

                    // Ensure the requested media type exists in the library.
                    // When authors are imported from a single-media root folder, only that media type is hydrated.
                    // If the user later imports the other media type, we should backfill the missing books/series.
                    var existingBooks = _bookService.GetBooksByAuthor(existingAuthor.Id);
                    var hasRequestedMediaType = existingBooks != null && existingBooks.Any(b => b.MediaType == bookMediaType);

                    if (!hasRequestedMediaType)
                    {
                        _logger.Debug("[V1-AUTHOR-IMPORT] Existing author missing {0} books; hydrating from provider", bookMediaType);

                        var hydrateConfig = new MonitoringConfig
                        {
                            IsManualAddition = true,
                            QueueIfUnavailable = false,
                            RequestedBy = "UserInterface",
                            CreateAudiobook = bookMediaType == BookMediaType.Audiobook,
                            CreateEbook = bookMediaType == BookMediaType.Ebook
                        };

                        if (bookMediaType == BookMediaType.Audiobook)
                        {
                            hydrateConfig.AudiobookRootFolderPath = selectedRootFolderPath;
                            hydrateConfig.AudiobookQualityProfileId = importResource.QualityProfileId;
                            hydrateConfig.AudiobookMetadataProfileId = importResource.MetadataProfileId;
                            hydrateConfig.AudiobookMonitorExisting = monitorExistingMode;
                            hydrateConfig.AudiobookMonitorFuture = importResource.MonitorFuture;
                        }
                        else
                        {
                            hydrateConfig.EbookRootFolderPath = selectedRootFolderPath;
                            hydrateConfig.EbookQualityProfileId = importResource.QualityProfileId;
                            hydrateConfig.EbookMetadataProfileId = importResource.MetadataProfileId;
                            hydrateConfig.EbookMonitorExisting = monitorExistingMode;
                            hydrateConfig.EbookMonitorFuture = importResource.MonitorFuture;
                        }

	                        try
	                        {
	                            existingAuthor = await _authorLibraryService.AddAuthorAsync(normalizedForeignAuthorId, hydrateConfig);
	                        }
	                        catch (AuthorNotFoundException ex)
	                        {
                            // Author exists locally but isn't available upstream (or provider failed).
                            // Keep the settings update, but skip hydration.
                            var mediaLabel = bookMediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
                            hydrationWarning = $"Author settings were saved, but the {mediaLabel} catalog could not be loaded from the metadata server. You may need to refresh the author later.";
                            _logger.Error(ex, "[V1-AUTHOR-IMPORT] Unable to hydrate missing media type for existing author: {0}", importResource.ForeignAuthorId);
                        }
                        catch (Exception ex)
                        {
                            var mediaLabel = bookMediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
                            hydrationWarning = $"Author settings were saved, but the {mediaLabel} catalog could not be loaded due to an unexpected error. You may need to refresh the author later.";
                            _logger.Error(ex, "[V1-AUTHOR-IMPORT] Unexpected error while hydrating existing author: {0}", importResource.ForeignAuthorId);
                        }
                    }

                    if (shouldSearchForMissingBooks)
                    {
                        _commandQueueManager.Push(new MissingBookSearchCommand
                        {
                            AuthorId = existingAuthor.Id
                        });
                    }

                    var authorResource = existingAuthor.ToResource(HttpContext.GetReadarrFacadeContext());
                    if (!string.IsNullOrWhiteSpace(hydrationWarning))
                    {
                        authorResource.HydrationWarning = hydrationWarning;
                    }

                    return Ok(authorResource);
                }

                _logger.Debug("[V1-AUTHOR-IMPORT] Author not found locally, importing from provider");

                var config = new MonitoringConfig
                {
                    MonitorNewItems = monitorExistingMode > 0 || importResource.MonitorFuture,
                    MonitorExisting = monitorAll,
                    MonitorFuture = importResource.MonitorFuture,
                    IsManualAddition = true,
                    QueueIfUnavailable = true,
                    RequestedBy = "UserInterface",
                    CreateAudiobook = bookMediaType == BookMediaType.Audiobook,
                    CreateEbook = bookMediaType == BookMediaType.Ebook,
                    AuthorName = "Pending Import",
                    SearchForMissingBooks = shouldSearchForMissingBooks
                };

                switch (authorProvider.ToLowerInvariant())
                {
                    case "hc":
                        config.AuthorName = "Pending Import";
                        break;
                    case "gr":
                        config.AuthorName = "Pending Import";
                        break;
                    case "ol":
                        config.AuthorName = "Pending Import";
                        break;
                    case "gb":
                        config.AuthorName = "Pending Import";
                        break;
                }

                if (bookMediaType == BookMediaType.Audiobook)
                {
                    config.AudiobookRootFolderPath = selectedRootFolderPath;
                    config.AudiobookQualityProfileId = importResource.QualityProfileId;
                    config.AudiobookMetadataProfileId = importResource.MetadataProfileId;
                    config.AudiobookMonitorExisting = monitorExistingMode;
                    config.AudiobookMonitorFuture = importResource.MonitorFuture;
                }
                else
                {
                    config.EbookRootFolderPath = selectedRootFolderPath;
                    config.EbookQualityProfileId = importResource.QualityProfileId;
                    config.EbookMetadataProfileId = importResource.MetadataProfileId;
                    config.EbookMonitorExisting = monitorExistingMode;
                    config.EbookMonitorFuture = importResource.MonitorFuture;
                }

	                _logger.Debug("[V1-AUTHOR-IMPORT] Calling AuthorLibraryService to import author");

	                var importedAuthor = await _authorLibraryService.AddAuthorAsync(normalizedForeignAuthorId, config);

                if (importedAuthor == null)
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("Import", "Failed to import author from provider")
                    });
                }

                // Pending import: Author not available in golden payloads yet; queued for retry.
                // AddAuthorAsync returns a negative ID marker: -pendingId
                if (importedAuthor.Id < 0)
                {
                    var pendingId = -importedAuthor.Id;

                    return Accepted(new
                    {
                        pendingId,
                        message = "The author isn't available yet on the metadata server. Chaptarr has queued the import and will automatically add them when they become available (you can visit the chaptarrbot channel in our discord to ask for updates)."
                    });
                }

                if (shouldSearchForMissingBooks)
                {
                    _commandQueueManager.Push(new MissingBookSearchCommand
                    {
                        AuthorId = importedAuthor.Id
                    });
                }

                return Created(importedAuthor.Id);
            }
            catch (ValidationException ex)
            {
                _logger.Error(ex, "[V1-AUTHOR-IMPORT] Validation error");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[V1-AUTHOR-IMPORT] Unexpected error during import");
                throw new ValidationException(new[]
                {
                    new ValidationFailure("Import", $"Failed to import author: {ex.Message}")
                });
            }
        }

	        [RestPutById]
	        public ActionResult<AuthorResource> UpdateAuthor([FromBody] AuthorResource authorResource, bool moveFiles = false)
	        {
                var facadeContext = HttpContext.GetReadarrFacadeContext();
                if (ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(authorResource.ForeignAuthorId, facadeContext))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("ForeignAuthorId", ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("foreignAuthorId"))
                    });
                }

	            var author = _authorService.GetAuthor(authorResource.Id);
                var wasSyncEnabled = author.SyncMonitoredAcrossFormats == true;

                // ToModel/ApplyChanges mutate `author` in place (the returned `model` below is the same
                // object), so everything the cascade below needs to know about the author's state
                // BEFORE this request - root folders, tri-state fields, derived monitored status - has
                // to be captured before that call, not read off `model` afterwards. Bundled into one
                // struct, captured in one line, specifically so there's nothing here for a later edit
                // to accidentally reorder relative to the ToModel call below.
                var storedMonitoringState = StoredAuthorMonitoringState.Capture(author);

	            if (moveFiles)
	            {
	                var sourcePath = author.Path;
	                var destinationPath = authorResource.Path;

                _commandQueueManager.Push(new MoveAuthorCommand
                {
                    AuthorId = author.Id,
                    SourcePath = sourcePath,
                    DestinationPath = destinationPath,
                    Trigger = CommandTrigger.Manual
                });
	            }

	            var model = authorResource.ToModel(author, facadeContext);
	            CascadeExplicitMonitoredIntoMediaTypeSettings(authorResource, model, facadeContext, storedMonitoringState);
	            var updatedAuthor = _authorService.UpdateAuthor(model);

                var shouldReconcile = updatedAuthor.SyncMonitoredAcrossFormats == true &&
                                      HasSyncMonitoredAcrossFormatsEligibility(updatedAuthor) &&
                                      (authorResource.SyncMonitoredAcrossFormats == true || !wasSyncEnabled);

                if (shouldReconcile)
                {
                    _commandQueueManager.Push(new BulkSyncFormatMonitoringCommand(new List<int> { updatedAuthor.Id }));
                }

	            // Broadcast a fresh resource with up-to-date statistics so tiles update live
	            BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(updatedAuthor));

	            return Accepted(updatedAuthor.Id);
	        }

        // Everything about the author's state BEFORE this request that the cascade below needs to
        // reason about - captured before ToModel/ApplyChanges mutate the author in place. Bundled into
        // one type, captured in one line at the top of UpdateAuthor, so there's no set of loose local
        // variables for a later edit to accidentally reorder relative to the ToModel call.
        internal readonly record struct StoredAuthorMonitoringState(
            string AudiobookRootFolderPath,
            string EbookRootFolderPath,
            int? AudiobookMonitorExisting,
            bool? AudiobookMonitorFuture,
            int? EbookMonitorExisting,
            bool? EbookMonitorFuture,
            bool WasMonitoredFromMediaSettings)
        {
            public static StoredAuthorMonitoringState Capture(NzbDrone.Core.Books.Author author)
            {
                return new StoredAuthorMonitoringState(
                    author.AudiobookRootFolderPath,
                    author.EbookRootFolderPath,
                    author.AudiobookMonitorExisting,
                    author.AudiobookMonitorFuture,
                    author.EbookMonitorExisting,
                    author.EbookMonitorFuture,
                    author.IsMonitoredFromMediaSettings());
            }
        }

        // AuthorService.UpdateAuthor recomputes the legacy Monitored flag from the per-media-type
        // tri-state settings on every save (author.Monitored = author.IsMonitoredFromMediaSettings()),
        // and separately fills any still-null tri-state field from the root folder's defaults before
        // that recompute runs. So a request that only sets the top-level "monitored" field - without
        // touching any of the four tri-state fields for a media type - has it silently discarded:
        // whatever those fields already were (or get defaulted to) wins, and the recompute overwrites
        // the requested value right back.
        //
        // The tricky part: almost every real client, including Chaptarr's own UI toggle and any script
        // that does GET -> flip one field -> PUT the whole object back, sends a full object on every
        // PUT - so both `monitored` AND the four tri-state fields ride along unchanged from the last
        // GET on saves that have nothing to do with monitoring. A client that only means to flip
        // `monitored` is indistinguishable, at the JSON level, from one that's echoing it - and the
        // same is true of each tri-state field individually. Treating a tri-state field's mere presence
        // in the request as "the client edited this" (an earlier version of this fix did exactly that)
        // breaks the GET-modify-PUT case specifically: the four tri-state fields ride along unchanged,
        // read as "touched", and the cascade skips every media type - the original bug, undisturbed.
        // The only reliable signal, for `monitored` and for each tri-state field alike, is whether the
        // request's value actually differs from what was already stored - a same-value echo is then
        // always a no-op, by construction, whichever fields happen to be present.
        //
        // The one case that signal alone would miss: a media type gaining a root folder in this exact
        // request. AuthorService's root-folder-defaults fill only resolves a media type that has a
        // root folder, so a media type with none yet isn't at risk from it - until this request gives
        // it one, at which point an explicit "monitored" on the same request needs to apply there too,
        // whether or not the top-level flag also happened to change.
        //
        // Per FIELD (not per media type - see the comment further down for why that distinction
        // matters), a field the client's request genuinely changes is left alone entirely (that
        // explicit edit wins, same as it always has), and a media type with no root folder - on the
        // author as it's stored OR as this same request is assigning one - is skipped entirely since
        // there's nothing to monitor there. This mirrors, and runs alongside, the equivalent single-
        // media-type cascade ToModel already does for a Readarr-facade request - kept as a separate
        // pass here (rather than folded into ToModel) because it needs the author's stored state, not
        // just what's on the incoming resource.
        // Internal (rather than private) so the test fixture can call it directly instead of via
        // reflection - a reflection-based call would only fail at test runtime, not compile time, if
        // this signature's parameter order or count ever changed. Thin AuthorResource-shaped wrapper
        // around the primitive-parameter overload below, which AuthorEditorController's bulk PUT also
        // calls directly (AuthorEditorResource has the same four tri-state fields plus Monitored, but
        // isn't an AuthorResource) - the underlying "is this a genuine change, per media type" logic is
        // identical for both endpoints, only the source of the requested values differs.
        internal static void CascadeExplicitMonitoredIntoMediaTypeSettings(
            AuthorResource authorResource,
            NzbDrone.Core.Books.Author model,
            ReadarrFacadeContext facadeContext,
            StoredAuthorMonitoringState stored)
        {
            if (facadeContext != null)
            {
                return;
            }

            CascadeExplicitMonitoredIntoMediaTypeSettings(
                authorResource.Monitored,
                authorResource.AudiobookMonitorExisting,
                authorResource.AudiobookMonitorFuture,
                authorResource.EbookMonitorExisting,
                authorResource.EbookMonitorFuture,
                model,
                stored);
        }

        internal static void CascadeExplicitMonitoredIntoMediaTypeSettings(
            bool? requestedMonitored,
            int? requestedAudiobookMonitorExisting,
            bool? requestedAudiobookMonitorFuture,
            int? requestedEbookMonitorExisting,
            bool? requestedEbookMonitorFuture,
            NzbDrone.Core.Books.Author model,
            StoredAuthorMonitoringState stored)
        {
            if (!requestedMonitored.HasValue)
            {
                return;
            }

            var monitored = requestedMonitored.Value;
            var isGenuineChange = monitored != stored.WasMonitoredFromMediaSettings;

            // Per FIELD, not per media type: a client that explicitly sends one of a media type's two
            // tri-state fields but not the other (e.g. Chaptarr's own bulk author editor, which always
            // sends MonitorExisting when changing monitoring but never sends MonitorFuture) still needs
            // the untouched field to pick up the top-level flag - otherwise that field is left wherever
            // it was, `IsMonitoredFromMediaSettings()` still reads it, and the requested change can
            // silently fail to take effect even though the field the client DID send applied correctly.
            // An earlier version of this gated on either field being present, which protected the whole
            // media type from any cascading the moment ONE field was touched - safe for the single-
            // author PUT's echo scenarios, but a no-op for exactly the real bulk-editor payload shape.
            var audiobookHasNewRootFolder = stored.AudiobookRootFolderPath.IsNullOrWhiteSpace() && !model.AudiobookRootFolderPath.IsNullOrWhiteSpace();
            var audiobookConfigured = !stored.AudiobookRootFolderPath.IsNullOrWhiteSpace() || audiobookHasNewRootFolder;

            // isGenuineChange alone would miss a media type gaining a root folder in THIS same
            // request. A media type with no root folder before this PUT isn't a candidate for
            // monitoring at all yet, whatever its tri-state fields happen to hold - AuthorService's
            // root-folder-defaults fill (which resolves any still-null field once a media type has a
            // root folder) is the common way that becomes reachable, but stale non-null values from a
            // previous, since-removed root folder are also possible. Either way, once this request
            // gives it a root folder, it's a real candidate, and the explicit "monitored" on this same
            // request should apply there too - whether or not the top-level flag also happened to
            // change relative to the author's overall prior state.
            if (audiobookConfigured && (isGenuineChange || audiobookHasNewRootFolder))
            {
                var audiobookFutureChanged = requestedAudiobookMonitorFuture.HasValue && requestedAudiobookMonitorFuture != stored.AudiobookMonitorFuture;
                var audiobookExistingChanged = requestedAudiobookMonitorExisting.HasValue && requestedAudiobookMonitorExisting != stored.AudiobookMonitorExisting;
                var overrodeSomething = false;

                // Turning off needs both fields cleared - MonitorExisting > 0 alone would still keep
                // the author monitored, so if the client isn't already clearing Future itself, this has
                // to. Turning on doesn't have that asymmetry: MonitorExisting > 0 alone already
                // satisfies "monitored", so only force Future when Existing (as this request leaves it)
                // won't - otherwise this would silently flip on "monitor new releases" for a media type
                // whose future-monitoring preference nobody asked to change.
                var audiobookNeedsFuture = !monitored || (model.AudiobookMonitorExisting ?? 0) <= 0;
                if (!audiobookFutureChanged && audiobookNeedsFuture)
                {
                    model.AudiobookMonitorFuture = monitored;
                    overrodeSomething = true;
                }

                if (!monitored && !audiobookExistingChanged)
                {
                    model.AudiobookMonitorExisting = 0;
                    overrodeSomething = true;
                }

                if (overrodeSomething)
                {
                    model.AudiobookSettingsManuallyOverridden = true;
                }
            }

            var ebookHasNewRootFolder = stored.EbookRootFolderPath.IsNullOrWhiteSpace() && !model.EbookRootFolderPath.IsNullOrWhiteSpace();
            var ebookConfigured = !stored.EbookRootFolderPath.IsNullOrWhiteSpace() || ebookHasNewRootFolder;

            if (ebookConfigured && (isGenuineChange || ebookHasNewRootFolder))
            {
                var ebookFutureChanged = requestedEbookMonitorFuture.HasValue && requestedEbookMonitorFuture != stored.EbookMonitorFuture;
                var ebookExistingChanged = requestedEbookMonitorExisting.HasValue && requestedEbookMonitorExisting != stored.EbookMonitorExisting;
                var overrodeSomething = false;

                var ebookNeedsFuture = !monitored || (model.EbookMonitorExisting ?? 0) <= 0;
                if (!ebookFutureChanged && ebookNeedsFuture)
                {
                    model.EbookMonitorFuture = monitored;
                    overrodeSomething = true;
                }

                if (!monitored && !ebookExistingChanged)
                {
                    model.EbookMonitorExisting = 0;
                    overrodeSomething = true;
                }

                if (overrodeSomething)
                {
                    model.EbookSettingsManuallyOverridden = true;
                }
            }
        }

        private bool HasSyncMonitoredAcrossFormatsEligibility(NzbDrone.Core.Books.Author author)
        {
            var rootFolders = _rootFolderService.All();
            return HasCompatibleRootFolder(author, rootFolders, BookMediaType.Audiobook) &&
                   HasCompatibleRootFolder(author, rootFolders, BookMediaType.Ebook);
        }

        private static bool HasCompatibleRootFolder(NzbDrone.Core.Books.Author author, List<RootFolder> rootFolders, BookMediaType mediaType)
        {
            if (author == null || rootFolders == null || rootFolders.Count == 0)
            {
                return false;
            }

            var rootFolderPath = mediaType == BookMediaType.Audiobook
                ? author.AudiobookRootFolderPath
                : author.EbookRootFolderPath;

            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var rootFolder = rootFolders.FirstOrDefault(r => r.Path.PathEquals(rootFolderPath));
            if (rootFolder == null)
            {
                return false;
            }

            return rootFolder.FolderType == FolderType.Mixed ||
                   (mediaType == BookMediaType.Audiobook && rootFolder.FolderType == FolderType.Audiobook) ||
                   (mediaType == BookMediaType.Ebook && rootFolder.FolderType == FolderType.Ebook);
        }

        [RestDeleteById]
        public async Task<ActionResult> DeleteAuthor(int id, bool deleteFiles = false, bool addImportListExclusion = false, bool readdAuthor = false)
        {
            if (readdAuthor && deleteFiles)
            {
                return BadRequest(new ApiErrorResource { Message = "Cannot combine file deletion with re-add." });
            }

            if (readdAuthor)
            {
                var author = _authorService.GetAuthor(id);

                // Resolve provider ID with full fallback chain and normalization
                var foreignAuthorId = !string.IsNullOrWhiteSpace(author.HardcoverAuthorId)
                    ? ProviderIdHelper.Normalize(author.HardcoverAuthorId, "hc")
                    : !string.IsNullOrWhiteSpace(author.GoodreadsAuthorId)
                        ? ProviderIdHelper.Normalize(author.GoodreadsAuthorId, "gr")
                        : !string.IsNullOrWhiteSpace(author.OpenLibraryAuthorId)
                            ? ProviderIdHelper.Normalize(author.OpenLibraryAuthorId, "ol")
                            : !string.IsNullOrWhiteSpace(author.GoogleBooksAuthorId)
                                ? ProviderIdHelper.Normalize(author.GoogleBooksAuthorId, "gb")
                                : !string.IsNullOrWhiteSpace(author.AudnexusAuthorId)
                                    ? ProviderIdHelper.Normalize(author.AudnexusAuthorId, "az")
                                    : null;

                if (string.IsNullOrWhiteSpace(foreignAuthorId))
                {
                    return BadRequest(new ApiErrorResource { Message = "Cannot re-add author: no provider ID found." });
                }

                var config = new MonitoringConfig
                {
                    MonitorNewItems = author.Monitored,
                    IsManualAddition = true,
                    CreateAudiobook = !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath),
                    CreateEbook = !string.IsNullOrWhiteSpace(author.EbookRootFolderPath),
                    AudiobookMonitorExisting = author.AudiobookMonitorExisting,
                    AudiobookMonitorFuture = author.AudiobookMonitorFuture,
                    EbookMonitorExisting = author.EbookMonitorExisting,
                    EbookMonitorFuture = author.EbookMonitorFuture,
                    AudiobookQualityProfileId = author.AudiobookQualityProfileId,
                    EbookQualityProfileId = author.EbookQualityProfileId,
                    AudiobookMetadataProfileId = author.AudiobookMetadataProfileId,
                    EbookMetadataProfileId = author.EbookMetadataProfileId,
                    AudiobookRootFolderPath = author.AudiobookRootFolderPath,
                    EbookRootFolderPath = author.EbookRootFolderPath,
                    Tags = author.Tags,
                    AudiobookTags = author.AudiobookTags,
                    EbookTags = author.EbookTags,
                    SearchForMissingBooks = false,
                    RequestedBy = "PurgeAndRescan",
                    AuthorName = author.Name
                };

                try
                {
                    // Preflight the metadata fetch/add path before deleting the local row. AddAuthorAsync fetches
                    // remote metadata before resolving the existing local author, so this catches metadata-server
                    // and mapper failures without stranding the library in a deleted-but-not-readded state.
                    await _authorLibraryService.AddAuthorAsync(foreignAuthorId, config);
                }
                catch (AuthorNotFoundException)
                {
                    return NotFound(new ApiErrorResource
                    {
                        Message = "Cannot re-add author: the author is not available on the metadata server."
                    });
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Cannot re-add author {0}: preflight failed before deleting local row", author.Name);
                    return StatusCode(500, new ApiErrorResource
                    {
                        Message = "Cannot re-add author because the metadata refresh preflight failed. The existing author was not deleted."
                    });
                }

                // Delete the author metadata but retain the existing BookFile rows. Synchronous
                // deletion handlers unlink those rows to EditionId=0 without discarding their
                // stored tags/media evidence; keep only their local row IDs for a targeted retry.
                var retainedBookFileIds = _authorService.DeleteAuthorForReadd(id) ?? new List<int>();

                // Re-add from scratch
                var newAuthor = await _authorLibraryService.AddAuthorAsync(foreignAuthorId, config);

                if (newAuthor != null && newAuthor.Id > 0 && retainedBookFileIds.Any())
                {
                    _commandQueueManager.Push(
                        new RetryUnmappedMatchCommand
                        {
                            MediaType = "all",
                            UnmappedFiles = new UnmappedFilesSelection
                            {
                                Scope = "selected",
                                BookFileIds = retainedBookFileIds
                            }
                        },
                        CommandPriority.Normal,
                        CommandTrigger.Manual);
                }

                return Ok();
            }

            _authorService.DeleteAuthor(id, deleteFiles, addImportListExclusion);
            return Ok();
        }

        [HttpPost("{id}/downloadmedia")]
        public IActionResult DownloadAuthorMedia(int id, bool forceDownload = false)
        {
            var author = _authorService.GetAuthor(id);

            if (author == null)
            {
                return NotFound();
            }

            _commandQueueManager.Push(new DownloadAuthorMediaCommand(id, forceDownload));

            return Accepted();
        }

        [HttpPost("statistics/aggregate")]
        public ActionResult<BookAggregateResource> GetAggregateStatistics([FromBody] AggregateStatisticsRequest request)
        {
            if (request?.AuthorIds == null || request.AuthorIds.Count == 0)
            {
                return Ok(new BookAggregateResource 
                { 
                    BookCount = 0, 
                    FileCount = 0, 
                    TotalFileSize = 0 
                });
            }

            var stats = _authorStatisticsService.GetAggregateStatistics(request.AuthorIds, request.MediaType ?? "all");
            
            return Ok(new BookAggregateResource 
            { 
                BookCount = stats.BookCount,
                FileCount = stats.BookFileCount,
                TotalFileSize = stats.SizeOnDisk
            });
        }

        private void MapCoversToLocal(params AuthorResource[] authors)
        {
            foreach (var authorResource in authors)
            {
                _coverMapper.ConvertToLocalUrls(authorResource.Id, MediaCoverEntity.Author, authorResource.Images, authorResource.SelectedPosterHash);
            }
        }

        private void LinkNextPreviousBooks(params AuthorResource[] authors)
        {
            var nextBooks = _bookService.GetNextBooksByAuthorId(authors.Select(x => x.Id));
            var lastBooks = _bookService.GetLastBooksByAuthorId(authors.Select(x => x.Id));

            foreach (var authorResource in authors)
            {
                authorResource.NextBook = ToAuthorIndexBookResource(nextBooks.FirstOrDefault(x => x.AuthorId == authorResource.Id));
                authorResource.LastBook = ToAuthorIndexBookResource(lastBooks.FirstOrDefault(x => x.AuthorId == authorResource.Id));
            }
        }

        private static BookResource ToAuthorIndexBookResource(Book book)
        {
            if (book == null)
            {
                return null;
            }

            var resource = book.ToResource();
            resource.Author = null;
            return resource;
        }

        private void FetchAndLinkAuthorStatistics(AuthorResource resource, string mediaType = null)
        {
            var normalizedMediaType = MediaTypeParameterParser.NormalizeOptional(mediaType);
            var stats = normalizedMediaType == null
                ? _authorStatisticsService.AuthorStatistics(resource.Id)
                : _authorStatisticsService.AuthorStatistics(resource.Id, normalizedMediaType);
            LinkAuthorStatistics(resource, stats);
        }

        private void LinkAuthorStatistics(List<AuthorResource> resources, Dictionary<int, AuthorStatistics> authorStatistics)
        {
            foreach (var author in resources)
            {
                if (authorStatistics.TryGetValue(author.Id, out var stats))
                {
                    LinkAuthorStatistics(author, stats);
                }
            }
        }

        private void LinkAuthorStatistics(AuthorResource resource, AuthorStatistics authorStatistics)
        {
            resource.Statistics = authorStatistics.ToResource();
        }

        private void LinkRootFolderPath(IEnumerable<NzbDrone.Core.Books.Author> authorModels, params AuthorResource[] authors)
        {
            var authorsById = authorModels.ToDictionary(author => author.Id);

            // Compute the author folder name for each author
            foreach (var resource in authors)
            {
                if (resource == null) continue;
                
                try
                {
                    if (authorsById.TryGetValue(resource.Id, out var author))
                    {
                        // Set the computed author folder name (uses the primary root folder)
                        resource.Folder = !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath)
                            ? _fileNameBuilder.GetAuthorFolder(author, mediaType: "audiobook")
                            : (!string.IsNullOrWhiteSpace(author.EbookRootFolderPath)
                                ? _fileNameBuilder.GetAuthorFolder(author, mediaType: "ebook")
                                : _fileNameBuilder.GetAuthorFolder(author));

                        // IMPORTANT: expose real per-media-type author folder paths when available.
                        // These are the discovered/linked paths (what's actually on disk), not computed naming-config targets.
                        if (!string.IsNullOrWhiteSpace(author.AudiobookPath))
                        {
                            resource.AudiobookFolder = author.AudiobookPath.GetCleanPath();
                        }

                        if (!string.IsNullOrWhiteSpace(author.EbookPath))
                        {
                            resource.EbookFolder = author.EbookPath.GetCleanPath();
                        }

                        // Back-compat fallback: if we don't have a discovered/linked per-type path yet, compute a best-effort
                        // target under the configured root folder using the naming config.
                        if (string.IsNullOrWhiteSpace(resource.AudiobookFolder) && !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath))
                        {
                            var authorFolderName = _fileNameBuilder.GetAuthorFolder(author, mediaType: "audiobook");
                            if (!string.IsNullOrWhiteSpace(authorFolderName))
                            {
                                resource.AudiobookFolder = Path.Combine(author.AudiobookRootFolderPath, authorFolderName).GetCleanPath();
                            }
                        }

                        if (string.IsNullOrWhiteSpace(resource.EbookFolder) && !string.IsNullOrWhiteSpace(author.EbookRootFolderPath))
                        {
                            var authorFolderName = _fileNameBuilder.GetAuthorFolder(author, mediaType: "ebook");
                            if (!string.IsNullOrWhiteSpace(authorFolderName))
                            {
                                resource.EbookFolder = Path.Combine(author.EbookRootFolderPath, authorFolderName).GetCleanPath();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to compute author folder for author {0}", resource.Id);
                }
            }
        }

        [HttpPut("{id:int}/monitor/{mediaType}")]
        public ActionResult SetMediaTypeMonitoring(int id, string mediaType, [FromBody] MonitoringResource resource)
        {
            var normalizedMediaType = MediaTypeParameterParser.ToApiValue(MediaTypeParameterParser.ParseRequired(mediaType));

            _authorService.SetMediaTypeMonitoring(id, normalizedMediaType, resource.Monitored);

            // Trigger UI update
            var author = _authorService.GetAuthor(id);
            _eventAggregator.PublishEvent(new AuthorEditedEvent(author, author));

            return Ok();
        }

        [HttpGet("{id:int}/size/{mediaType}")]
        public ActionResult<long> GetMediaTypeSize(int id, string mediaType)
        {
            var normalizedMediaType = MediaTypeParameterParser.ToApiValue(MediaTypeParameterParser.ParseRequired(mediaType));

            var size = _authorService.GetAuthorSizeForMediaType(id, normalizedMediaType);
            return Ok(size);
        }

        [HttpPut("{id:int}/selectedMediaType/{mediaType}")]
        public ActionResult UpdateSelectedMediaType(int id, string mediaType)
        {
            var normalizedMediaType = MediaTypeParameterParser.ToApiValue(MediaTypeParameterParser.ParseRequired(mediaType));

            _authorService.UpdateLastSelectedMediaType(id, normalizedMediaType);
            return Ok();
        }

        [HttpPut("{id:int}/primaryPhoto")]
        public async Task<ActionResult> SetPrimaryPhoto(int id, [FromBody] SetPrimaryPhotoRequest request)
        {
            var author = _authorService.GetAuthor(id);
            if (author == null)
            {
                return NotFound();
            }

            try
            {
                // Find the photo by URL
                var targetImage = author.Images
                    .Where(img => img.CoverType == MediaCoverTypes.Poster)
                    .FirstOrDefault(img => !string.IsNullOrEmpty(request.PhotoUrl) && img.Url == request.PhotoUrl);

                if (targetImage == null)
                {
                    return BadRequest("Photo not found");
                }

                // Ensure the selected image is downloaded on-demand
                var result = await _coverMapper.EnsureAuthorImage(author, targetImage);
                if (result.State == "error")
                {
                    _logger.Warn("Failed to download selected author image: {0}", result.ErrorCode);
                    if (result.ErrorCode == "placeholder_image")
                    {
                        RemoveRejectedAuthorImage(author, targetImage.Url);
                        return BadRequest("Selected photo is a provider placeholder");
                    }

                    return StatusCode(502, "Failed to download selected photo");
                }

                // Persist a stable selection token only after the replacement image has
                // passed content validation and exists locally.
                var selectedHash = AuthorImageHashHelper.ComputeStableImageHash(targetImage.Url, targetImage.CoverType);
                author.SelectedPosterHash = selectedHash;

                // Save the updated author with SelectedPosterHash
                _authorService.UpdateAuthor(author);

                _logger.Info("User set primary photo for author {0} (ID: {1}) to URL: {2} with hash: {3}",
                    author.Name, author.Id, targetImage.Url, selectedHash);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error setting primary photo for author {0}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpPost("{id:int}/loadImage")]
        [ProducesResponseType(typeof(LoadImageResponseResource), 200)]
        [ProducesResponseType(typeof(LoadImageResponseResource), 502)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ApiErrorResource), 404)]
        [ProducesResponseType(typeof(ApiErrorResource), 500)]
        public async Task<ActionResult<LoadImageResponseResource>> LoadAuthorImage(int id, [FromBody] LoadImageRequest request)
        {
            var author = _authorService.GetAuthor(id);
            if (author == null)
            {
                return NotFound(new ApiErrorResource { Error = "Author not found" });
            }

            if (string.IsNullOrEmpty(request?.ImageUrl))
            {
                return BadRequest(new ApiErrorResource { Error = "ImageUrl is required" });
            }

            try
            {
                // Find the image in author's metadata
                var targetImage = author.Images
                    .Where(img => img.CoverType == MediaCoverTypes.Poster)
                    .FirstOrDefault(img => img.Url == request.ImageUrl);

                if (targetImage == null)
                {
                    return BadRequest(new ApiErrorResource { Error = "Image not found in author metadata" });
                }

                // Download the image on-demand
                var result = await _coverMapper.EnsureAuthorImage(author, targetImage);

                if (result.State == "downloaded")
                {
                    // Return the local path for immediate display
                    return Ok(new LoadImageResponseResource
                    {
                        Status = "success",
                        LocalPath = result.Path?.Replace(_appFolderInfo.GetAppDataPath(), "").Replace("\\", "/"),
                        Message = "Image downloaded successfully"
                    });
                }
                else if (result.State == "pending")
                {
                    return Ok(new LoadImageResponseResource
                    {
                        Status = "pending",
                        Message = "Image download in progress"
                    });
                }
                else
                {
                    if (result.ErrorCode == "placeholder_image")
                    {
                        RemoveRejectedAuthorImage(author, targetImage.Url);
                    }

                    return StatusCode(502, new LoadImageResponseResource
                    {
                        Status = "error",
                        ErrorCode = result.ErrorCode,
                        Message = "Failed to download image"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error loading image for author {0}", id);
                return StatusCode(500, new ApiErrorResource { Error = "Internal server error" });
            }
        }

        private void RemoveRejectedAuthorImage(NzbDrone.Core.Books.Author author, string rejectedUrl)
        {
            if (author?.Images == null || string.IsNullOrWhiteSpace(rejectedUrl))
            {
                return;
            }

            var rejectedHash = AuthorImageHashHelper.ComputeStableImageHash(rejectedUrl, MediaCoverTypes.Poster);
            var before = author.Images.Count;
            author.Images = author.Images
                .Where(image => image != null && !MediaCoverRendition.IsKnownPlaceholderImageUrl(image.Url))
                .ToList();

            var clearedSelection = !string.IsNullOrWhiteSpace(author.SelectedPosterHash) &&
                                   author.SelectedPosterHash == rejectedHash;
            if (clearedSelection)
            {
                author.SelectedPosterHash = null;
            }

            if (author.Images.Count != before || clearedSelection)
            {
                _authorService.UpdateAuthor(author);
            }
        }

	        [NonAction]
	        public void Handle(BookImportedEvent message)
	        {
                var authorId = message.Author?.Id ?? message.Book?.AuthorId ?? 0;
                QueueAuthorUpdate(authorId);
	        }

        [NonAction]
        public void Handle(BookEditedEvent message)
        {
            // ALWAYS fetch fresh author data to ensure we have complete data including images
            // Don't trust message.Book.Author as it may be partial/lazy-loaded
            if (message.Book.AuthorId > 0)
            {
                var author = _authorService.GetAuthor(message.Book.AuthorId);
                BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(author));
            }
            // If we can't determine the author ID, skip the broadcast
            // (this should rarely happen with properly structured data)
        }

        [NonAction]
        public void Handle(BookFileDeletedEvent message)
        {
            if (message.Reason == DeleteMediaFileReason.Upgrade)
            {
                return;
            }

            try
            {
                var authorId = message.BookFile?.Author?.Id ?? message.BookFile?.Edition?.Book?.AuthorId ?? 0;
                QueueAuthorUpdate(authorId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[UI-BROADCAST] Failed to broadcast Author update for BookFileDeletedEvent");
            }
        }

	        [NonAction]
	        public void Handle(BookFileAddedEvent message)
	        {
	            try
	            {
                    var authorId = message.BookFile?.Author?.Id ?? message.BookFile?.Edition?.Book?.AuthorId ?? 0;
                    QueueAuthorUpdate(authorId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[UI-BROADCAST] Failed to broadcast Author update for BookFileAddedEvent");
            }
        }

	        [NonAction]
	        public void Handle(BookFilesAddedEvent message)
	        {
	            try
	            {
	                if (message?.BookFiles == null || message.BookFiles.Count == 0) return;

                var authorIds = new HashSet<int>();
                foreach (var f in message.BookFiles)
                {
                    var a = f?.Author;
                    if (a != null && a.Id > 0)
                    {
                        authorIds.Add(a.Id);
                    }
                    else if (f?.Edition?.Book != null)
                    {
                        authorIds.Add(f.Edition.Book.AuthorId);
                    }
                }

                foreach (var id in authorIds)
                {
                    QueueAuthorUpdate(id);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[UI-BROADCAST] Failed to broadcast Author update for BookFilesAddedEvent");
	            }
	        }

            [NonAction]
            public void Handle(BookFileUpdatedEvent message)
            {
                try
                {
                    var authorId = message.BookFile?.Author?.Id ?? message.BookFile?.Edition?.Book?.AuthorId ?? 0;
                    QueueAuthorUpdate(authorId);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[UI-BROADCAST] Failed to broadcast Author update for BookFileUpdatedEvent");
                }
            }

	        [NonAction]
            [EventHandleOrder(EventHandleOrder.Last)]
		        public void Handle(ImportStageProgressEvent message)
		        {
		            if (!message.CommandId.HasValue)
		            {
	                return;
	            }

                var shouldSync = false;
	            lock (_importStateLock)
	            {
	                if (message.Stage == ImportStage.ImportComplete)
	                {
	                    _activeImportCommands.Remove(message.CommandId.Value);
                        shouldSync = _activeImportCommands.Count == 0;
	                }
	                else
	                {
	                    _activeImportCommands.Add(message.CommandId.Value);
	                }
	            }

	            if (message.Stage == ImportStage.ImportComplete && shouldSync)
	            {
                    _pendingAuthorUpdates.Clear();
	                // Resync once when the import finishes to guarantee consistency.
		                BroadcastResourceChange(ModelAction.Sync);
		            }
		        }

	        [NonAction]
            [EventHandleOrder(EventHandleOrder.Last)]
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
	                _pendingAuthorUpdates.Clear();
	                BroadcastResourceChange(ModelAction.Sync);
	            }
	        }

		        [NonAction]
		        public void Handle(AuthorAddedEvent message)
	        {
	            BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(message.Author));
	        }

        [NonAction]
        public void Handle(AuthorUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(message.Author));
        }

        [NonAction]
        public void Handle(AuthorEditedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(message.Author));
        }

        [NonAction]
        public void Handle(AuthorDeletedEvent message)
        {
            BroadcastResourceChange(ModelAction.Deleted, message.Author.ToResource());
        }

        [NonAction]
        public void Handle(AuthorRenamedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, message.Author.Id);
        }

        [NonAction]
        public void Handle(MediaCoversUpdatedEvent message)
        {
            if (message.Author == null) return;
            BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(message.Author));
        }

        [NonAction]
        public void Handle(AuthorRefreshCompleteEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(message.Author));
        }
    }
}
