using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class UpdateFtsCommand : Command
    {
        public override bool SendUpdatesToClient => false;
        public override bool RequiresDiskAccess => false;
        public override bool IsExclusive => false;

        // This runs on startup to ensure FTS is properly normalized
        public override string CompletionMessage => "FTS update completed";
    }
}
