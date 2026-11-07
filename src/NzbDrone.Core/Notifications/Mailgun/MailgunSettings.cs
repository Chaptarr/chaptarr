using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Mailgun
{
    public class MailGunSettingsValidator : AbstractValidator<MailgunSettings>
    {
        public MailGunSettingsValidator()
        {
            RuleFor(c => c.ApiKey).NotEmpty();
            RuleFor(c => c.From).NotEmpty();
            RuleFor(c => c.Recipients).NotEmpty();
        }
    }

    public class MailgunSettings : IProviderConfig
    {
        private static readonly MailGunSettingsValidator Validator = new MailGunSettingsValidator();

        public MailgunSettings()
        {
            Recipients = new string[] { };
        }

        [FieldDefinition(0, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f2a05babe5664")]
        public string ApiKey { get; set; }

        [FieldDefinition(1, Label = "provider.field.f4cfb24435d8f", HelpText = "provider.field.f5165e5321550", Type = FieldType.Checkbox)]
        public bool UseEuEndpoint { get; set; }

        [FieldDefinition(2, Label = "provider.field.f60643eae6376")]
        public string From { get; set; }

        [FieldDefinition(3, Label = "provider.field.f890b32cf6373")]
        public string SenderDomain { get; set; }

        [FieldDefinition(4, Label = "provider.field.f72382eeea71e", Type = FieldType.Tag)]
        public IEnumerable<string> Recipients { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
