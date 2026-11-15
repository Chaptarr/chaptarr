using System;
using System.IO;
using System.Net;
using System.Threading;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.Goodreads;

namespace NzbDrone.Core.MediaCover
{
    internal static class MediaCoverHttpRequest
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        internal static HttpResponse Get(IHttpClient client, IConfigService config, string url, bool rangeRequest = false,
            string userAgent = null, Stream responseStream = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null, bool logHttpError = true)
        {
            return Execute(config, url, request =>
            {
                request.ResponseStream = responseStream;
                return client.Get(request);
            }, cancellationToken, timeout, userAgent, rangeRequest, logHttpError);
        }

        internal static void Download(IHttpClient client, IConfigService config, string url, string path, TimeSpan timeout)
        {
            Execute(config, url, request =>
            {
                client.DownloadFile(request, path);
                return true;
            }, timeout: timeout);
        }

        private static T Execute<T>(IConfigService config, string url, Func<HttpRequest, T> fetch,
            CancellationToken cancellationToken = default, TimeSpan? timeout = null,
            string userAgent = null, bool rangeRequest = false, bool logHttpError = true)
        {
            if (!MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl(url, config, out var safeUrl, out var reason))
            {
                throw MediaCoverUrlSafetyPolicy.BlockedUrl(url, reason);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? Timeout);
            var redirected = false;
            userAgent ??= ExternalImageRequestHeaders.GetRandomUserAgent();
            var goodreadsUserAgent = GoodreadsAndroidUserAgent.GetRandom();
            void ApplyHeaders(HttpRequest current) => ExternalImageRequestHeaders.ApplyExternalImageRequestHeaders(current,
                ExternalImageRequestHeaders.IsGoodreadsUrl(current.Url.FullUri)
                    ? goodreadsUserAgent : userAgent, rangeRequest);

            var request = new HttpRequest(safeUrl)
            {
                AllowAutoRedirect = true,
                RedirectLimit = 5,
                LogHttpError = logHttpError,
                RequestTimeout = timeout ?? Timeout,
                CancellationToken = deadline.Token,
                TargetPolicy = MediaCoverUrlSafetyPolicy.CreateTargetPolicy(config),
                OnRedirect = current =>
                {
                    redirected = true;
                    ApplyHeaders(current);
                }
            };
            ApplyHeaders(request);
            try
            {
                return fetch(request);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            // Structural redirect checks run before OnRedirect; their rejected URL
            // differs from the initially validated URL. DNS checks run after it.
            catch (UnsafeImageUrlException ex) when (redirected || ex.Url != safeUrl)
            {
                throw MediaCoverUrlSafetyPolicy.BlockedUrl(ex.Url, ex.Reason, "redirect target");
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
            {
                throw new WebException("Image request timed out.", ex, WebExceptionStatus.Timeout, null);
            }
        }
    }
}
