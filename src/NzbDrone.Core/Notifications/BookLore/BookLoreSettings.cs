using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.BookLore
{
    public class BookLoreSettingsValidator : AbstractValidator<BookLoreSettings>
    {
        public BookLoreSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.Username).NotEmpty();
            RuleFor(c => c.Password).NotEmpty();
        }
    }

    public class BookLoreSettings : IProviderConfig
    {
        private static readonly BookLoreSettingsValidator Validator = new BookLoreSettingsValidator();

        public BookLoreSettings()
        {
            BaseUrl = "http://localhost:6060";
        }

        [FieldDefinition(0, Label = "provider.field.f70589413a3c9", HelpText = "BookLoreBaseUrlHelpText")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "BookLoreUsernameHelpText")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "BookLorePasswordHelpText")]
        public string Password { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
