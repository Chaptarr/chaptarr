using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class RetryAuthorRefreshCommand : Command
    {
        public override bool SupportsCancellation => true;
        public override bool IsTypeExclusive => true;
        public override bool RequiresDiskAccess => false;
    }
}
