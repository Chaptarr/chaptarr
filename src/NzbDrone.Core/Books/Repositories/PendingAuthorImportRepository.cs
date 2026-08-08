using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IPendingAuthorImportRepository : IBasicRepository<PendingAuthorImport>
    {
        PendingAuthorImport GetByProviderId(string providerId);
        PendingAuthorImport GetActiveByProviderId(string providerId);
        List<PendingAuthorImport> GetDueForProcessing(DateTime cutoff, int limit);
        List<PendingAuthorImport> GetByStatus(PendingImportStatus status);
        List<PendingAuthorImport> GetAll();
        void DeleteOldCompleted(DateTime cutoff);
    }

    public class PendingAuthorImportRepository : BasicRepository<PendingAuthorImport>, IPendingAuthorImportRepository
    {
        public PendingAuthorImportRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public PendingAuthorImport GetByProviderId(string providerId)
        {
            return Query(x => x.ProviderId == providerId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefault();
        }

        public PendingAuthorImport GetActiveByProviderId(string providerId)
        {
            return Query(x => x.ProviderId == providerId &&
                            (x.OverallStatus == PendingImportStatus.Pending ||
                             x.OverallStatus == PendingImportStatus.InProgress ||
                             x.OverallStatus == PendingImportStatus.Retrying))
                .FirstOrDefault();
        }

        public List<PendingAuthorImport> GetDueForProcessing(DateTime cutoff, int limit)
        {
            return Query(x => x.NextAttemptAt <= cutoff &&
                            (x.OverallStatus == PendingImportStatus.Pending ||
                             x.OverallStatus == PendingImportStatus.Retrying))
                .OrderBy(x => x.NextAttemptAt)
                .Take(limit)
                .ToList();
        }

        public List<PendingAuthorImport> GetByStatus(PendingImportStatus status)
        {
            return Query(x => x.OverallStatus == status).ToList();
        }

        public List<PendingAuthorImport> GetAll()
        {
            return All().OrderByDescending(x => x.CreatedAt).ToList();
        }

        public void DeleteOldCompleted(DateTime cutoff)
        {
            Delete(x => x.UpdatedAt < cutoff &&
                       (x.OverallStatus == PendingImportStatus.Succeeded ||
                        x.OverallStatus == PendingImportStatus.Failed));
        }
    }
}
