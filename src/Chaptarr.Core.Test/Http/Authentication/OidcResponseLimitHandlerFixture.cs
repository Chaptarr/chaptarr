using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Http.Authentication;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    public class OidcResponseLimitHandlerFixture
    {
        private const int MaxResponseBytes = 1 << 20;

        [Test]
        public async Task should_accept_a_response_at_the_size_limit()
        {
            var inner = new ResponseHandler(() => new ByteArrayContent(new byte[MaxResponseBytes]));
            using var handler = new OidcResponseLimitHandler(inner);
            using var client = new HttpClient(handler);

            using var response = await client.GetAsync("https://issuer.example.com/.well-known/openid-configuration");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await response.Content.ReadAsByteArrayAsync()).Length, Is.EqualTo(MaxResponseBytes));
        }

        [Test]
        public void should_reject_a_chunked_response_over_the_size_limit()
        {
            var content = new StreamContent(new NonSeekableReadStream(new MemoryStream(new byte[MaxResponseBytes + 1])));
            Assert.That(content.Headers.ContentLength, Is.Null);

            var inner = new ResponseHandler(() => content);
            using var handler = new OidcResponseLimitHandler(inner);
            using var client = new HttpClient(handler);

            Assert.ThrowsAsync<HttpRequestException>(async () => await client.GetAsync("https://issuer.example.com/token"));
            Assert.That(inner.RequestCount, Is.EqualTo(1));
        }

        [Test]
        public void should_reject_a_declared_response_over_the_size_limit()
        {
            var inner = new ResponseHandler(() => new ByteArrayContent(new byte[MaxResponseBytes + 1]));
            using var handler = new OidcResponseLimitHandler(inner);
            using var client = new HttpClient(handler);

            Assert.ThrowsAsync<HttpRequestException>(async () => await client.GetAsync("https://issuer.example.com/userinfo"));
            Assert.That(inner.RequestCount, Is.EqualTo(1));
        }

        private sealed class ResponseHandler : HttpMessageHandler
        {
            private readonly System.Func<HttpContent> _contentFactory;

            public ResponseHandler(System.Func<HttpContent> contentFactory)
            {
                _contentFactory = contentFactory;
            }

            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = _contentFactory()
                });
            }
        }

        private sealed class NonSeekableReadStream : Stream
        {
            private readonly Stream _inner;

            public NonSeekableReadStream(Stream inner)
            {
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new System.NotSupportedException();

            public override long Position
            {
                get => throw new System.NotSupportedException();
                set => throw new System.NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                _inner.ReadAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();
            public override void SetLength(long value) => throw new System.NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
