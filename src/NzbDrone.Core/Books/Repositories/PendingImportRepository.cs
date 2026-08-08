using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IPendingImportRepository : IBasicRepository<PendingImport>
    {
        List<PendingImport> GetPendingImports();
        List<PendingImport> GetReadyForRetry();
        PendingImport GetByProviderIds(string providerIds);
        void MarkAsProcessing(int id);
        void MarkAsCompleted(int id, int authorId);
        void MarkAsFailed(int id, string errorMessage);
        void UpdateRetryInfo(int id, DateTime nextRetryAt, int retryCount);
        void DeleteOldCompleted(DateTime before);
    }

    public class PendingImportRepository : BasicRepository<PendingImport>, IPendingImportRepository
    {
        public PendingImportRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<PendingImport> GetPendingImports()
        {
            return Query(x => x.Status == PendingImportStatus.Pending);
        }

        public List<PendingImport> GetReadyForRetry()
        {
            var now = DateTime.UtcNow;
            return Query(x => x.Status == PendingImportStatus.Pending && x.NextRetryAt <= now)
                   .OrderBy(x => x.CreatedAt)
                   .ToList();
        }

        public PendingImport GetByProviderIds(string providerIds)
        {
            return Query(x => x.ProviderIds == providerIds && x.Status != PendingImportStatus.Succeeded)
                   .FirstOrDefault();
        }

        public void MarkAsProcessing(int id)
        {
            var item = Get(id);
            if (item != null)
            {
                item.Status = PendingImportStatus.InProgress;
                item.LastAttemptAt = DateTime.UtcNow;
                Update(item);
            }
        }

        public void MarkAsCompleted(int id, int authorId)
        {
            var item = Get(id);
            if (item != null)
            {
                item.Status = PendingImportStatus.Succeeded;
                item.CompletedAt = DateTime.UtcNow;
                item.AuthorId = authorId;
                item.ErrorMessage = null;
                Update(item);
            }
        }

        public void MarkAsFailed(int id, string errorMessage)
        {
            var item = Get(id);
            if (item != null)
            {
                item.Status = PendingImportStatus.Failed;
                item.ErrorMessage = errorMessage;
                Update(item);
            }
        }

        public void UpdateRetryInfo(int id, DateTime nextRetryAt, int retryCount)
        {
            var item = Get(id);
            if (item != null)
            {
                item.Status = PendingImportStatus.Pending;
                item.NextRetryAt = nextRetryAt;
                item.RetryCount = retryCount;
                Update(item);
            }
        }

        public void DeleteOldCompleted(DateTime before)
        {
            Delete(x => x.Status == PendingImportStatus.Succeeded && x.CompletedAt < before);
        }
    }
}
