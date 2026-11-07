using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Slack
{
    public class SlackSettingsValidator : AbstractValidator<SlackSettings>
    {
        public SlackSettingsValidator()
        {
            RuleFor(c => c.WebHookUrl).IsValidUrl();
            RuleFor(c => c.Username).NotEmpty();
        }
    }

    public class SlackSettings : IProviderConfig
    {
        private static readonly SlackSettingsValidator Validator = new SlackSettingsValidator();

        [FieldDefinition(0, Label = "provider.field.f84805a7574a8", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f8cc89a742cf5", Type = FieldType.Url, HelpLink = "https://my.slack.com/services/new/incoming-webhook/")]
        public string WebHookUrl { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "provider.field.f855c2d83dc98", Type = FieldType.Textbox)]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "provider.field.fa35abcd6dac9", HelpText = "provider.field.ffed63bc05b4c", Type = FieldType.Textbox, HelpLink = "http://www.emoji-cheat-sheet.com/")]
        public string Icon { get; set; }

        [FieldDefinition(3, Label = "provider.field.fce4683e7013a", HelpText = "provider.field.f0e145034ef57", Type = FieldType.Textbox)]
        public string Channel { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
