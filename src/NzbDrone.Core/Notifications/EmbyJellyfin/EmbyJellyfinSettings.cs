using System;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.EmbyJellyfin
{
    public class EmbyJellyfinSettingsValidator : AbstractValidator<EmbyJellyfinSettings>
    {
        public EmbyJellyfinSettingsValidator()
        {
            RuleFor(c => c.ServerType)
                .Must(v => Enum.IsDefined(typeof(EmbyJellyfinServerType), v))
                .WithMessage("Select Emby or Jellyfin");
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase)
                .ValidUrlBase()
                .When(c => c.UrlBase.IsNotNullOrWhiteSpace());
            RuleFor(c => c.ApiKey).NotEmpty().WithMessage("API Key is required");
        }
    }

    public class EmbyJellyfinSettings : IProviderConfig
    {
        private static readonly EmbyJellyfinSettingsValidator Validator = new EmbyJellyfinSettingsValidator();

        public EmbyJellyfinSettings()
        {
            ServerType = (int)EmbyJellyfinServerType.Jellyfin;
            Port = 8096;
        }

        [FieldDefinition(0, Label = "EmbyJellyfinServerType", Type = FieldType.Select, SelectOptions = typeof(EmbyJellyfinServerType), HelpText = "EmbyJellyfinServerTypeHelpText")]
        public int ServerType { get; set; }

        [FieldDefinition(1, Label = "Host", Placeholder = "jellyfin", HelpText = "EmbyJellyfinHostHelpText")]
        public string Host { get; set; }

        [FieldDefinition(2, Label = "Port")]
        public int Port { get; set; }

        [FieldDefinition(3, Label = "provider.field.f1bb2b1610fc9", Type = FieldType.Checkbox, HelpText = "NotificationsSettingsUseSslHelpText")]
        [FieldToken(TokenField.HelpText, "UseSsl", "serviceName", "Emby / Jellyfin")]
        public bool UseSsl { get; set; }

        [FieldDefinition(4, Label = "provider.field.f39e9193cf08e", Type = FieldType.Textbox, Advanced = true, HelpText = "EmbyJellyfinUrlBaseHelpText")]
        public string UrlBase { get; set; }

        [FieldDefinition(5, Label = "ApiKey", Type = FieldType.Password, Privacy = PrivacyLevel.ApiKey, HelpText = "EmbyJellyfinApiKeyHelpText")]
        public string ApiKey { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
