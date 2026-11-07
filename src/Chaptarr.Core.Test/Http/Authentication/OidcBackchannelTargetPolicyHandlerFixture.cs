using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Chaptarr.Http.Authentication;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    public class OidcBackchannelTargetPolicyHandlerFixture
    {
        [TestCase("http://127.0.0.1/.well-known/openid-configuration")]
        [TestCase("http://localhost/.well-known/openid-configuration")]
        [TestCase("https://10.0.0.4/token")]
        [TestCase("https://[::1]/userinfo")]
        public async Task should_reject_private_literal_destinations_by_default(string url)
        {
            var inner = new CountingHandler();
            using var handler = new OidcBackchannelTargetPolicyHandler(false, inner);
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            var action = () => client.SendAsync(request);

            await action.Should().ThrowAsync<HttpRequestException>()
                .WithMessage("OIDC provider resolved to a private or reserved network address.");
            inner.RequestCount.Should().Be(0);
        }

        [Test]
        public async Task should_allow_private_literal_destinations_when_explicitly_enabled()
        {
            var inner = new CountingHandler();
            using var handler = new OidcBackchannelTargetPolicyHandler(true, inner);
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/token");

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            inner.RequestCount.Should().Be(1);
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
