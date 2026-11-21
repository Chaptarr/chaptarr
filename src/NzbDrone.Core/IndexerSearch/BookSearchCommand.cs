using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class BookSearchCommand : Command
    {
        public List<int> BookIds { get; set; }

        public bool MonitoredOnly { get; set; }

        public static CommandTrigger SearchOnAddTrigger(Book book) => SearchOnAddTrigger(book.Author, book.MediaType);

        public static CommandTrigger SearchOnAddTrigger(Author author, BookMediaType mediaType) =>
            author != null && !author.IsMonitoredForMediaType(mediaType == BookMediaType.Audiobook)
                ? CommandTrigger.Manual : CommandTrigger.Unspecified;

        public override bool SendUpdatesToClient => true;

        public BookSearchCommand()
        {
        }

        public BookSearchCommand(List<int> bookIds)
        {
            BookIds = bookIds;
        }
    }
}
