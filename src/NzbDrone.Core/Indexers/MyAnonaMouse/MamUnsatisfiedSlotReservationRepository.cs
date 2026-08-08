using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public interface IMamUnsatisfiedSlotReservationRepository : IBasicRepository<MamUnsatisfiedSlotReservation>
    {
        List<MamUnsatisfiedSlotReservation> AllForIndexer(int indexerId);
        MamUnsatisfiedSlotReservation Find(int indexerId, string torrentId);
    }

    public class MamUnsatisfiedSlotReservationRepository : BasicRepository<MamUnsatisfiedSlotReservation>, IMamUnsatisfiedSlotReservationRepository
    {
        public MamUnsatisfiedSlotReservationRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MamUnsatisfiedSlotReservation> AllForIndexer(int indexerId)
        {
            return Query(r => r.IndexerId == indexerId);
        }

        public MamUnsatisfiedSlotReservation Find(int indexerId, string torrentId)
        {
            if (indexerId <= 0 || string.IsNullOrWhiteSpace(torrentId))
            {
                return null;
            }

            return Query(r => r.IndexerId == indexerId && r.TorrentId == torrentId).SingleOrDefault();
        }
    }
}
