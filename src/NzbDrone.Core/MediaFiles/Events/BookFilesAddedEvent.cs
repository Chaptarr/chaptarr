using System.Collections.Generic;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.MediaFiles.Events
{
    // Batch event for when multiple BookFile records are added together (e.g., a book unit)
    public class BookFilesAddedEvent : IEvent
    {
        public IReadOnlyList<BookFile> BookFiles { get; }

        public BookFilesAddedEvent(IReadOnlyList<BookFile> bookFiles)
        {
            BookFiles = bookFiles;
        }
    }
}

