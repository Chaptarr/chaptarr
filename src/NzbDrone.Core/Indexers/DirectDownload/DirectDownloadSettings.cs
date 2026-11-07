using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public class DirectDownloadSettingsValidator : AbstractValidator<DirectDownloadSettings>
    {
        public DirectDownloadSettingsValidator()
        {
            RuleFor(settings => settings)
                .Custom((settings, context) =>
                {
                    var input = settings.Urls;
                    var normalizedUrls = DirectDownloadSettings.NormalizeUrls(input);

                    if (normalizedUrls.Count == 0)
                    {
                        context.AddFailure("'URLs' must contain at least one http:// or https:// URL");
                        return;
                    }

                    var hasApiKey = !string.IsNullOrWhiteSpace(settings.ApiKey);
                    var hasSlowFallback = settings.EnableSlowFallback;

                    if (!hasApiKey && !hasSlowFallback)
                    {
                        context.AddFailure("At least one download method is required: configure an API key or enable the slow-download browser fallback.");
                        return;
                    }

                    var rawLines = input?
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n')
                        .Split('\n') ?? Array.Empty<string>();

                    foreach (var rawLine in rawLines)
                    {
                        var trimmed = rawLine.Trim();

                        if (trimmed.IsNullOrWhiteSpace())
                        {
                            continue;
                        }

                        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            context.AddFailure($"'{trimmed}' must be a valid URL that starts with http:// or https://");
                            return;
                        }

                        if (uri.UserInfo.IsNotNullOrWhiteSpace())
                        {
                            context.AddFailure($"'{trimmed}' must not include embedded credentials");
                            return;
                        }
                    }
                });
        }
    }

    public class DirectDownloadSettings : IIndexerSettings, IJsonOnDeserialized
    {
        private static readonly DirectDownloadSettingsValidator Validator = new DirectDownloadSettingsValidator();
        private string _urls;
        private string _apiKey;

        public DirectDownloadSettings()
        {
            Urls = string.Empty;
        }

        [FieldDefinition(0, Type = FieldType.TextArea, Label = "provider.field.f1240054e2040", HelpText = "provider.field.f5c8c10dd48e0")]
        [JsonConverter(typeof(DirectDownloadUrlConfigurationConverter))]
        public string Urls
        {
            get => _urls;
            set => _urls = NormalizeUrls(value).Count == 0
                ? NormalizeWhitespace(value)
                : string.Join("\n", NormalizeUrls(value));
        }

        [FieldDefinition(1, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f80cf5c07c14e")]
        public string ApiKey
        {
            get => _apiKey;
            set => _apiKey = NormalizeWhitespace(value).IsNullOrWhiteSpace() ? null : NormalizeWhitespace(value);
        }

        [FieldDefinition(2, Label = "provider.field.fcff0d77f9e68", Type = FieldType.Checkbox, HelpText = "provider.field.f0a31411d36c6")]
        public bool EnableSlowFallback { get; set; }

        public int? EarlyReleaseLimit { get; set; }

        [JsonIgnore]
        public string BaseUrl
        {
            get => NormalizeUrls(Urls).FirstOrDefault() ?? string.Empty;
            set => Urls = value;
        }

        public void OnDeserialized()
        {
            Urls = _urls;
            ApiKey = _apiKey;
        }

        public NzbDroneValidationResult Validate()
        {
            Urls = _urls;
            ApiKey = _apiKey;
            return new NzbDroneValidationResult(Validator.Validate(this));
        }

        public static IReadOnlyList<string> NormalizeUrls(string urls)
        {
            if (urls.IsNullOrWhiteSpace())
            {
                return Array.Empty<string>();
            }

            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in urls
                         .Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Replace('\r', '\n')
                         .Split('\n'))
            {
                var trimmed = NormalizeWhitespace(rawLine);

                if (trimmed.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var normalized = trimmed.TrimEnd('/');
                if (normalized.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    results.Add(normalized);
                }
            }

            return results;
        }

        private static string NormalizeWhitespace(string value)
        {
            return value?.Trim();
        }
    }

    public sealed class DirectDownloadUrlConfigurationConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Direct download URLs must be a string or a list of strings.");
            }

            var urls = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return string.Join("\n", urls);
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("Direct download URL entries must be strings.");
                }

                urls.Add(reader.GetString());
            }

            throw new JsonException("Direct download URL list is incomplete.");
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }
}
