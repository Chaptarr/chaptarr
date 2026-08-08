using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using FluentValidation;
using Newtonsoft.Json;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public class AudioBookShelfSettingsValidator : AbstractValidator<AudioBookShelfSettings>
    {
        public AudioBookShelfSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase).ValidUrlBase("/abs").When(c => c.UrlBase.IsNotNullOrWhiteSpace());
            RuleFor(c => c.ApiKey).NotEmpty().WithMessage("API Key is required");
            RuleFor(c => c.LibraryMappingsJson)
                .Must(AudioBookShelfSettings.IsValidLibraryMappingsJson)
                .When(c => c.LibraryMappingsJson.IsNotNullOrWhiteSpace())
                .WithMessage("Library mappings are invalid");
        }
    }

    public class AudioBookShelfLibraryMapping
    {
        public int RootFolderId { get; set; }
        public string MediaType { get; set; }
        public string LibraryId { get; set; }
        public string LibraryFolderId { get; set; }
        public string LibraryFolderPath { get; set; }
    }

    public class AudioBookShelfSettings : IProviderConfig, IJsonOnDeserialized
    {
        private static readonly AudioBookShelfSettingsValidator Validator = new AudioBookShelfSettingsValidator();

        public AudioBookShelfSettings()
        {
            Port = 13378;
            UseSsl = false;
            SignIn = "startOAuth";
        }

        // Legacy property (no longer exposed in the UI). When present in older configs, it will be migrated
        // to Host/Port/UseSsl/UrlBase on deserialization.
        public string ServerUrl { get; set; }

        [FieldDefinition(0, Label = "Host", Placeholder = "audiobookshelf", HelpText = "AudioBookShelf server hostname or IP address (no http://). For Docker, use the container/service name (e.g. 'audiobookshelf').")]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port")]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "Use SSL", Type = FieldType.Checkbox, HelpText = "Connect to AudioBookShelf over HTTPS instead of HTTP")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "Url Base", Type = FieldType.Textbox, Advanced = true, HelpText = "Adds a prefix to the AudioBookShelf url, e.g. http://[host]:[port]/[urlBase]/api")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "API Key", Type = FieldType.Password, Privacy = PrivacyLevel.ApiKey, HelpText = "AudioBookShelf API key for authentication")]
        public string ApiKey { get; set; }

        public string AudiobookLibraryId { get; set; }

        public string EbookLibraryId { get; set; }

        [FieldDefinition(5, Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string LibraryMappingsJson { get; set; }

        [FieldDefinition(9, Label = "Authenticate with AudioBookShelf (OIDC)", Type = FieldType.OAuth, Hidden = HiddenType.Hidden, HelpText = "Uses AudioBookShelf OpenID Connect (SSO) to generate an API key automatically. This will open a browser popup to the configured AudioBookShelf URL, so it must be reachable from your browser. Your AudioBookShelf server must allow this app's callback URL under OpenID Connect → Mobile Redirect URIs.")]
        public string SignIn { get; set; }

        public string LibraryId { get; set; } // Maintained for migration purposes
        public string Username { get; set; }
        public string Password { get; set; }

        public void OnDeserialized()
        {
            NormalizeLegacyServerUrl();
            NormalizeLibraryMappingsJson();
        }

        public NzbDroneValidationResult Validate()
        {
            NormalizeLegacyServerUrl();
            NormalizeLibraryMappingsJson();
            return new NzbDroneValidationResult(Validator.Validate(this));
        }

        public bool HasConfiguredLibraryMappings()
        {
            return GetLibraryMappings().Exists(IsUsableMapping);
        }

        public List<AudioBookShelfLibraryMapping> GetLibraryMappings()
        {
            if (LibraryMappingsJson.IsNullOrWhiteSpace())
            {
                return new List<AudioBookShelfLibraryMapping>();
            }

            try
            {
                return JsonConvert.DeserializeObject<List<AudioBookShelfLibraryMapping>>(LibraryMappingsJson) ?? new List<AudioBookShelfLibraryMapping>();
            }
            catch
            {
                return new List<AudioBookShelfLibraryMapping>();
            }
        }

        public void SetLibraryMappings(List<AudioBookShelfLibraryMapping> mappings)
        {
            LibraryMappingsJson = JsonConvert.SerializeObject(mappings ?? new List<AudioBookShelfLibraryMapping>());
        }

        public bool ClearLibraryMappings()
        {
            var hadMappings = LibraryMappingsJson.IsNotNullOrWhiteSpace() && GetLibraryMappings().Count > 0;
            var hadLegacyLibrarySelection = AudiobookLibraryId.IsNotNullOrWhiteSpace() ||
                                            EbookLibraryId.IsNotNullOrWhiteSpace() ||
                                            LibraryId.IsNotNullOrWhiteSpace();

            LibraryMappingsJson = null;
            AudiobookLibraryId = null;
            EbookLibraryId = null;
            LibraryId = null;

            return hadMappings || hadLegacyLibrarySelection;
        }

        private static bool IsUsableMapping(AudioBookShelfLibraryMapping mapping)
        {
            return mapping != null &&
                   mapping.RootFolderId > 0 &&
                   mapping.LibraryId.IsNotNullOrWhiteSpace() &&
                   (mapping.MediaType == "audiobook" || mapping.MediaType == "ebook");
        }

        private void NormalizeLegacyServerUrl()
        {
            // Prefer new fields. If Host is explicitly set, drop legacy ServerUrl (if any).
            if (Host.IsNotNullOrWhiteSpace())
            {
                ServerUrl = null;
                return;
            }

            if (ServerUrl.IsNullOrWhiteSpace())
            {
                return;
            }

            var trimmed = ServerUrl.Trim();

            // Avoid log injection / header confusion / userinfo parsing tricks.
            if (trimmed.Contains('\r') || trimmed.Contains('\n') || trimmed.Contains('@'))
            {
                Host = string.Empty;
                Port = 0;
                UrlBase = null;
                UseSsl = false;
                return;
            }

            if (!TryParseServerUrl(trimmed, out var host, out var port, out var useSsl, out var urlBase))
            {
                Host = string.Empty;
                Port = 0;
                UrlBase = null;
                UseSsl = false;
                return;
            }

            Host = host;
            Port = port;
            UseSsl = useSsl;
            UrlBase = urlBase;

            // Clear legacy value so it isn't re-serialized.
            ServerUrl = null;
        }

        private void NormalizeLibraryMappingsJson()
        {
            if (LibraryMappingsJson != null && LibraryMappingsJson.IsNullOrWhiteSpace())
            {
                LibraryMappingsJson = null;
            }
        }

        public static bool IsValidLibraryMappingsJson(string libraryMappingsJson)
        {
            try
            {
                var mappings = JsonConvert.DeserializeObject<List<AudioBookShelfLibraryMapping>>(libraryMappingsJson);

                if (mappings == null)
                {
                    return false;
                }

                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var mapping in mappings)
                {
                    if (mapping == null ||
                        mapping.RootFolderId <= 0 ||
                        mapping.LibraryId.IsNullOrWhiteSpace() ||
                        (mapping.MediaType != "audiobook" && mapping.MediaType != "ebook"))
                    {
                        return false;
                    }

                    var key = $"{mapping.RootFolderId}:{mapping.MediaType}";
                    if (!seenKeys.Add(key))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseServerUrl(string trimmedServerUrl, out string host, out int port, out bool useSsl, out string urlBase)
        {
            host = null;
            port = 0;
            useSsl = false;
            urlBase = null;

            Uri uri;
            if (trimmedServerUrl.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(trimmedServerUrl, UriKind.Absolute, out uri))
                {
                    return false;
                }

                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                {
                    return false;
                }

                useSsl = uri.Scheme == Uri.UriSchemeHttps;
            }
            else
            {
                if (!Uri.TryCreate($"http://{trimmedServerUrl}", UriKind.Absolute, out uri))
                {
                    return false;
                }

                useSsl = false;
            }

            if (uri.Host.IsNullOrWhiteSpace())
            {
                return false;
            }

            // Queries/fragments don't make sense for a base URL. Fail closed and force reconfiguration.
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            host = uri.Host;
            port = uri.IsDefaultPort ? (useSsl ? 443 : 80) : uri.Port;

            if (uri.AbsolutePath.IsNullOrWhiteSpace() || uri.AbsolutePath == "/")
            {
                urlBase = null;
            }
            else
            {
                urlBase = uri.AbsolutePath.TrimEnd('/');
            }

            return true;
        }
    }
}
