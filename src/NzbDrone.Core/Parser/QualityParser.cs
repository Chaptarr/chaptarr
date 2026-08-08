using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Parser
{
    public class QualityParser
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(QualityParser));

        private static readonly Regex AudioHintRegex = new(@"\b(audiobook|audio\s*book|audible|graphic\s*audio|full\s*cast|ungekuerzt|ungekürzt)\b",
                                                           RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ProperRegex = new(@"\b(?<proper>proper)\b",
                                                                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RepackRegex = new(@"\b(?<repack>repack|rerip)\b",
                                                                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VersionRegex = new(@"\d[-._ ]?v(?<version>\d)[-._ ]|\[v(?<version>\d)\]",
                                                                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RealRegex = new(@"\b(?<real>REAL)\b",
                                                                RegexOptions.Compiled);

        private static readonly Regex CodecRegex = new(@"\b(?:(?<PDF>PDF)|(?<MOBI>MOBI)|(?<EPUB>EPUB)|(?<AZW3>AZW3?)|(?<MP1>MPEG Version \d(.5)? Audio, Layer 1|MP1)|(?<MP2>MPEG Version \d(.5)? Audio, Layer 2|MP2)|(?<MP3VBR>MP3.*VBR|MPEG Version \d(.5)? Audio, Layer 3 vbr)|(?<MP3CBR>MP3|MPEG Version \d(.5)? Audio, Layer 3)|(?<FLAC>flac)|(?<WAVPACK>wavpack|wv)|(?<ALAC>alac)|(?<WMA>WMA\d?)|(?<WAV>WAV|PCM)|(?<AAC>M4A|M4P|M4B|AAC|mp4a|MPEG-4 Audio(?!.*alac))|(?<OGG>OGG|OGA|Vorbis))\b|(?<APE>monkey's audio|[\[|\(].*\bape\b.*[\]|\)])|(?<OPUS>Opus Version \d(.5)? Audio|[\[|\(].*\bopus\b.*[\]|\)])",
                                                             RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Structural helpers for DetectTitleFormats. All language-neutral by design: delimiters,
        // path separators and filename extensions mean the same thing in every language.
        private static readonly Regex TerminalExtensionRegex = new(@"\.(?<extension>[a-z0-9]{1,6})\s*$",
                                                                   RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FormatSeparatorRegex = new(@"^[\s,;/\\+&_\-\|\.\[\]\(\)\{\}]*$",
                                                                 RegexOptions.Compiled);

        private static readonly char[] PathSeparators = { '/', '\\' };
        private static readonly char[] GroupOpeners = { '[', '(', '{' };
        private static readonly char[] GroupClosers = { ']', ')', '}' };

        private const int MaxFormatTokenGap = 3;

        public static QualityModel ParseQuality(string name, string desc = null, List<int> categories = null, string indexerName = null, List<string> tags = null, int indexerFlags = 0)
        {
            Logger.Debug("Trying to parse quality for '{0}'", name);

            if (name.IsNullOrWhiteSpace() && desc.IsNullOrWhiteSpace())
            {
                return new QualityModel { Quality = Quality.Unknown };
            }

            var normalizedName = name.Replace('_', ' ').Trim().ToLower();
            var result = ParseQualityModifiers(name, normalizedName);

            if (desc.IsNotNullOrWhiteSpace())
            {
                var descCodec = ParseCodec(desc, "");
                Logger.Trace($"Got codec {descCodec}");

                result.Quality = FindQuality(descCodec);

                if (result.Quality != Quality.Unknown)
                {
                    result.QualityDetectionSource = QualityDetectionSource.TagLib;
                    return result;
                }
            }

            // Formats asserted by the title, tiered by structure: terminal extension > delimited
            // format list > loose word.
            var titleFormats = DetectTitleFormats(name);

            if (titleFormats.Any)
            {
                result.Quality = titleFormats.Qualities.First();
                result.DetectedQualities = titleFormats.Qualities.ToList();
                result.QualityDetectionSource = titleFormats.PrimaryFromExtension
                    ? QualityDetectionSource.Extension
                    : QualityDetectionSource.Name;
            }
            else
            {
                result.Quality = Quality.Unknown;
            }

            //Based on extension
            if (result.Quality == Quality.Unknown && !name.ContainsInvalidPathChars())
            {
                try
                {
                    result.Quality = MediaFileExtensions.GetQualityForExtension(name.GetPathExtension());
                    result.QualityDetectionSource = QualityDetectionSource.Extension;
                }
                catch (ArgumentException)
                {
                    //Swallow exception for cases where string contains illegal
                    //path characters.
                }
            }

            //Based on category
            if (result.Quality == Quality.Unknown && categories != null)
            {
                if (categories.Any(x => x >= 3000 && x < 4000))
                {
                    result.Quality = Quality.UnknownAudio;
                    result.QualityDetectionSource = QualityDetectionSource.Category;
                }
            }

            // If we still couldn't determine a codec but the title/description strongly indicates audio,
            // classify as Unknown Audio instead of Unknown Text.
            if (result.Quality == Quality.Unknown)
            {
                if (AudioHintRegex.IsMatch(name) || (desc.IsNotNullOrWhiteSpace() && AudioHintRegex.IsMatch(desc)))
                {
                    result.Quality = Quality.UnknownAudio;
                    result.QualityDetectionSource = QualityDetectionSource.Name;
                }
            }

            // MAM tier enhancements removed: keep generic base quality (MP3/M4B/FLAC/etc.)

            return result;
        }

        public static QualityModel ParseQualityFromFileType(string fileType, string title, int indexerFlags, string indexerName = null)
        {
            Logger.Debug("Parsing quality from fileType '{0}' for title '{1}'", fileType, title);

            var result = ParseQualityModifiers(title, title.Replace('_', ' ').Trim().ToLower());

            if (!string.IsNullOrWhiteSpace(fileType))
            {
                // Parse all formats from space/comma-separated string
                var detectedQualities = ParseAllFormatsFromFileType(fileType);

                if (detectedQualities.Any())
                {
                    // Set primary quality as first detected
                    var primaryQuality = detectedQualities.First();

                    result.Quality = primaryQuality;
                    result.DetectedQualities = detectedQualities;
                    result.QualityDetectionSource = QualityDetectionSource.TagLib;

                    Logger.Debug("Multi-format detection: Primary='{0}', All detected=[{1}]", primaryQuality.Name, string.Join(", ", detectedQualities.Select(q => q.Name)));

                    return result;
                }
            }

            // Fallback: try extension-based detection from title
            if (!string.IsNullOrWhiteSpace(title) && !title.ContainsInvalidPathChars())
            {
                try
                {
                    result.Quality = MediaFileExtensions.GetQualityForExtension(title.GetPathExtension());
                    if (result.Quality != Quality.Unknown)
                    {
                        result.QualityDetectionSource = QualityDetectionSource.Extension;
                        return result;
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore invalid path chars
                }
            }

            // Still unknown
            result.Quality = Quality.Unknown;
            return result;
        }

        private static List<Quality> ParseAllFormatsFromFileType(string fileType)
        {
            var qualities = new List<Quality>();

            if (string.IsNullOrWhiteSpace(fileType))
            {
                return qualities;
            }

            // Split on common separators used in multi-format strings
            var formats = fileType.Split(new[] { ' ', ',', ';', '+' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var format in formats)
            {
                var trimmedFormat = format.Trim();
                if (string.IsNullOrWhiteSpace(trimmedFormat))
                {
                    continue;
                }

                var codec = ParseCodec(trimmedFormat.ToLower(), trimmedFormat);
                if (codec != Codec.Unknown)
                {
                    var quality = FindQuality(codec);
                    if (quality != Quality.Unknown && !qualities.Contains(quality))
                    {
                        qualities.Add(quality);
                        Logger.Trace("Detected format: '{0}' -> Codec: {1} -> Quality: {2}", trimmedFormat, codec, quality.Name);
                    }
                }
            }

            return qualities;
        }

        /// <summary>
        /// Every format the title asserts, tiered by structure rather than by language so the same
        /// rules hold for a title in any language.
        /// </summary>
        public static TitleFormatEvidence DetectTitleFormats(string name)
        {
            var evidence = new TitleFormatEvidence();

            if (name.IsNullOrWhiteSpace())
            {
                return evidence;
            }

            // Underscores are word characters, so the codec vocabulary's \b boundaries never fire
            // inside scene-style names ("Author_-_Title_[EPUB_MOBI]"). ParseQuality has always
            // matched against an underscore-normalised copy; do the same here. The replacement is
            // length-preserving, so match offsets still index into the original title.
            var scanName = name.Replace('_', ' ');

            // Tier 3 — the extension terminating the last path segment names the actual payload.
            var lastSegmentStart = scanName.LastIndexOfAny(PathSeparators) + 1;
            var lastSegment = scanName.Substring(lastSegmentStart);
            var extensionMatch = TerminalExtensionRegex.Match(lastSegment.Trim());
            var extensionQuality = Quality.Unknown;

            if (extensionMatch.Success)
            {
                extensionQuality = MediaFileExtensions.GetQualityForExtension("." + extensionMatch.Groups["extension"].Value.ToLowerInvariant());
            }

            // Tiers 2/1 — every token the codec vocabulary recognises, in title order.
            var tokens = new List<(Match Match, Quality Quality)>();

            foreach (Match match in CodecRegex.Matches(scanName))
            {
                if (!match.Success)
                {
                    continue;
                }

                var quality = MapCodecToQuality(CodecFromMatch(match));

                if (quality != Quality.Unknown)
                {
                    tokens.Add((match, quality));
                }
            }

            var groupedInOtherSegment = new List<Quality>();
            var grouped = new List<Quality>();

            for (var i = 0; i < tokens.Count; i++)
            {
                var isGrouped = IsInsideDelimitedGroup(scanName, tokens[i].Match) ||
                                (i > 0 && AreAdjacent(scanName, tokens[i - 1].Match, tokens[i].Match)) ||
                                (i < tokens.Count - 1 && AreAdjacent(scanName, tokens[i].Match, tokens[i + 1].Match));

                if (!isGrouped)
                {
                    continue;
                }

                grouped.Add(tokens[i].Quality);

                if (tokens[i].Match.Index < lastSegmentStart)
                {
                    groupedInOtherSegment.Add(tokens[i].Quality);
                }
            }

            if (extensionQuality != Quality.Unknown)
            {
                evidence.PrimaryFromExtension = true;
                evidence.Qualities.Add(extensionQuality);

                // "Author - Title [azw3 epub mobi]/Author - Title.mobi" is a package that lists its
                // contents and then names one member: the extension is the concrete member, the
                // list is what else is in there, and the profile picks between them. A list in the
                // SAME segment as the extension ("Title [EPUB].mobi") is just a claim about the one
                // file whose extension we can already read, so the extension stands alone.
                if (groupedInOtherSegment.Any())
                {
                    evidence.Tier = FormatEvidenceTier.FormatGroup;
                    evidence.Qualities.AddRange(groupedInOtherSegment.Where(q => q != extensionQuality));
                    evidence.Qualities = evidence.Qualities.Distinct().ToList();
                }
                else
                {
                    evidence.Tier = FormatEvidenceTier.TerminalExtension;
                }

                return evidence;
            }

            if (!tokens.Any())
            {
                return evidence;
            }

            if (grouped.Any())
            {
                evidence.Tier = FormatEvidenceTier.FormatGroup;
                evidence.Qualities = grouped.Distinct().ToList();
                return evidence;
            }

            // Loose tokens carry ONE quality — the first, exactly as before tiering existed. Words
            // scattered through prose ("MP3 sourced from FLAC", "epub requested") are not a bundle,
            // and treating them as one would let an unrelated mention be promoted over the format
            // the release actually is.
            evidence.Tier = FormatEvidenceTier.LooseToken;
            evidence.Qualities = new List<Quality> { tokens.First().Quality };
            return evidence;
        }

        /// <summary>Two format tokens separated by nothing but delimiters are one deliberate list.</summary>
        private static bool AreAdjacent(string name, Match left, Match right)
        {
            var start = left.Index + left.Length;
            var length = right.Index - start;

            if (length < 0 || length > MaxFormatTokenGap)
            {
                return false;
            }

            return FormatSeparatorRegex.IsMatch(name.Substring(start, length));
        }

        /// <summary>A token wrapped in brackets/parens/braces is an explicit format annotation.</summary>
        private static bool IsInsideDelimitedGroup(string name, Match match)
        {
            var openIndex = name.LastIndexOfAny(GroupOpeners, System.Math.Max(match.Index - 1, 0));

            if (openIndex < 0 || openIndex >= match.Index)
            {
                return false;
            }

            // A closer between the opener and the token means the token is outside that group.
            if (name.IndexOfAny(GroupClosers, openIndex, match.Index - openIndex) >= 0)
            {
                return false;
            }

            var tokenEnd = match.Index + match.Length;

            return tokenEnd < name.Length && name.IndexOfAny(GroupClosers, tokenEnd) >= 0;
        }

        /// <summary>
        /// Codec to quality exactly as <see cref="ParseQuality"/> has always mapped it. Kept separate
        /// from <see cref="FindQuality"/>, which is the MAM FileType mapping and differs (WAV, and an
        /// MP3 default for unknown codecs).
        /// </summary>
        private static Quality MapCodecToQuality(Codec codec)
        {
            switch (codec)
            {
                case Codec.PDF:
                    return Quality.PDF;
                case Codec.EPUB:
                    return Quality.EPUB;
                case Codec.MOBI:
                    return Quality.MOBI;
                case Codec.AZW3:
                    return Quality.AZW3;
                case Codec.FLAC:
                case Codec.ALAC:
                case Codec.WAVPACK:
                    return Quality.FLAC;
                case Codec.AAC:
                    return Quality.M4B;
                case Codec.MP1:
                case Codec.MP2:
                case Codec.MP3VBR:
                case Codec.MP3CBR:
                case Codec.APE:
                case Codec.WMA:
                case Codec.WAV:
                case Codec.AACVBR:
                case Codec.OGG:
                case Codec.OPUS:
                    return Quality.MP3;
                default:
                    return Quality.Unknown;
            }
        }

        public static Codec ParseCodec(string name, string origName)
        {
            if (name.IsNullOrWhiteSpace())
            {
                return Codec.Unknown;
            }

            var match = CodecRegex.Match(name);

            if (!match.Success)
            {
                return Codec.Unknown;
            }

            return CodecFromMatch(match);
        }

        private static Codec CodecFromMatch(Match match)
        {
            if (match.Groups["PDF"].Success)
            {
                return Codec.PDF;
            }

            if (match.Groups["EPUB"].Success)
            {
                return Codec.EPUB;
            }

            if (match.Groups["MOBI"].Success)
            {
                return Codec.MOBI;
            }

            if (match.Groups["AZW3"].Success)
            {
                return Codec.AZW3;
            }

            if (match.Groups["FLAC"].Success)
            {
                return Codec.FLAC;
            }

            if (match.Groups["ALAC"].Success)
            {
                return Codec.ALAC;
            }

            if (match.Groups["WMA"].Success)
            {
                return Codec.WMA;
            }

            if (match.Groups["WAV"].Success)
            {
                return Codec.WAV;
            }

            if (match.Groups["AAC"].Success)
            {
                return Codec.AAC;
            }

            if (match.Groups["OGG"].Success)
            {
                return Codec.OGG;
            }

            if (match.Groups["OPUS"].Success)
            {
                return Codec.OPUS;
            }

            if (match.Groups["MP1"].Success)
            {
                return Codec.MP1;
            }

            if (match.Groups["MP2"].Success)
            {
                return Codec.MP2;
            }

            if (match.Groups["MP3VBR"].Success)
            {
                return Codec.MP3VBR;
            }

            if (match.Groups["MP3CBR"].Success)
            {
                return Codec.MP3CBR;
            }

            if (match.Groups["WAVPACK"].Success)
            {
                return Codec.WAVPACK;
            }

            if (match.Groups["APE"].Success)
            {
                return Codec.APE;
            }

            return Codec.Unknown;
        }

        private static Quality FindQuality(Codec codec)
        {
            switch (codec)
            {
                case Codec.PDF:
                    return Quality.PDF;
                case Codec.EPUB:
                    return Quality.EPUB;
                case Codec.MOBI:
                    return Quality.MOBI;
                case Codec.AZW3:
                    return Quality.AZW3;
                case Codec.ALAC:
                case Codec.FLAC:
                case Codec.WAVPACK:
                case Codec.WAV:
                    return Quality.FLAC;
                case Codec.AAC:
                    return Quality.M4B;
                default:
                    return Quality.MP3;
            }
        }

        private static QualityModel ParseQualityModifiers(string name, string normalizedName)
        {
            var result = new QualityModel { Quality = Quality.Unknown };

            if (ProperRegex.IsMatch(normalizedName))
            {
                result.Revision.Version = 2;
            }

            if (RepackRegex.IsMatch(normalizedName))
            {
                result.Revision.Version = 2;
                result.Revision.IsRepack = true;
            }

            var versionRegexResult = VersionRegex.Match(normalizedName);

            if (versionRegexResult.Success)
            {
                result.Revision.Version = Convert.ToInt32(versionRegexResult.Groups["version"].Value);
            }

            //TODO: re-enable this when we have a reliable way to determine real
            var realRegexResult = RealRegex.Matches(name);

            if (realRegexResult.Count > 0)
            {
                result.Revision.Real = realRegexResult.Count;
            }

            return result;
        }

        // MAM-specific helpers removed
    }

    public enum Codec
    {
        MP1,
        MP2,
        MP3CBR,
        MP3VBR,
        FLAC,
        ALAC,
        APE,
        WAVPACK,
        WMA,
        AAC,
        AACVBR,
        OGG,
        OPUS,
        WAV,
        PDF,
        EPUB,
        MOBI,
        AZW3,
        Unknown
    }
}
