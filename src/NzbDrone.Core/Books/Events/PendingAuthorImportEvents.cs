using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class PendingAuthorImportQueuedEvent : IEvent
    {
        public PendingAuthorImport PendingImport { get; private set; }

        public PendingAuthorImportQueuedEvent(PendingAuthorImport pendingImport)
        {
            PendingImport = pendingImport;
        }
    }

    public class PendingAuthorImportSucceededEvent : IEvent
    {
        public PendingAuthorImport PendingImport { get; private set; }
        public Author ImportedAuthor { get; private set; }

        public PendingAuthorImportSucceededEvent(PendingAuthorImport pendingImport, Author importedAuthor)
        {
            PendingImport = pendingImport;
            ImportedAuthor = importedAuthor;
        }
    }

    public class PendingAuthorImportFailedEvent : IEvent
    {
        public PendingAuthorImport PendingImport { get; private set; }

        public PendingAuthorImportFailedEvent(PendingAuthorImport pendingImport)
        {
            PendingImport = pendingImport;
        }
    }

    public class PendingAuthorImportCancelledEvent : IEvent
    {
        public PendingAuthorImport PendingImport { get; private set; }

        public PendingAuthorImportCancelledEvent(PendingAuthorImport pendingImport)
        {
            PendingImport = pendingImport;
        }
    }

    public class PendingAuthorImportRetryingEvent : IEvent
    {
        public PendingAuthorImport PendingImport { get; private set; }

        public PendingAuthorImportRetryingEvent(PendingAuthorImport pendingImport)
        {
            PendingImport = pendingImport;
        }
    }
}
