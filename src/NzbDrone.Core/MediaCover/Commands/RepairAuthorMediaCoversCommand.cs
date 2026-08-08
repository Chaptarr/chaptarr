using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaCover.Commands
{
    public class RepairAuthorMediaCoversCommand : Command
    {
        public override bool SendUpdatesToClient => false;
        public override bool IsTypeExclusive => true;
        public override bool IsLongRunning => true;
        public override string CompletionMessage => "Author cover repair completed";
    }
}
