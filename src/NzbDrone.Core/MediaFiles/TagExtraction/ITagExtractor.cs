using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public interface ITagExtractor
    {
        bool IsAvailable { get; }
        int Priority { get; } // Lower is better
        string Name { get; }

        // Extract all textual tags as multi-value dictionary. Non-text binary fields should be represented by a placeholder string.
        Dictionary<string, List<string>> ExtractTags(string path);
    }
}

