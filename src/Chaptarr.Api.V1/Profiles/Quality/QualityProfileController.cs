using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
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
        private readonly IBookshelfLegacyIdentityMapService _bookshelfLegacyIdentityMap;
        private readonly IBookshelfQualityProfileService _bookshelfProfiles;

        public QualityProfileController(IQualityProfileService qualityProfileService, ICustomFormatService formatService, IBookshelfLegacyIdentityMapService bookshelfLegacyIdentityMap = null, IBookshelfQualityProfileService bookshelfProfiles = null)
        {
            _qualityProfileService = qualityProfileService;
            _formatService = formatService;
            _bookshelfLegacyIdentityMap = bookshelfLegacyIdentityMap;
            _bookshelfProfiles = bookshelfProfiles;
            SharedValidator.RuleFor(c => c.Name).NotEmpty();
            SharedValidator.RuleFor(c => c.ProfileType)
                .Must(x => Enum.IsDefined(typeof(ProfileType), x))
                .WithMessage("Profile type must be Audiobook or Ebook")
                .When(_ => !IsGenericBookshelfRequest());
            SharedValidator.RuleFor(c => c.Cutoff).ValidCutoff();
            SharedValidator.RuleFor(c => c.Items).ValidItems();

            SharedValidator.RuleFor(c => c.FormatItems).NotNull();

            SharedValidator.RuleFor(c => c).Custom((profile, context) =>
            {
                var genericBookshelf = IsGenericBookshelfRequest();
                if (genericBookshelf)
                {
                    var qualities = FlattenQualityIds(profile.Items).Select(quality => quality.Id).OrderBy(id => id);
                    if (!qualities.SequenceEqual(NzbDrone.Core.Qualities.Quality.All.Select(quality => quality.Id).OrderBy(id => id)))
                    {
                        context.AddFailure("Items", "Must contain all qualities");
                    }
                }
                else
                {
                    foreach (var failure in GetProfileTypeQualityFailures(profile, GetRetainedImportedProfile(profile)))
                    {
                        context.AddFailure(failure.PropertyName, failure.Message);
                    }
                }

                var formatItems = profile.FormatItems ?? new List<ProfileFormatItemResource>();
                var expectedIds = _formatService.All()
                    .Where(format => genericBookshelf ? format.AppliesTo == CustomFormatMediaType.Both : format.AppliesToProfile(profile.ProfileType))
                    .Select(format => format.Id)
                    .ToHashSet();
                var suppliedIds = formatItems.Select(item => item.Format).ToList();

                if (genericBookshelf
                    ? !expectedIds.IsSubsetOf(suppliedIds)
                    : suppliedIds.Count != suppliedIds.Distinct().Count() || !expectedIds.SetEquals(suppliedIds))
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
            if (IsGenericBookshelfRequest())
            {
                var created = BookshelfProfiles.Create(ToGenericBookshelfModel(resource));
                return Created(created.LegacyId);
            }

            var model = resource.ToModel();
            model = _qualityProfileService.Add(model);
            return Created(model.Id);
        }

        [RestDeleteById]
        public void DeleteProfile(int id)
        {
            if (IsGenericBookshelfRequest())
            {
                BookshelfProfiles.Delete(id);
                return;
            }

            _qualityProfileService.Delete(id);
        }

        [RestPutById]
        public ActionResult<QualityProfileResource> Update([FromBody] QualityProfileResource resource)
        {
            if (IsGenericBookshelfRequest())
            {
                BookshelfProfiles.Update(resource.Id, ToGenericBookshelfModel(resource));
                return Accepted(resource.Id);
            }

            var model = resource.ToModel();

            _qualityProfileService.Update(model);

            return Accepted(model.Id);
        }

        protected override QualityProfileResource GetResourceById(int id)
        {
            if (IsGenericBookshelfRequest())
            {
                return ToGenericBookshelfResource(BookshelfProfiles.Get(id));
            }

            var bookshelfCompatibility = HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true;
            return _qualityProfileService.Get(id).ToResource(filterToProfileType: !bookshelfCompatibility);
        }

        [HttpGet]
        public List<QualityProfileResource> GetAll([FromQuery] string mediaType = null)
        {
            var requestedMediaType = QualityProfileMediaTypeParser.ParseOrNull(mediaType);
            if (IsGenericBookshelfRequest() && !requestedMediaType.HasValue)
            {
                return BookshelfProfiles.All().Select(ToGenericBookshelfResource).OrderBy(profile => profile.Id).ToList();
            }

            var bookshelfCompatibility = HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true;
            var profiles = requestedMediaType.HasValue
                ? _qualityProfileService.GetByType(requestedMediaType.Value.ToProfileType())
                : bookshelfCompatibility
                    ? _qualityProfileService.GetByType(ProfileType.Audiobook)
                    : _qualityProfileService.All();

            return profiles.ToResource(filterToProfileType: requestedMediaType.HasValue);
        }

        private IBookshelfQualityProfileService BookshelfProfiles => _bookshelfProfiles ??
            throw new InvalidOperationException("BookshelfNG quality profile compatibility is unavailable.");

        private bool IsGenericBookshelfRequest()
        {
            var facade = HttpContext?.GetReadarrFacadeContext();
            return facade?.IsBookshelfCompatibility == true && string.IsNullOrWhiteSpace(facade.MediaType);
        }

        private static QualityProfile ToGenericBookshelfModel(QualityProfileResource resource)
        {
            resource.ProfileType = ProfileType.Audiobook;
            return resource.ToModel();
        }

        private QualityProfileResource ToGenericBookshelfResource(BookshelfQualityProfile profile)
        {
            var resource = profile.Profile.ToResource(filterToProfileType: false);
            resource.Id = profile.LegacyId;
            var sharedFormats = _formatService.All().Where(format => format.AppliesTo == CustomFormatMediaType.Both)
                .Select(format => format.Id).ToHashSet();
            resource.FormatItems = resource.FormatItems.Where(item => sharedFormats.Contains(item.Format)).ToList();
            return resource;
        }

        private QualityProfileResource GetRetainedImportedProfile(QualityProfileResource profile)
        {
            if (profile.Id <= 0 || _bookshelfLegacyIdentityMap == null ||
                !string.Equals(HttpContext?.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var mediaType = profile.ProfileType == ProfileType.Audiobook ? 0 : 1;
            var imported = _bookshelfLegacyIdentityMap.GetMappingsByTargetIds("QualityProfile", new[] { profile.Id })
                .Any(mapping => mapping.Source == "BookshelfNG" && mapping.SourceId > 0 &&
                    mapping.TargetId == profile.Id && mapping.MediaType == mediaType);
            if (!imported)
            {
                return null;
            }

            var existing = _qualityProfileService.Get(profile.Id);
            return existing.ProfileType == profile.ProfileType ? existing.ToResource(filterToProfileType: false) : null;
        }

        private static IEnumerable<(string PropertyName, string Message)> GetProfileTypeQualityFailures(QualityProfileResource profile, QualityProfileResource retainedProfile)
        {
            // Generic predecessor profiles can contain both formats. Keep unchanged imported
            // choices editable without allowing creates or newly enabled incompatible qualities.
            var retainedEnabled = GetEnabledQualityIds(retainedProfile?.Items).Select(quality => quality.Id).ToHashSet();
            var retainedCutoff = retainedProfile != null && profile.Cutoff == retainedProfile.Cutoff
                ? GetCutoffQualityIds(retainedProfile.Items, retainedProfile.Cutoff).Select(quality => quality.Id).ToHashSet()
                : new HashSet<int>();

            foreach (var quality in GetEnabledQualityIds(profile.Items))
            {
                if (!ProfileResourceMapper.IsQualityAllowedForProfileType(quality.Id, profile.ProfileType) &&
                    !retainedEnabled.Contains(quality.Id))
                {
                    yield return ("Items", $"{quality.Name} cannot be enabled on a {profile.ProfileType} quality profile");
                }
            }

            foreach (var quality in GetCutoffQualityIds(profile.Items, profile.Cutoff))
            {
                if (!ProfileResourceMapper.IsQualityAllowedForProfileType(quality.Id, profile.ProfileType) &&
                    !retainedCutoff.Contains(quality.Id))
                {
                    yield return ("Cutoff", $"{quality.Name} cannot be used as the cutoff for a {profile.ProfileType} quality profile");
                }
            }

            if (profile.ConvertToQualityId.HasValue &&
                profile.ConvertToQualityId.Value > 0 &&
                !ProfileResourceMapper.IsQualityAllowedForProfileType(profile.ConvertToQualityId.Value, profile.ProfileType) &&
                profile.ConvertToQualityId != retainedProfile?.ConvertToQualityId)
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
