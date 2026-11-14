using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Parser
{
    public interface IParsingService
    {
        Author GetAuthor(string title);
        RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null);
        RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds);
        List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null);
        List<List<Book>> GetBooks(IReadOnlyList<ParsedBookInfo> parsedBooks, Author author);

        // Music stuff here
        Book GetLocalBook(string filename, Author author);
    }

    public class ParsingService : IParsingService
    {
        private const int SlowTitleLookupMilliseconds = 1000;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public ParsingService(IAuthorService authorService,
                              IBookService bookService,
                              IEditionService editionService,
                              IMediaFileService mediaFileService,
                              Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public Author GetAuthor(string title)
        {
            var parsedBookInfo = Parser.ParseBookTitle(title);

            if (parsedBookInfo != null && !parsedBookInfo.AuthorName.IsNullOrWhiteSpace())
            {
                title = parsedBookInfo.AuthorName;
            }

            return _authorService.FindByName(title);
        }

        public RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null)
        {
            var remoteBook = new RemoteBook
            {
                ParsedBookInfo = parsedBookInfo,
            };

            var author = GetAuthor(parsedBookInfo, searchCriteria);

            if (author == null)
            {
                return remoteBook;
            }

            remoteBook.Author = author;
            remoteBook.Books = GetBooks(parsedBookInfo, author, searchCriteria);

            return remoteBook;
        }

        public List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null)
        {
            return GetBooks(parsedBookInfo, author, searchCriteria, new ReleaseTitleMatchScorer.CatalogPreparation());
        }

        public List<List<Book>> GetBooks(IReadOnlyList<ParsedBookInfo> parsedBooks, Author author)
        {
            var timer = Stopwatch.StartNew();
            var preparation = new ReleaseTitleMatchScorer.CatalogPreparation();
            if (author != null && parsedBooks.Any(parsed => parsed?.BookTitle != null &&
                (!parsed.Discography || (parsed.DiscographyStart == 0 && parsed.DiscographyEnd == 0))))
            {
                preparation.GetAuthorCatalog(author.Id, _bookService);
            }

            var results = new List<List<Book>>(parsedBooks.Count);
            foreach (var parsed in parsedBooks)
            {
                results.Add(GetBooks(parsed, author, null, preparation));
            }

            _logger.Log(timer.ElapsedMilliseconds >= SlowTitleLookupMilliseconds ? LogLevel.Warn : LogLevel.Debug, "Matched parsed title batch: authorId={0}, requests={1}, matches={2}, elapsedMs={3:F3}",
                author?.Id ?? 0, parsedBooks.Count, results.Sum(matches => matches.Count), timer.Elapsed.TotalMilliseconds);
            return results;
        }

        private List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria,
                                    ReleaseTitleMatchScorer.CatalogPreparation preparation)
        {
            if (author == null || parsedBookInfo?.BookTitle == null)
            {
                return new List<Book>();
            }

            var mediaType = QualityMediaTypeHelper.GetKnownMediaType(parsedBookInfo.Quality?.Quality);
            if (parsedBookInfo.Discography)
            {
                if (parsedBookInfo.DiscographyStart < 0 || parsedBookInfo.DiscographyStart > 9999 ||
                    parsedBookInfo.DiscographyEnd < 0 || parsedBookInfo.DiscographyEnd > 9999 ||
                    (parsedBookInfo.DiscographyStart > 0 && parsedBookInfo.DiscographyEnd < parsedBookInfo.DiscographyStart))
                {
                    _logger.Warn("Cannot match collection with invalid year range {0}-{1}",
                        parsedBookInfo.DiscographyStart, parsedBookInfo.DiscographyEnd);
                    return new List<Book>();
                }

                List<Book> books;
                if (parsedBookInfo.DiscographyStart > 0)
                {
                    books = _bookService.AuthorBooksBetweenDates(author,
                        new DateTime(parsedBookInfo.DiscographyStart, 1, 1),
                        new DateTime(parsedBookInfo.DiscographyEnd, 12, 31), false);
                }
                else if (parsedBookInfo.DiscographyEnd > 0)
                {
                    books = _bookService.AuthorBooksBetweenDates(author,
                        new DateTime(1800, 1, 1), new DateTime(parsedBookInfo.DiscographyEnd, 12, 31), false);
                }
                else
                {
                    books = preparation.GetAuthorCatalog(author.Id, _bookService);
                }

                return Scope(books).ToList();
            }

            var timer = Stopwatch.StartNew();
            var catalog = Scope(searchCriteria?.AuthorCatalog ??
                preparation.GetAuthorCatalog(author.Id, _bookService)).ToList();
            var candidates = Scope(searchCriteria?.Books ?? catalog).ToList();
            var matches = ReleaseTitleMatchScorer.FindBooksByTitle(parsedBookInfo.BookTitle, author.Name,
                candidates, catalog, preparation);
            timer.Stop();
            _logger.Log(timer.ElapsedMilliseconds >= SlowTitleLookupMilliseconds ? LogLevel.Warn : LogLevel.Debug, "Title lookup for author {0}: catalog={1}, candidates={2}, outcome={3}, strongestMatches={4}, elapsedMs={5}",
                author.Id, catalog.Count, candidates.Count, matches.Count == 1 ? "matched" : matches.Count == 0 ? "unmatched" : "ambiguous",
                matches.Count, timer.Elapsed.TotalMilliseconds);
            return matches.Count == 1 ? matches : new List<Book>();

            IEnumerable<Book> Scope(IEnumerable<Book> books) => (books ?? Enumerable.Empty<Book>())
                .Where(book => book != null && book.AuthorId == author.Id &&
                               (!mediaType.HasValue || book.MediaType == mediaType.Value));
        }

	        public RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds)
	        {
	            var remoteBook = new RemoteBook
	            {
	                ParsedBookInfo = parsedBookInfo
	            };

	            if (authorId > 0)
	            {
	                try
	                {
	                    remoteBook.Author = _authorService.GetAuthor(authorId);
	                }
	                catch (ModelNotFoundException)
	                {
	                    _logger.Debug("[PARSING_SERVICE] Author with ID {0} no longer exists while mapping parsed info; leaving Author unset.", authorId);
	                }
	            }

	            if (bookIds != null)
	            {
	                var validBookIds = bookIds.Where(id => id > 0).Distinct().ToList();

	                if (validBookIds.Any())
	                {
	                    remoteBook.Books = _bookService.GetExistingBooks(validBookIds);
	                }
	            }

	            return remoteBook;
	        }

        private Author GetAuthor(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria)
        {
            Author author = null;

            _logger.Trace("[PARSING_SERVICE] ===== GetAuthor STARTED =====");
            _logger.Trace("[PARSING_SERVICE] Purpose: Find author for parsedAuthorName: '{0}'", parsedBookInfo?.AuthorName ?? "NULL");
            _logger.Trace("[PARSING_SERVICE] searchCriteria is {0}", searchCriteria != null ? "PROVIDED" : "NULL");

            if (searchCriteria != null)
            {
                _logger.Trace("[PARSING_SERVICE] SearchCriteria details:");
                _logger.Trace("  - Author: '{0}' (ID: {1})",
                    searchCriteria.Author?.Name ?? "NULL",
                    searchCriteria.Author?.Id ?? -1);
                _logger.Trace("  - AudiobookQualityProfileId: {0}", searchCriteria.Author?.AudiobookQualityProfileId ?? -999);
                _logger.Trace("  - EbookQualityProfileId: {0}", searchCriteria.Author?.EbookQualityProfileId ?? -999);
                _logger.Trace("  - AudiobookQualityProfile object: {0}",
                    searchCriteria.Author?.AudiobookQualityProfileId.HasValue == true
                        ? $"LOADED ('{searchCriteria.Author.AudiobookQualityProfile?.Value?.Name ?? "Unknown"}')"
                        : "NULL");
                _logger.Trace("  - EbookQualityProfile object: {0}",
                    searchCriteria.Author?.EbookQualityProfileId.HasValue == true
                        ? $"LOADED ('{searchCriteria.Author.EbookQualityProfile?.Value?.Name ?? "Unknown"}')"
                        : "NULL");

                if (AuthorNameMatcher.ContainsAuthorName(searchCriteria.Author.Name, parsedBookInfo.AuthorName))
                {
                    _logger.Trace("[PARSING_SERVICE] MATCH! Using author from search criteria");
                    _logger.Trace("[PARSING_SERVICE] Returning author with profiles - Audiobook: {0}, Ebook: {1}",
                        searchCriteria.Author.AudiobookQualityProfileId.HasValue ? "SET" : "MISSING",
                        searchCriteria.Author.EbookQualityProfileId.HasValue ? "SET" : "MISSING");
                    return searchCriteria.Author;
                }
                else
                {
                    _logger.Trace("[PARSING_SERVICE] NO MATCH - author evidence doesn't match, will search database");
                }
            }
            else
            {
                _logger.Trace("[PARSING_SERVICE] No searchCriteria provided, will search database");
            }

            author = _authorService.FindByName(parsedBookInfo.AuthorName);
            _logger.Trace("[PARSING_SERVICE] FindByName result: {0}",
                author != null ? $"'{author.Name}' (ID: {author.Id})" : "NULL");

            if (author == null)
            {
                _logger.Trace("[PARSING_SERVICE] No matching author found for '{0}'", parsedBookInfo.AuthorName);
                return null;
            }

            _logger.Trace("[PARSING_SERVICE] Final author quality profiles - AudiobookProfile: {0}, EbookProfile: {1}",
                author.AudiobookQualityProfileId.HasValue ? "LOADED" : "NULL",
                author.EbookQualityProfileId.HasValue ? "LOADED" : "NULL");

            return author;
        }

        public Book GetLocalBook(string filename, Author author)
        {
            if (Path.HasExtension(filename))
            {
                filename = Path.GetDirectoryName(filename);
            }

            var tracksInBook = _mediaFileService.GetFilesByAuthor(author.Id)
                .FindAll(s => Path.GetDirectoryName(s.Path) == filename)
                .DistinctBy(s => s.EditionId)
                .ToList();

            Book book = null;

            if (tracksInBook.Count == 1)
            {
                var track = tracksInBook.First();

                // Prefer the joined relationship (BookFile -> Edition -> Book) when available.
                book = track.Edition?.Book;

                // Fallback: resolve EditionId -> BookId safely.
                if (book == null && track.EditionId > 0)
                {
                    try
                    {
                        var edition = _editionService.GetEdition(track.EditionId);
                        book = _bookService.GetBook(edition.BookId);
                    }
                    catch (ModelNotFoundException ex)
                    {
                        _logger.Debug(ex, "Unable to resolve local book for extra-file folder '{0}' (EditionId={1})", filename, track.EditionId);
                        book = null;
                    }
                }
            }

            return book;
        }

    }
}
