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

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
        public override bool IsLongRunning => true;
    }
}
