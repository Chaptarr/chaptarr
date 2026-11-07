using System;
using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    /// <summary>
    /// Warns when an enabled MyAnonaMouse indexer protects unsatisfied-torrent slots but has no fresh
    /// account status, since the guard then blocks every grab from that indexer.
    /// </summary>
    [CheckOn(typeof(ProviderAddedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderStatusChangedEvent<IIndexer>))]
    public class MamUnsatisfiedSlotStatusCheck : HealthCheckBase
    {
        private readonly IIndexerFactory _indexerFactory;

        public MamUnsatisfiedSlotStatusCheck(IIndexerFactory indexerFactory, ILocalizationService localizationService)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
        }

        public override HealthCheck Check()
        {
            var now = DateTime.UtcNow;

            var stale = _indexerFactory.All()
                .Where(definition => definition.Enable &&
                                     definition.Settings is MyAnonaMouseSettings { ProtectUnsatisfiedSlots: true } settings &&
                                     !MamUnsatisfiedSlotGuard.HasFreshStatus(settings, now))
                .Select(definition => definition.Name)
                .ToList();

            if (stale.Any())
            {
                return new HealthCheck(GetType(),
                    HealthCheckResult.Warning,
                    string.Format(_localizationService.GetLocalizedString("MamUnsatisfiedSlotStatusStale"), string.Join(", ", stale)));
            }

            return new HealthCheck(GetType());
        }
    }
}
