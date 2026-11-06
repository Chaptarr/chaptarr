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

        protected string FormatFailures(IReadOnlyList<string> failures)
        {
            var details = string.Join("; ", failures.Take(5));
            if (failures.Count > 5)
            {
                details += "; " + _localizationService.GetLocalizedString("CountMore",
                    new Dictionary<string, object> { ["count"] = failures.Count - 5 });
            }

            return details;
        }

        public abstract HealthCheck Check();

        public virtual bool CheckOnStartup => true;

        public virtual bool CheckOnSchedule => true;
    }
}
