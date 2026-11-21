using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public enum AudioBookShelfEditionPinOutcome
    {
        Pinned,
        AlreadyPinned,
        NoAsin,
        NoFiles,
        AmbiguousFolder,
        NoMatchingEdition,
        MatchesOtherBook,
        NotTagged
    }

    public interface IAudioBookShelfEditionPinService
    {
        // Pins the Chaptarr edition whose ASIN matches the one AudioBookShelf matched for the item in
        // this folder, and links the folder's files to it. Only ever moves files between editions of the
        // same book; a match on another book is reported, never acted on.
        // connectionTags: the connection's tags; when set, only authors carrying one of them are pinned.
        AudioBookShelfEditionPinOutcome PinFolder(string folder, string mediaType, string asin, ISet<int> connectionTags = null);
    }

    public class AudioBookShelfEditionPinService : IAudioBookShelfEditionPinService
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IEditionService _editionService;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly Logger _logger;

        public AudioBookShelfEditionPinService(IMediaFileService mediaFileService,
                                               IEditionService editionService,
                                               IBookService bookService,
                                               IAuthorService authorService,
                                               Logger logger)
        {
            _mediaFileService = mediaFileService;
            _editionService = editionService;
            _bookService = bookService;
            _authorService = authorService;
            _logger = logger;
        }

        public AudioBookShelfEditionPinOutcome PinFolder(string folder, string mediaType, string asin, ISet<int> connectionTags = null)
        {
            var normalizedAsin = NormalizeAsin(asin);

            if (normalizedAsin.IsNullOrWhiteSpace())
            {
                _logger.Debug("AudioBookShelf: no ASIN on the item for '{0}', nothing to pin", folder);
                return AudioBookShelfEditionPinOutcome.NoAsin;
            }

            if (folder.IsNullOrWhiteSpace())
            {
                return AudioBookShelfEditionPinOutcome.NoFiles;
            }

            var fileEditions = (_mediaFileService.GetFilesWithBasePath(folder, mediaType) ?? new List<BookFile>())
                .Where(f => f != null && f.EditionId > 0)
                .Select(f => (File: f, Edition: _editionService.GetEdition(f.EditionId)))
                .Where(x => x.Edition != null && x.Edition.BookId > 0)
                .ToList();

            if (fileEditions.Count == 0)
            {
                _logger.Debug("AudioBookShelf: no Chaptarr book files under '{0}', nothing to pin", folder);
                return AudioBookShelfEditionPinOutcome.NoFiles;
            }

            var files = fileEditions.Select(x => x.File).ToList();
            var bookIds = fileEditions.Select(x => x.Edition.BookId).Distinct().ToList();

            if (bookIds.Count != 1)
            {
                _logger.Debug("AudioBookShelf: '{0}' holds files of {1} Chaptarr books, not pinning from a single ASIN", folder, bookIds.Count);
                return AudioBookShelfEditionPinOutcome.AmbiguousFolder;
            }

            var bookId = bookIds[0];

            // Pinning relinks every file of the book. If the book also has files in another folder,
            // that folder's AudioBookShelf item could carry a different ASIN and the two would
            // keep flipping the edition, so leave such books alone.
            var folderPrefix = folder.TrimEnd('/', '\\');
            var bookHasFilesElsewhere = (_mediaFileService.GetFilesByBook(bookId) ?? new List<BookFile>())
                .Any(f => f?.Path != null && !IsUnder(f.Path, folderPrefix));

            if (bookHasFilesElsewhere)
            {
                _logger.Debug("AudioBookShelf: the book in '{0}' also has files in other folders, not pinning from one item's ASIN", folder);
                return AudioBookShelfEditionPinOutcome.AmbiguousFolder;
            }

            var providerId = $"az:{normalizedAsin}";
            var editions = _editionService.GetEditionsByBook(bookId) ?? new List<Edition>();
            var target = SelectTarget(editions, files, providerId);
            var book = _bookService.GetBook(bookId);

            if (!IsTagged(book, connectionTags))
            {
                return AudioBookShelfEditionPinOutcome.NotTagged;
            }

            if (target == null)
            {
                return ReportMissingEdition(folder, normalizedAsin, providerId, book);
            }

            var alreadyPinned = target.Monitored &&
                                target.ManualAdd &&
                                !book.AnyEditionOk &&
                                files.All(f => f.EditionId == target.Id);

            if (alreadyPinned)
            {
                _logger.Debug("AudioBookShelf: '{0}' is already pinned to edition {1} (ASIN {2})", book.Title, target, normalizedAsin);
                return AudioBookShelfEditionPinOutcome.AlreadyPinned;
            }

            // Same path as an explicit edition choice in the UI: mark the edition as the manual pick,
            // stop "any edition" from replacing it, and let the book update relink the files.
            _editionService.SetMonitored(target, isManualSelection: true);

            book.Editions = _editionService.GetEditionsByBook(bookId);
            book.AnyEditionOk = false;
            _bookService.UpdateBook(book);

            _logger.Info("AudioBookShelf: pinned '{0}' to edition {1} from the AudioBookShelf ASIN {2}", book.Title, target, normalizedAsin);
            return AudioBookShelfEditionPinOutcome.Pinned;
        }

        private AudioBookShelfEditionPinOutcome ReportMissingEdition(string folder, string asin, string providerId, Book book)
        {
            var otherEdition = book.AuthorId > 0
                ? (_editionService.GetEditionsByAuthor(book.AuthorId) ?? new List<Edition>())
                    .Where(e => e.BookId != book.Id && BookEditionIdentity.EditionMatchesProviderId(e, providerId))
                    .FirstOrDefault(e => _bookService.GetBook(e.BookId)?.MediaType == book.MediaType)
                : null;

            if (otherEdition == null)
            {
                _logger.Debug("AudioBookShelf: '{0}' has no edition with ASIN {1}; leaving its edition as is", book.Title, asin);
                return AudioBookShelfEditionPinOutcome.NoMatchingEdition;
            }

            var otherBook = _bookService.GetBook(otherEdition.BookId);

            _logger.Warn("AudioBookShelf matched '{0}' to ASIN {1}, which is edition {2} of the Chaptarr book '{3}' (id {4}), but the files are linked to '{5}' (id {6}). Chaptarr does not move files between books on its own; re-match them with Manual Import if AudioBookShelf is right.",
                folder,
                asin,
                otherEdition,
                otherBook?.Title,
                otherEdition.BookId,
                book.Title,
                book.Id);

            return AudioBookShelfEditionPinOutcome.MatchesOtherBook;
        }

        // Several editions of a book often share one ASIN (the Audible, Hardcover and Goodreads records
        // of the same release). Prefer the least disruptive match: the edition the files are already
        // linked to, then the monitored one, then the Audible record itself.
        private static Edition SelectTarget(List<Edition> editions, List<BookFile> files, string providerId)
        {
            var matching = editions
                .Where(e => e != null && BookEditionIdentity.EditionMatchesProviderId(e, providerId))
                .ToList();

            if (matching.Count <= 1)
            {
                return matching.FirstOrDefault();
            }

            var linkedEditionIds = files.Select(f => f.EditionId).ToHashSet();

            return matching.FirstOrDefault(e => linkedEditionIds.Contains(e.Id))
                ?? matching.FirstOrDefault(e => e.Monitored)
                ?? matching.FirstOrDefault(e => e.ForeignEditionId?.StartsWith(providerId, StringComparison.OrdinalIgnoreCase) == true)
                ?? matching.First();
        }

        private bool IsTagged(Book book, ISet<int> connectionTags)
        {
            if (connectionTags == null || connectionTags.Count == 0)
            {
                return true;
            }

            var author = book.AuthorId > 0 ? _authorService.GetAuthor(book.AuthorId) : null;
            return author?.Tags != null && author.Tags.Any(connectionTags.Contains);
        }

        private static bool IsUnder(string path, string folder)
        {
            return path.StartsWith(folder + "/", StringComparison.Ordinal) ||
                   path.StartsWith(folder + "\\", StringComparison.Ordinal);
        }

        private static string NormalizeAsin(string asin)
        {
            if (asin.IsNullOrWhiteSpace())
            {
                return null;
            }

            var value = asin.Trim();

            if (value.StartsWith("az:", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(3);
            }

            return value.Trim().ToUpperInvariant();
        }
    }
}
