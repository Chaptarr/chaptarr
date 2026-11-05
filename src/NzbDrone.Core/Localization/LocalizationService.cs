using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Localization
{
    public interface ILocalizationService
    {
        Dictionary<string, string> GetLocalizationDictionary();

        Dictionary<string, string> GetBookshelfLocalizationDictionary() => GetLocalizationDictionary();

        string GetCurrentLocale();

        string GetLocalizedString(string phrase);
        string GetLocalizedString(string phrase, Dictionary<string, object> tokens);
    }

    public class LocalizationService : ILocalizationService, IHandleAsync<ConfigSavedEvent>
    {
        private const string DefaultCulture = "en";
        private static readonly Regex TokenRegex = new Regex(@"(?:\{)(?<token>[a-z0-9]+)(?:\})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly ICached<Dictionary<string, string>> _cache;

        private readonly IConfigService _configService;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;
        private readonly Dictionary<int, UiLocale> _uiLocales;

        public LocalizationService(IConfigService configService,
                                   IAppFolderInfo appFolderInfo,
                                   ICacheManager cacheManager,
                                   Logger logger)
        {
            _configService = configService;
            _appFolderInfo = appFolderInfo;
            _cache = cacheManager.GetCache<Dictionary<string, string>>(typeof(Dictionary<string, string>), "localization");
            _logger = logger;
            _uiLocales = LoadUiLocaleRegistry(appFolderInfo.StartUpFolder, logger);
        }

        public string GetCurrentLocale()
        {
            return GetSetLocale().Tag;
        }

        public Dictionary<string, string> GetLocalizationDictionary()
        {
            return GetLocalizationDictionary(GetSetLocale());
        }

        public Dictionary<string, string> GetBookshelfLocalizationDictionary()
        {
            // Preserve the predecessor's language IDs and regional fallback,
            // independently of the native UI registry's newer locale aliases.
            var isoLanguage = IsoLanguages.Get((Language)_configService.UILanguage) ?? IsoLanguages.Get(Language.English);
            var culture = isoLanguage.TwoLetterCode;
            if (isoLanguage.CountryCode.IsNotNullOrWhiteSpace())
            {
                culture += "_" + isoLanguage.CountryCode;
            }

            return _cache.Get("bookshelf-localization:" + culture, () =>
            {
                // Never mutate the shared native UI cache when projecting a
                // predecessor client response, including translated overrides.
                var result = new Dictionary<string, string>(GetLocalizationDictionary(), StringComparer.OrdinalIgnoreCase);
                var prefix = Path.Combine(_appFolderInfo.StartUpFolder, "Localization", "BookshelfCompatibility");
                var englishPath = Path.Combine(prefix, DefaultCulture + ".json");
                if (!File.Exists(englishPath))
                {
                    throw new FileNotFoundException("BookshelfNG localization compatibility data is missing", englishPath);
                }

                var legacy = GetDictionary(prefix, new UiLocale { File = culture }, DefaultCulture + ".json", allowMissingCulture: true)
                    .GetAwaiter().GetResult();
                foreach (var pair in legacy)
                {
                    result[pair.Key] = pair.Value;
                }

                return result;
            });
        }

        public string GetLocalizedString(string phrase)
        {
            return GetLocalizedString(phrase, new Dictionary<string, object>());
        }

        public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
        {
            if (string.IsNullOrEmpty(phrase))
            {
                throw new ArgumentNullException(nameof(phrase));
            }

            var locale = GetSetLocale();
            var dictionary = GetLocalizationDictionary(locale);

            if (dictionary.TryGetValue(phrase, out var value))
            {
                return ReplaceTokens(value, tokens);
            }

            return phrase;
        }

        private string ReplaceTokens(string input, Dictionary<string, object> tokens)
        {
            tokens.TryAdd("appName", "Chaptarr");

            return TokenRegex.Replace(input, (match) =>
            {
                var tokenName = match.Groups["token"].Value;

                tokens.TryGetValue(tokenName, out var token);

                return token?.ToString() ?? $"{{{tokenName}}}";
            });
        }

        private UiLocale GetSetLocale()
        {
            if (_uiLocales.TryGetValue(_configService.UILanguage, out var configuredLocale))
            {
                return configuredLocale;
            }

            var isoLanguage = IsoLanguages.Get((Language)_configService.UILanguage) ?? IsoLanguages.Get(Language.English);
            var file = isoLanguage.TwoLetterCode;

            if (isoLanguage.CountryCode.IsNotNullOrWhiteSpace())
            {
                file = string.Format("{0}_{1}", file, isoLanguage.CountryCode);
            }

            var parts = file.Split('_');
            return new UiLocale
            {
                Id = _configService.UILanguage,
                Tag = file.Replace('_', '-'),
                File = file,
                Fallback = parts.Length > 1 ? parts[0] : null
            };
        }

        private static Dictionary<int, UiLocale> LoadUiLocaleRegistry(string startupFolder, Logger logger)
        {
            var registryPath = Path.Combine(startupFolder, "Localization", "Core", "ui-locales.json");

            if (!File.Exists(registryPath))
            {
                return new Dictionary<int, UiLocale>();
            }

            try
            {
                var registry = JsonSerializer.Deserialize<UiLocaleRegistry>(
                    File.ReadAllText(registryPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var locales = new Dictionary<int, UiLocale>();

                foreach (var locale in registry?.Locales ?? new List<UiLocale>())
                {
                    if (locale.Id > 0 && locale.Tag.IsNotNullOrWhiteSpace() && locale.File.IsNotNullOrWhiteSpace())
                    {
                        locales[locale.Id] = locale;
                    }
                }

                return locales;
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "Could not load the UI locale registry. Existing language IDs will use their default mappings.");
                return new Dictionary<int, UiLocale>();
            }
        }

        private Dictionary<string, string> GetLocalizationDictionary(UiLocale locale)
        {
            var prefix = Path.Combine(_appFolderInfo.StartUpFolder, "Localization", "Core");

            return _cache.Get(
                "localization",
                () => GetDictionary(prefix, locale, DefaultCulture + ".json").GetAwaiter().GetResult());
        }

        private async Task<Dictionary<string, string>> GetDictionary(string prefix, UiLocale locale, string baseFilename, bool allowMissingCulture = false)
        {
            if (locale == null || locale.File.IsNullOrWhiteSpace())
            {
                throw new ArgumentNullException(nameof(locale));
            }

            var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var baseFilenamePath = Path.Combine(prefix, baseFilename);
            var alternativeFilenamePath = Path.Combine(prefix, GetResourceFilename(locale.File));

            await CopyInto(dictionary, baseFilenamePath).ConfigureAwait(false);

            var fallback = locale.Fallback;
            if (fallback.IsNullOrWhiteSpace() && locale.File.Contains("_"))
            {
                fallback = locale.File.Split('_')[0];
            }

            if (!fallback.IsNullOrWhiteSpace())
            {
                var fallbackFilenamePath = Path.Combine(prefix, GetResourceFilename(fallback));
                if (File.Exists(fallbackFilenamePath))
                {
                    await CopyInto(dictionary, fallbackFilenamePath).ConfigureAwait(false);
                }
            }

            if (!allowMissingCulture || File.Exists(alternativeFilenamePath))
            {
                await CopyInto(dictionary, alternativeFilenamePath).ConfigureAwait(false);
            }

            return dictionary;
        }

        private async Task CopyInto(IDictionary<string, string> dictionary, string resourcePath)
        {
            if (!File.Exists(resourcePath))
            {
                _logger.Error("Missing translation/culture resource: {0}", resourcePath);
                return;
            }

            await using var fs = File.OpenRead(resourcePath);
            var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs);

            foreach (var key in dict.Keys)
            {
                dictionary[key] = dict[key];
            }
        }

        private sealed class UiLocaleRegistry
        {
            public UiLocaleRegistry()
            {
            }

            public List<UiLocale> Locales { get; set; } = new List<UiLocale>();
        }

        private sealed class UiLocale
        {
            public int Id { get; set; }
            public string Tag { get; set; }
            public string File { get; set; }
            public string Fallback { get; set; }
            public string Direction { get; set; }
            public string EnglishName { get; set; }
            public bool UiSelectable { get; set; }
        }

        private static string GetResourceFilename(string culture)
        {
            var parts = culture.Split('_');

            if (parts.Length == 2)
            {
                var suffix = parts[1].Length == 4
                    ? char.ToUpperInvariant(parts[1][0]) + parts[1].Substring(1).ToLowerInvariant()
                    : parts[1].ToUpperInvariant();
                culture = parts[0].ToLowerInvariant() + "_" + suffix;
            }
            else
            {
                culture = culture.ToLowerInvariant();
            }

            return culture + ".json";
        }

        public void HandleAsync(ConfigSavedEvent message)
        {
            _cache.Clear();
        }
    }
}
