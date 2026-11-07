using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Gotify
{
    public class GotifySettingsValidator : AbstractValidator<GotifySettings>
    {
        public GotifySettingsValidator()
        {
            RuleFor(c => c.Server).IsValidUrl();
            RuleFor(c => c.AppToken).NotEmpty();
        }
    }

    public class GotifySettings : IProviderConfig
    {
        private static readonly GotifySettingsValidator Validator = new GotifySettingsValidator();

        public GotifySettings()
        {
            Priority = 5;
        }

        [FieldDefinition(0, Label = "provider.field.f54d8ab35aca1", HelpText = "provider.field.fcf600202ff5b")]
        public string Server { get; set; }

        [FieldDefinition(1, Label = "provider.field.f947c6197dfcb", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f99486a496ef9")]
        public string AppToken { get; set; }

        [FieldDefinition(2, Label = "Priority", Type = FieldType.Select, SelectOptions = typeof(GotifyPriority), HelpText = "provider.field.fa39ce719a563")]
        public int Priority { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
