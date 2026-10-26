using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class InteractiveAuthorSearchCommand : Command
    {
        public string SearchId { get; set; }
        public int AuthorId { get; set; }
        public BookMediaType MediaType { get; set; }

        public override bool UpdateScheduledTask => false;
        public override bool IsLongRunning => true;
    }
}
