using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public interface ITagExtractorWithDuration : ITagExtractor
    {
        (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path);
    }
}

