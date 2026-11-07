using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Pushover
{
    public class PushoverSettingsValidator : AbstractValidator<PushoverSettings>
    {
        public PushoverSettingsValidator()
        {
            RuleFor(c => c.UserKey).NotEmpty();
            RuleFor(c => c.Retry).GreaterThanOrEqualTo(30).LessThanOrEqualTo(86400).When(c => (PushoverPriority)c.Priority == PushoverPriority.Emergency);
            RuleFor(c => c.Retry).GreaterThanOrEqualTo(0).LessThanOrEqualTo(86400).When(c => (PushoverPriority)c.Priority == PushoverPriority.Emergency);
        }
    }

    public class PushoverSettings : IProviderConfig
    {
        private static readonly PushoverSettingsValidator Validator = new PushoverSettingsValidator();

        public PushoverSettings()
        {
            Priority = 0;
            Devices = new string[] { };
        }

        //TODO: Get Pushover to change our app name (or create a new app) when we have a new logo
        [FieldDefinition(0, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey, HelpLink = "https://pushover.net/apps/clone/chaptarr")]
        public string ApiKey { get; set; }

        [FieldDefinition(1, Label = "provider.field.f99715d7e1dca", Privacy = PrivacyLevel.UserName, HelpLink = "https://pushover.net/")]
        public string UserKey { get; set; }

        [FieldDefinition(2, Label = "provider.field.f4ba5121d4d1b", HelpText = "provider.field.f282ebfbf4a64", Type = FieldType.Tag)]
        public IEnumerable<string> Devices { get; set; }

        [FieldDefinition(3, Label = "Priority", Type = FieldType.Select, SelectOptions = typeof(PushoverPriority))]
        public int Priority { get; set; }

        [FieldDefinition(4, Label = "Retry", Type = FieldType.Textbox, HelpText = "provider.field.fd19d16924c8e")]
        public int Retry { get; set; }

        [FieldDefinition(5, Label = "provider.field.f1c29efccf85b", Type = FieldType.Textbox, HelpText = "provider.field.f5afe86ecec39")]
        public int Expire { get; set; }

        [FieldDefinition(6, Label = "provider.field.f46124e023603", Type = FieldType.Textbox, HelpText = "provider.field.f09a1e89c10cf", HelpLink = "https://pushover.net/api#sounds")]
        public string Sound { get; set; }

        public bool IsValid => !string.IsNullOrWhiteSpace(UserKey) && Priority >= -1 && Priority <= 2;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
