using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class MoveAuthorMediaCommand : Command
    {
        public int AuthorId { get; set; }
        public string Format { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string PreviewToken { get; set; }
        public List<AuthorMediaMoveFile> Files { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
    }
}
