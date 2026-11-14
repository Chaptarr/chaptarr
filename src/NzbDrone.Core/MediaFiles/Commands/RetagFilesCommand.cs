using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class RetagFilesCommand : RetagCommand
    {
        public int AuthorId { get; set; }
        public List<int> Files { get; set; }

        public RetagFilesCommand()
        {
        }

        public RetagFilesCommand(int authorId, List<int> files)
        {
            AuthorId = authorId;
            Files = files;
        }
    }
}
