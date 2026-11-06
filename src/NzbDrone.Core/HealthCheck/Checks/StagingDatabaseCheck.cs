using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(CommandExecutedEvent), CheckOnCondition.FailedOnly)]
    public class StagingDatabaseCheck : HealthCheckBase
    {
        private readonly IStagingDbContext _staging;
        private readonly IAppFolderInfo _appFolderInfo;
        public StagingDatabaseCheck(IStagingDbContext staging, IAppFolderInfo appFolderInfo, ILocalizationService localizationService)
            : base(localizationService)
        {
            _staging = staging;
            _appFolderInfo = appFolderInfo;
        }

        public override HealthCheck Check() => _staging.InitializationError == null
            ? new HealthCheck(GetType())
            : new HealthCheck(GetType(), HealthCheckResult.Error, _localizationService.GetLocalizedString("StagingDatabaseUnavailable", new Dictionary<string, object> { ["path"] = _appFolderInfo.GetStagingDatabase() }));
    }
}
