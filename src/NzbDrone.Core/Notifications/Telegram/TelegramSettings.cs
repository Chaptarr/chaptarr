using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Telegram
{
    public class TelegramSettingsValidator : AbstractValidator<TelegramSettings>
    {
        public TelegramSettingsValidator()
        {
            RuleFor(c => c.BotToken).NotEmpty();
            RuleFor(c => c.ChatId).NotEmpty();
            RuleFor(c => c.TopicId).Must(topicId => !topicId.HasValue || topicId > 1)
                                   .WithMessage("Topic ID must be greater than 1 or empty");
        }
    }

    public class TelegramSettings : IProviderConfig
    {
        private static readonly TelegramSettingsValidator Validator = new TelegramSettingsValidator();

        [FieldDefinition(0, Label = "provider.field.fcf8c0ac670d1", Privacy = PrivacyLevel.ApiKey, HelpLink = "https://core.telegram.org/bots")]
        public string BotToken { get; set; }

        [FieldDefinition(1, Label = "provider.field.f8fd1a38b785b", HelpLink = "http://stackoverflow.com/a/37396871/882971", HelpText = "provider.field.f695264c341a2")]
        public string ChatId { get; set; }

        [FieldDefinition(2, Label = "provider.field.fd1fe4f606f97", HelpLink = "https://stackoverflow.com/a/75178418", HelpText = "provider.field.f45a630586d69")]
        public int? TopicId { get; set; }

        [FieldDefinition(3, Label = "provider.field.fc27b9378955f", Type = FieldType.Checkbox, HelpText = "provider.field.f353fd0415272")]
        public bool SendSilently { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
