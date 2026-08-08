using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaCover.Commands
{
    public class DownloadBookMediaCommand : Command
    {
        public int BookId { get; set; }
        public bool ForceDownload { get; set; }

        public DownloadBookMediaCommand()
        {
        }

        public DownloadBookMediaCommand(int bookId, bool forceDownload = false)
        {
            BookId = bookId;
            ForceDownload = forceDownload;
        }

        public override bool SendUpdatesToClient => true;
    }
}
