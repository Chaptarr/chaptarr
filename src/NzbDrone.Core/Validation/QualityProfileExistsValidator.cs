using FluentValidation.Validators;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Validation
{
    public class QualityProfileExistsValidator : PropertyValidator
    {
        private readonly IQualityProfileService _qualityProfileService;

        public QualityProfileExistsValidator(IQualityProfileService qualityProfileService)
        {
            _qualityProfileService = qualityProfileService;
        }

        private ProfileType? _profileType;

        public QualityProfileExistsValidator ForType(ProfileType profileType) =>
            new QualityProfileExistsValidator(_qualityProfileService) { _profileType = profileType };

        protected override string GetDefaultMessageTemplate() => _profileType.HasValue
            ? $"Quality profile does not exist or is incompatible with {_profileType.Value}"
            : "Quality Profile does not exist";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context?.PropertyValue == null || (int)context.PropertyValue == 0)
            {
                return true;
            }

            return _qualityProfileService.Exists((int)context.PropertyValue, _profileType);
        }
    }
}
