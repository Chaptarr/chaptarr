using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    public class ImportCommandWorkCleanupHandler : IHandle<CommandExecutedEvent>
    {
        public void Handle(CommandExecutedEvent message)
        {
            try
            {
                var commandId = message?.Command?.Id ?? 0;
                if (commandId <= 0)
                {
                    return;
                }

                ImportCommandWorkTracker.Clear(commandId);
            }
            catch
            {
                // best-effort only
            }
        }
    }
}

