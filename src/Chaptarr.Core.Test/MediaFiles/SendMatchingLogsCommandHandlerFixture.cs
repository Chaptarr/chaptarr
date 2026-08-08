using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class SendMatchingLogsCommandHandlerFixture
    {
        private sealed class StubMatchingUploadLogger : IMatchingUploadLogger
        {
            public List<MatchingLogEntry> Logs { get; } = new List<MatchingLogEntry>();
            public int LastMaxEntries { get; private set; }

            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null)
            {
            }

            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null)
            {
            }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null)
            {
            }

            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000)
            {
                LastMaxEntries = maxEntries;
                return Logs.Take(maxEntries == int.MaxValue ? Logs.Count : maxEntries).ToList();
            }

            public void ClearLogs()
            {
            }
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> UnmappedFiles { get; } = new List<BookFile>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.GetUnmappedFiles))
                {
                    var mediaType = args?.Length >= 1 ? args[0] as string : null;
                    var ids = args?.Length == 2 ? ((IEnumerable<int>)args[0]).ToHashSet() : null;

                    return UnmappedFiles
                        .Where(file => file.EditionId == 0)
                        .Where(file => string.IsNullOrWhiteSpace(mediaType) || string.Equals(file.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                        .Where(file => ids == null || ids.Contains(file.Id))
                        .ToList();
                }

                throw new NotImplementedException($"Unexpected call to IMediaFileService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_scope_matching_logs_to_selected_unmapped_book_files()
        {
            var matchingLogger = new StubMatchingUploadLogger();
            matchingLogger.Logs.AddRange(new[]
            {
                Log("The 5 Second Rule/track01.mp3"),
                Log("Other Book/track01.mp3"),
                Log("Other Book/other.mp3"),
                Log("Unrelated/file.mp3")
            });

            var mediaFileService = CreateMediaFileService(new[]
            {
                new BookFile { Id = 1, Path = "/audiobooks/Mel Robbins/The 5 Second Rule/track01.mp3", EditionId = 0, MediaType = "audiobook" },
                new BookFile { Id = 2, Path = "/audiobooks/Other Book/other.mp3", EditionId = 0, MediaType = "audiobook" }
            });

            var logs = CollectLogs(matchingLogger, mediaFileService, new SendMatchingLogsCommand
            {
                MaxEntries = 1000,
                MinutesBack = 30,
                FailedMatchesOnly = true,
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection
                {
                    Scope = " selected ",
                    BookFileIds = new List<int> { 1 }
                }
            });

            Assert.That(logs.Select(log => log.FileName), Is.EquivalentTo(new[] { "The 5 Second Rule/track01.mp3" }));
            Assert.That(matchingLogger.LastMaxEntries, Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void should_not_fall_back_to_global_logs_when_selected_unmapped_scope_resolves_empty()
        {
            var matchingLogger = new StubMatchingUploadLogger();
            matchingLogger.Logs.Add(Log("Unrelated/file.mp3"));

            var logs = CollectLogs(matchingLogger, CreateMediaFileService(Array.Empty<BookFile>()), new SendMatchingLogsCommand
            {
                MaxEntries = 1000,
                MinutesBack = 30,
                FailedMatchesOnly = true,
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection
                {
                    Scope = "selected",
                    BookFileIds = new List<int> { 999 }
                }
            });

            Assert.That(logs, Is.Empty);
            Assert.That(matchingLogger.LastMaxEntries, Is.EqualTo(0));
        }

        private static MatchingLogEntry Log(string fileName)
        {
            return new MatchingLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                FileName = fileName,
                MediaType = "audiobook",
                MatchResult = new MatchResult { Success = false, Reason = "NO_MATCH" }
            };
        }

        private static IMediaFileService CreateMediaFileService(IEnumerable<BookFile> files)
        {
            var service = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)(object)service).UnmappedFiles.AddRange(files);
            return service;
        }

        private static List<MatchingLogEntry> CollectLogs(StubMatchingUploadLogger matchingLogger, IMediaFileService mediaFileService, SendMatchingLogsCommand command)
        {
            var collector = new MatchingLogsCollector(
                matchingLogger,
                mediaFileService,
                LogManager.GetCurrentClassLogger());

            return collector.Collect(command);
        }
    }
}
