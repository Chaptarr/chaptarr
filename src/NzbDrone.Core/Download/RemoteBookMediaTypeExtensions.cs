using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser.Model;
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

        public static List<Book> GetBooksMatchingReleaseMediaType(this RemoteBook remoteBook)
        {
            if (remoteBook?.Books == null || remoteBook.Books.Count == 0)
            {
                return new List<Book>();
            }

            var books = remoteBook.Books.Where(b => b != null).ToList();
            var mediaType = QualityMediaTypeHelper.DetectMediaType(remoteBook.ParsedBookInfo?.Quality?.Quality, remoteBook.Release);

            if (!mediaType.HasValue)
            {
                return books;
            }

            var matchingBooks = books.Where(b => b.MediaType == mediaType.Value).ToList();
            return matchingBooks.Any() ? matchingBooks : books;
        }
    }
}
