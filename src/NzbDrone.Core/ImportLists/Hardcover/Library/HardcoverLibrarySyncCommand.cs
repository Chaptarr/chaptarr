using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.ImportLists.Hardcover.Library
{
    public class HardcoverLibrarySyncCommand : Command
    {
        public override bool SendUpdatesToClient => true;
        public override bool IsTypeExclusive => true;
    }
}

