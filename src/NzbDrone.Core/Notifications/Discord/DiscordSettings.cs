using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Discord
{
    public class DiscordSettingsValidator : AbstractValidator<DiscordSettings>
    {
        public DiscordSettingsValidator()
        {
            RuleFor(c => c.WebHookUrl).IsValidUrl();
        }
    }

    public class DiscordSettings : IProviderConfig
    {
        private static readonly DiscordSettingsValidator Validator = new DiscordSettingsValidator();

        [FieldDefinition(0, Label = "provider.field.f84805a7574a8", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f8de6b9eb6b73")]
        public string WebHookUrl { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "provider.field.f2cca7c9e363a")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "provider.field.fca8e826d9c2e", HelpText = "provider.field.f8de7ad2cab3f", Type = FieldType.Textbox)]
        public string Avatar { get; set; }
        [FieldDefinition(3, Label = "Host", Advanced = true, HelpText = "provider.field.fad164e56581a", Type = FieldType.Textbox)]
        public string Author { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
