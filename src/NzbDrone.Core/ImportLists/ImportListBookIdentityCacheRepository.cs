using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.ImportLists
{
    public interface IImportListBookIdentityCacheRepository : IBasicRepository<ImportListBookIdentityCache>
    {
        ImportListBookIdentityCache FindBySourceProviderId(string sourceProviderId);
        List<ImportListBookIdentityCache> FindBySourceProviderIds(IEnumerable<string> sourceProviderIds);
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

        public List<ImportListBookIdentityCache> FindBySourceProviderIds(IEnumerable<string> sourceProviderIds)
        {
            var ids = sourceProviderIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var rows = new List<ImportListBookIdentityCache>();
            if (ids.Length == 0)
            {
                return rows;
            }

            foreach (var batch in ids.Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters : ids.Length))
            {
                rows.AddRange(Query(row => batch.Contains(row.SourceProviderId)));
            }

            return rows;
        }

        public ImportListBookIdentityCache UpsertBySourceProviderId(ImportListBookIdentityCache cache)
        {
            if (cache.EditionHasNarration == false)
            {
                cache.EditionNarratorNames = null;
            }

            var existing = FindBySourceProviderId(cache.SourceProviderId);

            if (existing == null)
            {
                Insert(cache);
                return cache;
            }

            var identityChanged = !string.Equals(existing.BookProviderId, cache.BookProviderId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.AuthorProviderId, cache.AuthorProviderId, StringComparison.OrdinalIgnoreCase);
            existing.BookProviderId = cache.BookProviderId;
            existing.AuthorProviderId = cache.AuthorProviderId;
            existing.Book = cache.Book;
            existing.Author = cache.Author;
            if (identityChanged || cache.EditionHasNarration.HasValue || !existing.EditionHasNarration.HasValue)
            {
                existing.EditionHasNarration = cache.EditionHasNarration;
                existing.UpdatedAt = cache.UpdatedAt;
            }
            if (identityChanged || cache.EditionHasNarration == false || cache.EditionNarratorNames?.Count > 0)
            {
                existing.EditionNarratorNames = cache.EditionNarratorNames;
                existing.UpdatedAt = cache.UpdatedAt;
            }
            Update(existing);
            return existing;
        }
    }
}
