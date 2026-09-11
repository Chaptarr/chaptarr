using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class AuthorRefreshCompleteEvent : IEvent
    {
        public Author Author { get; set; }
        public Author RemoteAuthor { get; }

        public AuthorRefreshCompleteEvent(Author author, Author remoteAuthor = null)
        {
            Author = author;
            RemoteAuthor = remoteAuthor;
        }
    }
}
