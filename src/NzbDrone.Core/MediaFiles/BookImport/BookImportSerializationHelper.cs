using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class BookImportSerializationHelper
    {
        internal static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            try
            {
                var full = Path.GetFullPath(path);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        internal static Dictionary<string, List<string>> SafeDeserializeTags(string json)
        {
            try
            {
                var raw = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json ?? "{}");
                if (raw == null)
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in raw)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                    {
                        continue;
                    }

                    tags[kv.Key] = kv.Value ?? new List<string>();
                }

                return tags;
            }
            catch
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
