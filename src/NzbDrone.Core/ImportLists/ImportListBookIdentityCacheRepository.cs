using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.ImportLists
{
    public interface IImportListBookIdentityCacheRepository : IBasicRepository<ImportListBookIdentityCache>
    {
        ImportListBookIdentityCache FindBySourceProviderId(string sourceProviderId);
        ImportListBookIdentityCache UpsertBySourceProviderId(ImportListBookIdentityCache cache);
    }

    public class ImportListBookIdentityCacheRepository : BasicRepository<ImportListBookIdentityCache>, IImportListBookIdentityCacheRepository
    {
        public ImportListBookIdentityCacheRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public ImportListBookIdentityCache FindBySourceProviderId(string sourceProviderId)
        {
            return Query(x => x.SourceProviderId == sourceProviderId).SingleOrDefault();
        }

        public ImportListBookIdentityCache UpsertBySourceProviderId(ImportListBookIdentityCache cache)
        {
            var existing = FindBySourceProviderId(cache.SourceProviderId);

            if (existing == null)
            {
                Insert(cache);
                return cache;
            }

            existing.BookProviderId = cache.BookProviderId;
            existing.AuthorProviderId = cache.AuthorProviderId;
            existing.Book = cache.Book;
            existing.Author = cache.Author;
            existing.UpdatedAt = cache.UpdatedAt;
            Update(existing);
            return existing;
        }
    }
}
