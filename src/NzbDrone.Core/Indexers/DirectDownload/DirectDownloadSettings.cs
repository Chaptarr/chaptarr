using System;
using System.Collections.Generic;
using System.Linq;
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

        [FieldDefinition(0, Type = FieldType.TextArea, Label = "URLs", HelpText = "Enter one http:// or https:// URL per line. Chaptarr preserves the configured order and uses it for deterministic fallback.")]
        public string Urls
        {
            get => _urls;
            set => _urls = NormalizeUrls(value).Count == 0
                ? NormalizeWhitespace(value)
                : string.Join("\n", NormalizeUrls(value));
        }

        [FieldDefinition(1, Label = "API Key", Privacy = PrivacyLevel.ApiKey, HelpText = "Optional. Leave blank when the selected source does not require a key.")]
        public string ApiKey
        {
            get => _apiKey;
            set => _apiKey = NormalizeWhitespace(value).IsNullOrWhiteSpace() ? null : NormalizeWhitespace(value);
        }

        [FieldDefinition(2, Label = "Enable Slow-Download Browser Fallback", Type = FieldType.Checkbox, HelpText = "When the API key is absent or its fast-download resolution fails, use a headless browser to attempt slow-download links. Requires Playwright/Chromium in the Docker runtime.")]
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
}
