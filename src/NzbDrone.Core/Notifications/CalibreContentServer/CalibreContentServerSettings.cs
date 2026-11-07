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
            RuleFor(c => c.Url).NotEmpty().WithMessage("URL cannot be empty");
            RuleFor(c => c.Url).IsValidUrl().When(c => c.Url.IsNotNullOrWhiteSpace());
            RuleFor(c => c.Username).NotEmpty().When(c => c.Password.IsNotNullOrWhiteSpace());
            RuleFor(c => c.Password).NotEmpty().When(c => c.Username.IsNotNullOrWhiteSpace());
        }
    }

    public class CalibreContentServerSettings : IProviderConfig
    {
        private static readonly CalibreContentServerSettingsValidator Validator = new CalibreContentServerSettingsValidator();

        [FieldDefinition(0, Label = "provider.field.fe7a241debad5", HelpText = "provider.field.fcc88585f2a35", HelpTextWarning = "provider.field.f2f1d8ba51d2a")]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "provider.field.f8709fb3ef39f")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "provider.field.f8709fb3ef39f")]
        public string Password { get; set; }

        [FieldDefinition(3, Label = "provider.field.f1be8def5571a", Type = FieldType.Checkbox, HelpText = "provider.field.fbcbcfeb99134")]
        public bool SyncChanges { get; set; }

        [FieldDefinition(4, Label = "provider.field.fe2fa8b9f4041", Type = FieldType.Checkbox, HelpText = "provider.field.f34ef6818e4c8")]
        public bool PushLibraryImports { get; set; }

        [FieldDefinition(5, Label = "provider.field.f2a41808a7b7a", Type = FieldType.Checkbox, HelpText = "provider.field.ffcf2138b1018")]
        public bool PushLibraryEdits { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
