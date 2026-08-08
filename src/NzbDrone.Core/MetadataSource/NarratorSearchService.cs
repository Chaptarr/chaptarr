using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Repositories;

namespace NzbDrone.Core.MetadataSource
{
    public interface INarratorSearchService
    {
        List<string> SearchForNarrators(string authorName, string bookTitle, bool useCache = true);
        List<string> SearchForNarratorsByAsin(string asin, bool useCache = true);
        List<string> GetStoredNarrators(int bookId);
        void StoreNarrators(int bookId, List<string> narrators, string source = "search");
        void StoreNarratorOptions(int bookId, List<string> narrators, string source);
        void FetchNarratorPhotosForBook(int bookId, string authorName, string bookTitle);
    }

    public class NarratorSearchService : INarratorSearchService
    {
        private readonly IBookService _bookService;
        private readonly IBookNarratorOptionRepository _narratorOptionRepository;
        private readonly Logger _logger;

        public NarratorSearchService(
            IBookService bookService,
            IBookNarratorOptionRepository narratorOptionRepository,
            Logger logger)
        {
            _bookService = bookService;
            _narratorOptionRepository = narratorOptionRepository;
            _logger = logger;
        }

        public List<string> SearchForNarrators(string authorName, string bookTitle, bool useCache = true)
        {
            if (authorName.IsNullOrWhiteSpace() || bookTitle.IsNullOrWhiteSpace())
            {
                _logger.Debug("Missing author name or book title for narrator search");
                return new List<string>();
            }

            _logger.Debug("External narrator discovery is disabled for {0} - {1}", authorName, bookTitle);
            return new List<string>();
        }

        public List<string> SearchForNarratorsByAsin(string asin, bool useCache = true)
        {
            if (asin.IsNullOrWhiteSpace())
            {
                _logger.Debug("Missing ASIN for narrator search");
                return new List<string>();
            }

            _logger.Debug("External narrator discovery by ASIN is disabled for {0}", asin);
            return new List<string>();
        }

        public List<string> GetStoredNarrators(int bookId)
        {
            try
            {
                var narratorOptions = _narratorOptionRepository.GetByBookId(bookId);
                var narrators = narratorOptions.Select(opt => opt.Narrator).ToList();

                if (!narrators.Any())
                {
                    var book = _bookService.GetBook(bookId);
                    if (book?.Narrator.IsNotNullOrWhiteSpace() == true)
                    {
                        narrators.Add(book.Narrator);
                    }
                }

                _logger.Debug("Retrieved {0} stored narrators for book {1}: {2}", narrators.Count, bookId, string.Join(", ", narrators));

                return narrators;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error retrieving stored narrators for book: {0}", bookId);
                return new List<string>();
            }
        }

        public void StoreNarrators(int bookId, List<string> narrators, string source = "search")
        {
            try
            {
                if (!narrators?.Any() == true)
                {
                    _logger.Debug("No narrators to store for book: {0}", bookId);
                    return;
                }

                var book = _bookService.GetBook(bookId);
                if (book == null)
                {
                    _logger.Debug("Book not found for narrator storage: {0}", bookId);
                    return;
                }

                var primaryNarrator = narrators.First();

                if (book.Narrator != primaryNarrator)
                {
                    book.Narrator = primaryNarrator;
                    _bookService.UpdateBook(book);

                    _logger.Debug("Stored primary narrator '{0}' for book {1} from source: {2}", primaryNarrator, bookId, source);
                }

                StoreNarratorOptions(bookId, narrators, source);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error storing narrators for book {0}: {1}", bookId, string.Join(", ", narrators ?? new List<string>()));
            }
        }

        public void StoreNarratorOptions(int bookId, List<string> narrators, string source)
        {
            try
            {
                if (!narrators?.Any() == true)
                {
                    return;
                }

                var existingOptions = _narratorOptionRepository.GetByBookId(bookId)
                    .Where(opt => opt.Source == source)
                    .ToList();

                foreach (var existingOption in existingOptions)
                {
                    _narratorOptionRepository.Delete(existingOption);
                }

                foreach (var narrator in narrators.Where(n => !string.IsNullOrWhiteSpace(n)))
                {
                    var option = new BookNarratorOption
                    {
                        BookId = bookId,
                        Narrator = narrator.Trim(),
                        Source = source,
                        DateDiscovered = DateTime.UtcNow,
                        IsPreferred = false
                    };

                    _narratorOptionRepository.Insert(option);
                }

                _logger.Debug("Stored {0} narrator options for book {1} from source: {2}", narrators.Count, bookId, source);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error storing narrator options for book {0}: {1}", bookId, ex.Message);
            }
        }

        public void FetchNarratorPhotosForBook(int bookId, string authorName, string bookTitle)
        {
            _logger.Debug("Narrator photo fetching is disabled for book {0}: {1} - {2}", bookId, authorName, bookTitle);
        }
    }
}
