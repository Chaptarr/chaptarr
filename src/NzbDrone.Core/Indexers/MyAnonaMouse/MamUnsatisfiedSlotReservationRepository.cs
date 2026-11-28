using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public interface IMamUnsatisfiedSlotReservationRepository : IBasicRepository<MamUnsatisfiedSlotReservation>
    {
    }

    public class MamUnsatisfiedSlotReservationRepository : BasicRepository<MamUnsatisfiedSlotReservation>, IMamUnsatisfiedSlotReservationRepository
    {
        public MamUnsatisfiedSlotReservationRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }
    }
}
