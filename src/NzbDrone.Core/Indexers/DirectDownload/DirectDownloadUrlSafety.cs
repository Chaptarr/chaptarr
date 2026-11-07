using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public static class DirectDownloadUrlSafety
    {
        private static readonly TimeSpan DefaultHostResolutionTimeout = TimeSpan.FromSeconds(5);

        public static Uri NormalizeAndValidate(string rawUrl, TimeSpan? hostResolutionTimeout = null)
        {
            if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri))
            {
                throw Unsafe(rawUrl, "Source URL must be an absolute http or https URL.");
            }

            ValidateAbsoluteHttpOrHttpsUri(uri, rawUrl, hostResolutionTimeout);

            return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");
        }

        public static Uri ValidateAbsoluteHttpOrHttpsUri(Uri uri, string rawUrl = null, TimeSpan? hostResolutionTimeout = null)
        {
            if (uri == null)
            {
                throw Unsafe(rawUrl, "Source URL is required.");
            }

            if (!uri.IsAbsoluteUri)
            {
                throw Unsafe(rawUrl ?? uri.ToString(), "Source URL must be absolute.");
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Unsupported source URL scheme '{uri.Scheme}'. Only http and https are supported.");
            }

            if (uri.UserInfo.IsNotNullOrWhiteSpace())
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' must not include embedded credentials.");
            }

            EnsureSafeHost(uri, rawUrl ?? uri.AbsoluteUri, hostResolutionTimeout);
            return uri;
        }

        public static void EnsureSafeHost(Uri uri, string rawUrl = null, TimeSpan? hostResolutionTimeout = null)
        {
            EnsureSafeHost(uri, rawUrl, hostResolutionTimeout, Dns.GetHostAddressesAsync);
        }

        internal static void EnsureSafeHost(
            Uri uri,
            string rawUrl,
            TimeSpan? hostResolutionTimeout,
            Func<string, CancellationToken, Task<IPAddress[]>> resolveHostAddresses)
        {
            if (uri == null)
            {
                throw Unsafe(rawUrl, "Source URL is required.");
            }

            if (resolveHostAddresses == null)
            {
                throw new ArgumentNullException(nameof(resolveHostAddresses));
            }

            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' is not allowed.");
            }

            if (IPAddress.TryParse(uri.Host, out var parsedAddress))
            {
                if (IsUnsafeAddress(parsedAddress))
                {
                    throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' is not allowed.");
                }

                return;
            }

            var timeout = hostResolutionTimeout is { } requestedTimeout &&
                requestedTimeout > TimeSpan.Zero && requestedTimeout < DefaultHostResolutionTimeout
                    ? requestedTimeout
                    : DefaultHostResolutionTimeout;
            using var timeoutSource = new CancellationTokenSource(timeout);

            IPAddress[] resolved;
            try
            {
                resolved = resolveHostAddresses(uri.Host, timeoutSource.Token)
                    .WaitAsync(timeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (TimeoutException)
            {
                timeoutSource.Cancel();
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' could not be verified before DNS resolution timed out.");
            }
            catch (OperationCanceledException)
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' could not be verified before DNS resolution timed out.");
            }
            catch
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' could not be safely resolved.");
            }

            if (resolved == null || resolved.Length == 0 || resolved.Any(IsUnsafeAddress))
            {
                throw Unsafe(rawUrl ?? uri.AbsoluteUri, $"Source target '{CleanseLogMessage.Cleanse(rawUrl ?? uri.AbsoluteUri)}' is not allowed.");
            }
        }

        private static bool IsUnsafeAddress(IPAddress address)
        {
            if (address == null)
            {
                return true;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            return IPAddress.IsLoopback(address) ||
                   address.IsLocalAddress() ||
                   address.IsCgnatIpAddress() ||
                   CloudMetadataTargetPolicy.IsBlocked(address);
        }

        private static DirectDownloadProbeException Unsafe(string rawUrl, string message)
        {
            return new DirectDownloadProbeException(CleanseLogMessage.Cleanse(message ?? rawUrl ?? "Source URL is not allowed."));
        }
    }
}
