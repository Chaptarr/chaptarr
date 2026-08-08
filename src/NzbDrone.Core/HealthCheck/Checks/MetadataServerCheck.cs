using System;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.HealthCheck.Checks
{
    public class MetadataServerCheck : HealthCheckBase
    {
        private readonly IMetadataServerHealthService _metadataServerHealthService;
        private readonly IMetadataServerHealthGate _metadataServerHealthGate;

        public MetadataServerCheck(IMetadataServerHealthService metadataServerHealthService,
                                   IMetadataServerHealthGate metadataServerHealthGate,
                                   ILocalizationService localizationService)
            : base(localizationService)
        {
            _metadataServerHealthService = metadataServerHealthService;
            _metadataServerHealthGate = metadataServerHealthGate;
        }

        public override HealthCheck Check()
        {
            var sourceName = _metadataServerHealthGate.SourceName;
            var status = _metadataServerHealthService.GetStatus(sourceName);

            if (status.IsHealthy && !status.IsRateLimited)
            {
                return new HealthCheck(GetType());
            }

            var retryAfter = GetRetryAfter(status);
            var retryText = retryAfter.HasValue
                ? MetadataServerHealthGate.FormatRetryAfter(retryAfter.Value)
                : _localizationService.GetLocalizedString("MetadataServerCheckRetrySoon");

            var message = string.Format(_localizationService.GetLocalizedString("MetadataServerCheckUnavailable"),
                                        sourceName,
                                        retryText,
                                        status.LastErrorMessage ?? _localizationService.GetLocalizedString("Unknown"));

            return new HealthCheck(GetType(),
                HealthCheckResult.Warning,
                message,
                "#metadata-server-unavailable");
        }

        private static TimeSpan? GetRetryAfter(MetadataServerStatus status)
        {
            if (status?.RateLimitedUntil == null)
            {
                return null;
            }

            var retryAfter = status.RateLimitedUntil.Value - DateTime.UtcNow;
            return retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero;
        }
    }
}
