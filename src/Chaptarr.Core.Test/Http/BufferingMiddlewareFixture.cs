using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Chaptarr.Http.Middleware;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class BufferingMiddlewareFixture
    {
        private sealed class NonSeekableReadStream : Stream
        {
            private readonly MemoryStream _inner;

            public NonSeekableReadStream(byte[] bytes)
            {
                _inner = new MemoryStream(bytes);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        [Test]
        public async Task should_enable_buffering_for_matching_write_requests()
        {
            var sawSeekableBody = false;
            var middleware = new BufferingMiddleware(
                context =>
                {
                    sawSeekableBody = context.Request.Body.CanSeek;
                    return Task.CompletedTask;
                },
                new List<PathString> { new("/api/v1") });

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/API/v1/books";
            context.Request.Body = new NonSeekableReadStream(new byte[] { 1, 2, 3 });

            await middleware.InvokeAsync(context);

            Assert.That(sawSeekableBody, Is.True);
            Assert.That(context.Request.Body.CanSeek, Is.True);
        }

        [Test]
        public async Task should_not_enable_buffering_for_read_only_requests()
        {
            var sawSeekableBody = true;
            var middleware = new BufferingMiddleware(
                context =>
                {
                    sawSeekableBody = context.Request.Body.CanSeek;
                    return Task.CompletedTask;
                },
                new List<PathString> { new("/api/v1") });

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/api/v1/books";
            context.Request.Body = new NonSeekableReadStream(new byte[] { 1, 2, 3 });

            await middleware.InvokeAsync(context);

            Assert.That(sawSeekableBody, Is.False);
            Assert.That(context.Request.Body.CanSeek, Is.False);
        }

        [Test]
        public async Task should_not_enable_buffering_for_non_matching_paths()
        {
            var sawSeekableBody = true;
            var middleware = new BufferingMiddleware(
                context =>
                {
                    sawSeekableBody = context.Request.Body.CanSeek;
                    return Task.CompletedTask;
                },
                new List<PathString> { new("/api/v1") });

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Patch;
            context.Request.Path = "/ping";
            context.Request.Body = new NonSeekableReadStream(new byte[] { 1, 2, 3 });

            await middleware.InvokeAsync(context);

            Assert.That(sawSeekableBody, Is.False);
            Assert.That(context.Request.Body.CanSeek, Is.False);
        }

        [Test]
        public async Task should_respect_segment_boundaries_when_matching_prefixes()
        {
            var sawSeekableBody = true;
            var middleware = new BufferingMiddleware(
                context =>
                {
                    sawSeekableBody = context.Request.Body.CanSeek;
                    return Task.CompletedTask;
                },
                new List<PathString> { new("/api/v1") });

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/v12/books";
            context.Request.Body = new NonSeekableReadStream(new byte[] { 1, 2, 3 });

            await middleware.InvokeAsync(context);

            Assert.That(sawSeekableBody, Is.False);
            Assert.That(context.Request.Body.CanSeek, Is.False);
        }

        [Test]
        public async Task should_match_request_path_independently_of_path_base()
        {
            var sawSeekableBody = false;
            var middleware = new BufferingMiddleware(
                context =>
                {
                    sawSeekableBody = context.Request.Body.CanSeek;
                    return Task.CompletedTask;
                },
                new List<PathString> { new("/api/v1") });

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Put;
            context.Request.PathBase = "/chaptarr";
            context.Request.Path = "/api/v1/books";
            context.Request.Body = new NonSeekableReadStream(new byte[] { 1, 2, 3 });

            await middleware.InvokeAsync(context);

            Assert.That(sawSeekableBody, Is.True);
            Assert.That(context.Request.Body.CanSeek, Is.True);
        }
    }
}
