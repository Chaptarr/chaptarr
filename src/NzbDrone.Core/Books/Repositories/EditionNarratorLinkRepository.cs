using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Repositories
{
    public interface IEditionNarratorLinkRepository : IBasicRepository<EditionNarratorLink>
    {
        List<EditionNarratorLink> GetByEditionIds(List<int> editionIds);
        void ApplyChanges(List<int> deletedLinkIds, List<EditionNarratorLink> newLinks, List<int> demotedPrimaryLinkIds = null);
        List<EditionNarratorLinkWithMonitored> GetByBookIds(List<int> bookIds);
    }

    public class EditionNarratorLinkWithMonitored
    {
        public int BookId { get; set; }
        public int NarratorId { get; set; }
        public bool IsPrimary { get; set; }
        public bool Monitored { get; set; }
    }

    public class EditionNarratorLinkRepository : BasicRepository<EditionNarratorLink>, IEditionNarratorLinkRepository
    {
        public EditionNarratorLinkRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<EditionNarratorLink> GetByEditionIds(List<int> editionIds)
        {
            if (editionIds == null || editionIds.Count == 0)
            {
                return new List<EditionNarratorLink>();
            }

            var links = new List<EditionNarratorLink>();
            using var conn = _database.OpenConnection();
            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;

            foreach (var batch in editionIds.Distinct().Chunk(isPostgres ? editionIds.Count : SqliteVariableLimit.MaxParameters))
            {
                links.AddRange(conn.Query<EditionNarratorLink, Narrator, NarratorMetadata, EditionNarratorLink>(
                    @"SELECT enl.*, n.*, m.*
                      FROM ""EditionNarratorLink"" enl
                      LEFT JOIN ""Narrators"" n ON n.""Id"" = enl.""NarratorId""
                      LEFT JOIN ""NarratorMetadata"" m ON m.""Id"" = n.""NarratorMetadataId""
                      WHERE enl.""EditionId"" " + (isPostgres ? @"= ANY(@ids)" : @"IN @ids"),
                    (link, narrator, metadata) =>
                    {
                        if (narrator != null) narrator.Metadata = metadata;
                        link.Narrator = narrator;
                        return link;
                    },
                    new { ids = batch.ToArray() }));
            }

            return links;
        }

        public void ApplyChanges(List<int> deletedLinkIds, List<EditionNarratorLink> newLinks, List<int> demotedPrimaryLinkIds = null)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            DeleteMany(deletedLinkIds, connection, transaction);
            if (demotedPrimaryLinkIds?.Count > 0)
            {
                var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;
                foreach (var batch in demotedPrimaryLinkIds.Distinct().Chunk(isPostgres ? demotedPrimaryLinkIds.Count : SqliteVariableLimit.MaxParameters))
                {
                    connection.Execute(@"UPDATE ""EditionNarratorLink"" SET ""IsPrimary"" = @primary WHERE ""Id"" " +
                        (isPostgres ? @"= ANY(@ids)" : @"IN @ids"),
                        new { primary = false, ids = batch.ToArray() }, transaction);
                }
            }

            InsertMany(newLinks, connection, transaction);
            transaction.Commit();
        }

        public List<EditionNarratorLinkWithMonitored> GetByBookIds(List<int> bookIds)
        {
            if (bookIds == null || bookIds.Count == 0)
            {
                return new List<EditionNarratorLinkWithMonitored>();
            }

            var results = new List<EditionNarratorLinkWithMonitored>();

            using var conn = _database.OpenConnection();
            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;

            foreach (var batch in bookIds.Chunk(SqliteVariableLimit.MaxParameters))
            {
                results.AddRange(conn.Query<EditionNarratorLinkWithMonitored>(
                    @"SELECT e.""BookId"" AS ""BookId"",
                             enl.""NarratorId"" AS ""NarratorId"",
                             enl.""IsPrimary"" AS ""IsPrimary"",
                             e.""Monitored"" AS ""Monitored""
                      FROM ""Editions"" e
                      INNER JOIN ""EditionNarratorLink"" enl ON enl.""EditionId"" = e.""Id""
                      WHERE e.""BookId"" " + (isPostgres ? @"= ANY(@bookIds)" : @"IN @bookIds"),
                    new { bookIds = isPostgres ? batch.ToArray() : batch }));
            }

            return results;
        }
    }
}
