using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles
{
    public class RenameBookFilePreview
    {
        public int AuthorId { get; set; }
        public int BookId { get; set; }
        public int EditionId { get; set; }
        public List<int> TrackNumbers { get; set; }
        public int BookFileId { get; set; }
        public string ExistingPath { get; set; }
        public string NewPath { get; set; }
        public bool CanOrganize { get; set; }
        public string Reason { get; set; }
    }
}
