using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class NarratorUpdatedEvent : IEvent
    {
        public Narrator Narrator { get; private set; }
        public Narrator OldNarrator { get; private set; }

        public NarratorUpdatedEvent(Narrator narrator, Narrator oldNarrator)
        {
            Narrator = narrator;
            OldNarrator = oldNarrator;
        }
    }
}
