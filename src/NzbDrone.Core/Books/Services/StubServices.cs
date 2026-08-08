// Temporary stub implementations to allow compilation
// These will be replaced as we complete the refactoring

using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Services
{
    // Stub for TrigramMaintenanceService
    public interface ITrigramMaintenanceService
    {
        void UpdateAllTrigrams();
        void UpdateAuthorTrigrams(int authorId);
        void UpdateBookTrigrams(int bookId);
    }

    public class TrigramMaintenanceService : ITrigramMaintenanceService,
        IHandle<AuthorAddedEvent>,
        IHandle<AuthorEditedEvent>,
        IHandle<BookAddedEvent>,
        IHandle<BookEditedEvent>
    {
        private readonly Logger _logger;

        public TrigramMaintenanceService(Logger logger)
        {
            _logger = logger;
        }

        public void UpdateAllTrigrams()
        {
            _logger.Debug("Trigram update skipped - old identification system disabled");
        }

        public void UpdateAuthorTrigrams(int authorId)
        {
            _logger.Debug("Author trigram update skipped - old identification system disabled");
        }

        public void UpdateBookTrigrams(int bookId)
        {
            _logger.Debug("Book trigram update skipped - old identification system disabled");
        }

        public void Handle(AuthorAddedEvent message)
        {
            // Skip
        }

        public void Handle(AuthorEditedEvent message)
        {
            // Skip
        }

        public void Handle(BookAddedEvent message)
        {
            // Skip
        }

        public void Handle(BookEditedEvent message)
        {
            // Skip
        }
    }

}
