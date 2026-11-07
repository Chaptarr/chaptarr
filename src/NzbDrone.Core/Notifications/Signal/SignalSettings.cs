using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Signal
{
    public class SignalSettingsValidator : AbstractValidator<SignalSettings>
    {
        public SignalSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).ValidPort();
            RuleFor(c => c.SenderNumber).NotEmpty();
            RuleFor(c => c.ReceiverId).NotEmpty();
        }
    }

    public class SignalSettings : IProviderConfig
    {
        private static readonly SignalSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox, HelpText = "provider.field.f49960de5880e")]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox, HelpText = "provider.field.f6c237681e709")]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.f8492c969f7b0")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "provider.field.f853997241a0b", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f5fc3803de462")]
        public string SenderNumber { get; set; }

        [FieldDefinition(4, Label = "provider.field.f1585c2aec9ee", HelpText = "provider.field.f512a785c521b")]
        public string ReceiverId { get; set; }

        [FieldDefinition(5, Label = "Login", Privacy = PrivacyLevel.UserName, HelpText = "provider.field.fedc96b5b9bc0")]
        public string AuthUsername { get; set; }

        [FieldDefinition(6, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "provider.field.fef97b1e8d4ea")]
        public string AuthPassword { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
