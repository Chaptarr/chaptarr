using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Profiles.Quality
{
    [V1ApiController]
    public class QualityProfileController : RestController<QualityProfileResource>
    {
        private readonly IQualityProfileService _qualityProfileService;
        private readonly ICustomFormatService _formatService;

        public QualityProfileController(IQualityProfileService qualityProfileService, ICustomFormatService formatService)
        {
            _qualityProfileService = qualityProfileService;
            _formatService = formatService;
            SharedValidator.RuleFor(c => c.FormatItems).NotNull();

            SharedValidator.RuleFor(c => c).Custom((profile, context) =>
            {
                foreach (var failure in new QualityProfileValidator().Validate(profile.ToModel()).Errors)
                {
                    context.AddFailure(failure);
                }

                var formatItems = profile.FormatItems ?? new List<ProfileFormatItemResource>();
                var expectedIds = _formatService.All()
                    .Where(format => format.AppliesToProfile(profile.ProfileType))
                    .Select(format => format.Id)
                    .ToHashSet();
                var suppliedIds = formatItems.Select(item => item.Format).ToList();

                if (suppliedIds.Count != suppliedIds.Distinct().Count() ||
                    !expectedIds.SetEquals(suppliedIds))
                {
                    context.AddFailure("FormatItems", "Every applicable Custom Format, and no incompatible ones, must be present. Try refreshing your browser.");
                }

            });
        }

        [RestPostById]
        public ActionResult<QualityProfileResource> Create([FromBody] QualityProfileResource resource)
        {
            var model = resource.ToModel();
            model = _qualityProfileService.Add(model);
            return Created(model.Id);
        }

        [RestDeleteById]
        public void DeleteProfile(int id)
        {
            _qualityProfileService.Delete(id);
        }

        [RestPutById]
        public ActionResult<QualityProfileResource> Update([FromBody] QualityProfileResource resource)
        {
            var model = resource.ToModel();

            _qualityProfileService.Update(model);

            return Accepted(model.Id);
        }

        protected override QualityProfileResource GetResourceById(int id)
        {
            return _qualityProfileService.Get(id).ToResource(filterToProfileType: true);
        }

        [HttpGet]
        public List<QualityProfileResource> GetAll([FromQuery] string mediaType = null)
        {
            var requestedMediaType = QualityProfileMediaTypeParser.ParseOrNull(mediaType);
            var profiles = requestedMediaType.HasValue
                ? _qualityProfileService.GetByType(requestedMediaType.Value.ToProfileType())
                : _qualityProfileService.All();

            return profiles.ToResource(filterToProfileType: requestedMediaType.HasValue);
        }

    }
}
