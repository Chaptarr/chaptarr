using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class AuthorRefreshCompleteEvent : IEvent
    {
        public Author Author { get; set; }

        // Whether this refresh actually changed anything (author fields or its books). Defaults to true
        // so callers that don't track this (most of them - see RefreshAuthorService for the one that does)
        // keep today's always-reconcile behavior.
        public bool AnyChanges { get; set; }

        public AuthorRefreshCompleteEvent(Author author, bool anyChanges = true)
        {
            Author = author;
            AnyChanges = anyChanges;
        }
    }
}
