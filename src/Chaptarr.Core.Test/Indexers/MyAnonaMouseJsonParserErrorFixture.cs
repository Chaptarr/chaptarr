using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    [NonParallelizable]
    public class MyAnonaMouseJsonParserErrorFixture
    {
        private LoggingConfiguration _previousLogging;
        private MemoryTarget _messages;

        [SetUp]
        public void SetUp()
        {
            _previousLogging = LogManager.Configuration;
            _messages = new MemoryTarget { Layout = "${level}|${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, _messages);
            LogManager.Configuration = logging;
        }

        [TearDown]
        public void TearDown()
        {
            LogManager.Configuration = _previousLogging;
        }

        [TestCase("{}", true)]
        [TestCase("{\"message\":\"unexpected response\"}", true)]
        [TestCase("{\"data\":null}", true)]
        [TestCase("{\"data\":[]}", false)]
        [TestCase("{\"error\":\"Nothing returned, out of 0\"}", false)]
        [TestCase("{\"error\":\"Nothing returned, out of 0\",\"data\":[]}", false)]
        public void only_unexplained_missing_data_should_warn_without_failing(string payload, bool shouldWarn)
        {
            Assert.That(Parse(JObject.Parse(payload)), Is.Empty);
            Assert.That(_messages.Logs.Count(message => message.StartsWith("Warn|MyAnonaMouse returned neither")),
                Is.EqualTo(shouldWarn ? 1 : 0));
        }

        [TestCase("not json", true)]
        [TestCase("{}", false)]
        [TestCase(null, false)]
        public void malformed_optional_series_should_log_and_keep_the_release(string seriesInfo, bool malformed)
        {
            var torrent = Parse(new JObject
            {
                ["data"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 123,
                        ["title"] = "Dune",
                        ["dl"] = "hash",
                        ["size"] = "500 MiB",
                        ["seeders"] = 12,
                        ["leechers"] = 0,
                        ["added"] = "2026-03-08 17:55:01",
                        ["series_info"] = seriesInfo,
                        ["category"] = 39,
                        ["main_cat"] = 13,
                        ["mediatype"] = 1,
                        ["catname"] = "Audiobooks"
                    }
                }
            }).OfType<NzbDrone.Core.Parser.Model.TorrentInfo>().Single();

            Assert.That(torrent.Title, Is.EqualTo("Dune"));
            Assert.That(torrent.SeriesInfo, Is.Empty);
            Assert.That(_messages.Logs.Count(message => message.StartsWith("Debug|Ignoring malformed series metadata for MyAnonaMouse torrent 123:")),
                Is.EqualTo(malformed ? 1 : 0));
        }

        [Test]
        public void documented_empty_result_error_should_remain_an_empty_result()
        {
            var releases = Parse(new JObject
            {
                ["error"] = "Nothing returned, out of 0",
                ["data"] = new JArray()
            });

            Assert.That(releases, Is.Empty);
        }

        [Test]
        public void reworded_empty_result_error_should_remain_an_empty_result()
        {
            var releases = Parse(new JObject
            {
                ["error"] = "Nothing returned, out of 0 results",
                ["data"] = new JArray()
            });

            Assert.That(releases, Is.Empty);
        }

        [Test]
        public void unexpected_api_error_should_be_reported_as_an_indexer_failure()
        {
            var exception = Assert.Throws<IndexerException>(() => Parse(new JObject
            {
                ["error"] = "Session expired",
                ["data"] = new JArray()
            }));

            Assert.That(exception.Message, Does.Contain("Session expired"));
        }

        private static IList<NzbDrone.Core.Parser.Model.ReleaseInfo> Parse(JObject payload)
        {
            var settings = new MyAnonaMouseSettings { BaseUrl = "https://www.myanonamouse.net" };
            var request = new HttpRequest("https://example.com/api");
            var indexerRequest = new IndexerRequest(request);
            var headers = new HttpHeader { ContentType = "application/json" };
            var response = new HttpResponse(request, headers, payload.ToString(), HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(indexerRequest, response);

            return new MyAnonaMouseJsonParser(settings).ParseResponse(indexerResponse);
        }
    }
}
