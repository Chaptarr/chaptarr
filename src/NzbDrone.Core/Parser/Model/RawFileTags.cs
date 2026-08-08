using System.Collections.Generic;

namespace NzbDrone.Core.Parser.Model
{
    public class RawFileTags
    {
        public Dictionary<string, List<string>> AllTags { get; set; }
    }
}
