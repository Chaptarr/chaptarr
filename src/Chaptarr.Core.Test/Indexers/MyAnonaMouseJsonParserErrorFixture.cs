using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MyAnonaMouseJsonParserErrorFixture
    {
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
