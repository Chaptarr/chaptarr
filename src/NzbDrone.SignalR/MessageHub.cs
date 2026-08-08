using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.SignalR
{
    public class SignalRMessageBroadcaster : IBroadcastSignalRMessage
    {
        private readonly IHubContext<MessageHub> _hubContext;

        public SignalRMessageBroadcaster(IHubContext<MessageHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task BroadcastMessage(SignalRMessage message)
        {
            await _hubContext.Clients.All.SendAsync("receiveMessage", message);
        }

        public bool IsConnected => MessageHub.IsConnected;
    }

    public class MessageHub : Hub
    {
        private const int MaxConnections = 100;
        private static HashSet<string> _connections = new HashSet<string>();

        public static bool IsConnected
        {
            get
            {
                lock (_connections)
                {
                    return _connections.Count != 0;
                }
            }
        }

        public override async Task OnConnectedAsync()
        {
            var accepted = false;

            lock (_connections)
            {
                if (_connections.Count < MaxConnections)
                {
                    accepted = true;
                    _connections.Add(Context.ConnectionId);
                }
            }

            if (!accepted)
            {
                Context.Abort();
                return;
            }

            var message = new SignalRMessage
            {
                Name = "version",
                Body = new
                {
                    Version = BuildInfo.Version.ToString()
                }
            };

            await Clients.Caller.SendAsync("receiveMessage", message);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            lock (_connections)
            {
                _connections.Remove(Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
