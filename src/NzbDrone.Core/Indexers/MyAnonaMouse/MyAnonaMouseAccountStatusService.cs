using System;
using System.Linq;
using NLog;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MyAnonaMouseAccountStatusService : IHandle<ApplicationStartedEvent>,
                                                    IHandle<HealthCheckCompleteEvent>,
                                                    IExecute<RefreshMyAnonaMouseAccountStatusCommand>
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;
        private readonly IMamUnsatisfiedSlotGuard _slotGuard;
        private readonly object _refreshLock = new object();

        private DateTime _lastRefreshAttemptUtc = DateTime.MinValue;
        private bool _refreshInProgress;

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

        public MyAnonaMouseAccountStatusService(IIndexerFactory indexerFactory,
                                                IMamUnsatisfiedSlotGuard slotGuard,
                                                Logger logger)
        {
            _indexerFactory = indexerFactory;
            _logger = logger;
            _slotGuard = slotGuard;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(ApplicationStartedEvent message)
        {
            RefreshIfDue("application startup");
        }

        public void Handle(HealthCheckCompleteEvent message)
        {
            RefreshIfDue("health cycle");
        }

        public void Execute(RefreshMyAnonaMouseAccountStatusCommand message)
        {
            RefreshIfDue("scheduled task");
        }

        private void RefreshIfDue(string source)
        {
            if (!TryBeginRefresh())
            {
                return;
            }

            try
            {
                RefreshAccountStatuses(source);
            }
            finally
            {
                EndRefresh();
            }
        }

        private void RefreshAccountStatuses(string source)
        {
            _logger.Debug("Refreshing MAM account status from {0}", source);

            var refreshed = 0;
            var changed = 0;

            foreach (var indexer in _indexerFactory.GetAvailableProviders().OfType<MyAnonaMouse>())
            {
                if (indexer.Definition is not IndexerDefinition definition ||
                    definition.Settings is not MyAnonaMouseSettings settings)
                {
                    continue;
                }

                var previousUserClass = settings.UserClass;
                var previousIsVip = settings.IsVip;
                var previousUnsatisfiedCount = settings.UnsatisfiedCount;
                var previousUnsatisfiedLimit = settings.UnsatisfiedLimit;
                var previousSnapshotUtc = settings.UnsatisfiedSnapshotUtc;

                try
                {
                    var status = indexer.RefreshAccountStatus().GetAwaiter().GetResult();
                    refreshed++;

                    // Persist every successful fetch because its refresh timestamp is the restart-safe freshness boundary.
                    _indexerFactory.Update(definition);
                    _slotGuard.Reconcile(indexer, status);

                    if (!string.Equals(previousUserClass, status.UserClass, StringComparison.Ordinal) ||
                        previousIsVip != status.IsVip ||
                        previousUnsatisfiedCount != status.UnsatisfiedCount ||
                        previousUnsatisfiedLimit != status.UnsatisfiedLimit ||
                        previousSnapshotUtc != status.SnapshotCreatedUtc)
                    {
                        changed++;
                        _logger.Info("Updated MAM account status for indexer '{0}': {1} (VIP: {2}), unsatisfied {3}/{4}",
                            definition.Name,
                            status.UserClass,
                            status.IsVip,
                            status.UnsatisfiedCount,
                            status.UnsatisfiedLimit);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to refresh MAM account status for indexer '{0}'", definition.Name);
                }
            }

            _logger.Debug("Refreshed MAM account status for {0} indexers; {1} changed", refreshed, changed);
        }

        private bool TryBeginRefresh()
        {
            lock (_refreshLock)
            {
                if (_refreshInProgress)
                {
                    return false;
                }

                if (DateTime.UtcNow - _lastRefreshAttemptUtc < RefreshInterval)
                {
                    return false;
                }

                _refreshInProgress = true;
                _lastRefreshAttemptUtc = DateTime.UtcNow;
                return true;
            }
        }

        private void EndRefresh()
        {
            lock (_refreshLock)
            {
                _refreshInProgress = false;
            }
        }
    }
}
