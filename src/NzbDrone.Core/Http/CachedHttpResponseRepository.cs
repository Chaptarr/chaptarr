using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Npgsql;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Http
{
    public interface ICachedHttpResponseRepository : IBasicRepository<CachedHttpResponse>
    {
        CachedHttpResponse FindByUrl(string url);
        CachedHttpResponse UpsertByUrl(CachedHttpResponse model);
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
            var edition = Query(x => x.Url == url).SingleOrDefault();

            return edition;
        }

        // The URL index is unique, so two concurrent lookups of the same URL cannot both insert: the loser hits
        // the unique constraint and updates the winner's row instead of creating a duplicate.
        public CachedHttpResponse UpsertByUrl(CachedHttpResponse model)
        {
            if (model.Id != 0)
            {
                return Update(model);
            }

            try
            {
                return Insert(model);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                var existing = FindByUrl(model.Url);

                if (existing == null)
                {
                    throw;
                }

                model.Id = existing.Id;

                return Update(model);
            }
        }

        private static bool IsUniqueViolation(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                // SQLITE_CONSTRAINT (19) covers unique and primary key violations; 23505 is unique_violation.
                if (current is SqliteException sqlite && sqlite.SqliteErrorCode == 19)
                {
                    return true;
                }

                if (current is PostgresException postgres && string.Equals(postgres.SqlState, "23505", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
