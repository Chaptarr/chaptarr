using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books.Services
{
    public interface IBookNarratorService
    {
        List<string> GetAvailableNarrators(int bookId);
        List<string> GetFilteredNarratorsForDropdown(int bookId);
        List<BookNarratorOption> GetNarratorOptions(int bookId);
        void AddNarratorOption(int bookId, string narrator, string source);
        void SetPreferredNarrator(int bookId, string narrator);
        void ClearPreferredNarrator(int bookId);
        bool HasExistingCopyWithNarrator(int bookId, string narrator);
        List<string> GetNarratorsFromExistingCopies(int bookId);
        List<Book> GetAllPhysicalCopies(int bookId);
        void DiscoverNarratorsForAllCopies(int bookId);
        NarratorDiscoveryResult DiscoverAndFilterNarrators(int bookId);
    }

    public class BookNarratorService : IBookNarratorService
    {
        private readonly IBookNarratorOptionRepository _narratorOptionRepository;
        private readonly IBookRepository _bookRepository;
        private readonly ISeriesRepository _seriesRepository;
        private readonly INarratorSearchService _narratorSearchService;
        private readonly IAuthorService _authorService;
        private readonly Logger _logger;

        public BookNarratorService(IBookNarratorOptionRepository narratorOptionRepository,
                                   IBookRepository bookRepository,
                                   ISeriesRepository seriesRepository,
                                   INarratorSearchService narratorSearchService,
                                   IAuthorService authorService,
                                   Logger logger)
        {
            _narratorOptionRepository = narratorOptionRepository;
            _bookRepository = bookRepository;
            _seriesRepository = seriesRepository;
            _narratorSearchService = narratorSearchService;
            _authorService = authorService;
            _logger = logger;
        }

        public List<string> GetAvailableNarrators(int bookId)
        {
            _logger.Debug("Getting available narrators for book ID: {0}", bookId);

            var narratorOptions = _narratorOptionRepository.GetByBookId(bookId);
            var narrators = narratorOptions.Select(x => x.Narrator).Where(x => x.IsNotNullOrWhiteSpace()).ToList();

            _logger.Debug("Found {0} narrator options for book {1}", narrators.Count, bookId);
            return narrators;
        }

        public List<string> GetFilteredNarratorsForDropdown(int bookId)
        {
            _logger.Debug("Getting filtered narrators for dropdown for book ID: {0}", bookId);

            // Get all available narrators for this book
            var availableNarrators = GetAvailableNarrators(bookId);

            // Get narrators from existing local copies to filter out
            var existingNarrators = GetNarratorsFromExistingCopies(bookId);

            // Filter out narrators that already exist in local copies
            var filteredNarrators = availableNarrators
                .Where(narrator => !existingNarrators.Any(existing =>
                    string.Equals(narrator, existing, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            _logger.Debug("Filtered {0} narrators from {1} available (excluded {2} existing)", filteredNarrators.Count, availableNarrators.Count, existingNarrators.Count);

            return filteredNarrators;
        }

        public List<BookNarratorOption> GetNarratorOptions(int bookId)
        {
            return _narratorOptionRepository.GetByBookId(bookId);
        }

        public void AddNarratorOption(int bookId, string narrator, string source)
        {
            if (narrator.IsNullOrWhiteSpace())
            {
                _logger.Debug("Skipping empty narrator for book {0}", bookId);
                return;
            }

            var existing = _narratorOptionRepository.FindByBookIdAndNarrator(bookId, narrator);
            if (existing != null)
            {
                _logger.Debug("Narrator option already exists for book {0} with narrator {1}", bookId, narrator);
                return;
            }

            var option = new BookNarratorOption
            {
                BookId = bookId,
                Narrator = narrator,
                Source = source,
                DateDiscovered = DateTime.UtcNow,
                IsPreferred = false
            };

            _narratorOptionRepository.Insert(option);
            _logger.Debug("Added narrator option for book {0}: {1} from {2}", bookId, narrator, source);
        }

        public void SetPreferredNarrator(int bookId, string narrator)
        {
            _logger.Debug("Setting preferred narrator for book {0}: {1}", bookId, narrator);
            _narratorOptionRepository.SetPreferred(bookId, narrator);
        }

        public void ClearPreferredNarrator(int bookId)
        {
            _logger.Debug("Clearing preferred narrator for book {0}", bookId);
            _narratorOptionRepository.ClearPreferred(bookId);
        }

        public bool HasExistingCopyWithNarrator(int bookId, string narrator)
        {
            if (narrator.IsNullOrWhiteSpace())
            {
                return false;
            }

            var existingNarrators = GetNarratorsFromExistingCopies(bookId);
            return existingNarrators.Any(existing =>
                string.Equals(narrator, existing, StringComparison.OrdinalIgnoreCase));
        }

        public List<string> GetNarratorsFromExistingCopies(int bookId)
        {
            _logger.Debug("Getting narrators from existing copies for book ID: {0}", bookId);

            try
            {
                var book = _bookRepository.Get(bookId);
                if (book == null)
                {
                    _logger.Warn("Book not found: {0}", bookId);
                    return new List<string>();
                }

                // For multi-copy architecture, find all books that represent different copies
                // Each copy has a unique LocalBookId but shares provider IDs
                var relatedBooks = new List<Book>();

	                // Find by matching provider IDs (books with same content but different copies).
	                // IMPORTANT: Do not use ISBN10/ISBN13 here; ISBNs are not stable identity keys for our multi-copy model.
                    relatedBooks.AddRange(_bookRepository.All()
                        .Where(b => b.MediaType == book.MediaType && b.Id != bookId && WorkIdMatcher.WorkIdMatches(book, b)));

                // Remove duplicates
                relatedBooks = relatedBooks.GroupBy(b => b.Id).Select(g => g.First()).ToList();

                _logger.Debug("Found {0} related books (other copies) for book {1}", relatedBooks.Count, book.Id);

                // Extract narrators from all related books
                var existingNarrators = new List<string>();

                foreach (var relatedBook in relatedBooks)
                {
                    // Check book-level narrator
                    if (relatedBook.Narrator.IsNotNullOrWhiteSpace())
                    {
                        existingNarrators.Add(relatedBook.Narrator);
                        _logger.Debug("Found narrator from book {0}: {1}", relatedBook.Id, relatedBook.Narrator);
                    }

                    // Check preferred narrator options
                    var preferredOptions = _narratorOptionRepository.GetPreferredByBookId(relatedBook.Id);
                    foreach (var option in preferredOptions)
                    {
                        if (option.Narrator.IsNotNullOrWhiteSpace())
                        {
                            existingNarrators.Add(option.Narrator);
                            _logger.Debug("Found narrator from preferred options for book {0}: {1}", relatedBook.Id, option.Narrator);
                        }
                    }
                }

                // Remove duplicates (case-insensitive)
                var uniqueNarrators = existingNarrators
                    .Where(n => n.IsNotNullOrWhiteSpace())
                    .GroupBy(n => n.ToLowerInvariant())
                    .Select(g => g.First())
                    .ToList();

                _logger.Debug("Found {0} unique existing narrators for book {1}", uniqueNarrators.Count, bookId);
                return uniqueNarrators;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting narrators from existing copies for book {0}", bookId);
                return new List<string>();
            }
        }

        public List<Book> GetAllPhysicalCopies(int bookId)
        {
            _logger.Debug("Getting all physical copies for book ID: {0}", bookId);

            try
            {
                var book = _bookRepository.Get(bookId);
                if (book == null)
                {
                    _logger.Warn("Book not found: {0}", bookId);
                    return new List<Book>();
                }

                // Find all copies including the original
                var allCopies = new List<Book> { book };

	                // Find by matching provider IDs (books with same content but different copies).
	                // IMPORTANT: Do not use ISBN10/ISBN13 here; ISBNs are not stable identity keys for our multi-copy model.
                    allCopies.AddRange(_bookRepository.All()
                        .Where(b => b.MediaType == book.MediaType && b.Id != bookId && WorkIdMatcher.WorkIdMatches(book, b)));

                // Remove duplicates
                allCopies = allCopies.GroupBy(b => b.Id).Select(g => g.First()).ToList();
                HydrateAuthors(allCopies);

                _logger.Debug("Found {0} physical copies for book {1}", allCopies.Count, bookId);
                return allCopies;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting all physical copies for book {0}", bookId);
                return new List<Book>();
            }
        }

        public void DiscoverNarratorsForAllCopies(int bookId)
        {
            _logger.Debug("Discovering narrators for all copies of book ID: {0}", bookId);

            try
            {
                var allCopies = GetAllPhysicalCopies(bookId);
                var processedBooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var copy in allCopies)
                {
                    // Skip if we've already processed this exact book/author combination
                    var bookKey = $"{copy.Title}|{copy.Author?.Name ?? "Unknown"}";
                    if (processedBooks.Contains(bookKey))
                    {
                        _logger.Debug("Skipping duplicate book/author combination: {0}", bookKey);
                        continue;
                    }

                    try
                    {
                        _logger.Debug("Discovering narrators for copy {0}: {1}", copy.Id, copy.Title);

                        // Check if this copy already has narrator options
                        var existingOptions = _narratorOptionRepository.GetByBookId(copy.Id);
                        if (existingOptions.Any())
                        {
                            _logger.Debug("Copy {0} already has {1} narrator options, skipping discovery", copy.Id, existingOptions.Count);
                            processedBooks.Add(bookKey);
                            continue;
                        }

                        // Perform narrator discovery for this copy
                        var authorName = copy.Author?.Name ?? "Unknown";
                        var foundNarrators = _narratorSearchService.SearchForNarrators(authorName, copy.Title);

                        if (foundNarrators.Any())
                        {
                            _logger.Debug("Found {0} narrators for copy {1}, storing options", foundNarrators.Count, copy.Id);
                            _narratorSearchService.StoreNarratorOptions(copy.Id, foundNarrators, "auto_discovery");
                        }
                        else
                        {
                            _logger.Debug("No narrators found for copy {0}", copy.Id);
                        }

                        processedBooks.Add(bookKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error discovering narrators for copy {0}", copy.Id);
                    }
                }

                _logger.Debug("Completed narrator discovery for {0} copies of book {1}", allCopies.Count, bookId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error discovering narrators for all copies of book {0}", bookId);
            }
        }

        public NarratorDiscoveryResult DiscoverAndFilterNarrators(int bookId)
        {
            _logger.Debug("Discovering and filtering narrators for book ID: {0}", bookId);

            var result = new NarratorDiscoveryResult
            {
                BookId = bookId,
                Success = false
            };

            try
            {
                var book = _bookRepository.Get(bookId);
                if (book == null)
                {
                    result.ErrorMessage = $"Book with id {bookId} not found";
                    return result;
                }

                HydrateAuthors(new List<Book> { book });

                result.BookTitle = book.Title;
                result.AuthorName = book.Author?.Name ?? "Unknown";

                // Step 1: Discover narrators for ALL physical copies
                _logger.Debug("Step 1: Discovering narrators for all physical copies");
                DiscoverNarratorsForAllCopies(bookId);

                // Step 2: Get all available narrators for this specific book
                var availableNarrators = GetAvailableNarrators(bookId);
                result.AvailableNarrators = availableNarrators;

                // Step 3: Get narrators from existing copies to filter out
                var existingNarrators = GetNarratorsFromExistingCopies(bookId);
                result.ExistingCopyNarrators = existingNarrators;

                // Step 4: Filter out narrators that already exist in other copies
                var filteredNarrators = availableNarrators
                    .Where(narrator => !existingNarrators.Any(existing =>
                        string.Equals(narrator, existing, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                result.FilteredNarrators = filteredNarrators;

                // Step 5: Get summary of all physical copies
                var allCopies = GetAllPhysicalCopies(bookId);
                result.TotalPhysicalCopies = allCopies.Count;
                result.CopiesWithNarrators = allCopies.Count(c => c.Narrator.IsNotNullOrWhiteSpace());

                result.Success = true;
                result.TotalAvailable = availableNarrators.Count;
                result.TotalFiltered = filteredNarrators.Count;
                result.TotalExisting = existingNarrators.Count;

                _logger.Debug("Narrator discovery complete for book {0}: {1} total copies, {2} available narrators, {3} filtered narrators, {4} existing narrators", bookId, result.TotalPhysicalCopies, result.TotalAvailable, result.TotalFiltered, result.TotalExisting);

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in discover and filter narrators for book {0}", bookId);
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        private void HydrateAuthors(List<Book> books)
        {
            if (books == null || books.Count == 0)
            {
                return;
            }

            var authorIds = books.Where(book => book?.AuthorId > 0)
                                 .Select(book => book.AuthorId)
                                 .Distinct()
                                 .ToList();

            if (!authorIds.Any())
            {
                return;
            }

            try
            {
                var authors = _authorService.GetAuthors(authorIds);
                if (authors == null || authors.Count == 0)
                {
                    return;
                }

                var authorsById = authors
                    .Where(author => author != null)
                    .ToDictionary(author => author.Id);

                foreach (var book in books.Where(book => book != null && book.AuthorId > 0))
                {
                    if (authorsById.TryGetValue(book.AuthorId, out var author))
                    {
                        book.Author = author;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to hydrate authors for narrator discovery");
            }
        }

        // No longer needed - we use provider IDs for matching instead of parsing ForeignBookId
    }

    public class NarratorDiscoveryResult
    {
        public int BookId { get; set; }
        public string BookTitle { get; set; }
        public string AuthorName { get; set; }
        public List<string> AvailableNarrators { get; set; } = new List<string>();
        public List<string> FilteredNarrators { get; set; } = new List<string>();
        public List<string> ExistingCopyNarrators { get; set; } = new List<string>();
        public int TotalPhysicalCopies { get; set; }
        public int CopiesWithNarrators { get; set; }
        public int TotalAvailable { get; set; }
        public int TotalFiltered { get; set; }
        public int TotalExisting { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }
}
