using System;
using System.Net.Http;
using NUnit.Framework;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HttpRequestBuilderFormDataFixture
    {
        [Test]
        public void should_reject_form_data_when_request_already_has_a_content_body()
        {
            var builder = new ContentRequestBuilder("https://example.com")
            {
                Method = HttpMethod.Post
            };
            builder.AddFormParameter("name", "value");

            Assert.Throws<ApplicationException>(() => builder.Build());
        }

        private sealed class ContentRequestBuilder : HttpRequestBuilder
        {
            public ContentRequestBuilder(string baseUrl)
                : base(baseUrl)
            {
            }

            protected override HttpRequest CreateRequest()
            {
                var request = base.CreateRequest();
                request.Content = new StringContent("body");
                return request;
            }
        }
    }
}
