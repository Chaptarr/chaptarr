using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class AuthorFolderImportReadyEvent : IEvent
    {
        public Author Author { get; }
        public string Prefix { get; }

        public AuthorFolderImportReadyEvent(Author author, string prefix)
        {
            Author = author;
            Prefix = prefix;
        }
    }
}

