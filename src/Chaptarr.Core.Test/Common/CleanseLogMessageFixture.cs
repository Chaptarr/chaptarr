using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Instrumentation;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class CleanseLogMessageFixture
    {
        [TestCase("http://user:pass@example.com/path", "user:pass")]
        [TestCase("http://user:pass@example.com/path?apikey=abc", "abc")]
        [TestCase("https://api.europeana.eu/record/v2/search.json?wskey=private-europeana-key&query=books", "private-europeana-key")]
        [TestCase("https://www.googleapis.com/books/v1/volumes?key=private-google-key&q=books", "private-google-key")]
        [TestCase("Authorization: Bearer supersecrettoken", "supersecrettoken")]
        [TestCase("Proxy-Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
        [TestCase("Cookie: session=abc123; other=def456", "abc123")]
        [TestCase("Set-Cookie: session=abc123; Path=/; HttpOnly", "abc123")]
        public void should_cleanse_common_secrets(string message, string secret)
        {
            var cleansed = CleanseLogMessage.Cleanse(message);

            Assert.That(cleansed, Does.Not.Contain(secret));
            Assert.That(cleansed, Does.Contain("(removed)"));
        }

        [Test]
        public void should_escape_log_line_separators()
        {
            var cleansed = CleanseLogMessage.Cleanse("download id\r\nforged\u0085\u2028\u2029entry");

            Assert.That(cleansed, Is.EqualTo("download id\\r\\nforged\\u0085\\u2028\\u2029entry"));
        }

        [Test]
        public void safe_logger_should_keep_provider_line_breaks_inside_one_log_entry()
        {
            var previousConfiguration = LogManager.Configuration;
            var memory = new MemoryTarget("safe-log-memory")
            {
                Layout = "${level}|${message}"
            };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, memory, "safe-log-fixture");

            try
            {
                LogManager.Configuration = configuration;
                LogManager.ReconfigExistingLoggers();

                LogManager.GetLogger("safe-log-fixture").WarnSafe(
                    "Provider title: {0}",
                    "Title\r\nWARN: forged\u0085\u2028\u2029entry");

                Assert.That(memory.Logs, Has.Count.EqualTo(1));
                Assert.That(memory.Logs[0], Does.Not.Contain("\r"));
                Assert.That(memory.Logs[0], Does.Not.Contain("\n"));
                Assert.That(
                    memory.Logs[0],
                    Does.Contain("Title\\r\\nWARN: forged\\u0085\\u2028\\u2029entry"));
            }
            finally
            {
                LogManager.Configuration = previousConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }
    }
}
