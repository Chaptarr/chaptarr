using NzbDrone.Core.Datastore;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    public class StagingDatabaseCheck : HealthCheckBase
    {
        private readonly IStagingDbContext _staging;
        public StagingDatabaseCheck(IStagingDbContext staging, ILocalizationService localizationService)
            : base(localizationService)
        {
            _staging = staging;
        }

        public override HealthCheck Check() => _staging.InitializationError == null
            ? new HealthCheck(GetType())
            : new HealthCheck(GetType(), HealthCheckResult.Error, _localizationService.GetLocalizedString("StagingDatabaseUnavailable"));
    }
}
