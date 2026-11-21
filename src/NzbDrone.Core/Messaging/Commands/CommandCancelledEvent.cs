using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Messaging.Commands
{
    /// <summary>
    /// Published when a user cancels a command. <see cref="WasQueued"/> is true when the command was
    /// removed before it started, so its handler never ran and never saw its cancellation token.
    /// </summary>
    public class CommandCancelledEvent : IEvent
    {
        public CommandModel Command { get; }
        public bool WasQueued { get; }

        public CommandCancelledEvent(CommandModel command, bool wasQueued)
        {
            Command = command;
            WasQueued = wasQueued;
        }
    }
}
