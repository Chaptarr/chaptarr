using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class NarratorAddedEvent : IEvent
    {
        public Narrator Narrator { get; set; }

        public NarratorAddedEvent(Narrator narrator)
        {
            Narrator = narrator;
        }
    }
}
