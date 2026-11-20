using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class RetagAuthorCommand : RetagCommand
    {
        public List<int> AuthorIds { get; set; }
        public string MediaType { get; set; }
        public RetagAuthorCommand()
        {
            AuthorIds = new List<int>();
        }
    }
}
