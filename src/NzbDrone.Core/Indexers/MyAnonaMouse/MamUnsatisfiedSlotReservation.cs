using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MamUnsatisfiedSlotReservation : ModelBase
    {
        public int IndexerId { get; set; }
        public string TorrentId { get; set; }
        public DateTime ReservedUtc { get; set; }
        public DateTime? FirstReservedUtc { get; set; }
        public DateTime? ConfirmedUtc { get; set; }
    }
}
