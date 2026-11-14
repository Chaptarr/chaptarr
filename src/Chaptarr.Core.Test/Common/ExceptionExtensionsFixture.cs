using System;
using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class ExceptionExtensionsFixture
    {
        [Test]
        public void should_add_response_metadata_without_copying_response_body_to_exception_data()
        {
            const string responseBody = "synthetic provider error detail";
            var request = new HttpRequest("https://indexer.example/api?query=synthetic-query");
            var headers = new HttpHeader
            {
                ContentType = "application/json"
            };
            var response = new HttpResponse(request, headers, responseBody, HttpStatusCode.BadGateway);
            var exception = new InvalidOperationException("synthetic parse failure").WithData(response);

            Assert.That(exception.Data.Contains("ContentSample"), Is.False);
            Assert.That(exception.Data["RequestUri"], Is.EqualTo("https://indexer.example/api?query=synthetic-query"));
            Assert.That(exception.Data["StatusCode"], Is.EqualTo(nameof(HttpStatusCode.BadGateway)));
            Assert.That(exception.Data["ContentType"], Is.EqualTo("application/json"));
            Assert.That(exception.Data["ContentLength"], Is.EqualTo(responseBody.Length.ToString()));
        }
    }
}
