using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class AuthorRefreshCompleteEvent : IEvent
    {
        public Author Author { get; set; }
        public Author RemoteAuthor { get; }

        // Whether this refresh actually changed anything (author fields or its books). Defaults to true so
        // callers that do not track this keep the always-reconcile behavior.
        public bool AnyChanges { get; set; }

        public AuthorRefreshCompleteEvent(Author author, Author remoteAuthor = null, bool anyChanges = true)
        {
            Author = author;
            RemoteAuthor = remoteAuthor;
            AnyChanges = anyChanges;
        }
    }
}
