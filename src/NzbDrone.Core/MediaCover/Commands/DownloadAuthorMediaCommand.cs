using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaCover.Commands
{
    public class DownloadAuthorMediaCommand : Command
    {
        public int AuthorId { get; set; }
        public bool ForceDownload { get; set; }

        public DownloadAuthorMediaCommand()
        {
        }

        public DownloadAuthorMediaCommand(int authorId, bool forceDownload = false)
        {
            AuthorId = authorId;
            ForceDownload = forceDownload;
        }

        public override bool SendUpdatesToClient => true;
    }
}
