using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Pneumatic
{
    public class PneumaticSettingsValidator : AbstractValidator<PneumaticSettings>
    {
        public PneumaticSettingsValidator()
        {
            RuleFor(c => c.NzbFolder).IsValidPath();
            RuleFor(c => c.StrmFolder).IsValidPath();
        }
    }

    public class PneumaticSettings : IProviderConfig
    {
        private static readonly PneumaticSettingsValidator Validator = new PneumaticSettingsValidator();

        [FieldDefinition(0, Label = "provider.field.f631c93b4e0b4", Type = FieldType.Path, HelpText = "provider.field.f916f287836e6")]
        public string NzbFolder { get; set; }

        [FieldDefinition(1, Label = "provider.field.fac6d99147595", Type = FieldType.Path, HelpText = "provider.field.f8fe35ee9e555")]
        public string StrmFolder { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
