using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Configuration
{
    public sealed class AudibleMarketplaceDescriptor
    {
        public AudibleMarketplaceDescriptor(string code, string domain, string locale)
        {
            Code = code;
            Domain = domain;
            Locale = locale;
        }

        public string Code { get; }
        public string Domain { get; }
        public string Locale { get; }
        public string ApiBaseUrl => $"https://api.audible.{Domain}";
        public string StorefrontBaseUrl => $"https://www.audible.{Domain}";

        public string AcceptLanguage
        {
            get
            {
                var language = Locale.Split('-')[0];
                return $"{Locale},{language};q=0.9";
            }
        }
    }

    public static class AudibleMarketplaceOptions
    {
        public const string Default = "us";

        private static readonly Dictionary<string, AudibleMarketplaceDescriptor> Marketplaces = new(StringComparer.OrdinalIgnoreCase)
        {
            ["us"] = new AudibleMarketplaceDescriptor("us", "com", "en-US"),
            ["uk"] = new AudibleMarketplaceDescriptor("uk", "co.uk", "en-GB"),
            ["fr"] = new AudibleMarketplaceDescriptor("fr", "fr", "fr-FR"),
            ["de"] = new AudibleMarketplaceDescriptor("de", "de", "de-DE"),
            ["it"] = new AudibleMarketplaceDescriptor("it", "it", "it-IT"),
            ["es"] = new AudibleMarketplaceDescriptor("es", "es", "es-ES"),
            ["ca"] = new AudibleMarketplaceDescriptor("ca", "ca", "en-CA"),
            ["au"] = new AudibleMarketplaceDescriptor("au", "com.au", "en-AU"),
            ["in"] = new AudibleMarketplaceDescriptor("in", "in", "en-IN"),
            ["jp"] = new AudibleMarketplaceDescriptor("jp", "co.jp", "ja-JP"),
            ["br"] = new AudibleMarketplaceDescriptor("br", "com.br", "pt-BR")
        };

        public static string Normalize(string code)
        {
            var normalized = code?.Trim();
            return normalized != null && Marketplaces.TryGetValue(normalized, out var marketplace)
                ? marketplace.Code
                : Default;
        }

        public static AudibleMarketplaceDescriptor Get(string code)
        {
            return Marketplaces[Normalize(code)];
        }
    }
}
