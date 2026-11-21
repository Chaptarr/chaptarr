using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class AuthorEditedEvent : IEvent
    {
        public Author Author { get; private set; }
        public Author OldAuthor { get; private set; }

        public bool IdentityReconciliation { get; }

        public AuthorEditedEvent(Author author, Author oldAuthor, bool identityReconciliation = false)
        {
            IdentityReconciliation = identityReconciliation;
            Author = author;
            OldAuthor = oldAuthor;
        }
    }
}
