using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NLog;
using NzbDrone.Core.Books;
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
        private readonly Logger _logger;

        // Cache per canonical book + unit so we don't create multiple clones for the same physical unit.
        private readonly ConcurrentDictionary<string, (int BookId, int EditionId)> _unitDestCache =
            new ConcurrentDictionary<string, (int BookId, int EditionId)>(StringComparer.OrdinalIgnoreCase);

        public BookUnitDestinationService(
            IMediaFileService mediaFileService,
            IBookService bookService,
            IEditionService editionService,
            IMainDatabase mainDatabase,
            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _bookService = bookService;
            _editionService = editionService;
            _mainDatabase = mainDatabase;
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
            var existingFiles = _mediaFileService.GetFilesByBook(canonicalBook.Id) ?? new List<BookFile>();
            if (existingFiles.Count == 0)
            {
                // No existing files on canonical book → reuse canonical
                var dest0 = (canonicalBook.Id, canonicalEdition.Id);
                if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = dest0;
                return dest0;
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

            // Clone book + chosen edition for this unit
            var clone = CloneBookAndEdition(canonicalBook, canonicalEdition, unitKeyHash, makeAutomaticCopy: false);
            _logger.Debug("[UNIT-CLONE] Created duplicate BookId={0} EditionId={1} for unitKey='{2}' from canonical BookId={3} EditionId={4}",
                clone.BookId, clone.EditionId, unitKey, canonicalBook.Id, canonicalEdition.Id);
            var dest2 = (clone.BookId, clone.EditionId);
            if (!string.IsNullOrWhiteSpace(cacheKey)) _unitDestCache[cacheKey] = dest2;
            return dest2;
        }

        private static string BuildUnitDestCacheKey(Book canonicalBook, string unitKeyOrHash)
        {
            if (canonicalBook == null || canonicalBook.Id <= 0 || string.IsNullOrWhiteSpace(unitKeyOrHash))
            {
                return null;
            }

            return $"{canonicalBook.Id}|{unitKeyOrHash}".ToLowerInvariant();
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
                    if (!string.Equals(key, unitKey, StringComparison.OrdinalIgnoreCase))
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
            catch (Exception ex)
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
