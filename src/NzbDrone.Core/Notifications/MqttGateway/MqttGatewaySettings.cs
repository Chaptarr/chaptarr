using System;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.MqttGateway
{
    public enum MqttGatewayQos
    {
        [FieldOption(Label = "provider.option.o99a4b3998d1c")]
        AtMostOnce = 0,
        [FieldOption(Label = "provider.option.oc13abe78e554")]
        AtLeastOnce = 1,
        [FieldOption(Label = "provider.option.o5afee72c1059")]
        ExactlyOnce = 2
    }

    public class MqttGatewaySettingsValidator : AbstractValidator<MqttGatewaySettings>
    {
        public MqttGatewaySettingsValidator()
        {
            RuleFor(c => c.Host).NotEmpty().ValidHost();
            RuleFor(c => c.Port).ValidPort();
            RuleFor(c => c.InstanceId).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9_-]+$");
            RuleFor(c => c.TopicPrefix).Must(IsValidTopicPrefix).WithMessage("Topic prefix must be a valid MQTT topic prefix.");
            RuleFor(c => c.ClientId).MaximumLength(128);
            RuleFor(c => c.EventQos).IsInEnum();
            RuleFor(c => c.DiscoveryPrefix).Must(IsValidTopicPrefix).WithMessage("Home Assistant discovery prefix must be a valid MQTT topic prefix.").When(c => c.PublishHomeAssistantDiscovery);
            RuleFor(c => c.SensorRefreshIntervalMinutes).InclusiveBetween(1, 1440);
            RuleFor(c => c.UpcomingReleaseWindowDays).InclusiveBetween(1, 365);
        }

        private static bool IsValidTopicPrefix(string prefix)
        {
            return !string.IsNullOrWhiteSpace(prefix) &&
                   prefix.Length <= 128 &&
                   !prefix.StartsWith("/") &&
                   !prefix.EndsWith("/") &&
                   !prefix.Contains("//") &&
                   !prefix.Contains('+') &&
                   !prefix.Contains('#') &&
                   !prefix.Contains('\0');
        }
    }

    public class MqttGatewaySettings : IProviderConfig
    {
        private static readonly MqttGatewaySettingsValidator Validator = new();

        public MqttGatewaySettings()
        {
            InstanceId = Guid.NewGuid().ToString("N");
        }

        [FieldDefinition(0, Label = "MqttGatewayBrokerHost", Type = FieldType.Textbox, HelpText = "MqttGatewayBrokerHostHelpText")]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "MqttGatewayBrokerPort", Type = FieldType.Number, HelpText = "MqttGatewayBrokerPortHelpText")]
        public int Port { get; set; } = 1883;

        [FieldDefinition(2, Label = "Username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(3, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(4, Label = "MqttGatewayUseTls", Type = FieldType.Checkbox, HelpText = "MqttGatewayUseTlsHelpText")]
        public bool UseTls { get; set; }

        [FieldDefinition(5, Label = "MqttGatewayAllowInvalidCertificates", Type = FieldType.Checkbox, Advanced = true, HelpTextWarning = "MqttGatewayAllowInvalidCertificatesWarning")]
        public bool AllowInvalidCertificates { get; set; }

        [FieldDefinition(6, Label = "MqttGatewayInstanceId", Type = FieldType.Textbox, Advanced = true, HelpText = "MqttGatewayInstanceIdHelpText")]
        public string InstanceId { get; set; }

        [FieldDefinition(7, Label = "MqttGatewayClientId", Type = FieldType.Textbox, HelpText = "MqttGatewayClientIdHelpText")]
        public string ClientId { get; set; }

        [FieldDefinition(8, Label = "MqttGatewayTopicPrefix", Type = FieldType.Textbox, HelpText = "MqttGatewayTopicPrefixHelpText")]
        public string TopicPrefix { get; set; } = "chaptarr";

        [FieldDefinition(9, Label = "MqttGatewayEventQos", Type = FieldType.Select, SelectOptions = typeof(MqttGatewayQos))]
        public MqttGatewayQos EventQos { get; set; } = MqttGatewayQos.AtLeastOnce;

        [FieldDefinition(10, Label = "MqttGatewayPublishSensors", Type = FieldType.Checkbox)]
        public bool PublishSensors { get; set; } = true;

        [FieldDefinition(11, Label = "MqttGatewayPublishEvents", Type = FieldType.Checkbox)]
        public bool PublishEvents { get; set; } = true;

        [FieldDefinition(12, Label = "MqttGatewayPublishHomeAssistantDiscovery", Type = FieldType.Checkbox)]
        public bool PublishHomeAssistantDiscovery { get; set; } = true;

        [FieldDefinition(13, Label = "MqttGatewayDiscoveryPrefix", Type = FieldType.Textbox, HelpText = "MqttGatewayDiscoveryPrefixHelpText")]
        public string DiscoveryPrefix { get; set; } = "homeassistant";

        [FieldDefinition(14, Label = "MqttGatewaySensorRefreshInterval", Type = FieldType.Number, Unit = "minutes")]
        public int SensorRefreshIntervalMinutes { get; set; } = 5;

        [FieldDefinition(15, Label = "MqttGatewayUpcomingReleaseWindow", Type = FieldType.Number, Unit = "days")]
        public int UpcomingReleaseWindowDays { get; set; } = 30;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
