using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class NarratorDeletedEvent : IEvent
    {
        public Narrator Narrator { get; private set; }

        public NarratorDeletedEvent(Narrator narrator)
        {
            Narrator = narrator;
        }
    }
}
