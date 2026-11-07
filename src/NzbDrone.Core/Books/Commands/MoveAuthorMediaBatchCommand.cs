using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class MoveAuthorMediaBatchCommand : Command
    {
        public string Format { get; set; }
        public string DestinationRootPath { get; set; }
        public string PreviewToken { get; set; }
        public List<AuthorMediaMoveBatchItem> Authors { get; set; } = new List<AuthorMediaMoveBatchItem>();

        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
    }
}
