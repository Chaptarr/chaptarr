using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IAuthorSyncQueueRepository : IBasicRepository<AuthorSyncQueue>
    {
        AuthorSyncQueue GetByPrefixedId(string prefixedAuthorId);
        List<AuthorSyncQueue> GetPending(int limit = 100, int afterId = 0);
        List<AuthorSyncQueue> GetProcessing();
        void ClearCompleted();
        void ClearAll();
        bool HasPending();
    }

    public class AuthorSyncQueueRepository : BasicRepository<AuthorSyncQueue>, IAuthorSyncQueueRepository
    {
        public AuthorSyncQueueRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public AuthorSyncQueue GetByPrefixedId(string prefixedAuthorId)
        {
            return Query(x => x.PrefixedAuthorId == prefixedAuthorId).SingleOrDefault();
        }

        public List<AuthorSyncQueue> GetPending(int limit = 100, int afterId = 0)
        {
            return Query(x => x.Status == SyncQueueStatus.Pending && x.Id > afterId)
                .OrderBy(x => x.Id)
                .Take(limit)
                .ToList();
        }

        public List<AuthorSyncQueue> GetProcessing()
        {
            return Query(x => x.Status == SyncQueueStatus.Processing).ToList();
        }

        public void ClearCompleted()
        {
            Delete(x => x.Status == SyncQueueStatus.Completed);
        }

        public void ClearAll()
        {
            DeleteMany(All().Select(x => x.Id).ToList());
        }

        public bool HasPending()
        {
            return Query(x => x.Status == SyncQueueStatus.Pending).Any();
        }
    }
}
