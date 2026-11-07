using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Http
{
    public interface ICachedHttpResponseRepository : IBasicRepository<CachedHttpResponse>
    {
        CachedHttpResponse FindByUrl(string url);
        CachedHttpResponse UpsertByUrl(CachedHttpResponse response);
    }

    public class CachedHttpResponseRepository : BasicRepository<CachedHttpResponse>, ICachedHttpResponseRepository
    {
        public CachedHttpResponseRepository(ICacheDatabase database,
                                            IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public CachedHttpResponse FindByUrl(string url)
        {
            return Query(x => x.Url == url)
                .OrderByDescending(x => x.LastRefresh)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();
        }

        public CachedHttpResponse UpsertByUrl(CachedHttpResponse response)
        {
            const string sql = @"
                INSERT INTO ""HttpResponse"" (""Url"", ""LastRefresh"", ""Expiry"", ""Value"", ""StatusCode"")
                VALUES (@Url, @LastRefresh, @Expiry, @Value, @StatusCode)
                ON CONFLICT (""Url"") DO UPDATE SET
                    ""LastRefresh"" = excluded.""LastRefresh"",
                    ""Expiry"" = excluded.""Expiry"",
                    ""Value"" = excluded.""Value"",
                    ""StatusCode"" = excluded.""StatusCode""
                RETURNING ""Id"";";

            using (var connection = _database.OpenConnection())
            {
                response.Id = connection.QuerySingle<int>(sql, response);
            }

            return response;
        }
    }
}
