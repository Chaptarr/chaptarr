using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Localization;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.RootFolders
{
    [V1ApiController]
    public class RootFolderController : RestControllerWithSignalR<RootFolderResource, RootFolder>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderScanService _rootFolderScanService;
        private readonly IAuthorService _authorService;
        private readonly ICalibreProxy _calibreProxy;
        private readonly IConfigService _configService;
        private readonly ILocalizationService _localizationService;

        public RootFolderController(IRootFolderService rootFolderService,
                                IRootFolderScanService rootFolderScanService,
                                IAuthorService authorService,
                                ICalibreProxy calibreProxy,
                                IConfigService configService,
                                ILocalizationService localizationService,
                                IBroadcastSignalRMessage signalRBroadcaster,
                                RecycleBinValidator recycleBinValidator,
                                RootFolderValidator rootFolderValidator,
                                PathExistsValidator pathExistsValidator,
                                MappedNetworkDriveValidator mappedNetworkDriveValidator,
                                StartupFolderValidator startupFolderValidator,
                                SystemFolderValidator systemFolderValidator,
                                FolderWritableValidator folderWritableValidator,
                                FolderReadableValidator folderReadableValidator,
                                QualityProfileExistsValidator qualityProfileExistsValidator,
                                MetadataProfileExistsValidator metadataProfileExistsValidator)
            : base(signalRBroadcaster)
        {
            _rootFolderService = rootFolderService;
            _rootFolderScanService = rootFolderScanService;
            _authorService = authorService;
            _calibreProxy = calibreProxy;
            _configService = configService;
            _localizationService = localizationService;

            SharedValidator.RuleFor(c => c.FolderType)
                .Must(value => Enum.IsDefined(typeof(FolderType), value))
                .WithMessage("Invalid folder type. Valid values are 0 (Mixed), 1 (Audiobook), 2 (Ebook)");

            SharedValidator.RuleFor(c => c.Path)
                .Cascade(CascadeMode.Stop)
                .IsValidPath()
                .SetValidator(mappedNetworkDriveValidator)
                .SetValidator(startupFolderValidator)
                .SetValidator(recycleBinValidator)
                .SetValidator(pathExistsValidator)
                .SetValidator(systemFolderValidator)
                .SetValidator(folderReadableValidator)
                .SetValidator(folderWritableValidator);

            PostValidator.RuleFor(c => c.Path)
                .SetValidator(rootFolderValidator);

            PostValidator.RuleFor(c => c)
                .Must(HasAtLeastOneConfiguredMediaSide)
                .WithMessage("Mixed root folders must configure at least one media type with quality and metadata profiles");

            AddMediaSettingsValidation(BookMediaType.Audiobook, qualityProfileExistsValidator, metadataProfileExistsValidator);
            AddMediaSettingsValidation(BookMediaType.Ebook, qualityProfileExistsValidator, metadataProfileExistsValidator);

            SharedValidator.RuleFor(c => c)
                .Must(x => CalibreLibraryOnlyUsedOnce(x))
                .When(x => x.IsCalibreLibrary)
                .WithMessage("Calibre library is already configured as a root folder");

            SharedValidator.RuleFor(c => c.Name)
                .NotEmpty();

            SharedValidator.RuleFor(c => c.Host).ValidHost().When(x => x.IsCalibreLibrary);
            SharedValidator.RuleFor(c => c.Port).InclusiveBetween(1, 65535).When(x => x.IsCalibreLibrary);
            SharedValidator.RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());
            SharedValidator.RuleFor(c => c.Username).NotEmpty().When(c => c.IsCalibreLibrary && !string.IsNullOrWhiteSpace(c.Password));
            PostValidator.RuleFor(c => c.Password).NotEmpty().When(c => c.IsCalibreLibrary && !string.IsNullOrWhiteSpace(c.Username));

            SharedValidator.RuleFor(c => c.OutputFormat).Must(x => x.Split(',').All(y => Enum.TryParse<CalibreFormat>(y, true, out _))).When(x => x.OutputFormat.IsNotNullOrWhiteSpace()).WithMessage("Invalid output formats");
            SharedValidator.RuleFor(c => c.OutputProfile).IsEnumName(typeof(CalibreProfile));
        }

        private void AddMediaSettingsValidation(
            BookMediaType mediaType,
            QualityProfileExistsValidator qualityProfileExistsValidator,
            MetadataProfileExistsValidator metadataProfileExistsValidator)
        {
            var mediaLabel = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";

            PostValidator.RuleFor(resource => RootFolderResourceMapper.GetQualityProfileId(resource, mediaType))
                .Cascade(CascadeMode.Stop)
                .NotNull()
                .WithMessage($"A {mediaLabel} quality profile is required")
                .GreaterThan(0)
                .WithMessage($"A valid {mediaLabel} quality profile is required")
                .SetValidator(qualityProfileExistsValidator)
                .When(resource => RequiresMediaSettings(resource, mediaType))
                .WithName($"{mediaLabel}QualityProfileId");

            PostValidator.RuleFor(resource => RootFolderResourceMapper.GetMetadataProfileId(resource, mediaType))
                .Cascade(CascadeMode.Stop)
                .NotNull()
                .WithMessage($"An {mediaLabel} metadata profile is required")
                .GreaterThan(0)
                .WithMessage($"A valid {mediaLabel} metadata profile is required")
                .SetValidator(metadataProfileExistsValidator)
                .When(resource => RequiresMediaSettings(resource, mediaType))
                .WithName($"{mediaLabel}MetadataProfileId");
        }

        private static bool HasAtLeastOneConfiguredMediaSide(RootFolderResource resource)
        {
            return resource?.FolderType != (int)FolderType.Mixed ||
                   RootFolderResourceMapper.HasAnyMediaTypeSettings(resource, BookMediaType.Audiobook) ||
                   RootFolderResourceMapper.HasAnyMediaTypeSettings(resource, BookMediaType.Ebook);
        }

        private static bool RequiresMediaSettings(RootFolderResource resource, BookMediaType mediaType)
        {
            if (resource == null)
            {
                return false;
            }

            if (!Enum.IsDefined(typeof(FolderType), resource.FolderType))
            {
                return false;
            }

            var folderType = (FolderType)resource.FolderType;
            if (folderType == FolderType.Audiobook)
            {
                return mediaType == BookMediaType.Audiobook;
            }

            if (folderType == FolderType.Ebook)
            {
                return mediaType == BookMediaType.Ebook;
            }

            return folderType == FolderType.Mixed &&
                   RootFolderResourceMapper.HasAnyMediaTypeSettings(resource, mediaType);
        }

        private bool CalibreLibraryOnlyUsedOnce(RootFolderResource settings)
        {
            var newUri = GetLibraryUri(settings);
            return !_rootFolderService.All().Exists(x => x.Id != settings.Id &&
                                                    x.CalibreSettings != null &&
                                                    GetLibraryUri(x.CalibreSettings) == newUri);
        }

        private string GetLibraryUri(RootFolderResource settings)
        {
            return HttpUri.CombinePath(HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase), settings.Library);
        }

        private string GetLibraryUri(CalibreSettings settings)
        {
            return HttpUri.CombinePath(HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase), settings.Library);
        }

        protected override RootFolderResource GetResourceById(int id)
        {
            var rootFolders = _rootFolderService.All();
            return _rootFolderService.Get(id).ToResource(rootFolders, _configService.DefaultAudiobookRootFolderPath, _configService.DefaultEbookRootFolderPath);
        }

        [RestPostById]
        public ActionResult<RootFolderResource> CreateRootFolder([FromBody] RootFolderResource rootFolderResource)
        {
            var model = rootFolderResource.ToModel();

            if (model.IsCalibreLibrary)
            {
                _calibreProxy.Test(model.CalibreSettings);
            }

            return Created(_rootFolderService.Add(model).Id);
        }

        [RestPutById]
        public ActionResult<RootFolderResource> UpdateRootFolder([FromBody] RootFolderResource rootFolderResource)
        {
            var model = rootFolderResource.ToModel();

            var existingRootFolder = _rootFolderService.Get(model.Id);

            if (existingRootFolder.Path.PathNotEquals(model.Path))
            {
                throw new BadRequestException("Cannot edit root folder path");
            }

            // Preserve the stored path value even if the incoming path is semantically equivalent
            // (e.g. trailing slash/casing differences), since path edits are not supported here.
            model.Path = existingRootFolder.Path;

            // A single-media root becoming Mixed only widens what the existing assignments may contain.
            // Every other type change can invalidate an assigned media path and remains fail-closed.
            if (existingRootFolder.FolderType != model.FolderType &&
                model.FolderType != FolderType.Mixed)
            {
                var hasAssignedAuthors = _authorService.GetAllAuthors(bypassCache: true)
                    .Any(author =>
                        author.AudiobookRootFolderPath.PathEquals(existingRootFolder.Path) ||
                        author.EbookRootFolderPath.PathEquals(existingRootFolder.Path));

                if (hasAssignedAuthors)
                {
                    throw new BadRequestException(_localizationService.GetLocalizedString("RootFolderTypeChangeAssignedMessage"));
                }
            }

            if (model.IsCalibreLibrary)
            {
                if (model.CalibreSettings != null &&
                    !string.IsNullOrWhiteSpace(model.CalibreSettings.Username) &&
                    string.IsNullOrWhiteSpace(rootFolderResource.Password) &&
                    existingRootFolder.CalibreSettings != null)
                {
                    model.CalibreSettings.Password = existingRootFolder.CalibreSettings.Password;
                }

                _calibreProxy.Test(model.CalibreSettings);
            }

            _rootFolderService.Update(model);

            return Accepted(model.Id);
        }

        [HttpGet]
        public List<RootFolderResource> GetRootFolders([FromQuery] string mediaType = null)
        {
            var allRootFolders = _rootFolderService.AllWithSpaceStats();
            var rootFolders = RootFolderMediaTypeFilter.Filter(allRootFolders, mediaType);

            return rootFolders.ToResource(allRootFolders, _configService.DefaultAudiobookRootFolderPath, _configService.DefaultEbookRootFolderPath);
        }

        [RestDeleteById]
        public void DeleteFolder(int id)
        {
            _rootFolderService.Remove(id);
        }

        [HttpPost("{id}/link-author")]
        public ActionResult<AuthorPathUpdateResource> LinkAuthorToFolder(int id, [FromBody] LinkAuthorRequest request)
        {
            var rootFolder = _rootFolderService.Get(id);
            if (rootFolder == null)
            {
                return NotFound();
            }

            var result = _rootFolderScanService.LinkAuthorToFolder(
                _authorService.GetAuthor(request.AuthorId),
                rootFolder,
                request.FolderPath);

            if (result == null)
            {
                return BadRequest("Failed to link author to folder");
            }

            return Ok(result.ToResource());
        }
    }

    public class LinkAuthorRequest
    {
        public int AuthorId { get; set; }
        public string FolderPath { get; set; }
    }
}
