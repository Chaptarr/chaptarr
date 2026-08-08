using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public class BookAddedHandler : IHandle<BookAddedEvent>
    {
        private readonly IManageCommandQueue _commandQueueManager;

        public BookAddedHandler(IManageCommandQueue commandQueueManager)
        {
            _commandQueueManager = commandQueueManager;
        }

        public void Handle(BookAddedEvent message)
        {
            if (message.DoRefresh && message.Book.AuthorId > 0)
            {
                // When a new book is added, we need both metadata refresh and folder scan to find its files
                _commandQueueManager.Push(new RefreshAuthorCommand(message.Book.AuthorId, refreshMetadata: true, rescanFolders: true, forceRefresh: true));
            }
        }
    }
}
