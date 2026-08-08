using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class AuthorScanCompletedEvent : IEvent
    {
        public Author Author { get; private set; }

        public AuthorScanCompletedEvent(Author author)
        {
            Author = author;
        }
    }
}
