using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public interface IBookUnitDestinationService
    {
        string BuildRootUnitKeyWithExtension(string anyFilePathInUnit, string editionTitle, BookMediaType mediaType);

        (int BookId, int EditionId) ResolveDestinationForUnit(Book canonicalBook, Edition canonicalEdition, string unitKey);
    }

    public class BookUnitDestinationService : IBookUnitDestinationService
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMainDatabase _mainDatabase;
        private readonly IRootFolderService _rootFolderService;
        private readonly IAuthorService _authorService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        // Cache per canonical book + unit so we don't create multiple clones for the same physical unit.
        private readonly ConcurrentDictionary<string, (int BookId, int EditionId)> _unitDestCache =
            new ConcurrentDictionary<string, (int BookId, int EditionId)>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _renamedEpubReservations =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public BookUnitDestinationService(
            IMediaFileService mediaFileService,
            IBookService bookService,
            IEditionService editionService,
            IMainDatabase mainDatabase,
            IDiskProvider diskProvider,
            Logger logger,
            IRootFolderService rootFolderService = null,
            IAuthorService authorService = null)
        {
            _mediaFileService = mediaFileService;
            _bookService = bookService;
            _editionService = editionService;
            _mainDatabase = mainDatabase;
            _rootFolderService = rootFolderService;
            _authorService = authorService;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public string BuildRootUnitKeyWithExtension(string anyFilePathInUnit, string editionTitle, BookMediaType mediaType)
        {
            try
            {
                var baseKey = BookCoalescingHelper.BuildRootUnitKey(anyFilePathInUnit, editionTitle, mediaType);
                var ext = Path.GetExtension(anyFilePathInUnit) ?? string.Empty;
                if (BookCoalescingHelper.IsStandaloneUnitExtension(ext))
                {
                    if (mediaType == BookMediaType.Ebook && ext.Equals(".epub", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{baseKey}|{ext}".ToLowerInvariant();
                    }

                    return baseKey.ToLowerInvariant();
                }

                return $"{baseKey}|{ext}".ToLowerInvariant();
            }
            catch
            {
                return anyFilePathInUnit ?? string.Empty;
            }
        }

        public (int BookId, int EditionId) ResolveDestinationForUnit(Book canonicalBook, Edition canonicalEdition, string unitKey)
        {
            if (canonicalBook == null || canonicalBook.Id <= 0 || canonicalEdition == null || canonicalEdition.Id <= 0)
            {
                throw new ArgumentException("Canonical book/edition must be provided for destination resolution");
            }

            // Calibre keeps every format on one record; never clone per-unit copies in calibre roots.
            if (IsCalibreLibraryBook(canonicalBook))
            {
                var calibreEditionId = ResolveEditionIdForDestination(canonicalBook, canonicalEdition);
                return (canonicalBook.Id, calibreEditionId);
            }

            unitKey = unitKey ?? string.Empty;
            var unitKeyHash = ComputeUnitKeyHash(unitKey);
            var canonicalEditions = _editionService.GetEditionsByBook(canonicalBook.Id) ?? new List<Edition>();
            var conflictingProtectedEdition = EditionPinPolicy.FindConflictingProtectedEdition(
                canonicalBook,
                canonicalEditions,
                canonicalEdition.Id);
            var canReuseCanonicalBook = conflictingProtectedEdition == null;

            var cacheKey = BuildUnitDestCacheKey(canonicalBook, unitKeyHash ?? unitKey);
            if (!string.IsNullOrWhiteSpace(cacheKey) && _unitDestCache.TryGetValue(cacheKey, out var cached))
            {
                if (cached.BookId != canonicalBook.Id || canReuseCanonicalBook)
                {
                    return cached;
                }

                // A user can pin another edition after this unit was cached. Never let the old
                // canonical destination bypass the newly-persisted pin.
                _unitDestCache.TryRemove(cacheKey, out _);
            }

            // Fast-path: if this unit has already been cloned and stamped with UnitKeyHash, re-use it deterministically.
            if (!string.IsNullOrWhiteSpace(unitKeyHash))
            {
                var existingByHash = FindExistingBookByUnitKeyHash(canonicalBook, unitKeyHash);
                if (existingByHash != null)
                {
                    if (existingByHash.Id != canonicalBook.Id || canReuseCanonicalBook)
                    {
                        var dest = (existingByHash.Id, ResolveEditionIdForDestination(existingByHash, canonicalEdition));
                        if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = dest;
                        return dest;
                    }
                }
            }

            // Legacy/backfill: an existing clone may not have UnitKeyHash populated. Scan other copies in the identity group.
            var existingByFiles = FindExistingDestinationByScanningFiles(canonicalBook, canonicalEdition, unitKey, unitKeyHash);
            if (existingByFiles.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = existingByFiles.Value;
                return existingByFiles.Value;
            }

            var emptyTargetedDestination = FindExistingEmptyTargetedDestination(canonicalBook, canonicalEdition, unitKeyHash);
            if (emptyTargetedDestination.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = emptyTargetedDestination.Value;
                return emptyTargetedDestination.Value;
            }

            if (!canReuseCanonicalBook)
            {
                var protectedClone = CloneBookAndEdition(
                    canonicalBook,
                    canonicalEdition,
                    unitKeyHash,
                    makeAutomaticCopy: true);
                _logger.Debug("[EDITION-PIN-PROTECTION] Preserved EditionId={0} on BookId={1}; routed matched EditionId={2} to copy BookId={3}",
                    conflictingProtectedEdition.Id,
                    canonicalBook.Id,
                    canonicalEdition.Id,
                    protectedClone.BookId);
                if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = protectedClone;
                return protectedClone;
            }

            // Check files on BOOK level, not edition - prevents different folders from collapsing onto same book
            // when they match different editions of the same work
            var trackedFiles = _mediaFileService.GetFilesByBook(canonicalBook.Id) ?? new List<BookFile>();
            var existingFiles = new List<BookFile>();
            var staleFiles = new List<BookFile>();
            foreach (var file in trackedFiles)
            {
                if (string.IsNullOrWhiteSpace(file?.Path) || !IsStillOnDisk(file.Path))
                {
                    staleFiles.Add(file);
                }
                else
                {
                    existingFiles.Add(file);
                }
            }

            if (staleFiles.Count > 0)
            {
                _logger.Debug("[UNIT-DEST] Ignored {0} tracked files missing from disk on BookId={1} while resolving unit destination", staleFiles.Count, canonicalBook.Id);
            }

            PruneRenamedEpubReservations(canonicalBook.Id, staleFiles);

            if (existingFiles.Count == 0)
            {
                // No existing rows means there is no physical copy to split from. A same-root
                // EPUB rename is the exception: retain the old row as evidence so only one new
                // unit can claim the vanished file, and only for its original edition.
                var canReuseCanonical = staleFiles.Count == 0 ||
                                        OnlyReplacedFilesRemain(staleFiles, canonicalEdition.Title, canonicalBook.MediaType, unitKey) ||
                                        TryReserveRenamedEpub(canonicalBook, canonicalEdition, staleFiles, unitKey);

                if (canReuseCanonical)
                {
                    var dest0 = (canonicalBook.Id, canonicalEdition.Id);
                    if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = dest0;
                    return dest0;
                }
            }

            // Check if existing files belong to the same unit key
            // Use BuildRootUnitKey (not simple folder+ext) to handle multi-CD structures
            // where CD1/, CD2/, etc. should all resolve to the same book root
            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in existingFiles)
            {
                existingKeys.Add(BuildRootUnitKeyWithExtension(f.Path, canonicalEdition.Title, canonicalBook.MediaType));
            }

            if (existingKeys.Contains(unitKey))
            {
                var dest1 = (canonicalBook.Id, canonicalEdition.Id);
                if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = dest1;
                return dest1;
            }

            // The book's files were replaced in place, e.g. an outside tool converted its MP3s into one M4B
            // in the same folder. A rescan places new files before it deletes the rows of files that have
            // disappeared, so the old rows are still here, and their extension differs from this unit's.
            // They are not a second copy of the book: reuse it, rather than leave it monitored with no files.
            if (existingFiles.Count > 0 && OnlyReplacedFilesRemain(existingFiles, canonicalEdition.Title, canonicalBook.MediaType, unitKey))
            {
                _logger.Debug("[UNIT-REPLACED] BookId={0} only has rows for files gone from this unit's folder; reusing it for unitKey='{1}' instead of creating a copy",
                    canonicalBook.Id, unitKey);
                var dest3 = (canonicalBook.Id, canonicalEdition.Id);
                if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = dest3;
                return dest3;
            }

            // Sibling ebook formats of one release (epub/azw3/mobi in the same folder) are
            // renditions of the same physical unit, never a second copy to clone for.
            if (canonicalBook.MediaType == BookMediaType.Ebook)
            {
                var incomingBaseKey = StripUnitKeyExtension(unitKey);

                if (!string.IsNullOrWhiteSpace(incomingBaseKey) &&
                    existingKeys.Any(k => StripUnitKeyExtension(k).Equals(incomingBaseKey, StringComparison.OrdinalIgnoreCase)))
                {
                    var destSibling = (canonicalBook.Id, canonicalEdition.Id);
                    if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = destSibling;
                    return destSibling;
                }
            }

            // Clone book + chosen edition for this unit
            var clone = CloneBookAndEdition(canonicalBook, canonicalEdition, unitKeyHash, makeAutomaticCopy: false);
            _logger.Debug("[UNIT-CLONE] Created duplicate BookId={0} EditionId={1} for unitKey='{2}' from canonical BookId={3} EditionId={4}",
                clone.BookId, clone.EditionId, unitKey, canonicalBook.Id, canonicalEdition.Id);
            var dest2 = (clone.BookId, clone.EditionId);
            if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = dest2;
            return dest2;
        }

        // True when every file the book has rows for sits in the incoming unit's own book folder and is
        // no longer on disk. A file that is still there, or one in a different folder, is a real copy.
        private bool OnlyReplacedFilesRemain(List<BookFile> existingFiles, string editionTitle, BookMediaType mediaType, string unitKey)
        {
            if (existingFiles == null || existingFiles.Count == 0)
            {
                return false;
            }

            var incomingFolder = FolderOfUnitKey(unitKey);

            foreach (var file in existingFiles)
            {
                var fileFolder = FolderOfUnitKey(BuildRootUnitKeyWithExtension(file.Path, editionTitle, mediaType));
                if (!string.Equals(fileFolder, incomingFolder, StringComparison.OrdinalIgnoreCase) || IsStillOnDisk(file.Path))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryReserveRenamedEpub(Book canonicalBook, Edition canonicalEdition, List<BookFile> staleFiles, string unitKey)
        {
            if (canonicalBook?.MediaType != BookMediaType.Ebook ||
                canonicalEdition == null ||
                staleFiles?.Count != 1 ||
                !unitKey.EndsWith("|.epub", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var staleFile = staleFiles[0];
            if (staleFile == null ||
                staleFile.EditionId != canonicalEdition.Id ||
                !string.Equals(Path.GetExtension(staleFile.Path), ".epub", StringComparison.OrdinalIgnoreCase) ||
                IsStillOnDisk(staleFile.Path))
            {
                return false;
            }

            var incomingPath = GetEpubPathFromUnitKey(unitKey);
            if (incomingPath.IsNullOrWhiteSpace() ||
                !TryGetAccessibleNonCalibreRoot(incomingPath, out var incomingRoot) ||
                !TryGetAccessibleNonCalibreRoot(staleFile.Path, out var previousRoot) ||
                !previousRoot.Path.PathEquals(incomingRoot.Path))
            {
                return false;
            }

            var reservationKey = GetRenamedEpubReservationKey(canonicalBook.Id, staleFile);
            var reservedUnitKey = _renamedEpubReservations.GetOrAdd(reservationKey, unitKey);

            if (!reservedUnitKey.Equals(unitKey, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("[UNIT-REPLACED-EPUB] Stale file BookFileId={0} for BookId={1} is already reserved for another incoming unit; preserving a separate copy",
                    staleFile.Id, canonicalBook.Id);
                return false;
            }

            _logger.Debug("[UNIT-REPLACED-EPUB] BookId={0} EditionId={1} is reusing one vanished EPUB under the same accessible library root for unitKey='{2}'",
                canonicalBook.Id, canonicalEdition.Id, unitKey);
            return true;
        }

        private void PruneRenamedEpubReservations(int bookId, List<BookFile> staleFiles)
        {
            var reservationPrefix = $"{bookId}|";
            var staleReservationKeys = new HashSet<string>(
                (staleFiles ?? new List<BookFile>())
                    .Where(file => file != null && string.Equals(Path.GetExtension(file.Path), ".epub", StringComparison.OrdinalIgnoreCase))
                    .Select(file => GetRenamedEpubReservationKey(bookId, file)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var reservationKey in _renamedEpubReservations.Keys)
            {
                if (reservationKey.StartsWith(reservationPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !staleReservationKeys.Contains(reservationKey))
                {
                    _renamedEpubReservations.TryRemove(reservationKey, out _);
                }
            }
        }

        private static string GetRenamedEpubReservationKey(int bookId, BookFile staleFile)
        {
            var staleFileIdentity = staleFile.Id > 0
                ? staleFile.Id.ToString()
                : (staleFile.Path ?? string.Empty).Trim().ToLowerInvariant();

            return $"{bookId}|{staleFileIdentity}";
        }

        private bool TryGetAccessibleNonCalibreRoot(string path, out RootFolder rootFolder)
        {
            rootFolder = null;

            try
            {
                rootFolder = _rootFolderService?.GetBestRootFolder(path);
                if (rootFolder == null ||
                    rootFolder.IsCalibreLibrary ||
                    rootFolder.Path.IsNullOrWhiteSpace() ||
                    !_diskProvider.FolderExists(rootFolder.Path))
                {
                    rootFolder = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                _logger.Debug(ex, "[UNIT-REPLACED-EPUB] Unable to verify the library root for '{0}'", path);
                rootFolder = null;
                return false;
            }
        }

        private static string GetEpubPathFromUnitKey(string unitKey)
        {
            if (unitKey.IsNullOrWhiteSpace() || !unitKey.EndsWith("|.epub", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var withoutExtension = unitKey.Substring(0, unitKey.Length - "|.epub".Length);
            var mediaTypeSeparator = withoutExtension.LastIndexOf('|');
            if (mediaTypeSeparator <= 0 ||
                !withoutExtension.Substring(mediaTypeSeparator + 1).Equals(nameof(BookMediaType.Ebook), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return withoutExtension.Substring(0, mediaTypeSeparator);
        }

        // Remove the optional container suffix while retaining the unit stem and media type.
        private static string FolderOfUnitKey(string unitKey)
        {
            if (string.IsNullOrEmpty(unitKey))
            {
                return string.Empty;
            }

            var separator = unitKey.LastIndexOf('|');
            return separator >= 0 && separator + 1 < unitKey.Length && unitKey[separator + 1] == '.'
                ? unitKey.Substring(0, separator)
                : unitKey;
        }

        private bool IsStillOnDisk(string path)
        {
            try
            {
                // The same check the scan's cleanup uses to decide which rows to delete.
                return _diskProvider.FileExistsCanonical(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                // If it can't be checked, keep counting it, which is the behaviour before this check existed.
                _logger.Debug(ex, "[UNIT-REPLACED] Could not check whether '{0}' is on disk; treating it as present", path);
                return true;
            }
        }

        private bool IsCalibreLibraryBook(Book book)
        {
            try
            {
                var path = book?.Author?.Path;

                if (path.IsNullOrWhiteSpace() && book != null && book.AuthorId > 0)
                {
                    path = _authorService.GetAuthor(book.AuthorId)?.Path;
                }

                if (path.IsNullOrWhiteSpace())
                {
                    return false;
                }

                var rootFolder = _rootFolderService.GetBestRootFolder(path);
                return rootFolder != null && rootFolder.IsCalibreLibrary;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to determine whether book {0} is in a calibre library", book?.Id);
                return false;
            }
        }

        private static string BuildUnitDestCacheKey(Book canonicalBook, string unitKeyOrHash)
        {
            if (canonicalBook == null || canonicalBook.Id <= 0 || string.IsNullOrWhiteSpace(unitKeyOrHash))
            {
                return null;
            }

            return $"{canonicalBook.Id}|{unitKeyOrHash}".ToLowerInvariant();
        }

        private static string StripUnitKeyExtension(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return key ?? string.Empty;
            }

            var idx = key.LastIndexOf('|');

            if (idx <= 0 || idx == key.Length - 1)
            {
                return key;
            }

            return key[idx + 1] == '.' ? key.Substring(0, idx) : key;
        }

        private static string ComputeUnitKeyHash(string unitKey)
        {
            if (string.IsNullOrWhiteSpace(unitKey))
            {
                return null;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(unitKey.Trim()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private Book FindExistingBookByUnitKeyHash(Book canonicalBook, string unitKeyHash)
        {
            if (canonicalBook == null || canonicalBook.Id <= 0 || string.IsNullOrWhiteSpace(unitKeyHash))
            {
                return null;
            }

            var identityGroup = GetIdentityGroupBooks(canonicalBook);

            return identityGroup.FirstOrDefault(b =>
                b != null &&
                b.Id > 0 &&
                !string.IsNullOrWhiteSpace(b.UnitKeyHash) &&
                b.UnitKeyHash.Equals(unitKeyHash, StringComparison.OrdinalIgnoreCase));
        }

        private (int BookId, int EditionId)? FindExistingDestinationByScanningFiles(Book canonicalBook, Edition canonicalEdition, string unitKey, string unitKeyHash)
        {
            var identityGroup = GetIdentityGroupBooks(canonicalBook)
                .Where(b => b != null && b.Id > 0 && b.Id != canonicalBook.Id)
                .ToList();

            foreach (var candidate in identityGroup)
            {
                var files = _mediaFileService.GetFilesByBook(candidate.Id) ?? new List<BookFile>();
                if (files.Count == 0)
                {
                    continue;
                }

                foreach (var f in files)
                {
                    var key = BuildRootUnitKeyWithExtension(f.Path, canonicalEdition.Title, canonicalBook.MediaType);
                    var sameUnit = string.Equals(key, unitKey, StringComparison.OrdinalIgnoreCase) ||
                                   (canonicalBook.MediaType == BookMediaType.Ebook &&
                                    StripUnitKeyExtension(key).Equals(StripUnitKeyExtension(unitKey), StringComparison.OrdinalIgnoreCase));

                    if (!sameUnit)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(unitKeyHash) && string.IsNullOrWhiteSpace(candidate.UnitKeyHash))
                    {
                        TryPersistUnitKeyHash(candidate, unitKeyHash);
                    }

                    var editionId = ResolveEditionIdForDestination(candidate, canonicalEdition);
                    return (candidate.Id, editionId);
                }
            }

            return null;
        }

        private (int BookId, int EditionId)? FindExistingEmptyTargetedDestination(Book canonicalBook, Edition canonicalEdition, string unitKeyHash)
        {
            var identityGroup = GetIdentityGroupBooks(canonicalBook)
                .Where(book => book != null && book.Id > 0 && book.Id != canonicalBook.Id)
                .Where(IsTargetedDestinationCandidate)
                .OrderBy(book => book.AnyEditionOk ? 1 : 0)
                .ThenBy(book => book.Id)
                .ToList();

            foreach (var candidate in identityGroup)
            {
                var files = _mediaFileService.GetFilesByBook(candidate.Id) ?? new List<BookFile>();
                if (files.Count > 0)
                {
                    continue;
                }

                var matchingEditionId = TryResolveExactEditionIdForDestination(candidate, canonicalEdition);
                if (!matchingEditionId.HasValue)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(unitKeyHash) && string.IsNullOrWhiteSpace(candidate.UnitKeyHash))
                {
                    TryPersistUnitKeyHash(candidate, unitKeyHash);
                }

                return (candidate.Id, matchingEditionId.Value);
            }

            return null;
        }

        private List<Book> GetIdentityGroupBooks(Book canonicalBook)
        {
            var all = _bookService.GetBooksByAuthor(canonicalBook.AuthorId) ?? new List<Book>();

            var sameMedia = all
                .Where(b => b != null && b.Id > 0 && b.MediaType == canonicalBook.MediaType)
                .ToList();

            var identityGroup = new List<Book>();

            // Local copy routing still needs the clone-family key so an import can reuse the
            // correct existing targeted/physical copy. This is not server refresh identity.
            if (!string.IsNullOrWhiteSpace(canonicalBook.BaseBookId))
            {
                identityGroup.AddRange(sameMedia.Where(b =>
                    !string.IsNullOrWhiteSpace(b.BaseBookId) &&
                    b.BaseBookId.Equals(canonicalBook.BaseBookId, StringComparison.OrdinalIgnoreCase)));
            }

            identityGroup.AddRange(sameMedia.Where(b => BookIdentity.MatchesByProviderIdIntersection(b, canonicalBook)));

            return identityGroup
                .Where(b => b != null)
                .GroupBy(b => b.Id)
                .Select(g => g.First())
                .OrderBy(b => b.Id)
                .ToList();
        }

        private void TryPersistUnitKeyHash(Book book, string unitKeyHash)
        {
            try
            {
                book.UnitKeyHash = unitKeyHash;
                _bookService.UpdateMany(new List<Book> { book });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                _logger.Debug(ex, "[UNIT-CLONE] Failed to persist UnitKeyHash for BookId={0}", book?.Id ?? 0);
            }
        }

        private int ResolveEditionIdForDestination(Book destinationBook, Edition canonicalEdition)
        {
            if (destinationBook == null || destinationBook.Id <= 0 || canonicalEdition == null)
            {
                return canonicalEdition?.Id ?? 0;
            }

            if (destinationBook.Id == canonicalEdition.BookId)
            {
                return canonicalEdition.Id;
            }

            var editions = destinationBook.Editions;
            if (editions == null || editions.Count == 0)
            {
                editions = _editionService.GetEditionsByBook(destinationBook.Id) ?? new List<Edition>();
            }

            var matching = editions.FirstOrDefault(e => BookEditionIdentity.EditionsMatch(e, canonicalEdition))
                           ?? editions.FirstOrDefault(e => e.Monitored)
                           ?? editions.FirstOrDefault();

            return matching?.Id ?? canonicalEdition.Id;
        }

        private int? TryResolveExactEditionIdForDestination(Book destinationBook, Edition canonicalEdition)
        {
            if (destinationBook == null || destinationBook.Id <= 0 || canonicalEdition == null)
            {
                return null;
            }

            if (destinationBook.Id == canonicalEdition.BookId)
            {
                return canonicalEdition.Id;
            }

            var editions = destinationBook.Editions;
            if (editions == null || editions.Count == 0)
            {
                editions = _editionService.GetEditionsByBook(destinationBook.Id) ?? new List<Edition>();
            }

            return editions.FirstOrDefault(e => BookEditionIdentity.EditionsMatch(e, canonicalEdition))?.Id;
        }

        private static bool IsTargetedDestinationCandidate(Book book)
        {
            if (book == null)
            {
                return false;
            }

            return !book.AnyEditionOk ||
                   book.AddOptions?.AddType == BookAddType.Manual;
        }

        private (int BookId, int EditionId) CloneBookAndEdition(
            Book canonicalBook,
            Edition canonicalEdition,
            string unitKeyHash,
            bool makeAutomaticCopy)
        {
            // Build a unique slug suffix
            var suffix = DateTime.UtcNow.Ticks.ToString().Substring(10);
            var sourceEdition = _editionService.GetEdition(canonicalEdition.Id) ?? canonicalEdition;
            var newBook = RefreshEntityCopy.CloneBook(canonicalBook, includeEditions: false);
            newBook.Author = canonicalBook.Author;
            newBook.AuthorId = canonicalBook.AuthorId;
            newBook.TitleSlug = (canonicalBook.TitleSlug ?? canonicalBook.Title).Trim() + "_copy_" + suffix;
            newBook.Added = DateTime.UtcNow;
            newBook.LastInfoSync = null;
            newBook.UnitKeyHash = unitKeyHash;
            newBook.ForeignEditionId = sourceEdition?.ForeignEditionId;
            newBook.ASIN = !string.IsNullOrWhiteSpace(sourceEdition?.Asin) ? sourceEdition.Asin : canonicalBook.ASIN;
            newBook.AudibleASIN = !string.IsNullOrWhiteSpace(sourceEdition?.AudibleASIN) ? sourceEdition.AudibleASIN : canonicalBook.AudibleASIN;

            if (makeAutomaticCopy)
            {
                newBook.AnyEditionOk = true;
                newBook.AddOptions = new AddBookOptions();
            }

            // Prefer explicit narrator values from the canonical book; fall back to the matched edition.
            newBook.Narrator = !string.IsNullOrWhiteSpace(canonicalBook.Narrator) ? canonicalBook.Narrator : sourceEdition?.Narrator;

            var newEdition = RefreshEntityCopy.CloneEdition(sourceEdition);
            newEdition.TitleSlug = (sourceEdition.TitleSlug ?? sourceEdition.Title).Trim() + "_copy_" + suffix;
            newEdition.LastUpdated = DateTime.UtcNow;
            if (makeAutomaticCopy)
            {
                newEdition.Monitored = true;
                newEdition.ManualAdd = false;
            }

            using (var connection = _mainDatabase.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    // Insert the book (Id is set on the instance by the repository).
                    _bookService.InsertMany(new List<Book> { newBook }, connection, transaction);
                    if (newBook.Id <= 0)
                    {
                        throw new InvalidOperationException("Failed to insert cloned Book (Id not assigned)");
                    }

                    newEdition.BookId = newBook.Id;
                    _editionService.InsertMany(new List<Edition> { newEdition }, connection, transaction);

                    if (newEdition.Id <= 0)
                    {
                        throw new InvalidOperationException("Failed to insert cloned Edition (Id not assigned)");
                    }

                    transaction.Commit();
                }
                catch
                {
                    try
                    {
                        transaction.Rollback();
                    }
                    catch
                    {
                        // Ignore rollback failures.
                    }

                    throw;
                }
            }

            _logger.Debug("[CLONE] Cloned matched edition only from Book {0} Edition {1} to Book {2} Edition {3}",
                canonicalBook.Id,
                canonicalEdition.Id,
                newBook.Id,
                newEdition.Id);

            return (newBook.Id, newEdition.Id);
        }
    }
}
