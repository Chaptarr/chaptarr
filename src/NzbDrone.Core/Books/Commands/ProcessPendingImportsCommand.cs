using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class ProcessPendingImportsCommand : Command
    {
        public override bool SendUpdatesToClient => false;
        public override bool UpdateScheduledTask => !ContinueUntilEmpty;
        public override bool RequiresDiskAccess => false;
        public override bool IsExclusive => false;
        public override bool IsTypeExclusive => true;

        public int BatchSize { get; set; } = 10;
        public bool ContinueUntilEmpty { get; set; }
        public int Continuation { get; set; }

        public ProcessPendingImportsCommand()
        {
            // Low priority background task
        }
    }
}
