using System;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Http
{
    public interface ICachedHttpResponseRepository : IBasicRepository<CachedHttpResponse>
    {
        CachedHttpResponse FindByUrl(string url);
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
            // Concurrent lookups of the same URL can each insert a row (the URL index is not unique). SingleOrDefault
            // then threw "Sequence contains more than one element" on every later lookup of that URL, so prefer the
            // newest row and remove the stale duplicates.
            var rows = Query(x => x.Url == url)
                .OrderByDescending(x => x.LastRefresh)
                .ThenByDescending(x => x.Id)
                .ToList();

            // Best effort: a failed cleanup (locked or read-only cache database) must not fail a lookup that
            // already has its answer. The duplicates are retried on the next lookup of this URL.
            foreach (var stale in rows.Skip(1))
            {
                try
                {
                    Delete(stale);
                }
                catch (Exception ex)
                {
                    NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Could not remove a stale duplicate cache row for {0}", url);
                    break;
                }
            }

            return rows.FirstOrDefault();
        }
    }
}
