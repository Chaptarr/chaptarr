using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.BookOrbit
{
    public class BookOrbitSettingsValidator : AbstractValidator<BookOrbitSettings>
    {
        public BookOrbitSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.Username).NotEmpty();
            RuleFor(c => c.Password).NotEmpty();
        }
    }

    public class BookOrbitSettings : IProviderConfig
    {
        private static readonly BookOrbitSettingsValidator Validator = new BookOrbitSettingsValidator();

        public BookOrbitSettings()
        {
            BaseUrl = "http://localhost:3000";
        }

        [FieldDefinition(0, Label = "provider.field.f70589413a3c9", HelpText = "BookOrbitBaseUrlHelpText")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "BookOrbitUsernameHelpText")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "BookOrbitPasswordHelpText")]
        public string Password { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
