using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class BulkSyncFormatMonitoringCommand : Command
    {
        public BulkSyncFormatMonitoringCommand()
        {
            AuthorIds = new List<int>();
            Trigger = CommandTrigger.Manual;
        }

        public BulkSyncFormatMonitoringCommand(List<int> authorIds)
            : this()
        {
            AuthorIds = authorIds ?? new List<int>();
        }

        public List<int> AuthorIds { get; set; }
        // Zero is an ordinary sync. Positive values resume initial setup; its retry count is persisted in AddOptions.
        public int InitialSetupAttempt { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
        public override bool IsLongRunning => true;
        public override bool IsTypeExclusive => true;
    }
}
