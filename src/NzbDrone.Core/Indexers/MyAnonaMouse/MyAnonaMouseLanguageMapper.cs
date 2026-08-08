using System;
using System.Collections.Generic;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    internal static class MyAnonaMouseLanguageMapper
    {
        private static readonly Dictionary<string, int> BrowseLanguageIds = new(StringComparer.OrdinalIgnoreCase)
        {
            // IDs 1-63 follow MAM's search dropdown as maintained by Prowlarr.
            // Common languages plus MAM's newer Albanian (64) and Welsh (65) were API-verified on 2026-07-10.
            ["eng"] = 1,
            ["afr"] = 17,
            ["sqi"] = 64,
            ["ara"] = 32,
            ["ben"] = 35,
            ["bos"] = 51,
            ["bul"] = 18,
            ["mya"] = 6,
            ["yue"] = 44,
            ["cat"] = 19,
            ["zho"] = 2,
            ["hrv"] = 49,
            ["ces"] = 20,
            ["dan"] = 21,
            ["nld"] = 22,
            ["est"] = 61,
            ["fas"] = 39,
            ["fin"] = 23,
            ["fra"] = 36,
            ["deu"] = 37,
            ["ell"] = 26,
            ["grc"] = 59,
            ["guj"] = 3,
            ["heb"] = 27,
            ["hin"] = 8,
            ["hun"] = 28,
            ["isl"] = 63,
            ["ind"] = 53,
            ["gle"] = 56,
            ["ita"] = 43,
            ["jpn"] = 38,
            ["jav"] = 12,
            ["kan"] = 5,
            ["kor"] = 41,
            ["lit"] = 50,
            ["lat"] = 46,
            ["lav"] = 62,
            ["msa"] = 33,
            ["mal"] = 58,
            ["glv"] = 57,
            ["mar"] = 9,
            ["nor"] = 48,
            ["pol"] = 45,
            ["por"] = 34,
            ["pan"] = 14,
            ["ron"] = 30,
            ["rus"] = 16,
            ["gla"] = 24,
            ["san"] = 60,
            ["srp"] = 31,
            ["slv"] = 54,
            ["spa"] = 4,
            ["swe"] = 40,
            ["tgl"] = 29,
            ["tam"] = 11,
            ["tel"] = 10,
            ["tha"] = 7,
            ["tur"] = 42,
            ["ukr"] = 25,
            ["urd"] = 15,
            ["vie"] = 13,
            ["cym"] = 65
        };

        private static readonly Dictionary<string, int> BrowseLanguageAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cantonese"] = 44,
            ["greek"] = 26,
            ["malay"] = 33,
            ["punjabi"] = 14,
            ["scottish gaelic"] = 24,
            ["brazilian portuguese"] = 52,
            ["portuguese (brazil)"] = 52,
            ["pt-br"] = 52,
            ["castilian spanish"] = 55,
            ["castilian"] = 55,
            ["es-es"] = 55,
            ["ancient greek"] = 59,
            ["greek, ancient"] = 59,
            ["farsi"] = 39,
            ["other"] = 47
        };

        private static readonly Dictionary<string, string> MamCodeAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // MAM uses a few legacy/non-ISO abbreviations in lang_code.
            ["jap"] = "jpn"
        };

        public static bool TryGetBrowseLanguageId(string language, out int languageId)
        {
            languageId = 0;

            var normalized = language?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && BrowseLanguageAliases.TryGetValue(normalized, out languageId))
            {
                return true;
            }

            return TryCanonicalize(language, out var canonical) &&
                   BrowseLanguageIds.TryGetValue(canonical, out languageId);
        }

        public static bool TryGetLanguage(string mamLanguageCode, out Language language, out string canonical)
        {
            language = null;
            canonical = null;

            if (!TryCanonicalize(mamLanguageCode, out canonical))
            {
                return false;
            }

            var isoLanguage = IsoLanguages.Find(canonical);
            if (isoLanguage?.Language == null)
            {
                return false;
            }

            language = isoLanguage.Language;
            return true;
        }

        public static bool TryCanonicalize(string language, out string canonical)
        {
            canonical = null;

            if (string.IsNullOrWhiteSpace(language))
            {
                return false;
            }

            var normalized = language.Trim();
            if (MamCodeAliases.TryGetValue(normalized, out var alias))
            {
                normalized = alias;
            }

            canonical = normalized.CanonicalizeLanguage();
            return !string.IsNullOrWhiteSpace(canonical);
        }
    }
}
