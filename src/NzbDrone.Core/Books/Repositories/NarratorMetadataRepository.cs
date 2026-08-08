using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public class NarratorMetadataRepository : BasicRepository<NarratorMetadata>, INarratorMetadataRepository
    {
        public NarratorMetadataRepository(IMainDatabase database, IEventAggregator eventAggregator, Logger logger)
            : base(database, eventAggregator)
        {
        }

        public List<NarratorMetadata> FindById(List<string> foreignIds)
        {
            if (foreignIds == null || foreignIds.Count == 0)
            {
                return new List<NarratorMetadata>();
            }

            var ids = foreignIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
            if (ids.Length == 0)
            {
                return new List<NarratorMetadata>();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && ids.Length > SqliteVariableLimit.MaxParameters)
            {
                var metadata = new List<NarratorMetadata>();
                foreach (var batch in ids.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchIds = batch.ToArray();
                    metadata.AddRange(Query(x => Enumerable.Contains(batchIds, x.Id.ToString())));
                }

                return metadata.DistinctBy(m => m.Id).ToList();
            }

            return Query(x => Enumerable.Contains(ids, x.Id.ToString()));
        }

        public List<NarratorMetadata> FindByProviderIds(IEnumerable<string> goodreadsNarratorIds, IEnumerable<string> hardcoverNarratorIds)
        {
            var results = new List<NarratorMetadata>();

            results.AddRange(FindByGoodreadsNarratorIds(goodreadsNarratorIds));
            results.AddRange(FindByHardcoverNarratorIds(hardcoverNarratorIds));

            return results.DistinctBy(m => m.Id).ToList();
        }

        public List<NarratorMetadata> FindByGoodreadsNarratorIds(IEnumerable<string> goodreadsNarratorIds)
        {
            if (goodreadsNarratorIds == null)
            {
                return new List<NarratorMetadata>();
            }

            var ids = goodreadsNarratorIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct()
                .ToArray();

            if (ids.Length == 0)
            {
                return new List<NarratorMetadata>();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && ids.Length > SqliteVariableLimit.MaxParameters)
            {
                var metadata = new List<NarratorMetadata>();
                foreach (var batch in ids.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchIds = batch.ToArray();
                    metadata.AddRange(Query(x => Enumerable.Contains(batchIds, x.GoodreadsNarratorId)));
                }

                return metadata.DistinctBy(m => m.Id).ToList();
            }

            return Query(x => Enumerable.Contains(ids, x.GoodreadsNarratorId));
        }

        public List<NarratorMetadata> FindByHardcoverNarratorIds(IEnumerable<string> hardcoverNarratorIds)
        {
            if (hardcoverNarratorIds == null)
            {
                return new List<NarratorMetadata>();
            }

            var ids = hardcoverNarratorIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct()
                .ToArray();

            if (ids.Length == 0)
            {
                return new List<NarratorMetadata>();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && ids.Length > SqliteVariableLimit.MaxParameters)
            {
                var metadata = new List<NarratorMetadata>();
                foreach (var batch in ids.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchIds = batch.ToArray();
                    metadata.AddRange(Query(x => Enumerable.Contains(batchIds, x.HardcoverNarratorId)));
                }

                return metadata.DistinctBy(m => m.Id).ToList();
            }

            return Query(x => Enumerable.Contains(ids, x.HardcoverNarratorId));
        }

        public bool UpsertMany(List<NarratorMetadata> data)
        {
            var existingMetadata = FindById(data.Select(x => x.Id.ToString()).ToList());
            var updateMetadataList = new List<NarratorMetadata>();
            var addMetadataList = new List<NarratorMetadata>();

            foreach (var metadata in data)
            {
                var existing = existingMetadata.SingleOrDefault(x => x.Id == metadata.Id);
                if (existing != null)
                {
                    metadata.Id = existing.Id;
                    metadata.UseMetadataFrom(existing);
                    updateMetadataList.Add(metadata);
                }
                else
                {
                    addMetadataList.Add(metadata);
                }
            }

            UpdateMany(updateMetadataList);
            InsertMany(addMetadataList);

            return updateMetadataList.Any() || addMetadataList.Any();
        }
    }
}
