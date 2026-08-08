using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.SignalR
{
    public class BulkAuthorBookProgressConnection : IHandle<BulkAuthorBookProgressEvent>
    {
        private readonly IBroadcastSignalRMessage _signalRBroadcaster;

        public BulkAuthorBookProgressConnection(IBroadcastSignalRMessage signalRBroadcaster)
        {
            _signalRBroadcaster = signalRBroadcaster;
        }

        public void Handle(BulkAuthorBookProgressEvent message)
        {
            if (message == null || message.CommandId <= 0 || string.IsNullOrWhiteSpace(message.Message))
            {
                return;
            }

            _signalRBroadcaster.BroadcastMessage(new SignalRMessage
            {
                Name = "BulkAuthorBookProgress",
                Body = new
                {
                    commandId = message.CommandId,
                    message = message.Message
                }
            });
        }
    }
}
