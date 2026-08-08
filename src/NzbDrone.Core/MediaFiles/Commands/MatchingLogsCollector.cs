using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public interface IMatchingLogsCollector
    {
        List<MatchingLogEntry> Collect(SendMatchingLogsCommand message);
    }

    public class MatchingLogsCollector : IMatchingLogsCollector
    {
        private readonly IMatchingUploadLogger _matchingLogger;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public MatchingLogsCollector(IMatchingUploadLogger matchingLogger, IMediaFileService mediaFileService, Logger logger)
        {
            _matchingLogger = matchingLogger;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public List<MatchingLogEntry> Collect(SendMatchingLogsCommand message)
        {
            var specificFilePaths = ResolveSpecificFilePaths(message, out var hasFileScope);

            if (hasFileScope && !specificFilePaths.Any())
            {
                _logger.Debug("Matching logs upload file scope resolved to zero current unmapped files");
                return new List<MatchingLogEntry>();
            }

            var logReadLimit = hasFileScope ? int.MaxValue : message.MaxEntries * 2;
            var allLogs = _matchingLogger.GetRecentLogs(logReadLimit);

            // MinutesBack takes precedence over DaysBack so the UI can preview the same rolling window that upload uses.
            if (message.MinutesBack.HasValue && message.MinutesBack.Value > 0)
            {
                var cutoffTimestamp = DateTimeOffset.UtcNow.AddMinutes(-message.MinutesBack.Value).ToUnixTimeSeconds();
                allLogs = allLogs.Where(log => log.Timestamp >= cutoffTimestamp).ToList();
            }
            else if (message.DaysBack > 0)
            {
                var cutoffTimestamp = DateTimeOffset.UtcNow.AddDays(-message.DaysBack).ToUnixTimeSeconds();
                allLogs = allLogs.Where(log => log.Timestamp >= cutoffTimestamp).ToList();
            }

            if (hasFileScope)
            {
                var specificPathKeys = BuildMatchingLogPathKeys(specificFilePaths);
                var specificBasenames = BuildMatchingLogBasenameKeys(specificFilePaths);

                allLogs = allLogs.Where(log => LogEntryMatchesPathScope(log.FileName, specificPathKeys, specificBasenames)).ToList();
            }

            if (message.FailedMatchesOnly)
            {
                allLogs = allLogs.Where(log =>
                    log.MatchResult != null && !log.MatchResult.Success
                ).ToList();
            }

            return allLogs.Take(message.MaxEntries).ToList();
        }

        private List<string> ResolveSpecificFilePaths(SendMatchingLogsCommand message, out bool hasFileScope)
        {
            if (message.UnmappedFiles == null)
            {
                hasFileScope = message.SpecificFilePaths?.Any() == true;
                return message.SpecificFilePaths?
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(PathEqualityComparer.Instance)
                    .ToList() ?? new List<string>();
            }

            hasFileScope = true;

            return UnmappedFileSelectionResolver.ResolvePaths(
                _mediaFileService,
                message.UnmappedFiles,
                message.MediaType,
                _logger,
                "Matching logs",
                allowEmptySelected: true);
        }

        private static HashSet<string> BuildMatchingLogPathKeys(IEnumerable<string> paths)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                foreach (var key in GetMatchingLogPathKeys(path))
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private static HashSet<string> BuildMatchingLogBasenameKeys(IEnumerable<string> paths)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                var basename = GetMatchingLogBasename(path);
                if (!string.IsNullOrWhiteSpace(basename))
                {
                    keys.Add(basename);
                }
            }

            return keys;
        }

        private static bool LogEntryMatchesPathScope(string logFileName, HashSet<string> pathKeys, HashSet<string> basenameKeys)
        {
            if (string.IsNullOrWhiteSpace(logFileName))
            {
                return false;
            }

            var normalized = NormalizeMatchingLogPath(logFileName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (GetMatchingLogPathKeys(normalized).Any(pathKeys.Contains))
            {
                return true;
            }

            // Older matching logs may only have a basename. Do not use basename fallback
            // for parent/filename entries, or common chapter names like 01.mp3 over-match.
            return !normalized.Contains("/") && basenameKeys.Contains(normalized);
        }

        private static IEnumerable<string> GetMatchingLogPathKeys(string path)
        {
            var normalized = NormalizeMatchingLogPath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            yield return normalized;

            var segments = normalized
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (!segments.Any())
            {
                yield break;
            }

            if (segments.Length >= 2)
            {
                yield return $"{segments[^2]}/{segments[^1]}";
            }
        }

        private static string GetMatchingLogBasename(string path)
        {
            var normalized = NormalizeMatchingLogPath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var segments = normalized
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            return segments.Any() ? segments[^1] : null;
        }

        private static string NormalizeMatchingLogPath(string path)
        {
            return path?.Replace('\\', '/').Trim().Trim('/');
        }
    }
}
