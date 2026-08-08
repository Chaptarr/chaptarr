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
            SharedValidator.RuleFor(c => c.Name).NotEmpty();
            SharedValidator.RuleFor(c => c.ProfileType)
                .Must(x => Enum.IsDefined(typeof(ProfileType), x))
                .WithMessage("Profile type must be Audiobook or Ebook");
            SharedValidator.RuleFor(c => c.Cutoff).ValidCutoff();
            SharedValidator.RuleFor(c => c.Items).ValidItems();

            SharedValidator.RuleFor(c => c.FormatItems).NotNull();

            SharedValidator.RuleFor(c => c).Custom((profile, context) =>
            {
                foreach (var failure in GetProfileTypeQualityFailures(profile))
                {
                    context.AddFailure(failure.PropertyName, failure.Message);
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

                var highestSingleScore = formatItems.Any() ? formatItems.Max(x => x.Score) : 0;
                if (formatItems.Where(x => x.Score > 0).Sum(x => x.Score) < profile.MinFormatScore &&
                    highestSingleScore < profile.MinFormatScore)
                {
                    context.AddFailure("Minimum Custom Format Score can never be satisfied");
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

        private static IEnumerable<(string PropertyName, string Message)> GetProfileTypeQualityFailures(QualityProfileResource profile)
        {
            foreach (var quality in GetEnabledQualityIds(profile.Items))
            {
                if (!ProfileResourceMapper.IsQualityAllowedForProfileType(quality.Id, profile.ProfileType))
                {
                    yield return ("Items", $"{quality.Name} cannot be enabled on a {profile.ProfileType} quality profile");
                }
            }

            foreach (var quality in GetCutoffQualityIds(profile.Items, profile.Cutoff))
            {
                if (!ProfileResourceMapper.IsQualityAllowedForProfileType(quality.Id, profile.ProfileType))
                {
                    yield return ("Cutoff", $"{quality.Name} cannot be used as the cutoff for a {profile.ProfileType} quality profile");
                }
            }

            if (profile.ConvertToQualityId.HasValue &&
                profile.ConvertToQualityId.Value > 0 &&
                !ProfileResourceMapper.IsQualityAllowedForProfileType(profile.ConvertToQualityId.Value, profile.ProfileType))
            {
                yield return ("ConvertToQualityId", $"Conversion target must be a {profile.ProfileType} quality");
            }
        }

        private static IEnumerable<NzbDrone.Core.Qualities.Quality> GetEnabledQualityIds(IEnumerable<QualityProfileQualityItemResource> items, bool parentAllowed = true)
        {
            foreach (var item in items ?? Enumerable.Empty<QualityProfileQualityItemResource>())
            {
                var itemAllowed = parentAllowed && item.Allowed;

                if (item.Quality != null)
                {
                    if (itemAllowed)
                    {
                        yield return item.Quality;
                    }

                    continue;
                }

                foreach (var child in GetEnabledQualityIds(item.Items, itemAllowed))
                {
                    yield return child;
                }
            }
        }

        private static IEnumerable<NzbDrone.Core.Qualities.Quality> GetCutoffQualityIds(IEnumerable<QualityProfileQualityItemResource> items, int cutoff)
        {
            foreach (var item in items ?? Enumerable.Empty<QualityProfileQualityItemResource>())
            {
                if (item.Quality != null)
                {
                    if (item.Quality.Id == cutoff)
                    {
                        yield return item.Quality;
                    }

                    continue;
                }

                if (item.Id == cutoff)
                {
                    foreach (var child in FlattenQualityIds(item.Items))
                    {
                        yield return child;
                    }
                }

                foreach (var child in GetCutoffQualityIds(item.Items, cutoff))
                {
                    yield return child;
                }
            }
        }

        private static IEnumerable<NzbDrone.Core.Qualities.Quality> FlattenQualityIds(IEnumerable<QualityProfileQualityItemResource> items)
        {
            foreach (var item in items ?? Enumerable.Empty<QualityProfileQualityItemResource>())
            {
                if (item.Quality != null)
                {
                    yield return item.Quality;
                    continue;
                }

                foreach (var child in FlattenQualityIds(item.Items))
                {
                    yield return child;
                }
            }
        }
    }
}
