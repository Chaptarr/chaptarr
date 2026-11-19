using System;
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
        private Func<object, int?> _storedProfileId;

        public MetadataProfileExistsValidator ForType(MetadataProfileType profileType, Func<object, int?> storedProfileId = null) =>
            new MetadataProfileExistsValidator(_profileService) { _profileType = profileType, _storedProfileId = storedProfileId };

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

            var id = (int)context.PropertyValue;
            var unchanged = !NzbDrone.Core.Configuration.ConfigValueValidation.HasChanged(id, _storedProfileId?.Invoke(context.InstanceToValidate));
            return _profileService.Exists(id, unchanged ? null : _profileType);
        }
    }
}
