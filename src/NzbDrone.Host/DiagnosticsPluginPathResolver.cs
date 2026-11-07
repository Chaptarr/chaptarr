using System;
using System.Collections.Generic;
using System.IO;

namespace NzbDrone.Host
{
    public static class DiagnosticsPluginPathResolver
    {
        public static IReadOnlyList<string> Resolve(
            string appDataFolder,
            string startupFolder,
            string currentSetting,
            string legacySetting,
            Action<string> warning)
        {
            var currentSettingIsPresent = !string.IsNullOrWhiteSpace(currentSetting);
            var enabled = string.Equals(currentSetting, "true", StringComparison.OrdinalIgnoreCase);
            if (!currentSettingIsPresent &&
                string.Equals(legacySetting, "true", StringComparison.OrdinalIgnoreCase))
            {
                var legacyPluginPath = Path.Combine(appDataFolder, "plugins", "Bookshelf.Diagnostics.dll");
                enabled = File.Exists(legacyPluginPath);
                if (!enabled)
                {
                    warning?.Invoke("BookshelfNG diagnostics were enabled, but no legacy diagnostics plugin was found. ChaptarrNG will not send diagnostics.");
                }
            }

            if (!enabled)
            {
                return Array.Empty<string>();
            }

            var pluginPath = Path.Combine(startupFolder, "Chaptarr.Diagnostics.dll");
            if (!File.Exists(pluginPath))
            {
                warning?.Invoke("Optional diagnostics are enabled, but Chaptarr.Diagnostics.dll is not installed. No diagnostics will be sent.");
                return Array.Empty<string>();
            }

            return new[] { pluginPath };
        }
    }
}
