using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class AuthorSearchCommand : Command
    {
        public override bool SupportsCancellation => true;

        public int AuthorId { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
