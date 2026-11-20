using System;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;

namespace NzbDrone.Core.MetadataSource
{
    public interface IMetadataServerHealthGate
    {
        bool TryBeginRequest(out TimeSpan retryAfter);
        bool CanAttemptWithoutProbe(out TimeSpan retryAfter);
        bool CanBeginRequest(out TimeSpan retryAfter);
        void ReportResponse(HttpResponse response);
        void ReportException(Exception exception);
        void Reset();
        string SourceName { get; }
    }

    public class MetadataServerHealthGate : IMetadataServerHealthGate
    {
        private readonly IConfigService _configService;
        private readonly IHttpServiceHealth _healthService;
        private readonly Logger _logger;

        public MetadataServerHealthGate(IConfigService configService,
                                        IHttpServiceHealth healthService,
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

        public bool CanBeginRequest(out TimeSpan retryAfter)
        {
            return _healthService.CanBeginRequest(SourceName, out retryAfter);
        }

        public void Reset()
        {
            _healthService.Reset(SourceName);
        }

        public void ReportResponse(HttpResponse response)
        {
            if (response != null)
            {
                ReportResult(response);
            }
        }

        public void ReportException(Exception exception)
        {
            ReportResult((exception as HttpException)?.Response, exception);
        }

        private void ReportResult(HttpResponse response, Exception exception = null)
        {
            if (string.Equals(response?.Headers?.GetSingleValue("X-Cache-Status"), "HIT", StringComparison.OrdinalIgnoreCase))
            {
                // Cached data cannot establish remote recovery; release a reserved probe.
                _healthService.ReportInconclusive(SourceName);
                return;
            }

            var failureMessage = HttpTransportErrors.GetStatusMessage(response, exception, "Metadata server returned");
            if (!HttpServiceHealth.OpensBackoff(_healthService.ReportResult(SourceName, response, exception, serverErrorsAreOutages: true,
                label: "Metadata server " + SourceName, failureMessage: failureMessage)))
            {
                _logger.Trace(exception, "Metadata server request completed without opening the backoff");
            }
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

    }
}
