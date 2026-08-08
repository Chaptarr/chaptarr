using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Repositories
{
    public interface IBookNarratorLinkRepository : IBasicRepository<BookNarratorLink>
    {
        void DeleteByBookIds(List<int> bookIds);
    }

    public class BookNarratorLinkRepository : BasicRepository<BookNarratorLink>, IBookNarratorLinkRepository
    {
        public BookNarratorLinkRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public void DeleteByBookIds(List<int> bookIds)
        {
            if (bookIds == null || bookIds.Count == 0)
            {
                return;
            }

            using var conn = _database.OpenConnection();
            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;

            foreach (var batch in bookIds.Chunk(SqliteVariableLimit.MaxParameters))
            {
                conn.Execute(
                    isPostgres
                        ? @"DELETE FROM ""BookNarratorLink"" WHERE ""BookId"" = ANY(@ids)"
                        : @"DELETE FROM ""BookNarratorLink"" WHERE ""BookId"" IN @ids",
                    new { ids = isPostgres ? batch.ToArray() : batch });
            }
        }
    }
}
