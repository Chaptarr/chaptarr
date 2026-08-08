using System;
using System.Net;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Webhook
{
    public class WebhookSettingsValidator : AbstractValidator<WebhookSettings>
    {
        public WebhookSettingsValidator()
        {
            RuleFor(c => c.Url).IsValidUrl();
            RuleFor(c => c.Url)
                .Must(url => !ResolvesToPrivateOrLocalNetwork(url))
                .WithMessage("Webhook URL resolves to a local or private network address. This is allowed, but only use it for internal services you trust.")
                .AsWarning()
                .When(c => !string.IsNullOrWhiteSpace(c.Url));
        }

        private static bool ResolvesToPrivateOrLocalNetwork(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var addresses = ResolveHostAddresses(uri);

            foreach (var address in addresses)
            {
                var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

                if (IPAddress.IsLoopback(ip))
                {
                    return true;
                }

                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var bytes = ip.GetAddressBytes();

                    if (bytes[0] == 10 ||
                        bytes[0] == 127 ||
                        bytes[0] == 0 ||
                        (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                        (bytes[0] == 192 && bytes[1] == 168) ||
                        (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                        (bytes[0] == 169 && bytes[1] == 254))
                    {
                        return true;
                    }
                }

                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                    (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || IsIpv6UniqueLocal(ip)))
                {
                    return true;
                }
            }

            return false;
        }

        private static IPAddress[] ResolveHostAddresses(Uri uri)
        {
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip))
            {
                return new[] { ip };
            }

            try
            {
                return Dns.GetHostAddresses(uri.Host);
            }
            catch
            {
                return Array.Empty<IPAddress>();
            }
        }

        private static bool IsIpv6UniqueLocal(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            return bytes.Length > 0 && (bytes[0] & 0xFE) == 0xFC;
        }
    }

    public class WebhookSettings : IProviderConfig
    {
        private static readonly WebhookSettingsValidator Validator = new WebhookSettingsValidator();

        public WebhookSettings()
        {
            Method = Convert.ToInt32(WebhookMethod.POST);
        }

        [FieldDefinition(0, Label = "URL", Type = FieldType.Url, Privacy = PrivacyLevel.ApiKey)]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "Method", Type = FieldType.Select, SelectOptions = typeof(WebhookMethod), HelpText = "Which HTTP method to use submit to the Webservice")]
        public int Method { get; set; }

        [FieldDefinition(2, Label = "Username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(3, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
