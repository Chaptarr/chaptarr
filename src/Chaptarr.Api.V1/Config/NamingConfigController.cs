using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using FluentValidation.Results;
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

            SharedValidator.RuleFor(c => c.StandardBookFormat).ValidBookFormat();
            SharedValidator.RuleFor(c => c.AuthorFolderFormat).ValidAuthorFolderFormat();

            SharedValidator
                .RuleFor(c => c.EbookStandardBookFormat)
                .ValidBookFormat()
                .When(c => c.EbookStandardBookFormat != null);

            SharedValidator
                .RuleFor(c => c.EbookAuthorFolderFormat)
                .ValidAuthorFolderFormat()
                .When(c => c.EbookAuthorFolderFormat != null);
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

            var nameSpec = config.ToModel().GetForMediaType(normalizedMediaType);
            var sampleResource = new NamingExampleResource();

            var singleTrackSampleResult = _filenameSampleService.GetStandardTrackSample(nameSpec);
            var multiDiscTrackSampleResult = _filenameSampleService.GetMultiDiscTrackSample(nameSpec);

            sampleResource.SingleBookExample = _filenameValidationService.ValidateTrackFilename(singleTrackSampleResult) != null
                    ? null
                    : singleTrackSampleResult.FileName;

            sampleResource.MultiPartBookExample = _filenameValidationService.ValidateTrackFilename(multiDiscTrackSampleResult) != null
                ? null
                : multiDiscTrackSampleResult.FileName;

            sampleResource.AuthorFolderExample = nameSpec.AuthorFolderFormat.IsNullOrWhiteSpace()
                ? null
                : _filenameSampleService.GetAuthorFolderSample(nameSpec);

            return sampleResource;
        }

        private void ValidateFormatResult(NamingConfig nameSpec)
        {
            var validationFailures = new List<ValidationFailure>();

            ValidateFormatResultForMediaType(nameSpec, "audiobook", validationFailures);
            ValidateFormatResultForMediaType(nameSpec, "ebook", validationFailures);

            if (validationFailures.Any())
            {
                throw new ValidationException(validationFailures.DistinctBy(v => v.PropertyName).ToArray());
            }
        }

        private void ValidateFormatResultForMediaType(NamingConfig nameSpec, string mediaType, List<ValidationFailure> validationFailures)
        {
            var spec = nameSpec.GetForMediaType(mediaType);
            var singleTrackSampleResult = _filenameSampleService.GetStandardTrackSample(spec);
            var singleTrackValidationResult = _filenameValidationService.ValidateTrackFilename(singleTrackSampleResult);

            if (singleTrackValidationResult != null)
            {
                // Map to the correct resource property so the UI can highlight the right field.
                if (mediaType == "ebook" && singleTrackValidationResult.PropertyName == nameof(NamingConfigResource.StandardBookFormat))
                {
                    singleTrackValidationResult.PropertyName = nameof(NamingConfigResource.EbookStandardBookFormat);
                }

                validationFailures.Add(singleTrackValidationResult);
            }
        }
    }
}
