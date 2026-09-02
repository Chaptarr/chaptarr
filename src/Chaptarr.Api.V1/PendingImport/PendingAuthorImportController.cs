using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Chaptarr.Api.V1.ProviderIds;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.PendingImport
{
    [V1ApiController]
    public class PendingAuthorImportController : RestControllerWithSignalR<PendingAuthorImportResource, PendingAuthorImport>
    {
        private readonly IPendingAuthorImportService _pendingImportService;
        private readonly IAuthorService _authorService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IProviderAliasService _providerAliasService;
        private readonly Logger _logger;

        public PendingAuthorImportController(
            IBroadcastSignalRMessage signalRBroadcaster,
            IPendingAuthorImportService pendingImportService,
            IAuthorService authorService,
            IQualityProfileService qualityProfileService,
            IMetadataProfileService metadataProfileService,
            IRootFolderService rootFolderService,
            IProviderAliasService providerAliasService = null,
            Logger logger = null)
            : base(signalRBroadcaster)
        {
            _pendingImportService = pendingImportService;
            _authorService = authorService;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _rootFolderService = rootFolderService;
            _providerAliasService = providerAliasService;
            _logger = logger;
        }

        protected override PendingAuthorImportResource GetResourceById(int id)
        {
            return _pendingImportService.GetAll()
                .Where(p => p.Id == id)
                .Select(p => p.ToResource())
                .FirstOrDefault();
        }

        [HttpGet]
        public List<PendingAuthorImportResource> GetAll()
        {
            return _pendingImportService.GetAll()
                .Select(p => p.ToResource())
                .ToList();
        }

        [HttpGet("author/exists/{providerId}")]
        [ProducesResponseType(typeof(PendingAuthorExistenceResource), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
        public Task<ActionResult<PendingAuthorExistenceResource>> CheckAuthorExists(string providerId)
        {
            if (!ProviderIdValidator.TryNormalize(providerId, out var normalizedProviderId, out var prefix, out var id, out var errorMessage))
            {
                return Task.FromResult<ActionResult<PendingAuthorExistenceResource>>(BadRequest(new ApiErrorResource { Message = errorMessage }));
            }
            
            var ambiguity = ProviderAmbiguityHelper.GetAuthorAmbiguity(
                _authorService,
                _providerAliasService,
                prefix,
                id,
                "providerId",
                _logger,
                "checking pending author import");
            if (ambiguity != null)
            {
                return Task.FromResult<ActionResult<PendingAuthorExistenceResource>>(StatusCode(ProviderAmbiguityHelper.StatusCode, ambiguity));
            }

            // Check library first, including provider aliases from prior server-side merges.
            var authorMatches = ProviderAmbiguityHelper.FindAuthorProviderMatches(_authorService, _providerAliasService, prefix, id, _logger);
            var author = authorMatches.Count == 1 ? authorMatches[0] : null;
            
            if (author != null)
            {
                return Task.FromResult<ActionResult<PendingAuthorExistenceResource>>(Ok(new PendingAuthorExistenceResource
                {
                    Exists = true,
                    AuthorId = author.Id,
                    AuthorName = author.Name,
                    Pending = false
                }));
            }
            
            // Check pending queue
            var pending = _pendingImportService.GetByProviderId(normalizedProviderId);
            if (pending != null)
            {
                return Task.FromResult<ActionResult<PendingAuthorExistenceResource>>(Ok(new PendingAuthorExistenceResource
                {
                    Exists = false,
                    Pending = true,
                    PendingId = pending.Id,
                    Status = pending.OverallStatus.ToString(),
                    AuthorName = pending.AuthorName,
                    NextAttempt = pending.NextAttemptAt,
                    AttemptCount = pending.AttemptCount
                }));
            }
            
            return Task.FromResult<ActionResult<PendingAuthorExistenceResource>>(Ok(new PendingAuthorExistenceResource
            {
                Exists = false,
                Pending = false
            }));
        }

        [HttpGet("profiles/options")]
        [ProducesResponseType(typeof(PendingImportProfileOptionsResource), 200)]
        public ActionResult<PendingImportProfileOptionsResource> GetProfileOptions()
        {
            var qualityProfiles = _qualityProfileService.All();
            var metadataProfiles = _metadataProfileService.All();
            var rootFolders = _rootFolderService.All();
            
            return Ok(new PendingImportProfileOptionsResource
            {
                Audiobook = new PendingImportMediaProfileOptionsResource
                {
                    QualityProfiles = qualityProfiles
                        .Where(p => p.ProfileType == ProfileType.Audiobook)
                        .Select(p => new PendingImportProfileOptionResource { Id = p.Id, Name = p.Name })
                        .ToList(),
                    MetadataProfiles = metadataProfiles
                        .Where(p => p.ProfileType == MetadataProfileType.General || p.ProfileType == MetadataProfileType.Audiobook)
                        .Select(p => new PendingImportProfileOptionResource { Id = p.Id, Name = p.Name })
                        .ToList(),
                    RootFolders = rootFolders
                        .Where(f => f.FolderType == FolderType.Audiobook || f.FolderType == FolderType.Mixed)
                        .Select(f => new PendingImportRootFolderOptionResource { Path = f.Path, Name = f.Name })
                        .ToList()
                },
                Ebook = new PendingImportMediaProfileOptionsResource
                {
                    QualityProfiles = qualityProfiles
                        .Where(p => p.ProfileType == ProfileType.Ebook)
                        .Select(p => new PendingImportProfileOptionResource { Id = p.Id, Name = p.Name })
                        .ToList(),
                    MetadataProfiles = metadataProfiles
                        .Where(p => p.ProfileType == MetadataProfileType.General || p.ProfileType == MetadataProfileType.Ebook)
                        .Select(p => new PendingImportProfileOptionResource { Id = p.Id, Name = p.Name })
                        .ToList(),
                    RootFolders = rootFolders
                        .Where(f => f.FolderType == FolderType.Ebook || f.FolderType == FolderType.Mixed)
                        .Select(f => new PendingImportRootFolderOptionResource { Path = f.Path, Name = f.Name })
                        .ToList()
                },
                // Also include all profiles for manual selection
                All = new PendingImportMediaProfileOptionsResource
                {
                    QualityProfiles = qualityProfiles.Select(p => new PendingImportProfileOptionResource { Id = p.Id, Name = p.Name }).ToList(),
                    MetadataProfiles = metadataProfiles.Select(p => new PendingImportProfileOptionResource { Id = p.Id, Name = p.Name }).ToList(),
                    RootFolders = rootFolders.Select(f => new PendingImportRootFolderOptionResource { Path = f.Path, Name = f.Name }).ToList()
                }
            });
        }

        [HttpPost("author/queue")]
        [ProducesResponseType(typeof(QueueAuthorResponseResource), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        public async Task<ActionResult<QueueAuthorResponseResource>> QueueAuthor([FromBody] QueueAuthorRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return BadRequest(new ApiErrorResource { Error = "ProviderId is required" });
            }

            if (!ProviderIdValidator.TryNormalize(request.ProviderId, out var normalizedProviderId, out _, out _, out var errorMessage))
            {
                return BadRequest(new ApiErrorResource { Error = errorMessage });
            }

            var audiobookSettings = ResolveMediaTypeSettings(request.Audiobook);
            var ebookSettings = ResolveMediaTypeSettings(request.Ebook);
            var config = new MonitoringConfig
            {
                QueueIfUnavailable = true,
                AuthorName = request.AuthorName,
                RequestedBy = request.RequestedBy ?? "3rdPartyApp",
                CreateAudiobook = audiobookSettings != null,
                CreateEbook = ebookSettings != null,
                Tags = request.Tags
            };

            // Set audiobook configuration
            if (audiobookSettings != null)
            {
                config.AudiobookMonitored = audiobookSettings.Monitored;
                config.AudiobookMonitorNewItems = audiobookSettings.MonitorNewItems;
                config.AudiobookMonitorExistingMode = audiobookSettings.MonitorExistingMode;
                config.AudiobookQualityProfileId = request.Audiobook.QualityProfileId;
                config.AudiobookMetadataProfileId = request.Audiobook.MetadataProfileId;
                config.AudiobookRootFolderPath = request.Audiobook.RootFolderPath;
                config.AudiobookBooksToMonitor = request.Audiobook.BooksToMonitor;
                config.AudiobookTags = request.Audiobook.Tags ?? request.Tags;
            }

            // Set ebook configuration
            if (ebookSettings != null)
            {
                config.EbookMonitored = ebookSettings.Monitored;
                config.EbookMonitorNewItems = ebookSettings.MonitorNewItems;
                config.EbookMonitorExistingMode = ebookSettings.MonitorExistingMode;
                config.EbookQualityProfileId = request.Ebook.QualityProfileId;
                config.EbookMetadataProfileId = request.Ebook.MetadataProfileId;
                config.EbookRootFolderPath = request.Ebook.RootFolderPath;
                config.EbookBooksToMonitor = request.Ebook.BooksToMonitor;
                config.EbookTags = request.Ebook.Tags ?? request.Tags;
            }

            config.SearchForMissingBooks = request.SearchForMissingBooks;

            var pendingId = await _pendingImportService.EnqueueAsync(
                normalizedProviderId,
                config,
                request.SourceApplication ?? "3rdPartyApp"
            );
            
            return Ok(new QueueAuthorResponseResource
            {
                PendingId = pendingId,
                Message = "Author queued for import",
                ProviderId = normalizedProviderId,
                Status = "Pending"
            });
        }

        private static MediaTypeSettings ResolveMediaTypeSettings(MediaTypeConfig request)
        {
            if (request == null)
            {
                return null;
            }

            var hasExactBookRequest = request.BooksToMonitor?.Any() == true;
            var usesLegacyContract = request.MonitorExisting.HasValue ||
                                     request.MonitorFuture.HasValue;

            // The published contract used Monitor=false to mean that the media side
            // was not requested at all. Preserve that meaning for legacy-shaped input.
            if (usesLegacyContract)
            {
                if (!request.Monitor)
                {
                    return null;
                }

                var legacySettings = new MediaTypeSettings();
                legacySettings.ApplyLegacyMonitoringSettings(
                    request.MonitorExisting ?? 0,
                    request.MonitorFuture ?? false);
                if (request.MonitorExisting == 2)
                {
                    legacySettings.MonitorExistingMode = MonitorTypes.SpecificBook;
                }

                return legacySettings;
            }

            var configuresMediaSide = request.Monitor ||
                                      hasExactBookRequest ||
                                      request.MonitorExistingMode.HasValue ||
                                      request.MonitorNewItems.HasValue;
            if (!configuresMediaSide)
            {
                return null;
            }

            return new MediaTypeSettings
            {
                Monitored = request.Monitor || hasExactBookRequest,
                MonitorNewItems = request.MonitorNewItems,
                MonitorExistingMode = request.MonitorExistingMode ??
                    (hasExactBookRequest ? MonitorTypes.SpecificBook : null)
            };
        }

        [HttpPost("{id}/retry")]
        [ProducesResponseType(typeof(ApiMessageResource), 200)]
        public ActionResult<ApiMessageResource> RetryNow(int id)
        {
            _pendingImportService.RetryNow(id);
            return Ok(new ApiMessageResource { Message = "Retry scheduled" });
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(typeof(ApiMessageResource), 200)]
        public ActionResult<ApiMessageResource> Cancel(int id)
        {
            _pendingImportService.Cancel(id);
            return Ok(new ApiMessageResource { Message = "Pending import cancelled" });
        }

    }

    public class QueueAuthorRequest
    {
        public string ProviderId { get; set; }
        public string AuthorName { get; set; }
        public MediaTypeConfig Audiobook { get; set; }
        public MediaTypeConfig Ebook { get; set; }
        public HashSet<int> Tags { get; set; }
        public bool? SearchForMissingBooks { get; set; }
        public string SourceApplication { get; set; }
        public string RequestedBy { get; set; }
    }

    public class MediaTypeConfig
    {
        public bool Monitor { get; set; }
        public int? MonitorExisting { get; set; }
        public bool? MonitorFuture { get; set; }
        public NewItemMonitorTypes? MonitorNewItems { get; set; }
        public MonitorTypes? MonitorExistingMode { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
        public string RootFolderPath { get; set; }
        public List<string> BooksToMonitor { get; set; }
        public HashSet<int> Tags { get; set; }
    }
}
