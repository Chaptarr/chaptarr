using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaCover.Commands
{
    public class DownloadBookMediaCommand : Command
    {
        public int BookId { get; set; }
        public List<int> BookIds { get; set; } = new List<int>();
        public bool ForceDownload { get; set; }

        public DownloadBookMediaCommand()
        {
        }

        public DownloadBookMediaCommand(int bookId, bool forceDownload = false)
        {
            BookId = bookId;
            ForceDownload = forceDownload;
        }

        public DownloadBookMediaCommand(IEnumerable<int> bookIds)
        {
            BookIds = bookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
        }

        public override bool SendUpdatesToClient => true;
    }
}
