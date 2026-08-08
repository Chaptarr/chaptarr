using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.REST;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.Profiles.Quality
{
    public class QualityProfileResource : RestResource
    {
        public string Name { get; set; }
        public ProfileType ProfileType { get; set; }
        public bool UpgradeAllowed { get; set; }
        public bool PreferCustomFormatsOverQuality { get; set; }
        public bool ConvertMp3ToM4b { get; set; }
        public int? ConvertToQualityId { get; set; }
        public int Cutoff { get; set; }
        public List<QualityProfileQualityItemResource> Items { get; set; }
        public int MinFormatScore { get; set; }
        public int CutoffFormatScore { get; set; }
        public List<ProfileFormatItemResource> FormatItems { get; set; }
    }

    public class QualityProfileQualityItemResource : RestResource
    {
        public string Name { get; set; }
        public NzbDrone.Core.Qualities.Quality Quality { get; set; }
        public List<QualityProfileQualityItemResource> Items { get; set; }
        public bool Allowed { get; set; }

        public QualityProfileQualityItemResource()
        {
            Items = new List<QualityProfileQualityItemResource>();
        }
    }

    public class ProfileFormatItemResource : RestResource
    {
        public int Format { get; set; }
        public string BuiltInKey { get; set; }
        public string Name { get; set; }
        public int Score { get; set; }
    }

    public static class ProfileResourceMapper
    {
        public static QualityProfileResource ToResource(this QualityProfile model)
        {
            return ToResource(model, filterToProfileType: false);
        }

        public static QualityProfileResource ToResource(this QualityProfile model, bool filterToProfileType)
        {
            if (model == null)
            {
                return null;
            }

            var items = filterToProfileType ? FilterItemsForProfileType(model.Items, model.ProfileType) : model.Items;
            var formatItems = (model.FormatItems ?? new List<ProfileFormatItem>())
                .Where(item => item?.Format?.AppliesToProfile(model.ProfileType) == true)
                .ToList();
            var convertToQualityId = filterToProfileType && !IsQualityAllowedForProfileType(model.ConvertToQualityId, model.ProfileType)
                ? null
                : model.ConvertToQualityId;
            var cutoff = filterToProfileType && !IsCutoffAllowedForProfileType(model.Cutoff, model.ProfileType, items)
                ? ResolveFallbackCutoff(items)
                : model.Cutoff;

            return new QualityProfileResource
            {
                Id = model.Id,
                Name = model.Name,
                ProfileType = model.ProfileType,
                UpgradeAllowed = model.UpgradeAllowed,
                PreferCustomFormatsOverQuality = model.ProfileType == ProfileType.Audiobook && model.PreferCustomFormatsOverQuality,
                ConvertMp3ToM4b = model.ConvertMp3ToM4b,
                ConvertToQualityId = convertToQualityId,
                Cutoff = cutoff,
                Items = items.ConvertAll(ToResource),
                MinFormatScore = model.MinFormatScore,
                CutoffFormatScore = model.CutoffFormatScore,
                FormatItems = formatItems.ConvertAll(ToResource)
            };
        }

        public static QualityProfileQualityItemResource ToResource(this QualityProfileQualityItem model)
        {
            if (model == null)
            {
                return null;
            }

            return new QualityProfileQualityItemResource
            {
                Id = model.Id,
                Name = model.Name,
                Quality = model.Quality,
                Items = model.Items.ConvertAll(ToResource),
                Allowed = model.Allowed
            };
        }

        public static ProfileFormatItemResource ToResource(this ProfileFormatItem model)
        {
            return new ProfileFormatItemResource
            {
                Format = model.Format.Id,
                BuiltInKey = model.Format.BuiltInKey,
                Name = model.Format.Name,
                Score = model.Score
            };
        }

        public static QualityProfile ToModel(this QualityProfileResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            if (!Enum.IsDefined(typeof(ProfileType), resource.ProfileType))
            {
                throw new BadRequestException("Profile type must be Audiobook or Ebook");
            }

            var convertToQualityId = ResolveConvertToQualityId(resource);

            return new QualityProfile
            {
                Id = resource.Id,
                Name = resource.Name,
                ProfileType = resource.ProfileType,
                UpgradeAllowed = resource.UpgradeAllowed,
                PreferCustomFormatsOverQuality = resource.ProfileType == ProfileType.Audiobook && resource.PreferCustomFormatsOverQuality,
                // Legacy compatibility field: this is true only for the original MP3 -> M4B
                // toggle. ConvertToQualityId is the canonical conversion target.
                ConvertMp3ToM4b = convertToQualityId == NzbDrone.Core.Qualities.Quality.M4B.Id,
                ConvertToQualityId = convertToQualityId,
                Cutoff = resource.Cutoff,
                Items = (resource.Items ?? new List<QualityProfileQualityItemResource>()).ConvertAll(ToModel),
                MinFormatScore = resource.MinFormatScore,
                CutoffFormatScore = resource.CutoffFormatScore,
                FormatItems = (resource.FormatItems ?? new List<ProfileFormatItemResource>()).ConvertAll(ToModel)
            };
        }

        private static int? ResolveConvertToQualityId(QualityProfileResource resource)
        {
            if (resource.ConvertToQualityId.HasValue)
            {
                return resource.ConvertToQualityId.Value > 0 ? resource.ConvertToQualityId : null;
            }

            // Older clients only know the legacy MP3->M4B checkbox. Preserve that
            // intent by mapping it to the current M4B target instead of clearing it.
            return resource.ConvertMp3ToM4b ? NzbDrone.Core.Qualities.Quality.M4B.Id : null;
        }

        public static QualityProfileQualityItem ToModel(this QualityProfileQualityItemResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            return new QualityProfileQualityItem
            {
                Id = resource.Id,
                Name = resource.Name,
                Quality = resource.Quality != null ? (NzbDrone.Core.Qualities.Quality)resource.Quality.Id : null,
                Items = resource.Items.ConvertAll(ToModel),
                Allowed = resource.Allowed
            };
        }

        public static ProfileFormatItem ToModel(this ProfileFormatItemResource resource)
        {
            return new ProfileFormatItem
            {
                Format = new CustomFormat { Id = resource.Format },
                Score = resource.Score
            };
        }

        public static List<QualityProfileResource> ToResource(this IEnumerable<QualityProfile> models)
        {
            return models.Select(ToResource).ToList();
        }

        public static List<QualityProfileResource> ToResource(this IEnumerable<QualityProfile> models, bool filterToProfileType)
        {
            return models.Select(model => ToResource(model, filterToProfileType)).ToList();
        }

        private static List<QualityProfileQualityItem> FilterItemsForProfileType(List<QualityProfileQualityItem> items, ProfileType profileType)
        {
            return (items ?? new List<QualityProfileQualityItem>())
                .Select(item => FilterItemForProfileType(item, profileType))
                .Where(item => item != null)
                .ToList();
        }

        private static QualityProfileQualityItem FilterItemForProfileType(QualityProfileQualityItem item, ProfileType profileType)
        {
            if (item == null)
            {
                return null;
            }

            if (item.Quality != null)
            {
                return IsQualityAllowedForProfileType(item.Quality.Id, profileType) ? item : null;
            }

            var children = FilterItemsForProfileType(item.Items, profileType);
            if (children.Count == 0)
            {
                return null;
            }

            if (children.Count == 1)
            {
                return children[0];
            }

            return new QualityProfileQualityItem
            {
                Id = item.Id,
                Name = item.Name,
                Items = children,
                Allowed = children.Any(child => child.Allowed)
            };
        }

        public static bool IsQualityAllowedForProfileType(int? qualityId, ProfileType profileType)
        {
            if (!qualityId.HasValue ||
                qualityId.Value < 0 ||
                qualityId.Value >= NzbDrone.Core.Qualities.Quality.AllLookup.Length ||
                NzbDrone.Core.Qualities.Quality.AllLookup[qualityId.Value] == null)
            {
                return false;
            }

            var quality = NzbDrone.Core.Qualities.Quality.FindById(qualityId.Value);

            return profileType == ProfileType.Audiobook
                ? QualityMediaTypeHelper.IsAudiobookQuality(quality)
                : QualityMediaTypeHelper.IsEbookFileQuality(quality);
        }

        private static bool IsCutoffAllowedForProfileType(int cutoff, ProfileType profileType, List<QualityProfileQualityItem> filteredItems)
        {
            return IsQualityAllowedForProfileType(cutoff, profileType) ||
                   ContainsCutoff(filteredItems, cutoff);
        }

        private static bool ContainsCutoff(IEnumerable<QualityProfileQualityItem> items, int cutoff)
        {
            foreach (var item in items ?? Enumerable.Empty<QualityProfileQualityItem>())
            {
                if (item == null)
                {
                    continue;
                }

                if (item.Id == cutoff || item.Quality?.Id == cutoff)
                {
                    return true;
                }

                if (ContainsCutoff(item.Items, cutoff))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ResolveFallbackCutoff(List<QualityProfileQualityItem> items)
        {
            var firstAllowed = items?.FirstOrDefault(item => item.Allowed);

            if (firstAllowed?.Quality != null)
            {
                return firstAllowed.Quality.Id;
            }

            if (firstAllowed?.Items?.Count > 0)
            {
                return firstAllowed.Id > 0 ? firstAllowed.Id : firstAllowed.Items.First().Quality.Id;
            }

            return items?.FirstOrDefault()?.Quality?.Id ?? 0;
        }
    }
}
