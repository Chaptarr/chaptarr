using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public static class TagCanonicalizer
    {
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> CanonicalAliasSources =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new[] { "ID3v2:TIT2", "MP4:©nam", "XIPH:TITLE", "APE:Title" },
                ["ALBUM"] = new[] { "ID3v2:TALB", "MP4:©alb", "XIPH:ALBUM", "ASF:WM/AlbumTitle", "APE:Album" },
                ["ARTIST"] = new[] { "ID3v2:TPE1", "MP4:©ART", "XIPH:ARTIST", "APE:Artist" },
                ["ALBUMARTIST"] = new[] { "ID3v2:TPE2", "MP4:aART", "XIPH:ALBUMARTIST", "ASF:WM/AlbumArtist", "APE:AlbumArtist" },
                ["COMPOSER"] = new[] { "ID3v2:TCOM", "MP4:©wrt", "XIPH:COMPOSER", "ASF:WM/Composer", "APE:Composer" },
                ["PUBLISHER"] = new[] { "ID3v2:TPUB", "MP4:©pub", "XIPH:PUBLISHER", "ASF:WM/Publisher", "APE:Publisher" },
                ["GENRE"] = new[] { "ID3v2:TCON", "MP4:©gen", "XIPH:GENRE", "APE:Genre" },
                ["DATE"] = new[] { "ID3v2:TDRC", "ID3v2:TYER", "ID3v2:TDAT", "MP4:©day", "XIPH:DATE", "APE:Year" },
                ["ORIGINALDATE"] = new[] { "ID3v2:TDOR", "XIPH:ORIGINALDATE", "ASF:WM/OriginalReleaseTime" },
                ["ORIGINALYEAR"] = new[] { "ASF:WM/OriginalReleaseYear" },
                ["COMMENT"] = new[] { "ID3v2:COMM:*", "MP4:©cmt", "XIPH:COMMENT" },
                ["TRACKNUMBER"] = new[] { "ID3v2:TRCK", "XIPH:TRACKNUMBER", "ASF:WM/TrackNumber", "APE:Track" },
                ["TOTALTRACKS"] = new[] { "ID3v2:TRCK", "XIPH:TOTALTRACKS" },
                ["DISCNUMBER"] = new[] { "ID3v2:TPOS", "XIPH:DISCNUMBER" },
                ["TOTALDISCS"] = new[] { "ID3v2:TPOS", "XIPH:TOTALDISCS" },
                ["NARRATOR"] = new[] { "ID3v2:TXXX:NARRATOR", "XIPH:NARRATOR", "ASF:WM/Narrator", "APE:Narrator" },
                ["SERIES"] = new[] { "ID3v2:TXXX:SERIES", "XIPH:SERIES" },
                ["LANGUAGE"] = new[] { "XIPH:LANGUAGE", "ASF:WM/Language", "APE:Language" },
                ["ISBN"] = new[] { "ID3v2:TXXX:ISBN", "XIPH:ISBN", "ASF:WM/ISBN", "APE:ISBN" },
                ["AUDIBLE_ASIN"] = new[]
                {
                    "MP4:----:com.pilabor.tone:AUDIBLE_ASIN",
                    "----:com.pilabor.tone:AUDIBLE_ASIN",
                    "ID3v2:TXXX:AUDIBLE_ASIN"
                },
                ["ASIN"] = new[]
                {
                    "AUDIBLE_ASIN",
                    "MP4:----:com.pilabor.tone:AUDIBLE_ASIN",
                    "----:com.pilabor.tone:AUDIBLE_ASIN",
                    "ID3v2:TXXX:AUDIBLE_ASIN",
                    "XIPH:ASIN",
                    "ASF:WM/ASIN",
                    "APE:ASIN"
                }
            };

        /// <summary>
        /// Returns true only when a friendly canonical key/value is backed by a known raw
        /// container key carrying that same value. This is extraction lineage, not a claim
        /// that TITLE/ARTIST/etc. have any semantic matching role.
        /// </summary>
        public static bool IsCanonicalAliasBackedByRawSource(
            string canonicalKey,
            string value,
            IDictionary<string, List<string>> tags)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey) ||
                string.IsNullOrWhiteSpace(value) ||
                tags == null ||
                !CanonicalAliasSources.TryGetValue(canonicalKey, out var sourcePatterns))
            {
                return false;
            }

            foreach (var pair in tags)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) ||
                    pair.Value == null ||
                    !sourcePatterns.Any(pattern => SourceKeyMatches(pattern, pair.Key)))
                {
                    continue;
                }

                if (pair.Value.Any(candidate =>
                        !string.IsNullOrWhiteSpace(candidate) &&
                        string.Equals(candidate.Trim(), value.Trim(), StringComparison.Ordinal)))
                    return true;
                {
                }
            }

            return false;
        }

        private static bool SourceKeyMatches(string pattern, string key)
        {
            return pattern.EndsWith("*", StringComparison.Ordinal)
                ? key.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase)
                : string.Equals(pattern, key, StringComparison.OrdinalIgnoreCase);
        }

        public static void AddCanonicalKeys(Dictionary<string, List<string>> tags)
        {
            if (tags == null || tags.Count == 0) return;

            void Add(string key, IEnumerable<string> values)
            {
                if (string.IsNullOrWhiteSpace(key) || values == null) return;
                if (!tags.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    tags[key] = list;
                }
                foreach (var v in values)
                {
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    var t = v.Trim();
                    if (!list.Contains(t)) list.Add(t);
                }
            }

            IEnumerable<string> Get(string key)
            {
                return tags.TryGetValue(key, out var vals) ? vals : Enumerable.Empty<string>();
            }

            // ID3v2 mappings (prefix ID3v2:)
            Add("TITLE", Get("ID3v2:TIT2"));
            Add("ALBUM", Get("ID3v2:TALB"));
            Add("ARTIST", Get("ID3v2:TPE1"));
            Add("ALBUMARTIST", Get("ID3v2:TPE2"));
            Add("COMPOSER", Get("ID3v2:TCOM"));
            Add("PUBLISHER", Get("ID3v2:TPUB"));
            Add("GENRE", Get("ID3v2:TCON"));
            // Date: prefer TDRC; fallback TYER (ID3v2.3) and TDAT (day+month). Keep raw string.
            var dates = Get("ID3v2:TDRC").Concat(Get("ID3v2:TYER")).Concat(Get("ID3v2:TDAT"));
            Add("DATE", dates);
            // Original release date kept separate
            Add("ORIGINALDATE", Get("ID3v2:TDOR"));
            // Comments
            foreach (var kv in tags.Where(kv => kv.Key.StartsWith("ID3v2:COMM:", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Add("COMMENT", kv.Value);
            }
            // Tracks and discs: TRCK and TPOS may be "n" or "n/m"
            foreach (var val in Get("ID3v2:TRCK"))
            {
                var parts = (val ?? "").Split('/');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) Add("TRACKNUMBER", new[] { parts[0] });
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) Add("TOTALTRACKS", new[] { parts[1] });
            }
            foreach (var val in Get("ID3v2:TPOS"))
            {
                var parts = (val ?? "").Split('/');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) Add("DISCNUMBER", new[] { parts[0] });
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) Add("TOTALDISCS", new[] { parts[1] });
            }
            // Common TXXX custom fields
            Add("NARRATOR", Get("ID3v2:TXXX:NARRATOR"));
            Add("SERIES", Get("ID3v2:TXXX:SERIES"));
            Add("ISBN", Get("ID3v2:TXXX:ISBN"));
            Add("ASIN", Get("ID3v2:TXXX:ASIN"));

            // MP4 (prefix MP4:)
            Add("TITLE", Get("MP4:©nam"));
            Add("ALBUM", Get("MP4:©alb"));
            Add("ARTIST", Get("MP4:©ART"));
            Add("ALBUMARTIST", Get("MP4:aART"));
            Add("COMPOSER", Get("MP4:©wrt"));
            Add("PUBLISHER", Get("MP4:©pub"));
            Add("GENRE", Get("MP4:©gen"));
            Add("DATE", Get("MP4:©day"));
            Add("COMMENT", Get("MP4:©cmt"));
            // Vendor atoms (custom): surface Audible ASIN
            Add("AUDIBLE_ASIN", Get("MP4:----:com.pilabor.tone:AUDIBLE_ASIN"));
            Add("AUDIBLE_ASIN", Get("----:com.pilabor.tone:AUDIBLE_ASIN"));
            // Mirror into ASIN for convenience
            Add("ASIN", Get("AUDIBLE_ASIN"));

            // XIPH (prefix XIPH:)
            Add("TITLE", Get("XIPH:TITLE"));
            Add("ALBUM", Get("XIPH:ALBUM"));
            Add("ARTIST", Get("XIPH:ARTIST"));
            Add("ALBUMARTIST", Get("XIPH:ALBUMARTIST"));
            Add("COMPOSER", Get("XIPH:COMPOSER"));
            Add("PUBLISHER", Get("XIPH:PUBLISHER"));
            Add("GENRE", Get("XIPH:GENRE"));
            Add("DATE", Get("XIPH:DATE"));
            Add("ORIGINALDATE", Get("XIPH:ORIGINALDATE"));
            Add("COMMENT", Get("XIPH:COMMENT"));
            Add("NARRATOR", Get("XIPH:NARRATOR"));
            Add("LANGUAGE", Get("XIPH:LANGUAGE"));
            Add("ISBN", Get("XIPH:ISBN"));
            Add("ASIN", Get("XIPH:ASIN"));
            Add("SERIES", Get("XIPH:SERIES"));
            Add("TRACKNUMBER", Get("XIPH:TRACKNUMBER"));
            Add("DISCNUMBER", Get("XIPH:DISCNUMBER"));
            Add("TOTALDISCS", Get("XIPH:TOTALDISCS"));
            Add("TOTALTRACKS", Get("XIPH:TOTALTRACKS"));

            // ASF (prefix ASF:)
            Add("ALBUM", Get("ASF:WM/AlbumTitle"));
            Add("ALBUMARTIST", Get("ASF:WM/AlbumArtist"));
            Add("COMPOSER", Get("ASF:WM/Composer"));
            Add("PUBLISHER", Get("ASF:WM/Publisher"));
            Add("NARRATOR", Get("ASF:WM/Narrator"));
            Add("LANGUAGE", Get("ASF:WM/Language"));
            Add("ISBN", Get("ASF:WM/ISBN"));
            Add("ASIN", Get("ASF:WM/ASIN"));
            Add("TRACKNUMBER", Get("ASF:WM/TrackNumber"));
            Add("ORIGINALDATE", Get("ASF:WM/OriginalReleaseTime"));
            Add("ORIGINALYEAR", Get("ASF:WM/OriginalReleaseYear"));

            // APE (prefix APE:)
            Add("TITLE", Get("APE:Title"));
            Add("ALBUM", Get("APE:Album"));
            Add("ARTIST", Get("APE:Artist"));
            Add("ALBUMARTIST", Get("APE:AlbumArtist"));
            Add("COMPOSER", Get("APE:Composer"));
            Add("PUBLISHER", Get("APE:Publisher"));
            Add("GENRE", Get("APE:Genre"));
            Add("DATE", Get("APE:Year"));
            Add("NARRATOR", Get("APE:Narrator"));
            Add("LANGUAGE", Get("APE:Language"));
            Add("ISBN", Get("APE:ISBN"));
            Add("ASIN", Get("APE:ASIN"));
            Add("TRACKNUMBER", Get("APE:Track"));
            
            // Additional ID3v2 vendor/custom AUDIBLE_ASIN mapping
            Add("AUDIBLE_ASIN", Get("ID3v2:TXXX:AUDIBLE_ASIN"));
            Add("ASIN", Get("AUDIBLE_ASIN"));
        }
    }
}
