using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(MyAnonaMouseAccountSummaryHealthChangedEvent))]
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class MyAnonaMouseAccountSummaryCheck : HealthCheckBase
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IMyAnonaMouseAccountStatusService _accountStatusService;

        public MyAnonaMouseAccountSummaryCheck(IIndexerFactory indexerFactory,
                                               IMyAnonaMouseAccountStatusService accountStatusService,
                                               ILocalizationService localizationService)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
            _accountStatusService = accountStatusService;
        }

        public override HealthCheck Check()
        {
            var unreadable = _accountStatusService.GetUnreadableSummaryFailures();
            if (unreadable.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            var indexerNames = _indexerFactory.All()
                .Where(indexer => indexer.Enable &&
                                  indexer.Settings is MyAnonaMouseSettings settings &&
                                  unreadable.TryGetValue(indexer.Id, out var failedAt) &&
                                  (settings.UnsatisfiedStatusRefreshedUtc is not { } refreshedAt || refreshedAt <= failedAt))
                .Select(indexer => indexer.Name)
                .ToList();
            if (indexerNames.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            return new HealthCheck(GetType(), HealthCheckResult.Warning,
                string.Format(_localizationService.GetLocalizedString("MyAnonaMouseAccountSummaryUnreadable"), string.Join(", ", indexerNames)));
        }
    }
}
