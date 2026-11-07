using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Join
{
    public class JoinSettingsValidator : AbstractValidator<JoinSettings>
    {
        public JoinSettingsValidator()
        {
            RuleFor(s => s.ApiKey).NotEmpty();
            RuleFor(s => s.DeviceIds).Empty().WithMessage("Use Device Names instead");
        }
    }

    public class JoinSettings : IProviderConfig
    {
        public JoinSettings()
        {
            Priority = (int)JoinPriority.Normal;
        }

        private static readonly JoinSettingsValidator Validator = new JoinSettingsValidator();

        [FieldDefinition(0, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.fb1d0448a0cf2", HelpLink = "https://joinjoaomgcd.appspot.com/")]
        public string ApiKey { get; set; }

        [FieldDefinition(1, Label = "provider.field.f00c0e6769b60", HelpText = "provider.field.faf9cbff5e244")]
        public string DeviceIds { get; set; }

        [FieldDefinition(2, Label = "provider.field.f5d08d1531c08", HelpText = "provider.field.fc7083c245d2b", HelpLink = "https://joaoapps.com/join/api/")]
        public string DeviceNames { get; set; }

        [FieldDefinition(3, Label = "provider.field.fb93be45094c3", Type = FieldType.Select, SelectOptions = typeof(JoinPriority))]
        public int Priority { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
