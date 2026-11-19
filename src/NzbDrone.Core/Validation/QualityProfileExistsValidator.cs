using System;
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
        private Func<object, int?> _storedProfileId;

        public QualityProfileExistsValidator ForType(ProfileType profileType, Func<object, int?> storedProfileId = null) =>
            new QualityProfileExistsValidator(_qualityProfileService) { _profileType = profileType, _storedProfileId = storedProfileId };

        protected override string GetDefaultMessageTemplate() => _profileType.HasValue
            ? $"Quality profile does not exist or is incompatible with {_profileType.Value}"
            : "Quality Profile does not exist";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context?.PropertyValue == null || (int)context.PropertyValue == 0)
            {
                return true;
            }

            var id = (int)context.PropertyValue;
            // Keep validating existence; only a newly selected profile must match the side.
            var unchanged = !NzbDrone.Core.Configuration.ConfigValueValidation.HasChanged(id, _storedProfileId?.Invoke(context.InstanceToValidate));
            return _qualityProfileService.Exists(id, unchanged ? null : _profileType);
        }
    }
}
