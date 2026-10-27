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
using Microsoft.AspNetCore.Mvc.Filters;
using NzbDrone.Common.Disk;
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
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
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
        private readonly IBookMonitoredService _bookMonitoredService;
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
                            IBookMonitoredService bookMonitoredService,
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
            _bookMonitoredService = bookMonitoredService;
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
	                           .SetValidator(qualityProfileExistsValidator.ForType(ProfileType.Audiobook))
	                           .When(s => s.AudiobookQualityProfileId.HasValue && s.AudiobookQualityProfileId.Value > 0);
	            SharedValidator.RuleFor(s => s.EbookQualityProfileId)
	                           .SetValidator(qualityProfileExistsValidator.ForType(ProfileType.Ebook))
	                           .When(s => s.EbookQualityProfileId.HasValue && s.EbookQualityProfileId.Value > 0);
	            SharedValidator.RuleFor(s => s.AudiobookMetadataProfileId)
	                           .SetValidator(metadataProfileExistsValidator.ForType(MetadataProfileType.Audiobook))
	                           .When(s => s.AudiobookMetadataProfileId.HasValue && s.AudiobookMetadataProfileId.Value > 0);
	            SharedValidator.RuleFor(s => s.EbookMetadataProfileId)
	                           .SetValidator(metadataProfileExistsValidator.ForType(MetadataProfileType.Ebook))
	                           .When(s => s.EbookMetadataProfileId.HasValue && s.EbookMetadataProfileId.Value > 0);
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

            public override void OnActionExecuting(ActionExecutingContext context)
            {
                var facadeContext = context.HttpContext.GetReadarrFacadeContext();
                var mediaType = context.HttpContext.Request.Query["mediaType"].ToString();
                var resources = context.ActionArguments.Values
                    .SelectMany(value => value switch
                    {
                        AuthorResource resource => new[] { resource },
                        IEnumerable<AuthorResource> multiple => multiple,
                        _ => Enumerable.Empty<AuthorResource>()
                    });

                foreach (var resource in resources)
                {
                    NormalizeWriteResource(resource, facadeContext, mediaType);
                }

                base.OnActionExecuting(context);
            }

            private static void NormalizeWriteResource(AuthorResource resource, ReadarrFacadeContext facadeContext, string mediaType)
            {
                AuthorResourceMapper.NormalizeLegacySingleFields(resource, facadeContext, mediaType);
                if (resource != null &&
                    !AuthorResourceMapper.HasAudiobookSpecificSettings(resource) &&
                    !AuthorResourceMapper.HasEbookSpecificSettings(resource) &&
                    (resource.QualityProfileId.HasValue || resource.MetadataProfileId.HasValue ||
                     !string.IsNullOrWhiteSpace(resource.RootFolderPath)))
                {
                    throw new Chaptarr.Http.REST.BadRequestException(MediaTypeParameterParser.FormatRequiredMessage);
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
            catch { /* Non-fatal: fall back to the combined statistics only */ }

            return resource;
        }

        [HttpGet]
        public List<AuthorResource> AllAuthors([FromQuery] string mediaType = null)
        {
            var normalizedMediaType = MediaTypeParameterParser.NormalizeOptional(mediaType);
            var authors = _authorService.GetAllAuthors();
            var authorResources = authors.ToResource(HttpContext.GetReadarrFacadeContext());

            MapCoversToLocal(authorResources.ToArray());
            LinkNextPreviousBooks(authorResources.ToArray());

            if (normalizedMediaType == null)
            {
                var audiobookStatistics = _authorStatisticsService.AuthorStatistics("audiobook").ToDictionary(x => x.AuthorId);
                var ebookStatistics = _authorStatisticsService.AuthorStatistics("ebook").ToDictionary(x => x.AuthorId);
                LinkMediaTypeAuthorStatistics(authorResources, audiobookStatistics, ebookStatistics);
            }
            else
            {
                var authorStatistics = _authorStatisticsService.AuthorStatistics(normalizedMediaType).ToDictionary(x => x.AuthorId);
                LinkAuthorStatistics(authorResources, authorStatistics);
            }

            LinkRootFolderPath(authors, authorResources.ToArray());

            return authorResources;
        }


        private ActionResult GetProviderAmbiguityResult(ProviderAmbiguityResource ambiguity)
        {
            return ambiguity == null ? null : StatusCode(ProviderAmbiguityHelper.StatusCode, ambiguity);
        }

        [RestPostById]
        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
        [ProducesResponseType(typeof(ApiErrorResource), 422)]
        public async Task<ActionResult<AuthorResource>> AddAuthor([FromBody] AuthorResource authorResource, [FromQuery] bool queueIfUnavailable = true, [FromQuery] string mediaType = null)
        {
            var facadeContext = HttpContext.GetReadarrFacadeContext();
            NormalizeWriteResource(authorResource, facadeContext, mediaType);
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

            var addOptions = authorResource.AddOptions;
            var addMonitorMode = addOptions?.Monitor;
            var audiobookMonitorExistingMode = authorResource.AudiobookMonitorExistingMode ??
                (addOptions?.MediaType is null || addOptions.MediaType == BookMediaType.Audiobook ? addMonitorMode : null);
            var ebookMonitorExistingMode = authorResource.EbookMonitorExistingMode ??
                (addOptions?.MediaType is null || addOptions.MediaType == BookMediaType.Ebook ? addMonitorMode : null);
            var specificBookProviderIds = addOptions?.Monitor == MonitorTypes.SpecificBook
                ? addOptions.BooksToMonitor?.Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var exactAudiobookRequest = specificBookProviderIds?.Any() == true && addOptions?.MediaType == BookMediaType.Audiobook;
            var exactEbookRequest = specificBookProviderIds?.Any() == true && addOptions?.MediaType == BookMediaType.Ebook;
            var audiobookSpecificBookProviderIds = addOptions?.MediaType == BookMediaType.Ebook ? null : specificBookProviderIds;
            var ebookSpecificBookProviderIds = addOptions?.MediaType == BookMediaType.Audiobook ? null : specificBookProviderIds;
            var lastSelectedMediaType = string.IsNullOrWhiteSpace(authorResource.LastSelectedMediaType)
                ? null
                : MediaTypeParameterParser.NormalizeOptional(authorResource.LastSelectedMediaType, allowAll: false);

            var config = new MonitoringConfig
            {
                IsManualAddition = true,
                IsAuthorSettingsRequest = !(exactAudiobookRequest || exactEbookRequest),
                SyncMonitoredAcrossFormats = authorResource.SyncMonitoredAcrossFormats,
                CreateAudiobook = !string.IsNullOrWhiteSpace(authorResource.AudiobookRootFolderPath),
                CreateEbook = !string.IsNullOrWhiteSpace(authorResource.EbookRootFolderPath),
                AudiobookMonitored = exactAudiobookRequest ? true : authorResource.AudiobookMonitored,
                AudiobookMonitorNewItems = authorResource.AudiobookMonitorNewItems,
                AudiobookMonitorExistingMode = audiobookMonitorExistingMode,
                EbookMonitored = exactEbookRequest ? true : authorResource.EbookMonitored,
                EbookMonitorNewItems = authorResource.EbookMonitorNewItems,
                EbookMonitorExistingMode = ebookMonitorExistingMode,
                AudiobookQualityProfileId = authorResource.AudiobookQualityProfileId,
                EbookQualityProfileId = authorResource.EbookQualityProfileId,
                AudiobookMetadataProfileId = authorResource.AudiobookMetadataProfileId,
                EbookMetadataProfileId = authorResource.EbookMetadataProfileId,
                AudiobookRootFolderPath = authorResource.AudiobookRootFolderPath,
                EbookRootFolderPath = authorResource.EbookRootFolderPath,
                LastSelectedMediaType = lastSelectedMediaType,
                QueueIfUnavailable = queueIfUnavailable,
                Tags = authorResource.Tags,
                AudiobookTags = authorResource.AudiobookTags,
                EbookTags = authorResource.EbookTags,
                SearchForMissingBooks = authorResource.AddOptions?.SearchForMissingBooks,
                RequestedBy = MonitoringConfig.NativeAuthorAddOrigin,
                AuthorName = authorResource.AuthorName,
                MonitorMode = addOptions?.Monitor,
                AudiobookBooksToMonitor = audiobookSpecificBookProviderIds?.ToList(),
                EbookBooksToMonitor = ebookSpecificBookProviderIds?.ToList(),
                SpecificBookProviderIds = specificBookProviderIds,
                SpecificBookMediaType = addOptions?.MediaType
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

            if (config.CreateAudiobook)
            {
                author = ApplyRequestedAuthorMonitoring(author, BookMediaType.Audiobook, config.AudiobookMonitored, config.AudiobookMonitorNewItems);
                if (config.AudiobookMonitorExistingMode == MonitorTypes.SpecificBook &&
                    ShouldApplyInitialBookMonitoring(config.ExistingCatalogMediaTypes.Contains(BookMediaType.Audiobook), config.AudiobookMonitorExistingMode))
                {
                    ApplyCurrentBookMonitoring(author, BookMediaType.Audiobook, config.AudiobookMonitorExistingMode, audiobookSpecificBookProviderIds);
                }
            }

            if (config.CreateEbook)
            {
                author = ApplyRequestedAuthorMonitoring(author, BookMediaType.Ebook, config.EbookMonitored, config.EbookMonitorNewItems);
                if (config.EbookMonitorExistingMode == MonitorTypes.SpecificBook &&
                    ShouldApplyInitialBookMonitoring(config.ExistingCatalogMediaTypes.Contains(BookMediaType.Ebook), config.EbookMonitorExistingMode))
                {
                    ApplyCurrentBookMonitoring(author, BookMediaType.Ebook, config.EbookMonitorExistingMode, ebookSpecificBookProviderIds);
                }
            }

            QueueInitialAuthorScan(author);
            return Created(author.Id);
        }

        [HttpPost("import")]
        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
        public async Task<ActionResult<AuthorResource>> ImportAuthor([FromBody] AuthorImportResource importResource)
        {
            var facadeContext = HttpContext.GetReadarrFacadeContext();
            if (ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(importResource.ForeignAuthorId, facadeContext))
                throw new ValidationException(new[] { new ValidationFailure("ForeignAuthorId",
                    ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("foreignAuthorId")) });
            var foreignAuthorId = ReadarrFacadeProviderIdTranslator.NormalizeBareProviderId(importResource.ForeignAuthorId, facadeContext);
            if (!ProviderIdValidator.TryNormalize(foreignAuthorId, out var normalizedForeignAuthorId, out var provider, out var providerId, out var error))
                throw new ValidationException(new[] { new ValidationFailure("ForeignAuthorId", error) });

            var mediaType = importResource.MediaType?.Trim().ToLowerInvariant();
            if (facadeContext != null &&
                MediaTypeParameterParser.ParseRequired(importResource.MediaType) != MediaTypeParameterParser.ParseRequired(facadeContext.MediaType))
                throw new ValidationException(new[] { new ValidationFailure("MediaType", "mediaType must agree with the media-scoped API base URL.") });
            if (mediaType is not ("audiobook" or "ebook" or "both"))
                throw new ValidationException(new[] { new ValidationFailure("MediaType", "Invalid mediaType. Expected 'audiobook', 'ebook', or 'both'.") });
            var createAudio = mediaType is "audiobook" or "both";
            var createEbook = mediaType is "ebook" or "both";
            var ambiguity = GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetAuthorAmbiguity(
                _authorService, _providerAliasService, provider, providerId, "foreignAuthorId", _logger, "importing author"));
            if (ambiguity != null) return ambiguity;
            var existingAuthor = ProviderAmbiguityHelper.FindAuthorProviderMatches(_authorService, _providerAliasService, provider, providerId, _logger).SingleOrDefault();
            var audio = ResolveImportMonitoring(importResource, BookMediaType.Audiobook);
            var ebook = ResolveImportMonitoring(importResource, BookMediaType.Ebook);
            if ((createAudio && audio.MonitorExistingMode == MonitorTypes.SpecificBook) ||
                (createEbook && ebook.MonitorExistingMode == MonitorTypes.SpecificBook))
                throw new ValidationException(new[] { new ValidationFailure("Monitor", "Specific-book monitoring requires book provider IDs; use the book add endpoint instead.") });
            var search = importResource.SearchForMissingBooks ??
                (createAudio && audio.MonitorExistingMode == MonitorTypes.All || createEbook && ebook.MonitorExistingMode == MonitorTypes.All);
            var config = new MonitoringConfig
            {
                IsManualAddition = true,
                IsAuthorSettingsRequest = true,
                SyncMonitoredAcrossFormats = importResource.SyncMonitoredAcrossFormats,
                QueueIfUnavailable = existingAuthor == null,
                RequestedBy = "UserInterface",
                CreateAudiobook = createAudio,
                CreateEbook = createEbook,
                AuthorName = importResource.AuthorName,
                SearchForMissingBooks = search,
                LastSelectedMediaType = mediaType == "both" ? "ebook" : mediaType,
                AudiobookRootFolderPath = createAudio ? importResource.AudiobookRootFolderPath ?? importResource.RootFolder : null,
                EbookRootFolderPath = createEbook ? importResource.EbookRootFolderPath ?? importResource.RootFolder : null,
                AudiobookQualityProfileId = createAudio ? importResource.AudiobookQualityProfileId ?? (importResource.QualityProfileId > 0 ? importResource.QualityProfileId : null) : null,
                EbookQualityProfileId = createEbook ? importResource.EbookQualityProfileId ?? (importResource.QualityProfileId > 0 ? importResource.QualityProfileId : null) : null,
                AudiobookMetadataProfileId = createAudio ? importResource.AudiobookMetadataProfileId ?? (importResource.MetadataProfileId > 0 ? importResource.MetadataProfileId : null) : null,
                EbookMetadataProfileId = createEbook ? importResource.EbookMetadataProfileId ?? (importResource.MetadataProfileId > 0 ? importResource.MetadataProfileId : null) : null,
                AudiobookMonitored = createAudio ? audio.Monitored : null,
                EbookMonitored = createEbook ? ebook.Monitored : null,
                AudiobookMonitorExistingMode = createAudio ? audio.MonitorExistingMode : importResource.AudiobookMonitorExistingMode,
                EbookMonitorExistingMode = createEbook ? ebook.MonitorExistingMode : importResource.EbookMonitorExistingMode,
                AudiobookMonitorNewItems = createAudio ? audio.MonitorNewItems : importResource.AudiobookMonitorNewItems,
                EbookMonitorNewItems = createEbook ? ebook.MonitorNewItems : importResource.EbookMonitorNewItems,
                AudiobookTags = createAudio ? importResource.AudiobookTags ?? importResource.Tags : null,
                EbookTags = createEbook ? importResource.EbookTags ?? importResource.Tags : null
            };

            NzbDrone.Core.Books.Author author;
            string hydrationWarning = null;
            try
            {
                author = await _authorLibraryService.AddAuthorAsync(normalizedForeignAuthorId, config);
            }
            catch (Exception ex) when (existingAuthor != null && ex is AuthorNotFoundException or NzbDrone.Core.MetadataSource.BookInfo.BookInfoException)
            {
                author = existingAuthor;
                hydrationWarning = "Author settings were saved, but the requested catalog could not be loaded from the metadata server. You may need to refresh the author later.";
                _logger.Error(ex, "[V1-AUTHOR-IMPORT] Unable to hydrate missing media type for existing author {0}", normalizedForeignAuthorId);
            }
            if (author.Id < 0)
                return Accepted(new { pendingId = -author.Id, message = "The author isn't available yet on the metadata server. Chaptarr has queued the import and will automatically add them when they become available (you can visit the chaptarrbot channel in our discord to ask for updates)." });

            var initializeAudio = createAudio && ShouldApplyInitialBookMonitoring(config.ExistingCatalogMediaTypes.Contains(BookMediaType.Audiobook), config.AudiobookMonitorExistingMode);
            var initializeEbook = createEbook && ShouldApplyInitialBookMonitoring(config.ExistingCatalogMediaTypes.Contains(BookMediaType.Ebook), config.EbookMonitorExistingMode);
            if (initializeAudio || initializeEbook || search)
            {
                author.AddOptions ??= new AddAuthorOptions();
                if (initializeAudio) author.AddOptions.AudiobookMonitor = config.AudiobookMonitorExistingMode;
                if (initializeEbook) author.AddOptions.EbookMonitor = config.EbookMonitorExistingMode;
                author.AddOptions.RecordInitialSearch(search,
                    initializeAudio || createAudio && author.AddOptions.AudiobookSetupPending,
                    initializeEbook || createEbook && author.AddOptions.EbookSetupPending);
                author.AddOptions.AwaitingMetadata |= hydrationWarning != null;
                author.AddOptions.AudiobookSetupPending |= initializeAudio;
                author.AddOptions.EbookSetupPending |= initializeEbook;
                author.AddOptions.AudiobookAwaitingMetadata |= initializeAudio && hydrationWarning != null;
                author.AddOptions.EbookAwaitingMetadata |= initializeEbook && hydrationWarning != null;
                _authorService.SetAddOptions(author);
            }
            if (createAudio)
            {
                author.AudiobookRootFolderPath = config.AudiobookRootFolderPath;
                author.AudiobookQualityProfileId = config.AudiobookQualityProfileId;
                author.AudiobookMetadataProfileId = config.AudiobookMetadataProfileId;
                if (config.AudiobookMonitored.HasValue) author.AudiobookMonitored = config.AudiobookMonitored;
                if (config.AudiobookTags is { } tags) author.AudiobookTags = new HashSet<int>(tags);
                if (importResource.ManualFlag) author.AudiobookSettingsManuallyOverridden = true;
            }
            if (createEbook)
            {
                author.EbookRootFolderPath = config.EbookRootFolderPath;
                author.EbookQualityProfileId = config.EbookQualityProfileId;
                author.EbookMetadataProfileId = config.EbookMetadataProfileId;
                if (config.EbookMonitored.HasValue) author.EbookMonitored = config.EbookMonitored;
                if (config.EbookTags is { } tags) author.EbookTags = new HashSet<int>(tags);
                if (importResource.ManualFlag) author.EbookSettingsManuallyOverridden = true;
            }
            if (config.SyncMonitoredAcrossFormats.HasValue) author.SyncMonitoredAcrossFormats = config.SyncMonitoredAcrossFormats;
            AuthorMonitoringSyncPolicy.ApplyNewItemChoices(author, null,
                config.AudiobookMonitorNewItems, config.EbookMonitorNewItems);
            author.Tags = author.GetCombinedTags();
            author.LastSelectedMediaType = config.LastSelectedMediaType;
            author = _authorService.UpdateAuthor(author);
            if (hydrationWarning == null)
            {
                QueueInitialAuthorScan(author);
                if (author.AddOptions == null && importResource.SyncMonitoredAcrossFormats == true &&
                    author.SyncMonitoredAcrossFormats == true)
                {
                    _commandQueueManager.Push(new BulkSyncFormatMonitoringCommand(new List<int> { author.Id }));
                }
            }
            if (existingAuthor == null) return Created(author.Id);
            var resource = author.ToResource(facadeContext);
            resource.HydrationWarning = hydrationWarning;
            return Ok(resource);
        }

        private void QueueInitialAuthorScan(NzbDrone.Core.Books.Author author)
        {
            if (author.AddOptions is { AwaitingMetadata: false })
            {
                _commandQueueManager.Push(RefreshAuthorCommand.InitialScan(author));
            }
        }

        private void ApplyCurrentBookMonitoring(
            NzbDrone.Core.Books.Author author,
            BookMediaType mediaType,
            MonitorTypes? monitorMode,
            IEnumerable<string> selectedProviderBookIds)
        {
            if (author == null || !monitorMode.HasValue || _bookMonitoredService == null)
            {
                return;
            }

            var options = new MonitoringOptions
            {
                Monitor = monitorMode.Value,
                MediaType = mediaType
            };

            if (monitorMode == MonitorTypes.SpecificBook)
            {
                var providerIds = (selectedProviderBookIds ?? Enumerable.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();
                options.BooksToMonitor = (_bookService.GetBooksByAuthor(author.Id) ?? new List<NzbDrone.Core.Books.Book>())
                    .Where(book => book.MediaType == mediaType && providerIds.Any(id => BookMatchesProviderId(book, id)))
                    .Select(book => book.Id.ToString())
                    .ToList();

                if (!options.BooksToMonitor.Any())
                {
                    _logger.Warn("No requested {0} book was present for author {1}; leaving current monitoring unchanged", mediaType, author.Id);
                    return;
                }
            }

            _bookMonitoredService.SetBookMonitoredStatus(author, options);
        }

        internal static bool ShouldApplyInitialBookMonitoring(bool mediaTypeCatalogAlreadyPresent, MonitorTypes? monitorMode)
        {
            return monitorMode.HasValue &&
                   (!mediaTypeCatalogAlreadyPresent || monitorMode == MonitorTypes.SpecificBook);
        }

        private NzbDrone.Core.Books.Author ApplyRequestedAuthorMonitoring(
            NzbDrone.Core.Books.Author author,
            BookMediaType mediaType,
            bool? monitored,
            NewItemMonitorTypes? monitorNewItems)
        {
            if (author == null || !author.ApplyMediaTypeMonitoringSettings(mediaType, monitored, monitorNewItems))
            {
                return author;
            }

            return _authorService.UpdateAuthor(author);
        }

        private static bool BookMatchesProviderId(NzbDrone.Core.Books.Book book, string providerId)
        {
            return BookIdentity.GetProviderIdentityTokens(book)
                .Contains(providerId.Trim().Trim('{', '}'), StringComparer.OrdinalIgnoreCase);
        }

        internal static (bool? Monitored, NewItemMonitorTypes? MonitorNewItems, MonitorTypes? MonitorExistingMode) ResolveImportMonitoring(
            AuthorImportResource resource,
            BookMediaType mediaType,
            bool legacySelectTargetsSpecificBook = false)
        {
            var monitored = mediaType == BookMediaType.Audiobook
                ? resource.AudiobookMonitored
                : resource.EbookMonitored;
            var legacySelected = string.Equals(resource.MonitorExisting?.Trim(), "select", StringComparison.OrdinalIgnoreCase);
            var legacyMode = ParseLegacyMonitorExistingMode(resource.MonitorExisting, legacySelectTargetsSpecificBook);
            var monitorNewItems = mediaType == BookMediaType.Audiobook
                ? resource.AudiobookMonitorNewItems
                : resource.EbookMonitorNewItems;
            var monitorExistingMode = mediaType == BookMediaType.Audiobook
                ? resource.AudiobookMonitorExistingMode
                : resource.EbookMonitorExistingMode;

            monitored ??= legacyMode.HasValue
                ? legacySelected || legacyMode.Value != MonitorTypes.None || resource.MonitorFuture == true
                : resource.MonitorFuture == true ? true : null;
            monitorNewItems ??= legacyMode == MonitorTypes.All
                ? NewItemMonitorTypes.All
                : resource.MonitorFuture == true
                    ? NewItemMonitorTypes.New
                    : legacyMode.HasValue || resource.MonitorFuture.HasValue
                        ? NewItemMonitorTypes.None
                        : null;
            monitorExistingMode ??= ParseMonitorMode(resource.Monitor) ?? legacyMode;

            return (monitored, monitorNewItems, monitorExistingMode);
        }

        private static MonitorTypes? ParseLegacyMonitorExistingMode(string value, bool selectTargetsSpecificBook)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "all" => MonitorTypes.All,
                "select" => selectTargetsSpecificBook ? MonitorTypes.SpecificBook : MonitorTypes.None,
                "none" => MonitorTypes.None,
                _ => null
            };
        }

        private static MonitorTypes? ParseMonitorMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "all" => MonitorTypes.All,
                "future" => MonitorTypes.Future,
                "missing" => MonitorTypes.Missing,
                "existing" => MonitorTypes.Existing,
                "first" => MonitorTypes.First,
                "latest" => MonitorTypes.Latest,
                "none" => MonitorTypes.None,
                "specificbook" => MonitorTypes.SpecificBook,
                _ => throw new ValidationException(new[]
                {
                    new ValidationFailure("Monitor", "Invalid monitor value. Expected all, future, missing, existing, first, latest, none, or specificBook.")
                })
            };
        }

	        [RestPutById]
	        public ActionResult<AuthorResource> UpdateAuthor([FromBody] AuthorResource authorResource, bool moveFiles = false, [FromQuery] string mediaType = null)
	        {
                var facadeContext = HttpContext.GetReadarrFacadeContext();
                NormalizeWriteResource(authorResource, facadeContext, mediaType);
                if (ReadarrFacadeProviderIdTranslator.RequiresProviderPrefix(authorResource.ForeignAuthorId, facadeContext))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("ForeignAuthorId", ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("foreignAuthorId"))
                    });
                }

	            var author = _authorService.GetAuthor(authorResource.Id);
                var wasSyncEnabled = author.SyncMonitoredAcrossFormats == true;

                var policy = new NzbDrone.Core.Books.Author
                {
                    SyncMonitoredAcrossFormats = author.SyncMonitoredAcrossFormats,
                    AudiobookMonitorNewItems = author.AudiobookMonitorNewItems,
                    EbookMonitorNewItems = author.EbookMonitorNewItems,
                    AudiobookRootFolderPath = authorResource.AudiobookRootFolderPath ?? author.AudiobookRootFolderPath,
                    EbookRootFolderPath = authorResource.EbookRootFolderPath ?? author.EbookRootFolderPath
                };
                var eligible = AuthorMonitoringSyncPolicy.IsEligible(policy, _rootFolderService.All());
                if (authorResource.SyncMonitoredAcrossFormats == true && !eligible)
                    throw new ValidationException(new[] { new ValidationFailure(nameof(authorResource.SyncMonitoredAcrossFormats),
                        "Sync monitored across formats requires both audiobook and ebook root folders to be configured for this author.") });
                AuthorMonitoringSyncPolicy.ApplyNewItemChoices(policy,
                    eligible ? authorResource.SyncMonitoredAcrossFormats : false,
                    authorResource.AudiobookMonitorNewItems, authorResource.EbookMonitorNewItems);
                var model = authorResource.ToModel(author, facadeContext, mediaType);
                model.AudiobookMonitorNewItems = policy.AudiobookMonitorNewItems;
                model.EbookMonitorNewItems = policy.EbookMonitorNewItems;

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

	            var updatedAuthor = _authorService.UpdateAuthor(model);

                var shouldReconcile = facadeContext == null &&
                                      updatedAuthor.SyncMonitoredAcrossFormats == true &&
                                      AuthorMonitoringSyncPolicy.IsEligible(updatedAuthor, _rootFolderService.All()) &&
                                      (authorResource.SyncMonitoredAcrossFormats == true || !wasSyncEnabled);

                if (shouldReconcile)
                {
                    _commandQueueManager.Push(new BulkSyncFormatMonitoringCommand(new List<int> { updatedAuthor.Id }));
                }

	            // Broadcast a fresh resource with up-to-date statistics so tiles update live
	            BroadcastResourceChange(ModelAction.Updated, GetAuthorResource(updatedAuthor));

	            return Accepted(updatedAuthor.Id);
	        }

        [RestDeleteById]
        [ProducesResponseType(200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ApiErrorResource), 409)]
        public async Task<ActionResult> DeleteAuthor(int id, bool deleteFiles = false, bool addImportListExclusion = false, bool readdAuthor = false, string mediaType = null, bool applyToBothFormats = false)
        {
            var scope = MediaTypeParameterParser.ParseDeletionScope(applyToBothFormats, HttpContext.GetReadarrFacadeContext()?.MediaType, mediaType);
            if (readdAuthor && deleteFiles)
            {
                return BadRequest(new ApiErrorResource { Message = "Cannot combine file deletion with re-add." });
            }

            if (readdAuthor && (addImportListExclusion || (scope.HasValue && !applyToBothFormats)))
            {
                return Conflict(new ApiErrorResource { Message = "Purge and re-add rebuilds the whole author. Set applyToBothFormats=true and do not request an import-list exclusion. Nothing was changed." });
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
                    IsManualAddition = true,
                    CreateAudiobook = !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath),
                    CreateEbook = !string.IsNullOrWhiteSpace(author.EbookRootFolderPath),
                    AudiobookMonitored = author.AudiobookMonitored,
                    AudiobookMonitorNewItems = author.AudiobookMonitorNewItems,
                    EbookMonitored = author.EbookMonitored,
                    EbookMonitorNewItems = author.EbookMonitorNewItems,
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
                    AuthorName = author.Name,
                    LastSelectedMediaType = author.LastSelectedMediaType
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

                if (newAuthor?.Id > 0) QueueInitialAuthorScan(newAuthor);
                return Ok();
            }

            _authorService.DeleteAuthors(new List<int> { id }, deleteFiles, addImportListExclusion, scope, applyToBothFormats);
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

        internal static void LinkMediaTypeAuthorStatistics(List<AuthorResource> resources,
                                                           Dictionary<int, AuthorStatistics> audiobookStatistics,
                                                           Dictionary<int, AuthorStatistics> ebookStatistics)
        {
            foreach (var author in resources)
            {
                author.AudiobookStatistics = GetStatisticsResource(audiobookStatistics, author.Id);
                author.EbookStatistics = GetStatisticsResource(ebookStatistics, author.Id);
                author.Statistics = AddStatistics(author.AudiobookStatistics, author.EbookStatistics);
            }
        }

        private static AuthorStatisticsResource GetStatisticsResource(Dictionary<int, AuthorStatistics> statistics, int authorId)
        {
            return statistics.TryGetValue(authorId, out var authorStatistics)
                ? authorStatistics.ToResource()
                : new AuthorStatisticsResource();
        }

        private static AuthorStatisticsResource AddStatistics(AuthorStatisticsResource left, AuthorStatisticsResource right)
        {
            return new AuthorStatisticsResource
            {
                BookFileCount = left.BookFileCount + right.BookFileCount,
                BookCount = left.BookCount + right.BookCount,
                AvailableBookCount = left.AvailableBookCount + right.AvailableBookCount,
                TotalBookCount = left.TotalBookCount + right.TotalBookCount,
                SizeOnDisk = left.SizeOnDisk + right.SizeOnDisk
            };
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
            // Monitoring an author can edit hundreds of child books in one operation.
            // Reuse the existing author-update coalescer instead of loading and broadcasting
            // the same fully populated author once for every edited book.
            QueueAuthorUpdate(message.Book.AuthorId);
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
