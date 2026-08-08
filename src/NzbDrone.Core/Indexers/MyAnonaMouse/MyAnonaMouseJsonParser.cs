using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MyAnonaMouseJsonParser : IParseIndexerResponse
    {
        private readonly MyAnonaMouseSettings _settings;

        // Compiled regex patterns for freeleech detection
        private static readonly Regex FreeleechPattern = new Regex(
            @"\b(?:freeleech|free\s*leech|free-leech|\[fl\]|^fl\s|\sfl\s|\sfl$|-fl-|\.fl\.|[\(\[]fl[\)\]]|personal\s*freeleech|vip\s*freeleech|0%\s*download|free\s*download|no\s*ratio|ratio\s*free)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PartialFreeleechPattern = new Regex(
            @"\b(?:(?:50|25|75)%\s*(?:freeleech|off)|half\s*leech|halfleech)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DoubleUploadPattern = new Regex(
            @"\b(?:double\s*upload|2x\s*upload|double\s*seed|2x\s*seed|\[du\]|\sdu\s)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex NarratorProseTokenRegex = new Regex(@"\b(?:author|book|chapter|download|file|format|kbs|kbps|mb|mib|gb|gib|story|torrent|true|upload)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NarratorLetterRegex = new Regex(@"\p{L}", RegexOptions.Compiled);
        private static readonly Regex NarratorWordRegex = new Regex(@"[\p{L}][\p{L}'’\.-]*", RegexOptions.Compiled);

        // Compiled regex patterns for duration extraction
        private static readonly List<Regex> DurationPatterns = new List<Regex>
        {
            // Pattern 1: "Length: X hrs and Y mins"
            new Regex(@"Length:\s*(\d+)\s*hrs?\s*and\s*(\d+)\s*mins?", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 2: "Approximate Running Time: X Hours"
            new Regex(@"Approximate Running Time:\s*(\d+)\s*Hours?", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 3: "X hrs and Y mins" (no prefix)
            new Regex(@"\b(\d+)\s*hrs?\s*and\s*(\d+)\s*mins?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 4: "Xh Ym" (compact)
            new Regex(@"\b(\d+)h\s*(\d+)m\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 5: "Listening Length : X Hours and Y Minutes"
            new Regex(@"Listening Length\s*:\s*(\d+)\s*Hours?\s*and\s*(\d+)\s*Minutes?", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 6: "Duration: X hours, Y minutes, Z seconds"
            new Regex(@"Duration:\s*(\d+)\s*hours?,\s*(\d+)\s*minutes?,\s*(\d+)\s*seconds?", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 7: "Duration: X Hrs, Y Mins" / "Duration: X Hours, Y Minutes"
            new Regex(@"Duration:\s*(\d+)\s*(?:hrs?|hours?)\s*,\s*(\d+)\s*(?:mins?|minutes?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 8: "Length X hours and Y minutes"
            new Regex(@"Length\s+(\d+)\s*hours?\s*and\s*(\d+)\s*minutes?", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 9: "Runtime: X hours, Y minutes"
            new Regex(@"Runtime:\s*(\d+)\s*hours?,\s*(\d+)\s*minutes?", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 10: "Total Runtime: X Hours Y Mins"
            new Regex(@"Total Runtime[\s.:]+(\d+)\s*Hours?\s*(?:and\s*)?(\d+)\s*Mins?", RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Pattern 11: "Xhr Ymin" (compact format without spaces)
            new Regex(@"\b(\d+)hr\s*(\d+)min\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        private static readonly List<Regex> LooseDurationPatterns = new List<Regex>
        {
            // Fallback only for concise metadata fields such as tags/title, not free-form descriptions
            new Regex(@"\b(\d+)\s*Hours?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        // Compiled regex patterns for narrator extraction
        private static readonly List<Regex> NarratorPatterns = new List<Regex>
        {
            new Regex(@"(?:Narrated by|Read by|Voice by)\s+([^,;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"Narrator:\s*([^,;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"([^-]+)\s*-\s*Narrator", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        // GraphicAudio detection patterns - use comprehensive list from AudioProductionConstants
        private static readonly Regex GraphicAudioPattern = new Regex(
            $@"\b(?:{string.Join("|", AudioProductionConstants.GraphicAudioIndicators.Select(Regex.Escape))})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public MyAnonaMouseJsonParser(MyAnonaMouseSettings settings)
        {
            _settings = settings;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var torrentInfos = new List<ReleaseInfo>();

            if (indexerResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new IndexerException(indexerResponse, $"Unexpected response status {indexerResponse.HttpResponse.StatusCode} code from API request");
            }

            if (!indexerResponse.HttpResponse.Headers.ContentType.Contains("json"))
            {
                throw new IndexerException(indexerResponse, $"Unexpected response content-type {indexerResponse.HttpResponse.Headers.ContentType} from API request, expected JSON");
            }

            var logger = NLog.LogManager.GetCurrentClassLogger();

            var jsonResponse = JsonConvert.DeserializeObject<JObject>(indexerResponse.Content);

            if (jsonResponse == null)
            {
                throw new IndexerException(indexerResponse, "Invalid JSON response from API request");
            }

            var data = jsonResponse["data"];
            if (data == null || !data.HasValues)
            {
                logger.Trace("MAM_API_RESPONSE: No torrent data found in response");
                return torrentInfos;
            }

            logger.Trace("MAM_API_RESPONSE: Found {0} torrents in response", data.Count());

            // MAM_CATEGORY_ANALYSIS: Log categories to determine audiobook category
            var categoryAnalysis = new Dictionary<string, int>();
            foreach (var row in data)
            {
                // Handle both JObject (direct) and JArray (wrapped) formats
                JObject torrent = null;

                if (row is JObject directTorrent)
                {
                    // Direct JObject format (current MAM API)
                    torrent = directTorrent;
                }
                else if (row is JArray rowArray && rowArray.Count > 0)
                {
                    // Wrapped JArray format (legacy compatibility)
                    torrent = rowArray[0] as JObject;
                }

                if (torrent != null)
                {
                    var categoryId = torrent.Value<string>("category");
                    var categoryName = torrent.Value<string>("catname");
                    var categoryKey = $"{categoryId}:{categoryName}";
                    if (!categoryAnalysis.ContainsKey(categoryKey))
                    {
                        categoryAnalysis[categoryKey] = 0;
                    }

                    categoryAnalysis[categoryKey]++;
                }
            }

            logger.Trace("MAM_CATEGORY_ANALYSIS: Categories found in this search:");
            foreach (var cat in categoryAnalysis.OrderByDescending(c => c.Value))
            {
                logger.Trace("MAM_CATEGORY_ANALYSIS: {0} ({1} torrents)", cat.Key, cat.Value);
            }

            foreach (var row in data)
            {
                // Handle both JObject (direct) and JArray (wrapped) formats
                JObject torrent = null;

                if (row is JObject directTorrent)
                {
                    // Direct JObject format (current MAM API)
                    torrent = directTorrent;
                }
                else if (row is JArray rowArray && rowArray.Count > 0)
                {
                    // Wrapped JArray format (legacy compatibility)
                    torrent = rowArray[0] as JObject;
                }

                if (torrent != null)
                {
                    var release = ParseTorrent(torrent);
                    if (release != null)
                    {
                        torrentInfos.Add(release);
                    }
                }
            }

            var reportedTotal = jsonResponse.Value<int?>("found") ?? jsonResponse.Value<int?>("total_found") ?? jsonResponse.Value<int?>("total");
            logger.Debug(
                "MAM API parsed {0} of {1} reported torrents (narrator={2}, duration={3}, structuredMediaInfo={4})",
                torrentInfos.Count,
                reportedTotal ?? data.Count(),
                torrentInfos.Count(release => !string.IsNullOrWhiteSpace(release.Narrator)),
                torrentInfos.Count(release => !string.IsNullOrWhiteSpace(release.Duration)),
                data.Count(HasStructuredMediaInfo));

            return torrentInfos.ToArray();
        }

        private TorrentInfo ParseTorrent(JToken torrent)
        {
            // Handle id as number, convert to string
            var id = torrent.Value<int?>("id")?.ToString();
            var title = GetTorrentTitle(torrent);

            var size = ParseSize(torrent.Value<string>("size"));
            var seeders = torrent.Value<int>("seeders");
            var leechers = torrent.Value<int>("leechers");
            var added = ParseDate(torrent.Value<string>("added"));

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            // Extract enhanced metadata
            var mediaInfo = ParseMediaInfo(torrent);
            var duration = ExtractDuration(torrent, mediaInfo);
            var isGraphicAudio = IsGraphicAudio(torrent);
            var narrator = ExtractNarrator(torrent, isGraphicAudio);

            // MAM_TRACE: Log metadata extraction for each torrent
            var logger = NLog.LogManager.GetCurrentClassLogger();
            logger.Trace("MAM_METADATA_EXTRACTION: ID={0}, Title='{1}'", id, title);
            logger.Trace("MAM_DETECTED_DURATION: '{0}' {1}", duration ?? "NOT_FOUND", duration != null ? "yes" : "no");
            logger.Trace("MAM_DETECTED_NARRATOR: '{0}' {1}", narrator ?? "NOT_FOUND", narrator != null ? "yes" : "no");
            logger.Trace("MAM_DETECTED_GRAPHICAUDIO: {0} {1}", isGraphicAudio ? "YES" : "NO", isGraphicAudio ? "yes" : "no");

            // Log source data for debugging
            var tags = torrent.Value<string>("tags");
            var description = torrent.Value<string>("description");
            var narratorInfo = torrent.Value<string>("narrator_info");

            logger.Trace("MAM_SOURCE_TAGS: '{0}'", tags ?? "EMPTY");
            logger.Trace("MAM_SOURCE_NARRATOR_INFO: '{0}'", narratorInfo ?? "EMPTY");
            logger.Trace("MAM_SOURCE_DESCRIPTION_PREVIEW: '{0}'",
                string.IsNullOrWhiteSpace(description) ? "EMPTY" :
                description.Length > 200 ? description.Substring(0, 200) + "..." : description);

            // Extract additional metadata first
            var authorInfo = ExtractAuthorFromJson(torrent.Value<string>("author_info"));

            // Get file format for quality detection
            var fileType = GetFileType(torrent, mediaInfo);
            var codec = mediaInfo?["Audio1"]?.Value<string>("Format");
            logger.Trace("[MAM_TORRENT] ID={0}, Title='{1}', FileType='{2}', Author='{3}'", id, title, fileType ?? "NULL", authorInfo ?? "NULL");
            var categoryId = ReadNullableInt(torrent, "category");
            var mainCategory = ReadNullableInt(torrent, "main_cat");
            var mediaType = ReadNullableInt(torrent, "mediatype");
            var categoryName = torrent.Value<string>("catname");
            var timesCompleted = torrent.Value<int?>("times_completed") ?? 0;
            var commentCount = torrent.Value<int?>("comments") ?? 0;

            var release = new TorrentInfo
            {
                Title = title, // Use original MAM title to preserve part info
                InfoUrl = $"{_settings.BaseUrl.TrimEnd('/')}/t/{id}",
                Guid = $"MAM-{id}",
                DownloadUrl = BuildDownloadUrl(id, torrent),
                PublishDate = added.DateTime,
                Size = size,
                Seeders = seeders,
                Peers = leechers + seeders,

                // Set basic metadata if available
                Author = authorInfo,
                Categories = categoryId.HasValue ? new List<int> { categoryId.Value } : null,
                CommentUrl = commentCount > 0 ? $"{_settings.BaseUrl.TrimEnd('/')}/t/{id}#comments" : null,

                // Set enhanced metadata properties
                Duration = duration,
                Narrator = narrator,
                IsGraphicAudio = isGraphicAudio,
                Codec = codec,
                FileType = fileType,
                Languages = BuildReleaseLanguages(torrent),
                MediaType = mediaType,
                MainCategory = mainCategory,
                CategoryName = categoryName
            };

            // Set indexer flags based on torrent properties
            var flags = ParseIndexerFlags(torrent);
            if (flags != 0)
            {
                release.IndexerFlags = flags;
            }

            logger.Trace("MAM_RELEASE_CREATED: Title='{0}', FileType='{1}', Flags={2}", release.Title, release.FileType ?? "NULL", flags);

            return release;
        }

        private IndexerFlags ParseIndexerFlags(JToken torrent)
        {
            var flags = (IndexerFlags)0;

            // Primary detection: Check JSON boolean flags first
            var isVipFromJson = ReadBooleanFlag(torrent, "vip");
            var isFreeVipFromJson = ReadBooleanFlag(torrent, "fl_vip");
            var isFreeleechFromJson = ReadBooleanFlag(torrent, "free") ||
                                     ReadBooleanFlag(torrent, "personal_freeleech") ||
                                     (isFreeVipFromJson && !isVipFromJson);

            // Check for numeric freeleech factors (downloadvolumefactor field)
            var downloadFactor = ReadNullableFloat(torrent, "downloadvolumefactor");
            var uploadFactor = ReadNullableFloat(torrent, "uploadvolumefactor");
            var isPartialFreeleechFromJson = false;

            if (downloadFactor.HasValue)
            {
                if (downloadFactor.Value == 0.0f)
                {
                    isFreeleechFromJson = true;
                }
                else if (downloadFactor.Value == 0.25f)
                {
                    flags |= IndexerFlags.Freeleech75; // 75% off, pay 25%
                    isPartialFreeleechFromJson = true;
                }
                else if (downloadFactor.Value == 0.5f)
                {
                    flags |= IndexerFlags.Halfleech; // 50% off
                    isPartialFreeleechFromJson = true;
                }
                else if (downloadFactor.Value == 0.75f)
                {
                    flags |= IndexerFlags.Freeleech25; // 25% off, pay 75%
                    isPartialFreeleechFromJson = true;
                }
            }

            // Check for double upload (uploadvolumefactor field)
            if (uploadFactor.HasValue && uploadFactor.Value == 2.0f)
            {
                flags |= IndexerFlags.DoubleUpload;
            }

            // Enhanced detection: Also check text fields for freeleech indicators
            var searchFields = GetFlagSearchFields(torrent).ToList();
            var torrentTitle = GetTorrentTitle(torrent);

            var isFreeleechFromText = false;
            var isPartialFreeleechFromText = false;
            var isDoubleUploadFromText = false;

            foreach (var field in searchFields)
            {
                if (!isFreeleechFromText && FreeleechPattern.IsMatch(field))
                {
                    isFreeleechFromText = true;
                }

                if (!isPartialFreeleechFromText && PartialFreeleechPattern.IsMatch(field))
                {
                    isPartialFreeleechFromText = true;
                }

                if (!isDoubleUploadFromText && DoubleUploadPattern.IsMatch(field))
                {
                    isDoubleUploadFromText = true;
                }

                // Early exit if all patterns have been found
                if (isFreeleechFromText && isPartialFreeleechFromText && isDoubleUploadFromText)
                {
                    break;
                }
            }

            // Apply text-based double upload detection
            if (isDoubleUploadFromText && !uploadFactor.HasValue)
            {
                flags |= IndexerFlags.DoubleUpload;
            }

            // Set flags based on both JSON and text detection
            var jsonFree = ReadBooleanFlag(torrent, "free");
            var jsonPersonalFree = ReadBooleanFlag(torrent, "personal_freeleech");
            var jsonFreeVip = ReadBooleanFlag(torrent, "fl_vip");
            var jsonVip = ReadBooleanFlag(torrent, "vip");

            var isVipExclusive = jsonVip;
            var isGloballyFreeleech = jsonFree ||
                                      jsonPersonalFree ||
                                      (jsonFreeVip && !isVipExclusive) ||
                                      downloadFactor is 0.0f ||
                                      isFreeleechFromText;

            if (isGloballyFreeleech || (_settings.IsVip && isVipExclusive))
            {
                flags |= IndexerFlags.Freeleech;
            }

            if (_settings.IsVip && isVipExclusive)
            {
                flags |= IndexerFlags.VipFreeleech;
            }

            if (isVipExclusive)
            {
                flags |= IndexerFlags.VipExclusive;
            }

            // Log enhanced detection for debugging
            var logger = NLog.LogManager.GetCurrentClassLogger();
            var id = torrent.Value<int?>("id")?.ToString();

            // Log all detection results for comprehensive debugging
            logger.Trace("MAM_FLAGS_DETECTION: Torrent {0} - JSON: free={1}, personal_freeleech={2}, fl_vip={3}, vip={4}, downloadvolumefactor={5}, uploadvolumefactor={6}",
                id,
                jsonFree,
                jsonPersonalFree,
                jsonFreeVip,
                jsonVip,
                downloadFactor,
                uploadFactor);

            logger.Trace("MAM_FLAGS_DETECTION: Torrent {0} - Text Detection: Freeleech={1}, PartialFreeleech={2}, DoubleUpload={3}", id, isFreeleechFromText, isPartialFreeleechFromText, isDoubleUploadFromText);

            if (isFreeleechFromText && !isFreeleechFromJson)
            {
                logger.Trace("MAM_FREELEECH_TEXT_DETECTION: Enhanced Freeleech detection found in text for torrent {0} '{1}'", id, torrentTitle);
            }

            if (isPartialFreeleechFromText && !isPartialFreeleechFromJson)
            {
                logger.Trace("MAM_PARTIAL_FREELEECH_TEXT_DETECTION: Enhanced partial freeleech detection found in text for torrent {0} '{1}'", id, torrentTitle);
            }

            if (isDoubleUploadFromText && !uploadFactor.HasValue)
            {
                logger.Trace("MAM_DOUBLE_UPLOAD_TEXT_DETECTION: Enhanced double upload detection found in text for torrent {0} '{1}'", id, torrentTitle);
            }

            if (downloadFactor.HasValue)
            {
                logger.Trace("MAM_DOWNLOAD_FACTOR: Torrent {0} has downloadvolumefactor={1}", id, downloadFactor.Value);
            }

            if (uploadFactor.HasValue)
            {
                logger.Trace("MAM_UPLOAD_FACTOR: Torrent {0} has uploadvolumefactor={1}", id, uploadFactor.Value);
            }

            // Log final flag assignment
            if (flags != 0)
            {
                logger.Trace("MAM_FLAGS_ASSIGNED: Torrent {0} assigned flags: {1}", id, flags);
            }

            return flags;
        }

        private static IEnumerable<string> GetFlagSearchFields(JToken torrent)
        {
            foreach (var fieldName in new[] { "title", "name", "tags", "description", "catname", "main_cat" })
            {
                var value = torrent.Value<string>(fieldName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }

        private static string GetFileType(JToken torrent, JObject mediaInfo)
        {
            return torrent.Value<string>("filetype") ??
                   torrent.Value<string>("filetypes") ??
                   mediaInfo?["Audio1"]?.Value<string>("Format");
        }

        private static List<Language> BuildReleaseLanguages(JToken torrent)
        {
            var languages = new List<Language>();
            var langCode = torrent.Value<string>("lang_code");

            if (MyAnonaMouseLanguageMapper.TryGetLanguage(langCode, out var language, out _))
            {
                languages.Add(language);
            }

            return languages;
        }

        private static bool IsGloballyFreeleechTorrent(JToken torrent)
        {
            if (ReadBooleanFlag(torrent, "free") ||
                ReadBooleanFlag(torrent, "personal_freeleech") ||
                (ReadBooleanFlag(torrent, "fl_vip") && !IsVipExclusiveTorrent(torrent)) ||
                ReadNullableFloat(torrent, "downloadvolumefactor") is 0.0f)
            {
                return true;
            }

            return !IsVipExclusiveTorrent(torrent) &&
                   GetFlagSearchFields(torrent).Any(field => FreeleechPattern.IsMatch(field));
        }

        private static bool IsVipExclusiveTorrent(JToken torrent)
        {
            return ReadBooleanFlag(torrent, "vip");
        }

        private static bool ReadBooleanFlag(JToken torrent, string fieldName)
        {
            var token = torrent?[fieldName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>() != 0;
            }

            var value = token.Value<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("y", StringComparison.OrdinalIgnoreCase);
        }

        private static float? ReadNullableFloat(JToken torrent, string fieldName)
        {
            var token = torrent?[fieldName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return token.Value<float>();
            }

            var value = token.Value<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static int? ReadNullableInt(JToken torrent, string fieldName)
        {
            var token = torrent?[fieldName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            var value = token.Value<string>()?.Trim();
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static JObject ParseMediaInfo(JToken torrent)
        {
            var token = torrent?["mediainfo"];
            if (token is JObject mediaInfoObject)
            {
                return mediaInfoObject;
            }

            var raw = token?.Value<string>();
            if (string.IsNullOrWhiteSpace(raw) || raw == "{}")
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<JObject>(raw);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool HasStructuredMediaInfo(JToken row)
        {
            var torrent = row is JArray rowArray ? rowArray.FirstOrDefault() : row;
            return ParseMediaInfo(torrent) != null;
        }

        private string ExtractDuration(JToken torrent, JObject mediaInfo)
        {
            try
            {
                var structuredDuration = ParseStructuredDuration(mediaInfo?["General"]?.Value<string>("Duration"));
                if (!string.IsNullOrWhiteSpace(structuredDuration))
                {
                    return structuredDuration;
                }

                // Priority order after structured media info: tags → description → title
                var searchFields = new List<(string Text, bool AllowLoosePatterns)>();

                // Check tags field first (most reliable)
                var tags = torrent.Value<string>("tags");
                if (!string.IsNullOrWhiteSpace(tags))
                {
                    searchFields.Add((tags, true));
                }

                // Check description field
                var description = torrent.Value<string>("description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    searchFields.Add((description, false));
                }

                // Check title as fallback
                var titleText = GetTorrentTitle(torrent);
                if (!string.IsNullOrWhiteSpace(titleText))
                {
                    searchFields.Add((titleText, true));
                }

                foreach (var (field, allowLoosePatterns) in searchFields)
                {
                    foreach (var pattern in DurationPatterns)
                    {
                        var match = pattern.Match(field);
                        if (match.Success)
                        {
                            return ParseDurationMatch(match, pattern);
                        }
                    }

                    if (!allowLoosePatterns)
                    {
                        continue;
                    }

                    foreach (var pattern in LooseDurationPatterns)
                    {
                        var match = pattern.Match(field);
                        if (match.Success)
                        {
                            return ParseDurationMatch(match, pattern);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore extraction errors, return null for unknown duration
            }

            return null;
        }

        private static string ParseStructuredDuration(string duration)
        {
            var match = Regex.Match(duration ?? string.Empty, @"^(?<hours>\d+):(?<minutes>[0-5]\d):(?<seconds>[0-5]\d)$");
            if (!match.Success ||
                !int.TryParse(match.Groups["hours"].Value, out var hours) ||
                !int.TryParse(match.Groups["minutes"].Value, out var minutes) ||
                hours > 1000)
            {
                return null;
            }

            return $"{hours}h {minutes:D2}m";
        }

        private string ParseDurationMatch(Match match, Regex pattern)
        {
            try
            {
                // Handle different pattern formats
                if (match.Groups.Count >= 3 && int.TryParse(match.Groups[1].Value, out var hours) && int.TryParse(match.Groups[2].Value, out var minutes))
                {
                    // Two-group patterns: hours and minutes
                    if (hours >= 0 && hours <= 200 && minutes >= 0 && minutes <= 59)
                    {
                        return $"{hours}h {minutes:D2}m";
                    }
                }
                else if (match.Groups.Count >= 2 && int.TryParse(match.Groups[1].Value, out var singleHours))
                {
                    // Single-group patterns: hours only
                    if (singleHours >= 0 && singleHours <= 200)
                    {
                        return $"{singleHours}h 00m";
                    }
                }
            }
            catch (Exception)
            {
                // Ignore parsing errors
            }

            return null;
        }

        private static string GetTorrentTitle(JToken torrent)
        {
            var title = torrent.Value<string>("title") ?? torrent.Value<string>("name");
            return CleanMetadataText(title) ?? title;
        }

        private string ExtractNarrator(JToken torrent, bool isGraphicAudio = false)
        {
            try
            {
                // If this is GraphicAudio, return "GraphicAudio" as the narrator
                if (isGraphicAudio)
                {
                    return "GraphicAudio";
                }

                // Priority order: narrator_info → tags → description

                // Check narrator_info field first (most reliable)
                var narratorInfo = torrent.Value<string>("narrator_info");
                if (!string.IsNullOrWhiteSpace(narratorInfo))
                {
                    try
                    {
                        // narrator_info is a JSON string like: "{\"19601\": \"Dominique Collignon-Maurin\"}"
                        var narratorDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(narratorInfo);
                        if (narratorDict != null && narratorDict.Count > 0)
                        {
                            // Get all narrator names and join them
                            var narrators = narratorDict.Values
                                .Select(CleanNarratorName)
                                .Where(n => !string.IsNullOrWhiteSpace(n))
                                .ToList();
                            if (narrators.Count == 1)
                            {
                                return narrators[0];
                            }
                            else if (narrators.Count > 1)
                            {
                                // Multiple narrators
                                return string.Join(", ", narrators.Take(2)) +
                                       (narrators.Count > 2 ? $" +{narrators.Count - 2}" : "");
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // If it's not valid JSON, treat it as a plain string
                        var cleanedNarrator = CleanNarratorName(narratorInfo);
                        if (!string.IsNullOrWhiteSpace(cleanedNarrator))
                        {
                            return cleanedNarrator;
                        }
                    }
                }

                // Fallback to regex extraction from tags and description
                var searchFields = new List<string>();

                var tags = torrent.Value<string>("tags");
                if (!string.IsNullOrWhiteSpace(tags))
                {
                    searchFields.Add(CleanMetadataText(tags));
                }

                var description = torrent.Value<string>("description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    searchFields.Add(CleanMetadataText(description));
                }

                foreach (var field in searchFields)
                {
                    foreach (var pattern in NarratorPatterns)
                    {
                        var match = pattern.Match(field);
                        if (match.Success && match.Groups.Count >= 2)
                        {
                            var narrator = CleanNarratorName(match.Groups[1].Value);
                            if (!string.IsNullOrWhiteSpace(narrator))
                            {
                                return narrator;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore extraction errors
            }

            return null;
        }

        private string CleanNarratorName(string narrator)
        {
            if (string.IsNullOrWhiteSpace(narrator))
            {
                return null;
            }

            narrator = CleanMetadataText(narrator);

            if (IsProseOrTechnicalText(narrator))
            {
                return null;
            }

            // Remove common prefixes if they slipped through
            narrator = Regex.Replace(narrator, @"^(?:Narrated by|Read by|Voice by)\s*", "", RegexOptions.IgnoreCase);

            // Remove parenthetical info like "(Narrator)"
            narrator = Regex.Replace(narrator, @"\s*\([^)]*\)\s*", " ");

            // Handle multiple narrators - if comma separated, take first narrator + count
            if (narrator.Contains(","))
            {
                var narrators = narrator.Split(',').Select(n => n.Trim()).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
                if (narrators.Length > 1)
                {
                    return narrators.Length > 2 ? $"{narrators[0]} +{narrators.Length - 1}" : $"{narrators[0]}, {narrators[1]}";
                }

                narrator = narrators.FirstOrDefault()?.Trim();
            }

            // Handle "and" separated narrators
            if (narrator.Contains(" and "))
            {
                var narrators = narrator.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).ToArray();
                if (narrators.Length > 1)
                {
                    return narrators.Length > 2 ? $"{narrators[0]} +{narrators.Length - 1}" : $"{narrators[0]}, {narrators[1]}";
                }

                narrator = narrators.FirstOrDefault()?.Trim();
            }

            // Final cleanup
            narrator = narrator?.Trim();

            // Validate result
            if (string.IsNullOrWhiteSpace(narrator) || narrator.Length < 2 || narrator.Length > 100)
            {
                return null;
            }

            if (IsProseOrTechnicalText(narrator))
            {
                return null;
            }

            // Filter out obvious non-names
            if (Regex.IsMatch(narrator, @"^(?:unknown|n/a|test|null|none)$", RegexOptions.IgnoreCase))
            {
                return null;
            }

            // Keep GraphicAudio indicators since they'll be handled by the ExtractNarrator method now
            // This filtering is no longer needed as GA detection and narrator assignment are coordinated
            return narrator;
        }

        private static string CleanMetadataText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var decoded = WebUtility.HtmlDecode(value);
            decoded = HtmlTagRegex.Replace(decoded, " ");
            return WhitespaceRegex.Replace(decoded, " ").Trim();
        }

        private static bool IsProseOrTechnicalText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (value.Contains("<") || value.Contains(">") || value.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!NarratorLetterRegex.IsMatch(value))
            {
                return true;
            }

            if (NarratorProseTokenRegex.IsMatch(value))
            {
                return true;
            }

            return NarratorWordRegex.Matches(value).Count > 8;
        }

        private bool IsGraphicAudio(JToken torrent)
        {
            try
            {
                // Check multiple fields for GraphicAudio indicators
                var searchFields = new List<string>();

                // Add all fields that the user requested for GA detection
                var tags = torrent.Value<string>("tags");
                if (!string.IsNullOrWhiteSpace(tags))
                {
                    searchFields.Add(tags);
                }

                var description = torrent.Value<string>("description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    searchFields.Add(description);
                }

                var titleField = torrent.Value<string>("title");
                if (!string.IsNullOrWhiteSpace(titleField))
                {
                    searchFields.Add(titleField);
                }

                var narratorInfo = torrent.Value<string>("narrator_info");
                if (!string.IsNullOrWhiteSpace(narratorInfo))
                {
                    searchFields.Add(narratorInfo);
                }

                // Add author field to GA detection search scope as requested
                var authorInfo = torrent.Value<string>("author_info");
                if (!string.IsNullOrWhiteSpace(authorInfo))
                {
                    searchFields.Add(authorInfo);
                }

                foreach (var field in searchFields)
                {
                    var match = GraphicAudioPattern.Match(field);
                    if (match.Success)
                    {
                        // Log which term was matched for debugging
                        var logger = NLog.LogManager.GetCurrentClassLogger();
                        var id = torrent.Value<int?>("id")?.ToString();
                        logger.Trace("MAM_GA_DETECTION: Found GraphicAudio indicator '{0}' for torrent {1}", match.Value, id);
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Ignore detection errors
            }

            return false;
        }

        private string ExtractAuthorFromJson(string authorInfo)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(authorInfo))
                {
                    return null;
                }

                // author_info is a JSON string like:
                // "{\"7640\": \"J K Rowling\"}" or
                // "{\"1\": \"Brian Herbert\", \"2\": \"Kevin J Anderson\"}"
                var authorDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(authorInfo);
                if (authorDict != null && authorDict.Count > 0)
                {
                    var authorNames = authorDict.Values
                        .Select(CleanMetadataText)
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (authorNames.Count == 0)
                    {
                        return null;
                    }

                    return string.Join(", ", authorNames);
                }
            }
            catch (Exception)
            {
                // Ignore JSON parsing errors
            }

            return null;
        }

        private long ParseSize(string sizeString)
        {
            if (string.IsNullOrWhiteSpace(sizeString))
            {
                return 0;
            }

            try
            {
                // Handle formats like "823.2 MiB", "1.1 GiB", "657.6 MiB", "1,016.6 KiB"
                var cleanSize = sizeString.Replace(",", "").Trim();
                var parts = cleanSize.Split(' ');

                if (parts.Length != 2)
                {
                    return 0;
                }

                if (!double.TryParse(parts[0], out var value))
                {
                    return 0;
                }

                var unit = parts[1].ToUpperInvariant();
                var multiplier = unit switch
                {
                    "KIB" => 1024L,
                    "MIB" => 1024L * 1024L,
                    "GIB" => 1024L * 1024L * 1024L,
                    "TIB" => 1024L * 1024L * 1024L * 1024L,
                    "KB" => 1000L,
                    "MB" => 1000L * 1000L,
                    "GB" => 1000L * 1000L * 1000L,
                    "TB" => 1000L * 1000L * 1000L * 1000L,
                    _ => 1L
                };

                return (long)(value * multiplier);
            }
            catch (Exception)
            {
                // If parsing fails, return 0
                return 0;
            }
        }

        private DateTimeOffset ParseDate(string dateString)
        {
            if (string.IsNullOrWhiteSpace(dateString))
            {
                return DateTimeOffset.UtcNow;
            }

            try
            {
                // Handle format: "2017-07-23 09:29:54"
                if (DateTime.TryParse(dateString, out var dateTime))
                {
                    return new DateTimeOffset(dateTime, TimeSpan.Zero);
                }
            }
            catch (Exception)
            {
                // If parsing fails, use current time
            }

            return DateTimeOffset.UtcNow;
        }

        private string BuildDownloadUrl(string id, JToken torrent)
        {
            var url = $"{_settings.BaseUrl.TrimEnd('/')}/tor/download.php?tid={id}";
            if (!IsGloballyFreeleechTorrent(torrent) && !IsVipExclusiveTorrent(torrent))
            {
                url += "&canUseToken=true";
            }

            if (ReadNullableInt(torrent, "main_cat") == 13)
            {
                url += "&isAudiobook=true";
            }

            return url;
        }
    }
}
