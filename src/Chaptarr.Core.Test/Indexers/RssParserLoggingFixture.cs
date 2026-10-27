using System;
using System.Linq;
using System.Net;
using System.Xml;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    [NonParallelizable]
    public class RssParserLoggingFixture
    {
        private LoggingConfiguration _previousConfiguration;

        [SetUp]
        public void SetUp()
        {
            _previousConfiguration = LogManager.Configuration;
            NzbDroneLogger.GetLogger(typeof(RssParser));
        }

        [TearDown]
        public void TearDown()
        {
            LogManager.Configuration = _previousConfiguration;
            LogManager.ReconfigExistingLoggers();
        }

        [Test]
        public void should_keep_malformed_response_sample_at_sanitized_trace()
        {
            var memory = new MemoryTarget
            {
                Layout = new CleanseLayout
                {
                    Inner = "${level}|${message}"
                }
            };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            LogManager.Configuration = configuration;
            LogManager.ReconfigExistingLoggers();

            const string secret = "synthetic-trace-secret";
            var responseBody = $"<rss><channel><title>Authorization: Bearer {secret}";
            var request = new HttpRequest("https://indexer.example/rss");
            var response = new HttpResponse(request, new HttpHeader { ContentType = "application/xml" }, responseBody, HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(new IndexerRequest(request), response);

            Assert.Throws<XmlException>(() => new NewznabRssParser().ParseResponse(indexerResponse));

            var traceLog = memory.Logs.Single(log => log.Contains("Truncated response content", StringComparison.Ordinal));
            Assert.That(traceLog, Does.StartWith("Trace|"));
            Assert.That(memory.Logs.Any(log => log.StartsWith("Debug|", StringComparison.Ordinal)), Is.False);
            Assert.That(traceLog, Does.Not.Contain(secret));
            Assert.That(traceLog, Does.Contain("(removed)"));
        }
    }
}
