using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.ImportLists
{
    [V1ApiController]
    public class ImportListExclusionController : RestController<ImportListExclusionResource>
    {
        private readonly IImportListExclusionService _importListExclusionService;

        public ImportListExclusionController(IImportListExclusionService importListExclusionService,
                                         ImportListExclusionExistsValidator importListExclusionExistsValidator,
                                         ImportListExclusionProviderIdValidator providerIdValidator)
        {
            _importListExclusionService = importListExclusionService;

            SharedValidator.RuleFor(c => c.ForeignId).NotEmpty().SetValidator(providerIdValidator);
            SharedValidator.RuleFor(c => c.AuthorName).NotEmpty();
            SharedValidator.RuleFor(c => c.MediaType)
                           .Must(IsValidMediaType)
                           .When(c => c.MediaType != null)
                           .WithMessage("mediaType must be 'all', 'audiobook', or 'ebook'");
            SharedValidator.RuleFor(c => c)
                           .Must(c => !importListExclusionExistsValidator.Exists(c.ForeignId, c.Id, ParseValidMediaType(c.MediaType)))
                           .When(c => !string.IsNullOrWhiteSpace(c.ForeignId) && IsValidMediaType(c.MediaType))
                           .WithMessage("Import list exclusion already exists for this provider ID and media type");
        }

        private static bool IsValidMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return true;
            }

            mediaType = mediaType.Trim();
            return mediaType.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                   mediaType.Equals("audiobook", StringComparison.OrdinalIgnoreCase) ||
                   mediaType.Equals("ebook", StringComparison.OrdinalIgnoreCase);
        }

        private static BookMediaType? ParseValidMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return mediaType.Trim().Equals("ebook", StringComparison.OrdinalIgnoreCase)
                ? BookMediaType.Ebook
                : BookMediaType.Audiobook;
        }

        protected override ImportListExclusionResource GetResourceById(int id)
        {
            return _importListExclusionService.Get(id).ToResource();
        }

        [HttpGet]
        public List<ImportListExclusionResource> GetImportListExclusions()
        {
            return _importListExclusionService.All().ToResource();
        }

        [RestPostById]
        public ActionResult<ImportListExclusionResource> AddImportListExclusion([FromBody] ImportListExclusionResource resource)
        {
            var customFilter = _importListExclusionService.Add(resource.ToModel());

            return Created(customFilter.Id);
        }

        [RestPutById]
        public ActionResult<ImportListExclusionResource> UpdateImportListExclusion([FromBody] ImportListExclusionResource resource)
        {
            _importListExclusionService.Update(resource.ToModel());
            return Accepted(resource.Id);
        }

        [RestDeleteById]
        public void DeleteImportListExclusionResource(int id)
        {
            _importListExclusionService.Delete(id);
        }

        [HttpDelete("bulk")]
        [Produces("application/json")]
        public object DeleteImportListExclusions([FromBody] ImportListExclusionBulkResource resource)
        {
            _importListExclusionService.Delete(resource?.Ids?.ToList() ?? new List<int>());

            return new { };
        }
    }
}
