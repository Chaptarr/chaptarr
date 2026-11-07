using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Email
{
    public class EmailSettingsValidator : AbstractValidator<EmailSettings>
    {
        public EmailSettingsValidator()
        {
            RuleFor(c => c.Server).NotEmpty();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.From).NotEmpty();
            RuleForEach(c => c.To).EmailAddress();
            RuleForEach(c => c.Cc).EmailAddress();
            RuleForEach(c => c.Bcc).EmailAddress();

            // Only require one of three send fields to be set
            RuleFor(c => c.To).NotEmpty().Unless(c => c.Bcc.Any() || c.Cc.Any());
            RuleFor(c => c.Cc).NotEmpty().Unless(c => c.To.Any() || c.Bcc.Any());
            RuleFor(c => c.Bcc).NotEmpty().Unless(c => c.To.Any() || c.Cc.Any());
        }
    }

    public class EmailSettings : IProviderConfig
    {
        private static readonly EmailSettingsValidator Validator = new EmailSettingsValidator();

        public EmailSettings()
        {
            Server = "smtp.gmail.com";
            Port = 587;

            To = Array.Empty<string>();
            Cc = Array.Empty<string>();
            Bcc = Array.Empty<string>();
        }

        [FieldDefinition(0, Label = "provider.field.faef7de28d529", HelpText = "provider.field.fd7f4ba9ed2d0")]
        public string Server { get; set; }

        [FieldDefinition(1, Label = "Port")]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "provider.field.f79e669c0dcf4", HelpText = "provider.field.f895167b520ae", Type = FieldType.Checkbox)]
        public bool RequireEncryption { get; set; }

        [FieldDefinition(3, Label = "Username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(4, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(5, Label = "provider.field.f60643eae6376")]
        public string From { get; set; }

        [FieldDefinition(6, Label = "provider.field.f72382eeea71e", HelpText = "provider.field.f0d6ced609bf9")]
        public IEnumerable<string> To { get; set; }

        [FieldDefinition(7, Label = "provider.field.f83b271834ed3", HelpText = "provider.field.f99bfb9cfd585", Advanced = true)]
        public IEnumerable<string> Cc { get; set; }

        [FieldDefinition(8, Label = "provider.field.f981e30ee295e", HelpText = "provider.field.faf4d131c5dbd", Advanced = true)]
        public IEnumerable<string> Bcc { get; set; }

        [FieldDefinition(9, Label = "provider.field.ff91546a4f56f", HelpText = "provider.field.fab071934a5ed", Type = FieldType.Checkbox)]
        public bool AttachFiles { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
