using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    [NonParallelizable]
    public class ReleaseEvaluationLoggingFixture
    {
        private LoggingConfiguration _previousConfiguration;
        private LogLevel _previousGlobalThreshold;

        [SetUp]
        public void SetUp()
        {
            NzbDrone.Core.Parser.Parser.ParseBookTitle("Logging Fixture - Warmup EPUB");
            _previousConfiguration = LogManager.Configuration;
            _previousGlobalThreshold = LogManager.GlobalThreshold;
        }

        [TearDown]
        public void TearDown()
        {
            LogManager.GlobalThreshold = _previousGlobalThreshold;
            LogManager.Configuration = _previousConfiguration;
            LogManager.ReconfigExistingLoggers();
        }

        [Test]
        public void debug_should_not_emit_per_release_parser_or_containment_details()
        {
            var memory = ConfigureLogging(LogLevel.Debug);

            ExerciseReleaseEvaluation();
            LogManager.Flush();

            Assert.That(memory.Logs.Any(IsPerReleaseDetail), Is.False);
        }

        [Test]
        public void trace_should_retain_per_release_parser_and_containment_details()
        {
            var memory = ConfigureLogging(LogLevel.Trace);

            ExerciseReleaseEvaluation();
            LogManager.Flush();

            Assert.Multiple(() =>
            {
                Assert.That(memory.Logs, Has.Some.Contains("Trace|ParseBookTitle called with: 'Stephen King - The Stand EPUB'"));
                Assert.That(memory.Logs, Has.Some.Contains("Trace|Trying to parse quality for 'Stephen King - The Stand EPUB'"));
                Assert.That(memory.Logs.Any(log => log.Contains("Trace|[CONTAINMENT] Author parsed into words: ['john'(Full), 'gwynne'(Full)]", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(memory.Logs, Has.Some.Contains("Trace|[CONTAINMENT] Author 'John Gwynne' NOT FOUND in any single field"));
                Assert.That(memory.Logs, Has.Some.Contains("Trace|[CONTAINMENT] Searched 1 fields with 1 total values"));
            });
        }

        private static void ExerciseReleaseEvaluation()
        {
            NzbDrone.Core.Parser.Parser.ParseBookTitle("Stephen King - The Stand EPUB");

            var validator = new ContainmentValidator(
                new TagNormalizer(),
                LogManager.GetLogger(nameof(ReleaseEvaluationLoggingFixture)));
            validator.ValidateAuthorInTags("John Gwynne", new Dictionary<string, List<string>>
            {
                ["RELEASE_TITLE"] = new List<string> { "Unrelated Release" }
            });
        }

        private static bool IsPerReleaseDetail(string log)
        {
            return log.Contains("ParseBookTitle called", StringComparison.Ordinal) ||
                   log.Contains("Trying to parse quality", StringComparison.Ordinal) ||
                   log.Contains("[CONTAINMENT]", StringComparison.Ordinal);
        }

        private static MemoryTarget ConfigureLogging(LogLevel minimumLevel)
        {
            var memory = new MemoryTarget("release-evaluation-memory")
            {
                Layout = "${level}|${message}"
            };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(minimumLevel, LogLevel.Fatal, memory);
            LogManager.GlobalThreshold = minimumLevel;
            LogManager.Configuration = configuration;
            LogManager.ReconfigExistingLoggers();
            return memory;
        }
    }
}
