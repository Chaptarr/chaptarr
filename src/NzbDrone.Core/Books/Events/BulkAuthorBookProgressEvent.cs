using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class BulkAuthorBookProgressEvent : IEvent
    {
        public int CommandId { get; }
        public string Message { get; }

        public BulkAuthorBookProgressEvent(int commandId, string message)
        {
            CommandId = commandId;
            Message = message;
        }
    }
}
