using System.Linq;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ConfigSavedEvent))]
    public class ConfigValueCheck : HealthCheckBase
    {
        private readonly IConfigService _config;
        public ConfigValueCheck(IConfigService config, ILocalizationService localization) : base(localization)
        {
            _config = config;
        }

        public override HealthCheck Check()
        {
            var errors = ConfigValueValidation.Validate(_config);
            return errors.Count == 0 ? new HealthCheck(GetType()) : new HealthCheck(GetType(), HealthCheckResult.Warning,
                string.Format(_localizationService.GetLocalizedString("StoredSettingsInvalid"),
                    string.Join("; ", errors.Select(error => $"{error.PropertyName}: {error.ErrorMessage}"))));
        }
    }
}
