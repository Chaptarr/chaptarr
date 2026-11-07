using System;
using System.Threading;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider.Status;

namespace NzbDrone.Core.ImportLists
{
    public interface IImportListStatusService : IProviderStatusServiceBase<ImportListStatus>
    {
        IDisposable BeginReadOnlyScope() => null;

        DateTime? GetLastSyncListInfo(int importListId);

        void UpdateListSyncStatus(int importListId);
    }

    public class ImportListStatusService : ProviderStatusServiceBase<IImportList, ImportListStatus>, IImportListStatusService
    {
        private readonly AsyncLocal<int> _readOnlyScopeCount = new();

        public ImportListStatusService(IImportListStatusRepository providerStatusRepository, IEventAggregator eventAggregator, IRuntimeInfo runtimeInfo, Logger logger)
            : base(providerStatusRepository, eventAggregator, runtimeInfo, logger)
        {
        }

        public IDisposable BeginReadOnlyScope()
        {
            _readOnlyScopeCount.Value++;
            return new ReadOnlyScope(_readOnlyScopeCount);
        }

        public override void RecordSuccess(int providerId)
        {
            if (_readOnlyScopeCount.Value == 0)
            {
                base.RecordSuccess(providerId);
            }
        }

        public override void RecordFailure(int providerId, TimeSpan minimumBackOff = default(TimeSpan))
        {
            if (_readOnlyScopeCount.Value == 0)
            {
                base.RecordFailure(providerId, minimumBackOff);
            }
        }

        public override void RecordConnectionFailure(int providerId)
        {
            if (_readOnlyScopeCount.Value == 0)
            {
                base.RecordConnectionFailure(providerId);
            }
        }

        public DateTime? GetLastSyncListInfo(int importListId)
        {
            return GetProviderStatus(importListId).LastInfoSync;
        }

        public void UpdateListSyncStatus(int importListId)
        {
            if (_readOnlyScopeCount.Value > 0)
            {
                return;
            }

            lock (_syncRoot)
            {
                var status = GetProviderStatus(importListId);

                status.LastInfoSync = DateTime.UtcNow;

                _providerStatusRepository.Upsert(status);
            }
        }

        private sealed class ReadOnlyScope : IDisposable
        {
            private AsyncLocal<int> _scopeCount;

            public ReadOnlyScope(AsyncLocal<int> scopeCount)
            {
                _scopeCount = scopeCount;
            }

            public void Dispose()
            {
                var scopeCount = Interlocked.Exchange(ref _scopeCount, null);
                if (scopeCount != null && scopeCount.Value > 0)
                {
                    scopeCount.Value--;
                }
            }
        }
    }
}
