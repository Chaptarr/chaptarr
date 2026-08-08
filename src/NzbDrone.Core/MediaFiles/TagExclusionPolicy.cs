using System;
using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles
{
    /// <summary>
    /// Canonical tag exclusion policy.
    ///
    /// This class intentionally separates:
    /// - extraction cleanup noise: synthetic keys that should never be persisted from raw extraction
    /// - matching exclusion: tags that may be stored/displayed but should never participate in matching
    ///
    /// Keep this as the single shared policy surface so matching, V5 payloads, and extraction
    /// can converge on the same rules over time.
    /// </summary>
    public static class TagExclusionPolicy
    {
        private static readonly HashSet<string> MatchingExclusionTagKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Cover/artwork — binary data, never useful
            "covr", "APIC", "cover", "artwork", "picture",
            "MP4:covr", "ID3v2:bin:APIC",

            // Path-derived — users may have legacy-bad folder/file names
            "path", "folder", "pathcomponents", "filename",

            // Series/subtitle — often collection metadata and can cause false positives
            "series", "subtitle",

            // Duration — used for tie-breaking, not token search
            "duration", "runtime", "length", "totalduration", "total_time",
            "TLEN", "TLEN_MS", "ID3v2:TLEN",

            // Genre — "Fantasy", "Romance" etc. cause false evidence matches
            "genre", "ID3v2:TCON", "MP4:©gen", "XIPH:GENRE",

            // Language — "English", "en" never useful for matching
            "language", "XIPH:LANGUAGE",

            // Writing mode / contributor backup
            "primary-writing-mode", "contributor_bkp",

            // Copyright/rights boilerplate — legal text frequently names unrelated authors,
            // series, publishers, and years. Keep it stored for display, never as match evidence.
            "copyright", "rights", "TCOP", "©cpy", "cprt",

            // Encoding / tool markers that should not imply "real book metadata"
            "ENCODEDBY", "ENCODED_BY", "ENCODER", "ENCODING", "TENC", "TOOL", "SOFTWARE", "©too",
            "ENCODERSETTINGS", "ENCODER_SETTINGS",

            // Common audio-only quality tags that should not imply "real book metadata"
            "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK",
            "REPLAYGAIN_ALBUM_GAIN", "REPLAYGAIN_ALBUM_PEAK",

            // Comment-like fields that should never influence matching
            "©cmt", "desc", "©des", "COMM", "comment", "description", "lyr", "©lyr", "USLT",
            "MP4:©cmt", "MP4:©des", "MP4:©lyr",
            "XIPH:COMMENT", "XIPH:DESCRIPTION"
        };

        public static bool IsExcludedFromMatching(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return true;
            }

            if (MatchingExclusionTagKeys.Contains(key))
            {
                return true;
            }

            // TagLibSharpExtractor emits ID3v2 comments/lyrics as "ID3v2:COMM:<desc>" / "ID3v2:USLT:<desc>"
            if (key.StartsWith("ID3v2:COMM:", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("ID3v2:USLT:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Container-prefixed MP4 comment/lyrics boxes (e.g., "MP4:©cmtFoo")
            if (key.StartsWith("MP4:\u00a9cmt", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("MP4:\u00a9des", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("MP4:\u00a9lyr", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var lastSeparator = key.LastIndexOf(':');
            if (lastSeparator > -1 && lastSeparator < key.Length - 1)
            {
                var suffix = key.Substring(lastSeparator + 1);
                if (MatchingExclusionTagKeys.Contains(suffix))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsExtractionNoiseKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return true;
            }

            // Keep extraction cleanup conservative: match the current intended raw-tag cleanup only.
            // Synthetic keys emitted by some extractors are not user metadata and should never be persisted.
            return key.IndexOf("encode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.StartsWith("__", StringComparison.Ordinal);
        }
    }
}
