using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class AuthorScannedEvent : IEvent
    {
        public Author Author { get; private set; }
        public bool CanCompleteInitialSetup { get; }

        public AuthorScannedEvent(Author author, bool canCompleteInitialSetup = true)
        {
            Author = author;
            CanCompleteInitialSetup = canCompleteInitialSetup;
        }
    }
}
