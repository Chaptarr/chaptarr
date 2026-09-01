using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MetadataSource
{
    public interface IMetadataServerHealthGate
    {
        bool TryBeginRequest(out TimeSpan retryAfter);
        bool CanAttemptWithoutProbe(out TimeSpan retryAfter);
        void ReportResponse(HttpResponse response);
        void ReportException(Exception exception);
        void Reset();
        string SourceName { get; }
    }

    public class MetadataServerHealthGate : IMetadataServerHealthGate
    {
        private readonly IConfigService _configService;
        private readonly IMetadataServerHealthService _healthService;
        private readonly Logger _logger;

        public MetadataServerHealthGate(IConfigService configService,
                                        IMetadataServerHealthService healthService,
                                        Logger logger)
        {
            _configService = configService;
            _healthService = healthService;
            _logger = logger;
        }

        public string SourceName => GetMetadataServerSourceName(_configService.MetadataServerUrl);

        public bool TryBeginRequest(out TimeSpan retryAfter)
        {
            return _healthService.TryBeginRequest(SourceName, out retryAfter);
        }

        public bool CanAttemptWithoutProbe(out TimeSpan retryAfter)
        {
            return _healthService.CanAttemptWithoutProbe(SourceName, out retryAfter);
        }

        public void Reset()
        {
            _healthService.Reset(SourceName);
        }

        public void ReportResponse(HttpResponse response)
        {
            if (response == null)
            {
                return;
            }

            if (string.Equals(response.Headers?.GetSingleValue("X-Cache-Status"), "HIT", StringComparison.OrdinalIgnoreCase))
            {
                // A local cache hit says nothing about whether the remote server recovered.
                // Release a half-open probe so the next eligible request can test the network.
                _healthService.ReportInconclusive(SourceName);
                return;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _healthService.ReportRateLimited(SourceName, GetRetryAfter(response));
                return;
            }

            if (IsTransientServerStatus(response.StatusCode))
            {
                _healthService.ReportFailure(SourceName, new WebException($"Metadata server returned {(int)response.StatusCode} {response.StatusCode}"));
                return;
            }

            _healthService.ReportSuccess(SourceName);
        }

        public void ReportException(Exception exception)
        {
            if (exception is TooManyRequestsException tooManyRequests)
            {
                _healthService.ReportRateLimited(SourceName, tooManyRequests.RetryAfter > TimeSpan.Zero ? tooManyRequests.RetryAfter : null);
                return;
            }

            if (exception is HttpException httpException && httpException.Response != null)
            {
                ReportResponse(httpException.Response);
                return;
            }

            if (IsTransientTransportException(exception))
            {
                _healthService.ReportFailure(SourceName, exception);
                return;
            }

            _logger.Trace(exception, "Metadata server request failed with non-circuit-breaking exception");
        }

        private static string GetMetadataServerSourceName(string metadataServerUrl)
        {
            if (string.IsNullOrWhiteSpace(metadataServerUrl))
            {
                return "Chaptarr Metadata Server";
            }

            var trimmed = metadataServerUrl.Trim().TrimEnd('/');
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }

            if (Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out var uriWithScheme))
            {
                return uriWithScheme.GetLeftPart(UriPartial.Authority);
            }

            return "Chaptarr Metadata Server";
        }

        private static bool IsTransientServerStatus(HttpStatusCode statusCode)
        {
            var status = (int)statusCode;
            return statusCode == HttpStatusCode.RequestTimeout ||
                   statusCode == HttpStatusCode.InternalServerError ||
                   statusCode == HttpStatusCode.BadGateway ||
                   statusCode == HttpStatusCode.ServiceUnavailable ||
                   statusCode == HttpStatusCode.GatewayTimeout ||
                   (status >= 520 && status <= 526);
        }

        private static bool IsTransientTransportException(Exception exception)
        {
            if (exception is WebException webException)
            {
                return webException.Status is WebExceptionStatus.ConnectFailure or
                       WebExceptionStatus.ConnectionClosed or
                       WebExceptionStatus.KeepAliveFailure or
                       WebExceptionStatus.NameResolutionFailure or
                       WebExceptionStatus.ReceiveFailure or
                       WebExceptionStatus.SecureChannelFailure or
                       WebExceptionStatus.Timeout or
                       WebExceptionStatus.TrustFailure;
            }

            return exception is HttpRequestException or TimeoutException or TaskCanceledException;
        }

        public static TimeSpan? GetRetryAfter(HttpResponse response)
        {
            if (response.Headers == null || !response.Headers.ContainsKey("Retry-After"))
            {
                return null;
            }

            var retryAfter = response.Headers["Retry-After"];
            if (int.TryParse(retryAfter, out var seconds))
            {
                return ClampRetryAfter(TimeSpan.FromSeconds(seconds));
            }

            if (DateTime.TryParse(retryAfter, out var date))
            {
                var delta = date.ToUniversalTime() - DateTime.UtcNow;
                return ClampRetryAfter(delta > TimeSpan.Zero ? delta : TimeSpan.Zero);
            }

            return null;
        }

        private static TimeSpan ClampRetryAfter(TimeSpan retryAfter)
        {
            if (retryAfter < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return retryAfter > MetadataServerHealthService.MaxRateLimitRetryAfter
                ? MetadataServerHealthService.MaxRateLimitRetryAfter
                : retryAfter;
        }

        public static string FormatRetryAfter(TimeSpan retryAfter)
        {
            if (retryAfter.TotalHours >= 1)
            {
                return $"{retryAfter.TotalHours:0.#}h";
            }

            if (retryAfter.TotalMinutes >= 1)
            {
                return $"{retryAfter.TotalMinutes:0.#}m";
            }

            return $"{Math.Max(1, retryAfter.TotalSeconds):0}s";
        }
    }
}
