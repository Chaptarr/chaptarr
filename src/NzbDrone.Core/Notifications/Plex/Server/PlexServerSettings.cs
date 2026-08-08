using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Plex.Server
{
    public class PlexServerSettingsValidator : AbstractValidator<PlexServerSettings>
    {
        public PlexServerSettingsValidator()
        {
            RuleFor(c => c.AuthToken).NotEmpty().WithMessage("Authenticate with Plex.tv first");
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.LibrarySectionId)
                .Must(v => v.IsNullOrWhiteSpace() || (int.TryParse(v, out var id) && id > 0))
                .WithMessage("Invalid Plex library selection");
            RuleFor(c => c.MapFrom).NotEmpty().Unless(c => c.MapTo.IsNullOrWhiteSpace());
            RuleFor(c => c.MapTo).NotEmpty().Unless(c => c.MapFrom.IsNullOrWhiteSpace());
        }
    }

    public class PlexServerSettings : IProviderConfig
    {
        private static readonly PlexServerSettingsValidator Validator = new PlexServerSettingsValidator();

        public PlexServerSettings()
        {
            Port = 32400;
            UseSsl = true;
            UpdateLibrary = true;
            SignIn = "startOAuth";
        }

        [FieldDefinition(0, Label = "NotificationsPlexSettingsAuthenticateWithPlexTv", Type = FieldType.OAuth)]
        public string SignIn { get; set; }

        [FieldDefinition(1, Label = "PlexServerConnection", Type = FieldType.Select, SelectOptionsProviderAction = "getPlexServers", HelpText = "PlexServerConnectionHelpText")]
        public string ServerConnection { get; set; } = "";

        [FieldDefinition(2, Label = "Host")]
        public string Host { get; set; }

        [FieldDefinition(3, Label = "Port")]
        public int Port { get; set; }

        [FieldDefinition(4, Label = "UseSsl", Type = FieldType.Checkbox, HelpText = "NotificationsSettingsUseSslHelpText")]
        [FieldToken(TokenField.HelpText, "UseSsl", "serviceName", "Plex")]
        public bool UseSsl { get; set; }

        [FieldDefinition(5, Label = "Library", Type = FieldType.Select, SelectOptionsProviderAction = "getPlexLibraries", HelpText = "PlexLibraryHelpText")]
        public string LibrarySectionId { get; set; } = "";

        public bool UpdateLibrary { get; set; }

        [FieldDefinition(6, Label = "NotificationsPlexSettingsAuthToken", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, Advanced = true)]
        public string AuthToken { get; set; }

        [FieldDefinition(7, Label = "UrlBase", Type = FieldType.Textbox, Advanced = true, HelpText = "ConnectionSettingsUrlBaseHelpText")]
        [FieldToken(TokenField.HelpText, "UrlBase", "connectionName", "Plex")]
        [FieldToken(TokenField.HelpText, "UrlBase", "url", "http://[host]:[port]/[urlBase]/plex")]
        public string UrlBase { get; set; }

        [FieldDefinition(8, Label = "NotificationsSettingsUpdateMapPathsFrom", Type = FieldType.Textbox, Advanced = true, HelpText = "NotificationsSettingsUpdateMapPathsFromHelpText")]
        [FieldToken(TokenField.HelpText, "NotificationsSettingsUpdateMapPathsFrom", "serviceName", "Plex")]
        public string MapFrom { get; set; }

        [FieldDefinition(9, Label = "NotificationsSettingsUpdateMapPathsTo", Type = FieldType.Textbox, Advanced = true, HelpText = "NotificationsSettingsUpdateMapPathsToHelpText")]
        [FieldToken(TokenField.HelpText, "NotificationsSettingsUpdateMapPathsTo", "serviceName", "Plex")]
        public string MapTo { get; set; }

        // When Host is an IP address and UseSsl is enabled, Plex requires a plex.direct hostname
        // to satisfy TLS certificate validation. This suffix is discovered automatically from the
        // Plex server TLS certificate during Test/Save.
        public string PlexDirectSuffix { get; set; }

        public bool IsValid => !string.IsNullOrWhiteSpace(Host);

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
