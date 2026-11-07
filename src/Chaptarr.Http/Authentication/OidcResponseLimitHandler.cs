using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Chaptarr.Http.Authentication
{
    /// <summary>Bounds response bodies received from the OpenID Provider.</summary>
    internal sealed class OidcResponseLimitHandler : DelegatingHandler
    {
        private const int MaxResponseBytes = 1 << 20;

        public OidcResponseLimitHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var originalContent = response.Content;
            if (originalContent == null)
            {
                return response;
            }

            if (originalContent.Headers.ContentLength > MaxResponseBytes)
            {
                response.Dispose();
                throw new HttpRequestException("OIDC provider response exceeds the configured size limit.");
            }

            try
            {
                await using var source = await originalContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var destination = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (destination.Length + read > MaxResponseBytes)
                    {
                        response.Dispose();
                        throw new HttpRequestException("OIDC provider response exceeds the configured size limit.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                var boundedContent = new ByteArrayContent(destination.ToArray());
                foreach (var header in originalContent.Headers.ToArray())
                {
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        boundedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                originalContent.Dispose();
                response.Content = boundedContent;
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}
