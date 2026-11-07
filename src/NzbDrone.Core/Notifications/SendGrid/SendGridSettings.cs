using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.SendGrid
{
    public class SendGridSettingsValidator : AbstractValidator<SendGridSettings>
    {
        public SendGridSettingsValidator()
        {
            RuleFor(c => c.ApiKey).NotEmpty();
            RuleFor(c => c.From).NotEmpty().EmailAddress();
            RuleFor(c => c.Recipients).NotEmpty();
            RuleForEach(c => c.Recipients).NotEmpty().EmailAddress();
        }
    }

    public class SendGridSettings : IProviderConfig
    {
        private static readonly SendGridSettingsValidator Validator = new SendGridSettingsValidator();

        public SendGridSettings()
        {
            BaseUrl = "https://api.sendgrid.com/v3/";
            Recipients = new string[] { };
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f06f81fd2699a", HelpLink = "https://sendgrid.com/docs/ui/account-and-settings/api-keys/#creating-an-api-key")]
        public string ApiKey { get; set; }

        [FieldDefinition(2, Label = "provider.field.f60643eae6376")]
        public string From { get; set; }

        [FieldDefinition(3, Label = "provider.field.f72382eeea71e", Type = FieldType.Tag)]
        public IEnumerable<string> Recipients { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
