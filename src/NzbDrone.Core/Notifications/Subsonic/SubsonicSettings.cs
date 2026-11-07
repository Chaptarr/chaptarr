using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Subsonic
{
    public class SubsonicSettingsValidator : AbstractValidator<SubsonicSettings>
    {
        public SubsonicSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());
        }
    }

    public class SubsonicSettings : IProviderConfig
    {
        private static readonly SubsonicSettingsValidator Validator = new SubsonicSettingsValidator();

        public SubsonicSettings()
        {
            Port = 4040;
        }

        [FieldDefinition(0, Label = "Host")]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port")]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "provider.field.fafbbcc619aac", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.fba5ea9a5b31d")]
        public string UrlBase { get; set; }

        [FieldDefinition(3, Label = "Username")]
        public string Username { get; set; }

        [FieldDefinition(4, Label = "Password", Type = FieldType.Password)]
        public string Password { get; set; }

        [FieldDefinition(5, Label = "provider.field.faa6115ddf3a5", Type = FieldType.Checkbox)]
        public bool Notify { get; set; }

        [FieldDefinition(6, Label = "NotificationsSettingsUpdateLibrary", HelpText = "provider.field.f436f30a5f35d", Type = FieldType.Checkbox)]
        public bool UpdateLibrary { get; set; }

        [FieldDefinition(7, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.fb3b8aedc8274")]
        public bool UseSsl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
