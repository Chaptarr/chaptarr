using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Organizer;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/naming")]
    public class NamingConfigController : RestController<NamingConfigResource>
    {
        private readonly INamingConfigService _namingConfigService;
        private readonly IFilenameSampleService _filenameSampleService;
        private readonly IFilenameValidationService _filenameValidationService;
        private readonly IBuildFileNames _filenameBuilder;

        public NamingConfigController(INamingConfigService namingConfigService,
                                  IFilenameSampleService filenameSampleService,
                                  IFilenameValidationService filenameValidationService,
                                  IBuildFileNames filenameBuilder)
        {
            _namingConfigService = namingConfigService;
            _filenameSampleService = filenameSampleService;
            _filenameValidationService = filenameValidationService;
            _filenameBuilder = filenameBuilder;

            SharedValidator.RuleFor(value => value).Custom((value, context) =>
            {
                foreach (var error in NzbDrone.Core.Configuration.ConfigValueValidation.ValidateNaming(value.ToModel()))
                {
                    context.AddFailure(error);
                }
            });
        }

        protected override NamingConfigResource GetResourceById(int id)
        {
            return GetNamingConfig();
        }

        [HttpGet]
        public NamingConfigResource GetNamingConfig()
        {
            var nameSpec = _namingConfigService.GetConfig();
            var resource = nameSpec.ToResource();

            if (resource.StandardBookFormat.IsNotNullOrWhiteSpace())
            {
                var basicConfig = _filenameBuilder.GetBasicNamingConfig(nameSpec);
                basicConfig.AddToResource(resource);
            }

            return resource;
        }

        [RestPutById]
        public ActionResult<NamingConfigResource> UpdateNamingConfig([FromBody] NamingConfigResource resource)
        {
            var nameSpec = _namingConfigService.GetConfig();

            nameSpec.RenameBooks = resource.RenameBooks;
            nameSpec.ReplaceIllegalCharacters = resource.ReplaceIllegalCharacters;
            nameSpec.ColonReplacementFormat = (ColonReplacementFormat)resource.ColonReplacementFormat;
            nameSpec.StandardBookFormat = resource.StandardBookFormat;
            nameSpec.AuthorFolderFormat = resource.AuthorFolderFormat;

            if (resource.EbookRenameBooks.HasValue)
            {
                nameSpec.EbookRenameBooks = resource.EbookRenameBooks.Value;
            }

            if (resource.EbookReplaceIllegalCharacters.HasValue)
            {
                nameSpec.EbookReplaceIllegalCharacters = resource.EbookReplaceIllegalCharacters.Value;
            }

            if (resource.EbookColonReplacementFormat.HasValue)
            {
                nameSpec.EbookColonReplacementFormat = (ColonReplacementFormat)resource.EbookColonReplacementFormat.Value;
            }

            if (resource.EbookStandardBookFormat != null)
            {
                nameSpec.EbookStandardBookFormat = resource.EbookStandardBookFormat;
            }

            if (resource.EbookAuthorFolderFormat != null)
            {
                nameSpec.EbookAuthorFolderFormat = resource.EbookAuthorFolderFormat;
            }

            ValidateFormatResult(nameSpec);

            _namingConfigService.Save(nameSpec);

            return Accepted(resource.Id);
        }

        [HttpGet("examples")]
        public object GetExamples([FromQuery] NamingConfigResource config, [FromQuery] string mediaType = "audiobook")
        {
            var normalizedMediaType = MediaTypeParameterParser.ToApiValue(MediaTypeParameterParser.ParseRequired(mediaType));
            if (config.Id == 0)
            {
                config = GetNamingConfig();
            }

            var nameSpec = config.ToModel();
            var formatSpec = nameSpec.GetForMediaType(normalizedMediaType);
            var errors = new[]
            {
                new FluentValidation.Results.ValidationFailure(nameof(config.StandardBookFormat), FileNameValidation.GetBraceError(formatSpec.StandardBookFormat)),
                new FluentValidation.Results.ValidationFailure(nameof(config.AuthorFolderFormat), FileNameValidation.GetBraceError(formatSpec.AuthorFolderFormat))
            }.Where(error => error.ErrorMessage != null).ToList();
            if (errors.Any())
            {
                throw new ValidationException(errors);
            }

            var sampleResource = new NamingExampleResource();

            var singleTrackSampleResult = _filenameSampleService.GetStandardTrackSample(nameSpec, normalizedMediaType);
            var multiDiscTrackSampleResult = _filenameSampleService.GetMultiDiscTrackSample(nameSpec, normalizedMediaType);

            sampleResource.SingleBookExample = _filenameValidationService.ValidateTrackFilename(singleTrackSampleResult) != null
                    ? null
                    : singleTrackSampleResult.FileName;

            sampleResource.MultiPartBookExample = _filenameValidationService.ValidateTrackFilename(multiDiscTrackSampleResult) != null
                ? null
                : multiDiscTrackSampleResult.FileName;

            sampleResource.AuthorFolderExample = nameSpec.GetForMediaType(normalizedMediaType).AuthorFolderFormat.IsNullOrWhiteSpace()
                ? null
                : _filenameSampleService.GetAuthorFolderSample(nameSpec, normalizedMediaType);

            return sampleResource;
        }

        private void ValidateFormatResult(NamingConfig nameSpec)
        {
            var validationFailures = _filenameValidationService.ValidateNamingConfig(nameSpec);

            if (validationFailures.Any())
            {
                throw new ValidationException(validationFailures);
            }
        }
    }
}
