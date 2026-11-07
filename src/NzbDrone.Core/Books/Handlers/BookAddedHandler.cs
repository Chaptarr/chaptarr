using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public class BookAddedHandler : IHandle<BookAddedEvent>
    {
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ICheckIfAuthorShouldBeRefreshed _checkIfAuthorShouldBeRefreshed;

        public BookAddedHandler(
            IManageCommandQueue commandQueueManager,
            ICheckIfAuthorShouldBeRefreshed checkIfAuthorShouldBeRefreshed)
        {
            _commandQueueManager = commandQueueManager;
            _checkIfAuthorShouldBeRefreshed = checkIfAuthorShouldBeRefreshed;
        }

        public void Handle(BookAddedEvent message)
        {
            if (message.DoRefresh && message.Book.AuthorId > 0)
            {
                var author = message.Book.Author;
                var refreshMetadata = author == null || _checkIfAuthorShouldBeRefreshed.ShouldRefresh(author);
                var command = new RefreshAuthorCommand(
                    message.Book.AuthorId,
                    refreshMetadata: refreshMetadata,
                    rescanFolders: true);

                // A book add can expose media that was already on disk. Keep the
                // scan even when the metadata freshness gate skips a provider call.
                command.RescanWhenMetadataUnchanged = true;
                _commandQueueManager.Push(command);
            }
        }
    }
}
