using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public sealed class DirectDownloadResponseReader
    {
        private const int MaxRedirects = 5;
        private readonly IHttpClient _httpClient;

        public DirectDownloadResponseReader(IHttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<DirectDownloadFetchedResponse> GetAsync(Uri uri, DirectDownloadProbeRequest request)
        {
            var currentUri = uri;
            for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
            {
                DirectDownloadUrlSafety.ValidateAbsoluteHttpOrHttpsUri(currentUri, currentUri.AbsoluteUri);

                using var buffer = new CappedMemoryStream(request.MaxResponseBytes);
                var httpRequest = new HttpRequest(currentUri.AbsoluteUri)
                {
                    AllowAutoRedirect = false,
                    RequestTimeout = request.RequestTimeout,
                    ResponseStream = buffer
                };
                httpRequest.Headers.Accept = "text/html, application/json;q=0.9, */*;q=0.1";

                try
                {
                    var response = await _httpClient.ExecuteAsync(httpRequest);
                    if (response.HasHttpRedirect)
                    {
                        var location = response.Headers.GetSingleValue("Location");
                        if (string.IsNullOrWhiteSpace(location))
                        {
                            throw new DirectDownloadProbeException($"Redirect response from '{CleanseLogMessage.Cleanse(currentUri.AbsoluteUri)}' is missing a Location header.");
                        }

                        currentUri = new Uri(currentUri, location);
                        DirectDownloadUrlSafety.ValidateAbsoluteHttpOrHttpsUri(currentUri, currentUri.AbsoluteUri);
                        continue;
                    }

                    var responseBytes = response.ResponseData ?? buffer.ToArray();
                    var responseText = response.ResponseData == null
                        ? HttpHeader.GetEncodingFromContentType(response.Headers.ContentType).GetString(responseBytes)
                        : response.Content;

                    return new DirectDownloadFetchedResponse(response.StatusCode, response.Headers.ContentType, responseText);
                }
                catch (WebException ex) when (ex.InnerException is IOException ioException && ioException.Message.Contains("maximum response size", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DirectDownloadProbeException($"Response from '{CleanseLogMessage.Cleanse(currentUri.AbsoluteUri)}' exceeded the maximum response size.", ex);
                }
                catch (Exception ex) when (!(ex is DirectDownloadProbeException))
                {
                    throw new DirectDownloadProbeException(CleanseLogMessage.Cleanse(ex.Message), ex);
                }
            }

            throw new DirectDownloadProbeException($"Too many redirects were attempted for '{CleanseLogMessage.Cleanse(uri.AbsoluteUri)}'.");
        }

        public sealed class DirectDownloadFetchedResponse
        {
            public DirectDownloadFetchedResponse(HttpStatusCode statusCode, string contentType, string content)
            {
                StatusCode = statusCode;
                ContentType = contentType ?? string.Empty;
                Content = content ?? string.Empty;
            }

            public HttpStatusCode StatusCode { get; }

            public string ContentType { get; }

            public string Content { get; }
        }

        private sealed class CappedMemoryStream : MemoryStream
        {
            private readonly int _maxBytes;

            public CappedMemoryStream(int maxBytes)
            {
                _maxBytes = maxBytes > 0 ? maxBytes : 256 * 1024;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                EnsureCapacityWithinLimit(count);
                base.Write(buffer, offset, count);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
            {
                EnsureCapacityWithinLimit(buffer.Length);
                return base.WriteAsync(buffer, cancellationToken);
            }

            private void EnsureCapacityWithinLimit(int incomingBytes)
            {
                if (Length + incomingBytes > _maxBytes)
                {
                    throw new IOException("maximum response size exceeded");
                }
            }
        }
    }
}
