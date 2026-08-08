using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    public class ImportSessionProgressCleanupHandler : IHandle<ImportStageProgressEvent>,
                                                       IHandle<CommandExecutedEvent>
    {
        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(ImportStageProgressEvent message)
        {
            var commandId = message?.CommandId ?? 0;
            if (commandId <= 0)
            {
                return;
            }

            if (message.Stage == ImportStage.ImportComplete)
            {
                ImportSessionProgressTracker.Complete(commandId);
            }
            else
            {
                ImportSessionProgressTracker.Activate(commandId);
            }
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(CommandExecutedEvent message)
        {
            var commandId = message?.Command?.Id ?? 0;
            if (commandId <= 0)
            {
                return;
            }

            // Command completion is the failure/cancellation fallback when no ImportComplete
            // progress event was published. Complete before clearing progress counters so every
            // later event handler observes the shared import session as terminal.
            ImportSessionProgressTracker.Complete(commandId);
            ImportSessionProgressTracker.Clear(commandId);
        }
    }
}
