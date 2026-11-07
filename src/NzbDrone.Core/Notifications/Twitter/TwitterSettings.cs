using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Twitter
{
    public class TwitterSettingsValidator : AbstractValidator<TwitterSettings>
    {
        public TwitterSettingsValidator()
        {
            RuleFor(c => c.ConsumerKey).NotEmpty();
            RuleFor(c => c.ConsumerSecret).NotEmpty();
            RuleFor(c => c.AccessToken).NotEmpty();
            RuleFor(c => c.AccessTokenSecret).NotEmpty();

            //TODO: Validate that it is a valid username (numbers, letters and underscores - I think)
            RuleFor(c => c.Mention).NotEmpty().When(c => c.DirectMessage);

            RuleFor(c => c.DirectMessage).Equal(true)
                                         .WithMessage("Using Direct Messaging is recommended, or use a private account.")
                                         .AsWarning();

            RuleFor(c => c.AuthorizeNotification).Empty()
                                                 .When(c => c.AccessToken.IsNullOrWhiteSpace() || c.AccessTokenSecret.IsNullOrWhiteSpace())
                                                 .WithMessage("Authenticate app.");
        }
    }

    public class TwitterSettings : IProviderConfig
    {
        private static readonly TwitterSettingsValidator Validator = new TwitterSettingsValidator();

        public TwitterSettings()
        {
            DirectMessage = true;
            AuthorizeNotification = "startOAuth";
        }

        [FieldDefinition(0, Label = "provider.field.f282690e03cd8", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f3e9c99374508", HelpLink = "https://discord.gg/nqFGsGUug2")]
        public string ConsumerKey { get; set; }

        [FieldDefinition(1, Label = "provider.field.f190fd47bff2e", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.fedec108b0608", HelpLink = "https://discord.gg/nqFGsGUug2")]
        public string ConsumerSecret { get; set; }

        [FieldDefinition(2, Label = "provider.field.ff9db72ced1ee", Privacy = PrivacyLevel.ApiKey, Advanced = true)]
        public string AccessToken { get; set; }

        [FieldDefinition(3, Label = "provider.field.f3efeb5b0889f", Privacy = PrivacyLevel.ApiKey, Advanced = true)]
        public string AccessTokenSecret { get; set; }

        [FieldDefinition(4, Label = "provider.field.fd820d8de70e4", HelpText = "provider.field.f14f23b87f76a")]
        public string Mention { get; set; }

        [FieldDefinition(5, Label = "provider.field.fa22fdec85d18", Type = FieldType.Checkbox, HelpText = "provider.field.f17181a60f2c1")]
        public bool DirectMessage { get; set; }

        [FieldDefinition(6, Label = "provider.field.fc91a944bf6b7", Type = FieldType.OAuth)]
        public string AuthorizeNotification { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
