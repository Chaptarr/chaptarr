using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Profiles.Qualities
{
    public class QualityProfileValidator : AbstractValidator<QualityProfile>
    {
        public QualityProfileValidator()
        {
            RuleFor(value => value.Name).NotEmpty();
            RuleFor(value => value.ProfileType).IsInEnum();
            RuleFor(value => value.Cutoff).ValidCutoff();
            RuleFor(value => (IList<QualityProfileQualityItem>)value.Items).ValidItems().OverridePropertyName("Items");
            RuleFor(value => value.FormatItems).NotNull();
            RuleFor(value => value).Custom((profile, context) =>
            {
                foreach (var failure in GetProfileTypeQualityFailures(profile)) context.AddFailure(failure.PropertyName, failure.Message);
                if (profile.FormatItems != null && profile.FormatItems.Where(item => item.Score > 0).Sum(item => item.Score) < profile.MinFormatScore &&
                    (!profile.FormatItems.Any() || profile.FormatItems.Max(item => item.Score) < profile.MinFormatScore))
                {
                    context.AddFailure("Minimum Custom Format Score can never be satisfied");
                }
            });
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

        private static IEnumerable<(string PropertyName, string Message)> GetProfileTypeQualityFailures(QualityProfile profile)
        {
            foreach (var quality in GetEnabledQualityIds(profile.Items))
            {
                if (!IsQualityAllowedForProfileType(quality.Id, profile.ProfileType))
                {
                    yield return ("Items", $"{quality.Name} cannot be enabled on a {profile.ProfileType} quality profile");
                }
            }

            foreach (var quality in GetCutoffQualityIds(profile.Items, profile.Cutoff))
            {
                if (!IsQualityAllowedForProfileType(quality.Id, profile.ProfileType))
                {
                    yield return ("Cutoff", $"{quality.Name} cannot be used as the cutoff for a {profile.ProfileType} quality profile");
                }
            }

            if (profile.ConvertToQualityId.HasValue &&
                profile.ConvertToQualityId.Value > 0 &&
                !IsQualityAllowedForProfileType(profile.ConvertToQualityId.Value, profile.ProfileType))
            {
                yield return ("ConvertToQualityId", $"Conversion target must be a {profile.ProfileType} quality");
            }
        }

        private static IEnumerable<NzbDrone.Core.Qualities.Quality> GetEnabledQualityIds(IEnumerable<QualityProfileQualityItem> items, bool parentAllowed = true)
        {
            foreach (var item in items ?? Enumerable.Empty<QualityProfileQualityItem>())
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

        private static IEnumerable<NzbDrone.Core.Qualities.Quality> GetCutoffQualityIds(IEnumerable<QualityProfileQualityItem> items, int cutoff)
        {
            foreach (var item in items ?? Enumerable.Empty<QualityProfileQualityItem>())
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

        private static IEnumerable<NzbDrone.Core.Qualities.Quality> FlattenQualityIds(IEnumerable<QualityProfileQualityItem> items)
        {
            foreach (var item in items ?? Enumerable.Empty<QualityProfileQualityItem>())
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
