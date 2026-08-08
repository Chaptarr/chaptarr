using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class BookFileUpdatedEvent : IEvent
    {
        public BookFile BookFile { get; }

        public BookFileUpdatedEvent(BookFile bookFile)
        {
            BookFile = bookFile;
        }
    }
}
