using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaCover;

namespace NzbDrone.Core.MetadataSource
{
    public interface INarratorPhotoService
    {
        void FetchAndStoreNarratorPhotos(int narratorId, string narratorName);
        void FetchAndStoreNarratorPhotosFromBook(int bookId, string authorName, string bookTitle);
        List<MediaCover.MediaCover> GetNarratorPhotosFromMetadata(string narratorName);
    }

    public class NarratorPhotoService : INarratorPhotoService
    {
        private readonly Logger _logger;

        public NarratorPhotoService(Logger logger)
        {
            _logger = logger;
        }

        public void FetchAndStoreNarratorPhotos(int narratorId, string narratorName)
        {
            if (narratorName.IsNullOrWhiteSpace())
            {
                _logger.Debug("Missing narrator name for photo fetch: {0}", narratorId);
                return;
            }

            _logger.Debug("Narrator photo fetch is disabled for: {0} (ID: {1})", narratorName, narratorId);
        }

        public void FetchAndStoreNarratorPhotosFromBook(int bookId, string authorName, string bookTitle)
        {
            if (authorName.IsNullOrWhiteSpace() || bookTitle.IsNullOrWhiteSpace())
            {
                _logger.Debug("Missing author name or book title for narrator photo fetch from book: {0}", bookId);
                return;
            }

            _logger.Debug("Narrator photo fetch from book is disabled for: {0} - {1} (Book ID: {2})", authorName, bookTitle, bookId);
        }

        public List<MediaCover.MediaCover> GetNarratorPhotosFromMetadata(string narratorName)
        {
            if (narratorName.IsNullOrWhiteSpace())
            {
                return new List<MediaCover.MediaCover>();
            }

            _logger.Debug("Narrator photo metadata lookup is disabled for: {0}", narratorName);
            return new List<MediaCover.MediaCover>();
        }
    }
}
