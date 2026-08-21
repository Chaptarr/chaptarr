using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.CalibreContentServer
{
    public class CalibreContentServerSettingsValidator : AbstractValidator<CalibreContentServerSettings>
    {
        public CalibreContentServerSettingsValidator()
        {
            RuleFor(c => c.Url).IsValidUrl();
            RuleFor(c => c.Username).NotEmpty().When(c => c.Password.IsNotNullOrWhiteSpace());
        }
    }

    public class CalibreContentServerSettings : IProviderConfig
    {
        private static readonly CalibreContentServerSettingsValidator Validator = new CalibreContentServerSettingsValidator();

        [FieldDefinition(0, Label = "URL", HelpText = "Calibre content server URL, including http(s):// and port, e.g. http://localhost:8080")]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "Optional, only needed if the content server requires authentication")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "Optional, only needed if the content server requires authentication")]
        public string Password { get; set; }

        [FieldDefinition(3, Label = "Allow Edits & Deletes", Type = FieldType.Checkbox, HelpText = "Allow Chaptarr to edit or delete matching books on the content server when books change in Chaptarr or are deleted from Chaptarr")]
        public bool SyncChanges { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
