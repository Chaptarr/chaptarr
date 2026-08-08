using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Handlers
{
    public class NarratorAddedHandler : IHandle<NarratorAddedEvent>
    {
        public void Handle(NarratorAddedEvent message)
        {
            // Narrator metadata no longer uses external enrichment providers.
        }
    }
}
