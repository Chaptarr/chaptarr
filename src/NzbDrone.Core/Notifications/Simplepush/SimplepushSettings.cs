using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Simplepush
{
    public class SimplepushSettingsValidator : AbstractValidator<SimplepushSettings>
    {
        public SimplepushSettingsValidator()
        {
            RuleFor(c => c.Key).NotEmpty();
        }
    }

    public class SimplepushSettings : IProviderConfig
    {
        private static readonly SimplepushSettingsValidator Validator = new SimplepushSettingsValidator();

        [FieldDefinition(0, Label = "provider.field.f99a52df3ff3d", Privacy = PrivacyLevel.ApiKey, HelpLink = "https://simplepush.io/features")]
        public string Key { get; set; }

        [FieldDefinition(1, Label = "provider.field.f4e1f49a9c8ae", HelpText = "provider.field.fa4361126b6b0", HelpLink = "https://simplepush.io/features")]
        public string Event { get; set; }

        public bool IsValid => !string.IsNullOrWhiteSpace(Key);

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
