using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace NzbDrone.Core.Notifications.MqttGateway
{
    public static class MqttGatewayMqtt
    {
        private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web);

        public static string GetRootTopic(MqttGatewaySettings settings)
        {
            return $"{settings.TopicPrefix.Trim('/')}/{settings.InstanceId}";
        }

        public static string GetEventTopic(MqttGatewaySettings settings, string eventName)
        {
            return $"{GetRootTopic(settings)}/{eventName}";
        }

        public static string GetSettingsFingerprint(MqttGatewaySettings settings)
        {
            var json = JsonSerializer.Serialize(settings, SettingsJsonOptions);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }

        public static string GetClientId(MqttGatewaySettings settings)
        {
            return string.IsNullOrWhiteSpace(settings.ClientId)
                ? $"chaptarr-{settings.InstanceId}"
                : settings.ClientId.Trim();
        }

        public static MqttClientOptions BuildClientOptions(MqttGatewaySettings settings,
                                                           string availabilityTopic,
                                                           string clientIdOverride = null,
                                                           bool includeAvailabilityWill = true)
        {
            var options = new MqttClientOptionsBuilder()
                .WithClientId(clientIdOverride ?? GetClientId(settings))
                .WithTcpServer(settings.Host, settings.Port)
                .WithProtocolVersion(MqttProtocolVersion.V500);

            if (includeAvailabilityWill)
            {
                options
                    .WithWillTopic(availabilityTopic)
                    .WithWillPayload("offline")
                    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithWillRetain(true);
            }

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                options.WithCredentials(settings.Username, settings.Password ?? string.Empty);
            }

            if (settings.UseTls)
            {
                options.WithTlsOptions(tls =>
                {
                    tls.UseTls(true);
                    if (settings.AllowInvalidCertificates)
                    {
                        tls.WithAllowUntrustedCertificates();
                        tls.WithIgnoreCertificateChainErrors();
                        tls.WithIgnoreCertificateRevocationErrors();
                        tls.WithCertificateValidationHandler(_ => true);
                    }
                });
            }

            return options.Build();
        }

        public static MqttApplicationMessage BuildMessage(string topic, string payload, MqttGatewayQos qos, bool retain = false)
        {
            return new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload ?? string.Empty))
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)(int)qos)
                .WithRetainFlag(retain)
                .Build();
        }

        public static MqttApplicationMessage BuildAvailabilityMessage(string topic, string payload)
        {
            return new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(true)
                .Build();
        }
    }
}
