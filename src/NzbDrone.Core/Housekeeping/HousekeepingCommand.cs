using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Housekeeping
{
    public class HousekeepingCommand : Command
    {
        // Scratch results belong to the running scan until its inventory handoff finishes.
        public override bool RequiresDiskAccess => true;
    }
}
