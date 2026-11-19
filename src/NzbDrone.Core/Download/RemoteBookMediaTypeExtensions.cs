using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Download
{
    public static class RemoteBookMediaTypeExtensions
    {
        public static BookMediaType GetPreferredMediaType(this RemoteBook remoteBook)
        {
            if (remoteBook?.Books == null || remoteBook.Books.Count == 0)
            {
                return BookMediaType.Audiobook;
            }

            var hasAudiobooks = remoteBook.Books.Any(b => b.MediaType == BookMediaType.Audiobook);
            var hasEbooks = remoteBook.Books.Any(b => b.MediaType == BookMediaType.Ebook);

            if (hasAudiobooks && !hasEbooks)
            {
                return BookMediaType.Audiobook;
            }

            if (hasEbooks && !hasAudiobooks)
            {
                return BookMediaType.Ebook;
            }

            // Ambiguous (mixed media types). Prefer audiobook to match historical behavior.
            return BookMediaType.Audiobook;
        }

        public static BookMediaType? GetMediaType(this RemoteBook remoteBook)
        {
            var mediaTypes = (remoteBook?.Books ?? new List<Book>()).Where(book => book != null)
                .Select(book => book.MediaType).Distinct().Take(2).ToList();

            // Resolved target rows own the media side; format evidence only resolves unscoped releases.
            return mediaTypes.Count == 1
                ? mediaTypes[0]
                : QualityMediaTypeHelper.DetectMediaType(remoteBook?.ParsedBookInfo?.Quality?.Quality, remoteBook?.Release);
        }

        public static QualityProfile GetQualityProfile(this RemoteBook remoteBook)
        {
            var mediaType = remoteBook.GetMediaType();
            return mediaType.HasValue ? remoteBook?.Author?.GetQualityProfileForMediaType(mediaType.Value) : null;
        }

        public static List<Book> GetBooksMatchingReleaseMediaType(this RemoteBook remoteBook)
        {
            if (remoteBook?.Books == null || remoteBook.Books.Count == 0)
            {
                return new List<Book>();
            }

            var books = remoteBook.Books.Where(b => b != null).ToList();
            var mediaType = remoteBook.GetMediaType();

            if (!mediaType.HasValue)
            {
                return books;
            }

            return books.Where(b => b.MediaType == mediaType.Value).ToList();
        }
    }
}
