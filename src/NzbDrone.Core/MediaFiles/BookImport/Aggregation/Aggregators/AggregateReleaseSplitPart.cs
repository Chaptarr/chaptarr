using System.IO;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport.Aggregation.Aggregators
{
    /// <summary>
    /// Marks files that come from one part of a split release ("Fourth Wing (Part 1 of 2)").
    /// The release title decides; the folder name is next. A file name only counts when it says
    /// "Part N of M" outright, because inside a multi-file release "(3 of 12)" numbers tracks.
    /// </summary>
    public class AggregateReleaseSplitPart : IAggregate<LocalBook>
    {
        public LocalBook Aggregate(LocalBook localTrack, bool otherFiles)
        {
            var split = ReleasePackDetector.DetectSplitPart(localTrack.DownloadClientBookInfo?.ReleaseTitle) ??
                        ReleasePackDetector.DetectSplitPart(localTrack.FolderTrackInfo?.ReleaseTitle);

            if (split == null && localTrack.Path.IsNotNullOrWhiteSpace())
            {
                split = ReleasePackDetector.DetectSplitPart(Path.GetFileNameWithoutExtension(localTrack.Path), fileName: true);
            }

            localTrack.ReleasePart = split?.Part ?? 0;
            localTrack.ReleasePartCount = split?.Count ?? 0;

            return localTrack;
        }
    }
}
