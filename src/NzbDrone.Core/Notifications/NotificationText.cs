using System.Collections.Generic;
using System.Text.RegularExpressions;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.Notifications
{
    public interface ILocalizedNotification
    {
        void ConfigureLocalization(ILocalizationService localizationService);
    }

    // Only project-authored templates enter this helper. Captured titles, paths,
    // provider responses and user data are tokens, never translation keys.
    public sealed class NotificationText
    {
        private static readonly Regex Token = new Regex(@"\{(?<name>[a-zA-Z0-9]+)\}", RegexOptions.Compiled);
        private readonly ILocalizationService _localizationService;

        public NotificationText(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
        }

        public string LocaleFingerprint(string settingsFingerprint)
        {
            return settingsFingerprint + "|uiLocale:" + (_localizationService?.GetCurrentLocale() ?? "en");
        }

        public string Get(string id, string source, Dictionary<string, object> tokens = null)
        {
            // Resolve the template before one-pass substitution. A value containing
            // braces must not become a second template or another message lookup.
            var template = _localizationService?.GetLocalizedString(id);
            if (string.IsNullOrEmpty(template) || template == id)
            {
                template = source;
            }

            return Token.Replace(template, match =>
                tokens != null && tokens.TryGetValue(match.Groups["name"].Value, out var value)
                    ? value?.ToString() ?? string.Empty
                    : match.Value);
        }
    }
}
