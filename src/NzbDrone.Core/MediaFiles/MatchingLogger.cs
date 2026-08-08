using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMatchingUploadLogger
    {
        void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null);
        void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null);
        void LogFinalDecision(string filePath,
            MatchResult matchResult,
            Dictionary<string, List<string>> extractedTags = null,
            int? commandId = null,
            string correlationId = null)
        {
            LogFinalDecision(filePath,
                matchResult?.Decision,
                matchResult?.Reason,
                extractedTags,
                matchResult?.AuthorMatched,
                matchResult?.BookMatched,
                matchResult?.EditionMatched,
                matchResult?.Rejections,
                commandId,
                correlationId);
        }
        void LogFinalDecision(string filePath,
            string decision,
            string reason,
            Dictionary<string, List<string>> extractedTags = null,
            string authorMatched = null,
            string bookMatched = null,
            string editionMatched = null,
            List<CandidateRejection> rejections = null,
            int? commandId = null,
            string correlationId = null);
        List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000);
        void ClearLogs();
    }

    public class MatchingLogger : IMatchingUploadLogger
    {
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly object _lockObject = new object();
        private DateTime _lastCleanup = DateTime.MinValue;

        private const int MaxTagKeys = 64;
        private const int MaxTagValuesPerKey = 5;
        private const int MaxTagValueChars = 200;
        private const int MaxTotalTagChars = 8 * 1024;

        private static readonly HashSet<string> PathDerivedTagKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "path",
            "folder",
            "pathcomponents",
            "filename"
        };

        public MatchingLogger(
            IAppFolderInfo appFolderInfo,
            IDiskProvider diskProvider,
            Logger logger)
        {
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null)
        {
            try
            {
                var entry = new MatchingLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    CommandId = commandId,
                    CorrelationId = correlationId,
                    FileName = SanitizeFilePath(filePath),
                    MediaType = DetermineMediaType(filePath),
                    ExtractedTags = CleanTags(extractedTags),
                    MatchResult = result
                };

                WriteLogEntry(entry);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to log matching attempt for file: {0}", filePath);
            }
        }

        public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null)
        {
            try
            {
                var responseText = response ?? string.Empty;
                var success = responseText.StartsWith("SUCCESS: Found", StringComparison.OrdinalIgnoreCase);

                var entry = new MatchingLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    CommandId = commandId,
                    CorrelationId = correlationId,
                    FileName = !string.IsNullOrWhiteSpace(filePath) ? SanitizeFilePath(filePath) : "V5_API_REQUEST",
                    MediaType = mediaType,
                    ExtractedTags = CleanTags(tags),
                    MatchResult = new MatchResult
                    {
                        Success = success,
                        Reason = responseText.Length > 200 ? "V5_RESPONSE_TRUNCATED" : responseText,
                        V5Query = query,
                        V5Response = responseText.Length > 500 ? responseText.Substring(0, 500) + "..." : responseText
                    }
                };

                WriteLogEntry(entry);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to log V5 request");
            }
        }

        public void LogFinalDecision(string filePath,
            MatchResult matchResult,
            Dictionary<string, List<string>> extractedTags = null,
            int? commandId = null,
            string correlationId = null)
        {
            try
            {
                var entry = new MatchingLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    CommandId = commandId,
                    CorrelationId = correlationId,
                    FileName = SanitizeFilePath(filePath),
                    MediaType = DetermineMediaType(filePath),
                    ExtractedTags = CleanTags(extractedTags),
                    MatchResult = matchResult ?? new MatchResult()
                };

                WriteLogEntry(entry);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to log final decision for file: {0}", filePath);
            }
        }

        public void LogFinalDecision(string filePath,
            string decision,
            string reason,
            Dictionary<string, List<string>> extractedTags = null,
            string authorMatched = null,
            string bookMatched = null,
            string editionMatched = null,
            List<CandidateRejection> rejections = null,
            int? commandId = null,
            string correlationId = null)
        {
            LogFinalDecision(filePath,
                new MatchResult
                {
                    Success = decision == "MATCHED",
                    Reason = reason,
                    AuthorMatched = authorMatched,
                    BookMatched = bookMatched,
                    EditionMatched = editionMatched,
                    Decision = decision,
                    Rejections = rejections
                },
                extractedTags,
                commandId,
                correlationId);
        }

        public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000)
        {
            var logs = new List<MatchingLogEntry>();
            var logFilePath = GetCurrentLogFilePath();

            _logger.Debug("Attempting to read matching logs from: {0}", logFilePath);

            if (!_diskProvider.FileExists(logFilePath))
            {
                _logger.Debug("Log file does not exist: {0}", logFilePath);
                return logs;
            }

            try
            {
                lock (_lockObject)
                {
                    _logger.Debug("Reading matching log file...");
                    var text = _diskProvider.ReadAllText(logFilePath);
                    
                    if (string.IsNullOrEmpty(text))
                    {
                        _logger.Debug("Log file is empty");
                        return logs;
                    }
                    
                    var lines = text.Split(new[] { Environment.NewLine, "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    _logger.Debug("Found {0} lines in log file", lines.Length);
                    
                    var count = 0;

                    // Read from end to get most recent entries first
                    for (int i = lines.Length - 1; i >= 0 && count < maxEntries; i--)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;

                        try
                        {
                            var entry = JsonConvert.DeserializeObject<MatchingLogEntry>(lines[i]);
                            if (entry != null)
                            {
                                logs.Add(entry);
                                count++;
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.Debug(ex, "Failed to deserialize log entry: {0}", lines[i].Length > 100 ? lines[i].Substring(0, 100) + "..." : lines[i]);
                        }
                    }
                    
                    _logger.Debug("Successfully loaded {0} matching log entries out of {1} requested from {2} total lines", logs.Count, maxEntries, lines.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to read matching logs from: {0}", logFilePath);
            }

            return logs;
        }

        public void ClearLogs()
        {
            try
            {
                var logDir = GetLogDirectory();
                if (_diskProvider.FolderExists(logDir))
                {
                    var files = _diskProvider.GetFiles(logDir, false).Where(f => Path.GetExtension(f) == ".json");
                    foreach (var file in files)
                    {
                        _diskProvider.DeleteFile(file);
                    }
                }
                _logger.Info("Cleared all matching logs");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to clear matching logs");
            }
        }

        private void WriteLogEntry(MatchingLogEntry entry)
        {
            var logFilePath = GetCurrentLogFilePath();
            var logDir = Path.GetDirectoryName(logFilePath);

            if (!_diskProvider.FolderExists(logDir))
            {
                _diskProvider.CreateFolder(logDir);
            }

            var json = JsonConvert.SerializeObject(entry, Formatting.None);

            lock (_lockObject)
            {
                // Use proper file append instead of read-modify-write
                // This eliminates the O(n²) performance issue
                // Use disk provider + file abstraction for portability/testability.
                using (var writer = _diskProvider.GetFileInfo(logFilePath).AppendText())
                {
                    writer.Write(json);
                    writer.Write(Environment.NewLine);
                }
            }

            // Only cleanup logs once per day, not on every write
            // This eliminates 66 directory scans per book import
            var now = DateTime.UtcNow;
            if ((now - _lastCleanup).TotalHours > 24)
            {
                // Double-check pattern with volatile field for thread safety
                lock (_lockObject)
                {
                    if ((now - _lastCleanup).TotalHours > 24)
                    {
                        CleanupOldLogs();
                        _lastCleanup = now;
                    }
                }
            }
        }

        private string GetLogDirectory()
        {
            return Path.Combine(_appFolderInfo.GetLogFolder(), "matching");
        }

        private string GetCurrentLogFilePath()
        {
            var logDir = GetLogDirectory();
            var fileName = $"matching-{DateTime.UtcNow:yyyy-MM-dd}.json";
            return Path.Combine(logDir, fileName);
        }

        private void CleanupOldLogs()
        {
            try
            {
                var logDir = GetLogDirectory();
                if (!_diskProvider.FolderExists(logDir)) return;

                var cutoffDate = DateTime.UtcNow.AddDays(-7);
                var files = _diskProvider.GetFiles(logDir, false).Where(f => 
                    Path.GetFileName(f).StartsWith("matching-") && Path.GetExtension(f) == ".json");

                foreach (var file in files)
                {
                    var fileInfo = _diskProvider.GetFileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffDate)
                    {
                        _diskProvider.DeleteFile(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to cleanup old matching logs");
            }
        }

        private string SanitizeFilePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "unknown";

            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "unknown";
            }

            // Redact user/home components while still keeping enough context to distinguish duplicates.
            // Strategy:
            // - Normalize separators
            // - Strip common home prefixes (Windows: C:/Users/<user>/..., Unix: /home/<user>/..., /Users/<user>/...)
            // - Return only the last 2 segments (parent + filename)
            var normalized = filePath.Replace('\\', '/');
            var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            // Windows: <drive>:/Users/<user>/...
            var usersIndex = segments.FindIndex(s => s.Equals("Users", StringComparison.OrdinalIgnoreCase));
            if (usersIndex >= 0 && usersIndex + 2 < segments.Count)
            {
                // Drop everything up to and including the username segment.
                segments = segments.Skip(usersIndex + 2).ToList();
            }
            else if (segments.Count >= 3 &&
                     (segments[0].Equals("home", StringComparison.OrdinalIgnoreCase) ||
                      segments[0].Equals("Users", StringComparison.OrdinalIgnoreCase)))
            {
                // Unix/macOS: /home/<user>/... or /Users/<user>/...
                segments = segments.Skip(2).ToList();
            }

            if (segments.Count >= 2)
            {
                return $"{segments[^2]}/{segments[^1]}";
            }

            return fileName;
        }

        private string DetermineMediaType(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "unknown";

            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            if (MediaFileExtensions.AudioExtensions.Contains(extension))
            {
                return "audiobook";
            }

            if (MediaFileExtensions.TextExtensions.Contains(extension))
            {
                return "ebook";
            }

            return "unknown";
        }

        private static string SanitizeTagValue(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var sanitized = value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Replace("\u200B", "")
                .Replace("\u200C", "")
                .Replace("\u200D", "")
                .Replace("\uFEFF", "")
                .Trim();

            return sanitized.Length > maxChars ? sanitized.Substring(0, maxChars) + "..." : sanitized;
        }

        private Dictionary<string, List<string>> CleanTags(Dictionary<string, List<string>> tags)
        {
            if (tags == null) return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var cleaned = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var totalChars = 0;

            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag.Key))
                {
                    continue;
                }

                if (PathDerivedTagKeys.Contains(tag.Key))
                {
                    // Path-derived synthetic tags can leak user/share names.
                    // Uploads already drop these keys; keep local JSON logs safer too.
                    continue;
                }

                if (cleaned.Count >= MaxTagKeys || totalChars >= MaxTotalTagChars)
                {
                    break;
                }

                var values = tag.Value ?? new List<string>();
                var cleanedValues = new List<string>();
                foreach (var v in values)
                {
                    var val = SanitizeTagValue(v, MaxTagValueChars);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        totalChars += val.Length;
                        if (totalChars > MaxTotalTagChars)
                        {
                            break;
                        }

                        cleanedValues.Add(val);
                        if (cleanedValues.Count >= MaxTagValuesPerKey)
                        {
                            break;
                        }
                    }
                }
                if (cleanedValues.Count > 0)
                {
                    cleaned[tag.Key] = cleanedValues;
                }
            }

            return cleaned;
        }
    }

    public class MatchingLogEntry
    {
        [JsonProperty("t")]
        public long Timestamp { get; set; }

        [JsonProperty("cmd", NullValueHandling = NullValueHandling.Ignore)]
        public int? CommandId { get; set; }

        [JsonProperty("cid", NullValueHandling = NullValueHandling.Ignore)]
        public string CorrelationId { get; set; }

        [JsonProperty("f")]
        public string FileName { get; set; }

        [JsonProperty("m")]
        public string MediaType { get; set; }

        [JsonProperty("tags")]
        public Dictionary<string, List<string>> ExtractedTags { get; set; }

        [JsonProperty("result")]
        public MatchResult MatchResult { get; set; }
    }

    public class MatchResult
    {
        [JsonProperty("ok")]
        public bool Success { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("author")]
        public string AuthorMatched { get; set; }

        [JsonProperty("book")]
        public string BookMatched { get; set; }

        [JsonProperty("edition")]
        public string EditionMatched { get; set; }

        [JsonProperty("confidence")]
        public double? Confidence { get; set; }

        [JsonProperty("v5_query")]
        public string V5Query { get; set; }

        [JsonProperty("v5_response")]
        public string V5Response { get; set; }

        [JsonProperty("decision")]
        public string Decision { get; set; }

        [JsonProperty("matched_author", NullValueHandling = NullValueHandling.Ignore)]
        public string MatchedAuthor { get; set; }

        [JsonProperty("matched_book", NullValueHandling = NullValueHandling.Ignore)]
        public string MatchedBook { get; set; }

        [JsonProperty("matched_edition", NullValueHandling = NullValueHandling.Ignore)]
        public string MatchedEdition { get; set; }

        [JsonProperty("matched_via", NullValueHandling = NullValueHandling.Ignore)]
        public string MatchedVia { get; set; }

        [JsonProperty("provenance", NullValueHandling = NullValueHandling.Ignore)]
        public MatchProvenance Provenance { get; set; }

        [JsonProperty("matched_edition_title", NullValueHandling = NullValueHandling.Ignore)]
        public string MatchedEditionTitle { get; set; }

        [JsonProperty("matched_edition_narrators", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> MatchedEditionNarrators { get; set; }

        [JsonProperty("author_proved_by", NullValueHandling = NullValueHandling.Ignore)]
        public List<MatchEvidence> AuthorProvedBy { get; set; }

        [JsonProperty("book_proved_by", NullValueHandling = NullValueHandling.Ignore)]
        public List<MatchEvidence> BookProvedBy { get; set; }

        [JsonProperty("narrator_proved_by", NullValueHandling = NullValueHandling.Ignore)]
        public List<MatchEvidence> NarratorProvedBy { get; set; }

        [JsonProperty("pinned_target_result", NullValueHandling = NullValueHandling.Ignore)]
        public string PinnedTargetResult { get; set; }

        [JsonProperty("pinned_target_failure", NullValueHandling = NullValueHandling.Ignore)]
        public string PinnedTargetFailure { get; set; }

        [JsonProperty("path_fallback_used", NullValueHandling = NullValueHandling.Ignore)]
        public bool? PathFallbackUsed { get; set; }

        [JsonProperty("path_fallback_suppressed_reason", NullValueHandling = NullValueHandling.Ignore)]
        public string PathFallbackSuppressedReason { get; set; }

        [JsonProperty("outcome", NullValueHandling = NullValueHandling.Ignore)]
        public string Outcome { get; set; }

        [JsonProperty("outcome_reason", NullValueHandling = NullValueHandling.Ignore)]
        public string OutcomeReason { get; set; }

        [JsonProperty("rej", NullValueHandling = NullValueHandling.Ignore)]
        public List<CandidateRejection> Rejections { get; set; }
    }

    public class MatchEvidence
    {
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)]
        public string Source { get; set; }

        [JsonProperty("field", NullValueHandling = NullValueHandling.Ignore)]
        public string Field { get; set; }

        [JsonProperty("key", NullValueHandling = NullValueHandling.Ignore)]
        public string Key { get; set; }

        [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)]
        public string Value { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }

    public class CandidateRejection
    {
        [JsonProperty("p")]
        public string Phase { get; set; } // e.g. "scoped/main-tags" or "unscoped/with-path"

        [JsonProperty("eid", NullValueHandling = NullValueHandling.Ignore)]
        public int? EditionId { get; set; } // null for phase-level failures

        [JsonProperty("s", NullValueHandling = NullValueHandling.Ignore)]
        public double? Score { get; set; } // BM25 score if available

        [JsonProperty("t", NullValueHandling = NullValueHandling.Ignore)]
        public string TitleSnippet { get; set; } // max 80 chars

        [JsonProperty("r")]
        public string Reason { get; set; } // stable reason code

        [JsonProperty("d", NullValueHandling = NullValueHandling.Ignore)]
        public string Detail { get; set; } // human-readable, max 200 chars

        [JsonProperty("fd", NullValueHandling = NullValueHandling.Ignore)]
        public string FallbackDisposition { get; set; } // insufficient or contradictory embedded evidence
    }
}
