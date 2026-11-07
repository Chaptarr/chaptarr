using System;
using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Apprise
{
    public class AppriseSettingsValidator : AbstractValidator<AppriseSettings>
    {
        public AppriseSettingsValidator()
        {
            RuleFor(c => c.ServerUrl).IsValidUrl();

            RuleFor(c => c.ConfigurationKey).NotEmpty()
                .When(c => c.StatelessUrls.IsNullOrWhiteSpace())
                .WithMessage("Use either Configuration Key or Stateless URLs");

            RuleFor(c => c.ConfigurationKey).Matches("^[a-z0-9-]*$")
                .WithMessage("Allowed characters a-z, 0-9 and -");

            RuleFor(c => c.StatelessUrls).NotEmpty()
                .When(c => c.ConfigurationKey.IsNullOrWhiteSpace())
                .WithMessage("Use either Configuration Key or Stateless URLs");

            RuleFor(c => c.StatelessUrls).Empty()
                .When(c => c.ConfigurationKey.IsNotNullOrWhiteSpace())
                .WithMessage("Use either Configuration Key or Stateless URLs");

            RuleFor(c => c.Tags).Empty()
                .When(c => c.StatelessUrls.IsNotNullOrWhiteSpace())
                .WithMessage("Stateless URLs do not support tags");
        }
    }

    public class AppriseSettings : IProviderConfig
    {
        private static readonly AppriseSettingsValidator Validator = new();

        public AppriseSettings()
        {
            NotificationType = (int)AppriseNotificationType.Info;
            Tags = Array.Empty<string>();
        }

        [FieldDefinition(1, Label = "provider.field.fb29859a635ea", Type = FieldType.Url, Placeholder = "http://localhost:8000", HelpText = "provider.field.f8acece0624d6", HelpLink = "https://github.com/caronc/apprise-api")]
        public string ServerUrl { get; set; }

        [FieldDefinition(2, Label = "provider.field.fe1c79d2205ed", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f023f9ed3122e", HelpLink = "https://github.com/caronc/apprise-api#persistent-storage-solution")]
        public string ConfigurationKey { get; set; }

        [FieldDefinition(3, Label = "provider.field.f658dfc5315ec", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.fce417b5d7e08", HelpLink = "https://github.com/caronc/apprise#productivity-based-notifications")]
        public string StatelessUrls { get; set; }

        [FieldDefinition(4, Label = "provider.field.f3446a71f644e", Type = FieldType.Select, SelectOptions = typeof(AppriseNotificationType))]
        public int NotificationType { get; set; }

        [FieldDefinition(5, Label = "provider.field.feb30cf8f492e", Type = FieldType.Tag, HelpText = "provider.field.fdd6851e46211")]
        public IEnumerable<string> Tags { get; set; }

        [FieldDefinition(6, Label = "Username", Type = FieldType.Textbox, HelpText = "provider.field.f6832b304d4fe", Privacy = PrivacyLevel.UserName)]
        public string AuthUsername { get; set; }

        [FieldDefinition(7, Label = "Password", Type = FieldType.Password, HelpText = "provider.field.fc909ee67c6a0", Privacy = PrivacyLevel.Password)]
        public string AuthPassword { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
