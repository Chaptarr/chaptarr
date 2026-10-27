using FluentValidation.Validators;
using NzbDrone.Core.Profiles.Metadata;

namespace NzbDrone.Core.Validation
{
    public class MetadataProfileExistsValidator : PropertyValidator
    {
        private readonly IMetadataProfileService _profileService;

        public MetadataProfileExistsValidator(IMetadataProfileService profileService)
        {
            _profileService = profileService;
        }

        private MetadataProfileType? _profileType;

        public MetadataProfileExistsValidator ForType(MetadataProfileType profileType) =>
            new MetadataProfileExistsValidator(_profileService) { _profileType = profileType };

        protected override string GetDefaultMessageTemplate() => _profileType.HasValue
            ? $"Metadata profile does not exist or is incompatible with {_profileType.Value}"
            : "Metadata profile does not exist";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return true;
            }

            if ((int)context.PropertyValue == 0)
            {
                return true;
            }

            return _profileService.Exists((int)context.PropertyValue, _profileType);
        }
    }
}
