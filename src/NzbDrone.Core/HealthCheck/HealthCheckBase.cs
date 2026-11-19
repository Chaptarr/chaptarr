using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck
{
    public abstract class HealthCheckBase : IProvideHealthCheck
    {
        public readonly ILocalizationService _localizationService;
        public HealthCheckBase(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
        }

        protected string FormatFailures(IReadOnlyList<string> failures, int? totalCount = null)
        {
            var details = string.Join("; ", failures.Take(5));
            var remaining = (totalCount ?? failures.Count) - System.Math.Min(5, failures.Count);
            if (remaining > 0)
            {
                details += "; " + _localizationService.GetLocalizedString("CountMore",
                    new Dictionary<string, object> { ["count"] = remaining });
            }

            return details;
        }

        public abstract HealthCheck Check();

        public virtual bool CheckOnStartup => true;

        public virtual bool CheckOnSchedule => true;
    }
}
