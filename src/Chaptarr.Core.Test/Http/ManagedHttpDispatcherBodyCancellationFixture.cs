using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class ManagedHttpDispatcherBodyCancellationFixture
    {
        [Test]
        public void should_propagate_caller_cancellation_during_body_copy_to_response_stream()
        {
            var dispatcher = new TestDispatcher(new CacheManager(), new BlockingHandler());
            using var cancellationTokenSource = new CancellationTokenSource();
            using var responseStream = new MemoryStream();
            var request = new HttpRequest("https://downloads.example/dune.epub")
            {
                ResponseStream = responseStream,
                CancellationToken = cancellationTokenSource.Token,
                RequestTimeout = TimeSpan.FromSeconds(30)
            };

            var task = dispatcher.GetResponseAsync(request, new CookieContainer());
            cancellationTokenSource.Cancel();

            Assert.That(async () => await task, Throws.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public void should_propagate_caller_cancellation_during_body_read_without_response_stream()
        {
            var dispatcher = new TestDispatcher(new CacheManager(), new BlockingHandler());
            using var cancellationTokenSource = new CancellationTokenSource();
            var request = new HttpRequest("https://downloads.example/dune.epub")
            {
                CancellationToken = cancellationTokenSource.Token,
                RequestTimeout = TimeSpan.FromSeconds(30)
            };

            var task = dispatcher.GetResponseAsync(request, new CookieContainer());
            cancellationTokenSource.Cancel();

            Assert.That(async () => await task, Throws.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public void should_keep_non_cancellation_body_read_errors_as_receive_failure()
        {
            var dispatcher = new TestDispatcher(new CacheManager(), new ThrowingHandler(new IOException("stream broke")));
            var request = new HttpRequest("https://downloads.example/dune.epub")
            {
                RequestTimeout = TimeSpan.FromSeconds(30)
            };

            var exception = Assert.ThrowsAsync<WebException>(async () => await dispatcher.GetResponseAsync(request, new CookieContainer()));

            Assert.That(exception.Status, Is.EqualTo(WebExceptionStatus.ReceiveFailure));
            Assert.That(exception.InnerException, Is.TypeOf<IOException>());
        }

        [Test]
        public void should_keep_timeout_conversion_distinct_from_caller_cancellation()
        {
            var dispatcher = new TestDispatcher(new CacheManager(), new BlockingHandler());
            var request = new HttpRequest("https://downloads.example/dune.epub")
            {
                RequestTimeout = TimeSpan.FromMilliseconds(10)
            };

            var exception = Assert.ThrowsAsync<WebException>(async () => await dispatcher.GetResponseAsync(request, new CookieContainer()));

            Assert.That(exception.Status, Is.EqualTo(WebExceptionStatus.Timeout));
        }

        private sealed class TestDispatcher : ManagedHttpDispatcher
        {
            private readonly System.Net.Http.HttpClient _client;

            public TestDispatcher(CacheManager cacheManager, HttpMessageHandler handler)
                : base(new NoProxySettingsProvider(), new NoopSocketsHttpHandlerFactory(), new TestUserAgentBuilder(), cacheManager, LogManager.GetCurrentClassLogger())
            {
                _client = new System.Net.Http.HttpClient(handler)
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
            }

            protected override System.Net.Http.HttpClient GetClient(HttpUri uri) => _client;
        }

        private sealed class BlockingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new BlockingContent()
                });
            }
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            private readonly Exception _exception;

            public ThrowingHandler(Exception exception)
            {
                _exception = exception;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ThrowingContent(_exception)
                });
            }
        }

        private sealed class BlockingContent : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }
        }

        private sealed class ThrowingContent : HttpContent
        {
            private readonly Exception _exception;

            public ThrowingContent(Exception exception)
            {
                _exception = exception;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
            {
                return Task.FromException(_exception);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }
        }

        private sealed class NoProxySettingsProvider : IHttpProxySettingsProvider
        {
            public HttpProxySettings GetProxySettings(HttpUri uri) => null;
            public HttpProxySettings GetProxySettings() => null;
        }

        private sealed class NoopSocketsHttpHandlerFactory : ICreateManagedSocketsHttpHandler
        {
            public SocketsHttpHandler CreateHandler(HttpProxySettings proxySettings, bool allowAutoRedirect, bool useCookies, ICredentials credentials, bool preAuthenticate, int maxConnectionsPerServer)
            {
                return new SocketsHttpHandler();
            }
        }

        private sealed class TestUserAgentBuilder : IUserAgentBuilder
        {
            public string GetUserAgent(bool simplified = false) => "ChaptarrTest/1.0";
        }
    }
}
