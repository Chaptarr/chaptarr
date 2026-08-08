using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Books
{
    public interface IAuthorMediaTypeRemovalService
    {
        void RemoveMediaType(int authorId, BookMediaType mediaType, bool deleteFiles);
    }

    public class AuthorMediaTypeRemovalService : IAuthorMediaTypeRemovalService
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public AuthorMediaTypeRemovalService(IAuthorService authorService, IBookService bookService, Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _logger = logger;
        }

        public void RemoveMediaType(int authorId, BookMediaType mediaType, bool deleteFiles)
        {
            var author = _authorService.GetAuthor(authorId);
            if (author == null)
            {
                _logger.Debug("RemoveMediaType called for missing author ID {0}; ignoring", authorId);
                return;
            }

            var books = _bookService.GetBooksByAuthor(authorId) ?? new List<Book>();

            var otherMediaType = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            var otherBooksExist = books.Any(b => b.MediaType == otherMediaType);
            var otherRootFolderPath = otherMediaType == BookMediaType.Audiobook ? author.AudiobookRootFolderPath : author.EbookRootFolderPath;
            var otherRootConfigured = otherRootFolderPath.IsNotNullOrWhiteSpace();

            // If the author is only configured for the requested media type, a scoped delete becomes a full delete.
            if (!otherBooksExist && !otherRootConfigured)
            {
                _authorService.DeleteAuthor(authorId, deleteFiles, false);
                return;
            }

            // Delete only the selected media-type books.
            foreach (var book in books.Where(b => b.MediaType == mediaType).ToList())
            {
                _bookService.DeleteBook(book.Id, deleteFiles, false);
            }

            ClearMediaTypeSettings(author, mediaType);
            _authorService.UpdateAuthor(author);
        }

        private static void ClearMediaTypeSettings(Author author, BookMediaType mediaType)
        {
            if (mediaType == BookMediaType.Audiobook)
            {
                // NOTE: AudiobookMetadataProfileId is the refresh gate for remote audiobook children.
                // RefreshAuthorService.GetRemoteChildren() treats "no audiobook metadata profile" as "audiobooks disabled"
                // and will otherwise include remote audiobooks again on refresh.
                author.AudiobookMetadataProfileId = null;
                author.AudiobookRootFolderPath = null;
                author.AudiobookPath = null;
                author.AudiobookQualityProfileId = null;
                author.AudiobookMonitorExisting = null;
                author.AudiobookMonitorFuture = null;
                author.AudiobookSettingsManuallyOverridden = false;
            }
            else
            {
                // NOTE: EbookMetadataProfileId is the refresh gate for remote ebook children.
                author.EbookMetadataProfileId = null;
                author.EbookRootFolderPath = null;
                author.EbookPath = null;
                author.EbookQualityProfileId = null;
                author.EbookMonitorExisting = null;
                author.EbookMonitorFuture = null;
                author.EbookSettingsManuallyOverridden = false;
            }
        }
    }
}

