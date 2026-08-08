using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Repositories
{
    public interface IEditionNarratorLinkRepository : IBasicRepository<EditionNarratorLink>
    {
        void DeleteByEditionIds(List<int> editionIds);
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

        public void DeleteByEditionIds(List<int> editionIds)
        {
            if (editionIds == null || editionIds.Count == 0)
            {
                return;
            }

            using var conn = _database.OpenConnection();
            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;

            foreach (var batch in editionIds.Chunk(SqliteVariableLimit.MaxParameters))
            {
                conn.Execute(
                    isPostgres
                        ? @"DELETE FROM ""EditionNarratorLink"" WHERE ""EditionId"" = ANY(@ids)"
                        : @"DELETE FROM ""EditionNarratorLink"" WHERE ""EditionId"" IN @ids",
                    new { ids = isPostgres ? batch.ToArray() : batch });
            }
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
