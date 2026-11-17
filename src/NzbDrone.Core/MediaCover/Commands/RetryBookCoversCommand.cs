using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaCover.Commands
{
    public class RetryBookCoversCommand : Command
    {
        public override bool SendUpdatesToClient => false;
        public override bool IsTypeExclusive => true;
    }
}
