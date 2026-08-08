using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public static class FtsNormalization
    {
        // Normalize strings for FTS: lowercase, strip diacritics/punctuation, collapse whitespace
        public static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var lower = input.ToLowerInvariant();
            var decomp = lower.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomp.Length);
            foreach (var ch in decomp)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc == UnicodeCategory.NonSpacingMark) continue; // strip diacritics
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)) sb.Append(ch);
                else sb.Append(' ');
            }
            var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n|\r|\s+", " ").Trim();
            return collapsed;
        }

        public static IEnumerable<string> NormalizeValues(Dictionary<string, List<string>> tags)
        {
            if (tags == null) yield break;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var values in tags.Values)
            {
                foreach (var v in values ?? new List<string>())
                {
                    var n = Normalize(v);
                    if (n.Length == 0) continue;
                    if (seen.Add(n)) yield return n;
                }
            }
        }
    }
}

