using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MatchingLoggerFixture
    {
        private sealed class AppFolderInfoStub : IAppFolderInfo
        {
            public string AppDataFolder { get; init; }
            public string TempFolder { get; init; } = "/tmp";
            public string StartUpFolder { get; init; } = "/app";
        }

        private class DiskProviderProxy : DispatchProxy
        {
            private readonly FileSystem _fileSystem = new FileSystem();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.FolderExists):
                        return Directory.Exists((string)args[0]);
                    case nameof(IDiskProvider.CreateFolder):
                        Directory.CreateDirectory((string)args[0]);
                        return null;
                    case nameof(IDiskProvider.FileExists):
                        return File.Exists((string)args[0]);
                    case nameof(IDiskProvider.GetFiles):
                        return Directory.Exists((string)args[0])
                            ? Directory.GetFiles((string)args[0])
                            : Array.Empty<string>();
                    case nameof(IDiskProvider.DeleteFile):
                        if (File.Exists((string)args[0]))
                        {
                            File.Delete((string)args[0]);
                        }
                        return null;
                    case nameof(IDiskProvider.ReadAllText):
                        return File.ReadAllText((string)args[0]);
                    case nameof(IDiskProvider.GetFileInfo):
                        return _fileSystem.FileInfo.FromFileName((string)args[0]);
                    default:
                        throw new NotImplementedException($"Test proxy does not implement {typeof(IDiskProvider).Name}.{targetMethod?.Name}");
                }
            }
        }

        private static MatchingLogger CreateLogger(string root)
        {
            var appFolderInfo = new AppFolderInfoStub
            {
                AppDataFolder = root
            };

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            return new MatchingLogger(appFolderInfo, diskProvider, LogManager.GetLogger("test"));
        }

        [Test]
        public void log_entries_should_redact_path_derived_tags_and_keep_core_metadata()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"matching_logger_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            try
            {
                var logger = CreateLogger(root);

                var extractedTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Dune" } },
                    { "COMMENT", new List<string> { "legacy plot summary" } },
                    { "pathcomponents", new List<string> { "audiobooks", "Frank Herbert" } },
                    { "filename", new List<string> { "Dune.m4b" } }
                };

                var result = new MatchResult
                {
                    Success = true,
                    Reason = "MATCHED",
                    AuthorMatched = "Frank Herbert",
                    BookMatched = "Dune",
                    EditionMatched = "Dune",
                    Decision = "MATCHED"
                };

                var filePath = Path.Combine(root, "audiobooks", "Frank Herbert", "Dune.m4b");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, "x");

                logger.LogMatchAttempt(filePath, extractedTags, result);

                var recent = logger.GetRecentLogs(10);

                Assert.That(recent, Has.Count.EqualTo(1));
                Assert.That(recent[0].FileName, Is.EqualTo("Frank Herbert/Dune.m4b"));
                Assert.That(recent[0].ExtractedTags.ContainsKey("TITLE"), Is.True);
                Assert.That(recent[0].ExtractedTags.ContainsKey("COMMENT"), Is.True);
                Assert.That(recent[0].ExtractedTags.ContainsKey("pathcomponents"), Is.False);
                Assert.That(recent[0].ExtractedTags.ContainsKey("filename"), Is.False);
                Assert.That(recent[0].MatchResult.AuthorMatched, Is.EqualTo("Frank Herbert"));
                Assert.That(recent[0].MatchResult.BookMatched, Is.EqualTo("Dune"));
                Assert.That(recent[0].MatchResult.EditionMatched, Is.EqualTo("Dune"));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void log_final_decision_should_preserve_fully_populated_match_result_fields()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"matching_logger_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            try
            {
                var logger = CreateLogger(root);

                var extractedTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Dune" } }
                };

                var matchResult = new MatchResult
                {
                    Success = true,
                    Reason = "MATCHED",
                    Decision = "MATCHED",
                    MatchedAuthor = "Frank Herbert",
                    MatchedBook = "Dune",
                    MatchedEdition = "Dune [Audible Edition]",
                    MatchedEditionTitle = "Dune",
                    MatchedEditionNarrators = new List<string> { "Scott Brick" },
                    AuthorProvedBy = new List<MatchEvidence>
                    {
                        new MatchEvidence
                        {
                            Source = "embedded",
                            Field = "ALBUMARTIST",
                            Key = "albumartist",
                            Value = "Frank Herbert"
                        }
                    },
                    BookProvedBy = new List<MatchEvidence>
                    {
                        new MatchEvidence
                        {
                            Source = "embedded",
                            Field = "TITLE",
                            Key = "title",
                            Value = "Dune"
                        }
                    },
                    NarratorProvedBy = new List<MatchEvidence>
                    {
                        new MatchEvidence
                        {
                            Source = "embedded",
                            Field = "ARTIST",
                            Key = "artist",
                            Value = "Scott Brick"
                        }
                    },
                    PathFallbackUsed = false,
                    PathFallbackSuppressedReason = "author_confirmed",
                    Outcome = "MATCHED",
                    OutcomeReason = "full proof"
                };

                var filePath = Path.Combine(root, "audiobooks", "Frank Herbert", "Dune.m4b");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, "x");

                logger.LogFinalDecision(filePath, matchResult, extractedTags, commandId: 7, correlationId: "cid-1");

                var recent = logger.GetRecentLogs(10);

                Assert.That(recent, Has.Count.EqualTo(1));
                Assert.That(recent[0].MatchResult.MatchedAuthor, Is.EqualTo("Frank Herbert"));
                Assert.That(recent[0].MatchResult.MatchedBook, Is.EqualTo("Dune"));
                Assert.That(recent[0].MatchResult.MatchedEdition, Is.EqualTo("Dune [Audible Edition]"));
                Assert.That(recent[0].MatchResult.MatchedEditionTitle, Is.EqualTo("Dune"));
                Assert.That(recent[0].MatchResult.MatchedEditionNarrators, Is.EquivalentTo(new[] { "Scott Brick" }));
                Assert.That(recent[0].MatchResult.AuthorProvedBy, Has.Count.EqualTo(1));
                Assert.That(recent[0].MatchResult.AuthorProvedBy[0].Field, Is.EqualTo("ALBUMARTIST"));
                Assert.That(recent[0].MatchResult.BookProvedBy, Has.Count.EqualTo(1));
                Assert.That(recent[0].MatchResult.BookProvedBy[0].Field, Is.EqualTo("TITLE"));
                Assert.That(recent[0].MatchResult.NarratorProvedBy, Has.Count.EqualTo(1));
                Assert.That(recent[0].MatchResult.NarratorProvedBy[0].Field, Is.EqualTo("ARTIST"));
                Assert.That(recent[0].MatchResult.PathFallbackUsed, Is.False);
                Assert.That(recent[0].MatchResult.PathFallbackSuppressedReason, Is.EqualTo("author_confirmed"));
                Assert.That(recent[0].MatchResult.Outcome, Is.EqualTo("MATCHED"));
                Assert.That(recent[0].MatchResult.OutcomeReason, Is.EqualTo("full proof"));
                Assert.That(recent[0].CommandId, Is.EqualTo(7));
                Assert.That(recent[0].CorrelationId, Is.EqualTo("cid-1"));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void legacy_log_final_decision_overload_should_still_populate_legacy_match_fields()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"matching_logger_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            try
            {
                var logger = CreateLogger(root);

                var extractedTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Dune" } }
                };

                var filePath = Path.Combine(root, "audiobooks", "Frank Herbert", "Dune.m4b");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, "x");

                logger.LogFinalDecision(filePath,
                    "MATCHED",
                    "legacy reason",
                    extractedTags,
                    authorMatched: "Frank Herbert",
                    bookMatched: "Dune",
                    editionMatched: "Dune",
                    rejections: new List<CandidateRejection>
                    {
                        new CandidateRejection
                        {
                            Phase = "scoped",
                            Reason = "CONTAINMENT_FAILED"
                        }
                    },
                    commandId: 9,
                    correlationId: "cid-legacy");

                var recent = logger.GetRecentLogs(10);

                Assert.That(recent, Has.Count.EqualTo(1));
                Assert.That(recent[0].MatchResult.Success, Is.True);
                Assert.That(recent[0].MatchResult.Reason, Is.EqualTo("legacy reason"));
                Assert.That(recent[0].MatchResult.AuthorMatched, Is.EqualTo("Frank Herbert"));
                Assert.That(recent[0].MatchResult.BookMatched, Is.EqualTo("Dune"));
                Assert.That(recent[0].MatchResult.EditionMatched, Is.EqualTo("Dune"));
                Assert.That(recent[0].MatchResult.Decision, Is.EqualTo("MATCHED"));
                Assert.That(recent[0].MatchResult.Rejections, Has.Count.EqualTo(1));
                Assert.That(recent[0].CommandId, Is.EqualTo(9));
                Assert.That(recent[0].CorrelationId, Is.EqualTo("cid-legacy"));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void matching_log_entry_should_ignore_unknown_future_provenance_fields_when_deserialized()
        {
            var json = @"{
  ""t"": 1,
  ""f"": ""Frank Herbert/Dune.m4b"",
  ""m"": ""audiobook"",
  ""tags"": { ""TITLE"": [""Dune""] },
  ""result"": {
    ""ok"": true,
    ""reason"": ""MATCHED"",
    ""author"": ""Frank Herbert"",
    ""book"": ""Dune"",
    ""edition"": ""Dune"",
    ""decision"": ""MATCHED"",
    ""author_proved_by"": [{ ""field"": ""ALBUMARTIST"", ""value"": ""Frank Herbert"" }],
    ""book_proved_by"": [{ ""field"": ""TITLE"", ""value"": ""Dune"" }],
    ""narrator_proved_by"": [{ ""field"": ""ARTIST"", ""value"": ""Scott Brick"" }],
    ""path_fallback_used"": false
  }
}";

            var entry = JsonConvert.DeserializeObject<MatchingLogEntry>(json);

            Assert.That(entry, Is.Not.Null);
            Assert.That(entry.FileName, Is.EqualTo("Frank Herbert/Dune.m4b"));
            Assert.That(entry.MediaType, Is.EqualTo("audiobook"));
            Assert.That(entry.MatchResult.Success, Is.True);
            Assert.That(entry.MatchResult.Reason, Is.EqualTo("MATCHED"));
            Assert.That(entry.MatchResult.AuthorMatched, Is.EqualTo("Frank Herbert"));
            Assert.That(entry.MatchResult.BookMatched, Is.EqualTo("Dune"));
            Assert.That(entry.MatchResult.EditionMatched, Is.EqualTo("Dune"));
            Assert.That(entry.MatchResult.Decision, Is.EqualTo("MATCHED"));
            Assert.That(entry.MatchResult.Rejections, Is.Null);
        }
    }
}
