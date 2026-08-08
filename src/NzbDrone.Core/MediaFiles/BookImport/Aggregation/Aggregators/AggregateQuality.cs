using System.IO;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport.Aggregation.Aggregators
{
    public class AggregateQuality : IAggregate<LocalBook>
    {
        private readonly Logger _logger;

        public AggregateQuality(Logger logger)
        {
            _logger = logger;
        }

        public LocalBook Aggregate(LocalBook localTrack, bool otherFiles)
        {
            QualityModel quality = null;

            if (quality == null)
            {
                quality = localTrack.FolderTrackInfo?.Quality;
            }

            if (quality == null)
            {
                quality = localTrack.DownloadClientBookInfo?.Quality;
            }

            // CRITICAL FIX: If still no quality detected, use file extension as fallback
            // This fixes M4B files being detected as "Unknown Text" instead of proper quality
            if (quality == null || quality.Quality == Quality.Unknown)
            {
                var fileExtension = Path.GetExtension(localTrack.Path);
                var extensionQuality = MediaFileExtensions.GetQualityForExtension(fileExtension);

                if (extensionQuality != Quality.Unknown)
                {
                    _logger.Debug("Using file extension quality detection: {0} -> {1}", fileExtension, extensionQuality);
                    quality = new QualityModel(extensionQuality, new Revision(version: 1));
                    quality.QualityDetectionSource = QualityDetectionSource.Extension;
                }
            }

            localTrack.Quality = quality;
            return localTrack;
        }
    }
}
