using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class CutoffUnmetBookSearchCommand : Command
    {
        public int? AuthorId { get; set; }
        public string MediaType { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool IsTypeExclusive => !AuthorId.HasValue;

        public CutoffUnmetBookSearchCommand()
        {
        }

        public CutoffUnmetBookSearchCommand(int authorId)
        {
            AuthorId = authorId;
        }
    }
}
