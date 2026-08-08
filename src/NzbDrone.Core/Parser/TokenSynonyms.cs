using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Parser
{
    /// <summary>
    /// Simple synonym lookup for smoke test fallback.
    /// Only used when exact match fails - not for pre-normalization.
    /// </summary>
    public static class TokenSynonyms
    {
        // MINIMAL SET - structural abbreviations ONLY
        // Do NOT add numbers/Roman numerals here (use contextual ordinal matching if needed later)
        private static readonly string[][] SynonymGroups = new[]
        {
            new[] { "volume", "vol" },
            new[] { "part", "pt" },
            new[] { "book", "bk" },
        };

        private static readonly Dictionary<string, string> ToCanonical;

        static TokenSynonyms()
        {
            ToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in SynonymGroups)
            {
                var canonical = group[0];
                foreach (var token in group)
                {
                    ToCanonical[token] = canonical;
                }
            }
        }

        /// <summary>
        /// Check if two tokens are synonyms (vol/volume, pt/part, bk/book).
        /// Only returns true for tokens in the synonym table - not for case-only differences.
        /// </summary>
        public static bool AreSynonyms(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

            // Both must be in the synonym table
            if (!ToCanonical.TryGetValue(a, out var canonA)) return false;
            if (!ToCanonical.TryGetValue(b, out var canonB)) return false;

            // Must map to the same canonical form
            return string.Equals(canonA, canonB, StringComparison.OrdinalIgnoreCase);
        }
    }
}
