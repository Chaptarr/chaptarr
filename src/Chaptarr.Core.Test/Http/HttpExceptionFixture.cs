using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HttpExceptionFixture
    {
        [Test]
        public void should_sanitize_request_url_in_default_message()
        {
            var request = new HttpRequest("https://example.com/api?apikey=real-secret&query=value");
            var response = new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.Unauthorized);

            var exception = new HttpException(request, response);

            Assert.That(exception.Message, Does.Not.Contain("real-secret"));
            Assert.That(exception.Message, Does.Contain("apikey=(removed)"));
        }
    }
}
