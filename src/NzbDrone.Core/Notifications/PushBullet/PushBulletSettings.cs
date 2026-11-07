using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.PushBullet
{
    public class PushBulletSettingsValidator : AbstractValidator<PushBulletSettings>
    {
        public PushBulletSettingsValidator()
        {
            RuleFor(c => c.ApiKey).NotEmpty();
        }
    }

    public class PushBulletSettings : IProviderConfig
    {
        private static readonly PushBulletSettingsValidator Validator = new PushBulletSettingsValidator();

        public PushBulletSettings()
        {
            DeviceIds = new string[] { };
            ChannelTags = new string[] { };
        }

        [FieldDefinition(0, Label = "provider.field.ff9db72ced1ee", Privacy = PrivacyLevel.ApiKey, HelpLink = "https://www.pushbullet.com/#settings/account")]
        public string ApiKey { get; set; }

        [FieldDefinition(1, Label = "provider.field.f00c0e6769b60", HelpText = "provider.field.fcb9e75f0c19f", Type = FieldType.Device)]
        public IEnumerable<string> DeviceIds { get; set; }

        [FieldDefinition(2, Label = "provider.field.fdf0e743ee267", HelpText = "provider.field.f747e885bd66d", Type = FieldType.Tag)]
        public IEnumerable<string> ChannelTags { get; set; }

        [FieldDefinition(3, Label = "provider.field.f98a9bbd4b07b", HelpText = "provider.field.f7ce7fa72e138")]
        public string SenderId { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
