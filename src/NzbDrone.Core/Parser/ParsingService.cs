using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
// using NzbDrone.Core.MediaFiles.BookImport.Identification; // Disabled - old identification system
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Parser
{
    public interface IParsingService
    {
        Author GetAuthor(string title);
        RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null);
        RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds);
        List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null);

        ParsedBookInfo ParseBookTitleFuzzy(string title);

        // Music stuff here
        Book GetLocalBook(string filename, Author author);
    }

    public class ParsingService : IParsingService
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAudioTagService _audioTagService;
        private readonly Logger _logger;
        // private readonly IDeterministicMatcher _deterministicMatcher; // Disabled - old identification system
        // private readonly IExactStringMatcher _stringMatcher; // Disabled - old identification system

        // Enhanced title cleaning regex for audiobook artifacts (same as DistanceCalculator)
        private static readonly Regex CleanTitleCruft = new Regex(@"\((?:unabridged|abridged|audiobook|audio\s*book)\)|\[(?:MP3|M4B|AAC|FLAC|OGG)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Series detection patterns
        private static readonly Regex[] SeriesPatterns = new Regex[]
        {
            new Regex(@"^(.*?)\s+(?:Book|Series|Part|Vol|Volume|#)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^(.*?)\s*[:\-,]\s+(?:Book|Series|Part|Vol|Volume|#)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^(.*?)\s*\((?:Book|Series|Part|Vol|Volume|#)\s*(\d+(?:\.\d+)?)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        public ParsingService(IAuthorService authorService,
                              IBookService bookService,
                              IEditionService editionService,
                              IMediaFileService mediaFileService,
                              IAudioTagService audioTagService,
                              Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _audioTagService = audioTagService;
            _logger = logger;
            // _deterministicMatcher = new DeterministicMatchingService(logger); // Disabled - old identification system
            // _stringMatcher = new ExactStringMatcher(logger); // Disabled - old identification system
        }

        public Author GetAuthor(string title)
        {
            var parsedBookInfo = Parser.ParseBookTitle(title);

            if (parsedBookInfo != null && !parsedBookInfo.AuthorName.IsNullOrWhiteSpace())
            {
                title = parsedBookInfo.AuthorName;
            }

            var authorInfo = _authorService.FindByName(title);

            if (authorInfo == null)
            {
                _logger.Debug("Trying inexact author match for {0}", title);
                authorInfo = _authorService.FindByNameInexact(title);
            }

            return authorInfo;
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
            var bookTitle = parsedBookInfo.BookTitle;
            var result = new List<Book>();
            var requestedMediaType = GetRequestedMediaType(parsedBookInfo);

            if (parsedBookInfo.BookTitle == null)
            {
                return new List<Book>();
            }

            Book bookInfo = null;

            if (parsedBookInfo.Discography)
            {
                if (parsedBookInfo.DiscographyStart > 0)
                {
                    return _bookService.AuthorBooksBetweenDates(author,
                        new DateTime(parsedBookInfo.DiscographyStart, 1, 1),
                        new DateTime(parsedBookInfo.DiscographyEnd, 12, 31),
                        false);
                }

                if (parsedBookInfo.DiscographyEnd > 0)
                {
                    return _bookService.AuthorBooksBetweenDates(author,
                        new DateTime(1800, 1, 1),
                        new DateTime(parsedBookInfo.DiscographyEnd, 12, 31),
                        false);
                }

                return _bookService.GetBooksByAuthor(author.Id);
            }

            // Enhanced title processing with smart cleaning and author name removal
            var originalTitle = bookTitle;
            var cleanedTitle = CleanTitleCruft.Replace(bookTitle, "").Trim();
            var authorRemovedTitle = RemoveAuthorFromTitle(cleanedTitle, author.Name);
            var normalizedTitle = NormalizeForComparison(cleanedTitle);

            var titleVariants = new[] { originalTitle, cleanedTitle, authorRemovedTitle, normalizedTitle }
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            _logger.Debug("Searching for book with title variants: {0}", string.Join(" | ", titleVariants));

            if (searchCriteria != null)
            {
                var candidateBooks = FilterBooksByMediaType(searchCriteria.Books, requestedMediaType).ToList();
                var cleanTitle = Parser.CleanAuthorName(parsedBookInfo.BookTitle);
                bookInfo = candidateBooks.ExclusiveOrDefault(e => e.Title == bookTitle || e.CleanTitle == cleanTitle);

                // Enhanced search criteria matching
                if (bookInfo == null)
                {
                    foreach (var variant in titleVariants)
                    {
                        bookInfo = candidateBooks.ExclusiveOrDefault(e =>
                            IsEnhancedTitleMatch(e.Title, variant) ||
                            IsEnhancedTitleMatch(e.CleanTitle, variant));
                        if (bookInfo != null)
                        {
                            _logger.Debug("Found book via enhanced search criteria matching with variant: {0}", variant);
                            break;
                        }
                    }
                }
            }

            // Try exact matches with title variants
            if (bookInfo == null)
            {
                foreach (var variant in titleVariants)
                {
                    bookInfo = FindExactBookMatch(author.Id, variant, requestedMediaType);
                    if (bookInfo != null)
                    {
                        _logger.Debug("Found book via exact title match with variant: {0}", variant);
                        break;
                    }
                }
            }

            // Try edition matches with title variants
            if (bookInfo == null)
            {
                foreach (var variant in titleVariants)
                {
                    var edition = FindExactEditionMatch(author.Id, variant, requestedMediaType);
                    if (edition?.Book != null)
                    {
                        bookInfo = edition.Book;
                        _logger.Debug("Found book via exact edition match with variant: {0}", variant);
                        break;
                    }
                }
            }

            // Try inexact book matches with enhanced logic
            if (bookInfo == null)
            {
                _logger.Debug("Trying inexact book match for {0}", parsedBookInfo.BookTitle);
                foreach (var variant in titleVariants)
                {
                    bookInfo = FindInexactBookMatch(author.Id, variant, requestedMediaType);
                    if (bookInfo != null)
                    {
                        _logger.Debug("Found book via inexact match with variant: {0}", variant);
                        break;
                    }
                }
            }

            // Try inexact edition matches with enhanced logic
            if (bookInfo == null)
            {
                _logger.Debug("Trying inexact edition match for {0}", parsedBookInfo.BookTitle);
                foreach (var variant in titleVariants)
                {
                    var edition = FindInexactEditionMatch(author.Id, variant, requestedMediaType);
                    if (edition?.Book != null)
                    {
                        bookInfo = edition.Book;
                        _logger.Debug("Found book via inexact edition match with variant: {0}", variant);
                        break;
                    }
                }
            }

            if (bookInfo != null)
            {
                result.Add(bookInfo);
                _logger.Debug("Found book via enhanced matching: {0}", bookInfo.Title);
            }
            else
            {
                _logger.Debug("Unable to find {0} with any title variant", parsedBookInfo);
            }

            return result;
        }

        private static IEnumerable<Book> FilterBooksByMediaType(IEnumerable<Book> books, BookMediaType? requestedMediaType)
        {
            var candidates = books?.Where(b => b != null) ?? Enumerable.Empty<Book>();

            if (!requestedMediaType.HasValue)
            {
                return candidates;
            }

            return candidates.Where(b => b.MediaType == requestedMediaType.Value);
        }

        private Book FindExactBookMatch(int authorId, string title, BookMediaType? requestedMediaType)
        {
            var book = _bookService.FindByTitle(authorId, title);

            if (!requestedMediaType.HasValue || book == null || book.MediaType == requestedMediaType.Value)
            {
                return book;
            }

            return _bookService.GetBooksByAuthorId(authorId)
                .Where(b => b.MediaType == requestedMediaType.Value && IsExactBookTitleMatch(b, title))
                .OrderBy(b => b.Id)
                .FirstOrDefault();
        }

        private Edition FindExactEditionMatch(int authorId, string title, BookMediaType? requestedMediaType)
        {
            var edition = _editionService.FindByTitle(authorId, title);

            if (!requestedMediaType.HasValue || edition?.Book == null || edition.Book.MediaType == requestedMediaType.Value)
            {
                return edition;
            }

            return _editionService.GetEditionsByAuthor(authorId)
                .Where(e => e?.Book != null &&
                            e.Monitored &&
                            e.Book.MediaType == requestedMediaType.Value &&
                            string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Id)
                .FirstOrDefault();
        }

        private Book FindInexactBookMatch(int authorId, string title, BookMediaType? requestedMediaType)
        {
            var book = _bookService.FindByTitleInexact(authorId, title);

            if (!requestedMediaType.HasValue || book == null || book.MediaType == requestedMediaType.Value)
            {
                return book;
            }

            var requestedCandidates = _bookService.GetCandidates(authorId, title)
                .Where(b => b.MediaType == requestedMediaType.Value)
                .OrderBy(b => b.Id)
                .ToList();

            return requestedCandidates.Count == 1 ? requestedCandidates[0] : null;
        }

        private Edition FindInexactEditionMatch(int authorId, string title, BookMediaType? requestedMediaType)
        {
            var edition = _editionService.FindByTitleInexact(authorId, title);

            if (!requestedMediaType.HasValue || edition?.Book == null || edition.Book.MediaType == requestedMediaType.Value)
            {
                return edition;
            }

            var requestedCandidates = _editionService.GetCandidates(authorId, title)
                .Where(e => e?.Book != null && e.Book.MediaType == requestedMediaType.Value)
                .OrderBy(e => e.Id)
                .ToList();

            return requestedCandidates.Count == 1 ? requestedCandidates[0] : null;
        }

        private static bool IsExactBookTitleMatch(Book book, string title)
        {
            if (book == null || string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            var cleanTitle = Parser.CleanAuthorName(title);
            if (string.IsNullOrEmpty(cleanTitle))
            {
                cleanTitle = title;
            }

            return string.Equals(book.Title, title, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(book.CleanTitle, cleanTitle, StringComparison.OrdinalIgnoreCase);
        }

        private static BookMediaType? GetRequestedMediaType(ParsedBookInfo parsedBookInfo)
        {
            return QualityMediaTypeHelper.GetKnownMediaType(parsedBookInfo?.Quality?.Quality);
        }

        /// <summary>
        /// Enhanced title matching logic using deterministic matching
        /// </summary>
        private bool IsEnhancedTitleMatch(string dbTitle, string searchTitle)
        {
            if (string.IsNullOrWhiteSpace(dbTitle) || string.IsNullOrWhiteSpace(searchTitle))
            {
                return false;
            }

            // Use deterministic book title matching
            return dbTitle.Equals(searchTitle, StringComparison.OrdinalIgnoreCase);
        }

        // Note: The series-aware matching logic was removed because it was unreachable after
        // the deterministic matcher return. If series-aware matching is needed, it should be
        // implemented within the deterministic matcher.

        /// <summary>
        /// Remove author name from title to improve matching accuracy
        /// </summary>
        private string RemoveAuthorFromTitle(string title, string authorName)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(authorName))
            {
                return title;
            }

            // Try to use existing SplitBookTitle extension if available
            try
            {
                var (titlePart, _) = title.SplitBookTitle(authorName);
                if (!string.IsNullOrWhiteSpace(titlePart) && titlePart != title)
                {
                    return titlePart;
                }
            }
            catch
            {
                // Fallback to regex patterns if SplitBookTitle fails
            }

            // Fallback patterns for author name removal
            var authorPatterns = new[]
            {
                $@"^{Regex.Escape(authorName)}\s*[-:]\s*(.+)$", // "Author - Title" or "Author: Title"
                $@"^(.+?)\s+by\s+{Regex.Escape(authorName)}\s*$", // "Title by Author"
                $@"^(.+?)\s*\({Regex.Escape(authorName)}\)\s*$" // "Title (Author)"
            };

            foreach (var pattern in authorPatterns)
            {
                var match = Regex.Match(title, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var extractedTitle = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(extractedTitle) && extractedTitle.Length > 3)
                    {
                        return extractedTitle;
                    }
                }
            }

            return title;
        }

        /// <summary>
        /// Extract series information from title
        /// </summary>
        private (string seriesTitle, double? seriesNumber) ExtractSeriesInfo(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return (null, null);
            }

            foreach (var pattern in SeriesPatterns)
            {
                var match = pattern.Match(title.Trim());
                if (match.Success)
                {
                    var seriesName = match.Groups[1].Value.Trim(' ', ',', '.', ':', ';', '-');
                    var numberStr = match.Groups[2].Value;

                    if (double.TryParse(numberStr, out var seriesNumber))
                    {
                        return (seriesName, seriesNumber);
                    }
                }
            }

            return (null, null);
        }

        /// <summary>
        /// Normalize title for comparison by removing artifacts and standardizing format
        /// </summary>
        private string NormalizeForComparison(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var normalized = title;

            // Remove common audiobook artifacts
            normalized = Regex.Replace(normalized, @"\b(?:unabridged|abridged|audiobook|audio\s*book|complete)\b", "", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\[(?:mp3|m4b|aac|flac|ogg)\]", "", RegexOptions.IgnoreCase);

            return UnicodeComparisonNormalizer.NormalizeWords(normalized);
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
            var authorCandidates = GetAuthorCandidates(parsedBookInfo?.AuthorName);

            _logger.Trace("[PARSING_SERVICE] ===== GetAuthor STARTED =====");
            _logger.Trace("[PARSING_SERVICE] Purpose: Find author for parsedAuthorName: '{0}'", parsedBookInfo?.AuthorName ?? "NULL");
            _logger.Trace("[PARSING_SERVICE] searchCriteria is {0}", searchCriteria != null ? "PROVIDED" : "NULL");

            if (searchCriteria != null)
            {
                _logger.Trace("[PARSING_SERVICE] SearchCriteria details:");
                _logger.Trace("  - Author: '{0}' (ID: {1})",
                    searchCriteria.Author?.Name ?? "NULL",
                    searchCriteria.Author?.Id ?? -1);
                _logger.Trace("  - Author CleanName: '{0}'", searchCriteria.Author?.CleanName ?? "NULL");
                _logger.Trace("  - Parsed CleanName: '{0}'", parsedBookInfo?.AuthorName?.CleanAuthorName() ?? "NULL");
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

                var cleanNameMatch = searchCriteria.Author.CleanName == parsedBookInfo.AuthorName.CleanAuthorName();
                var candidateCleanNameMatch = authorCandidates.Any(candidate => searchCriteria.Author.CleanName == candidate.CleanAuthorName());
                _logger.Trace("[PARSING_SERVICE] Clean name comparison: '{0}' == '{1}' = {2} (candidate match: {3})",
                    searchCriteria.Author.CleanName,
                    parsedBookInfo.AuthorName.CleanAuthorName(),
                    cleanNameMatch,
                    candidateCleanNameMatch);

                if (cleanNameMatch || candidateCleanNameMatch)
                {
                    _logger.Trace("[PARSING_SERVICE] MATCH! Using author from search criteria");
                    _logger.Trace("[PARSING_SERVICE] Returning author with profiles - Audiobook: {0}, Ebook: {1}",
                        searchCriteria.Author.AudiobookQualityProfileId.HasValue ? "SET" : "MISSING",
                        searchCriteria.Author.EbookQualityProfileId.HasValue ? "SET" : "MISSING");
                    return searchCriteria.Author;
                }
                else
                {
                    _logger.Trace("[PARSING_SERVICE] NO MATCH - clean names don't match, will search database");
                }
            }
            else
            {
                _logger.Trace("[PARSING_SERVICE] No searchCriteria provided, will search database");
            }

            author = _authorService.FindByName(parsedBookInfo.AuthorName);
            _logger.Trace("[PARSING_SERVICE] FindByName result: {0}",
                author != null ? $"'{author.Name}' (ID: {author.Id})" : "NULL");

            if (author == null && authorCandidates.Count > 1)
            {
                author = FindUniqueAuthorCandidate(authorCandidates, candidate => _authorService.FindByName(candidate));
                _logger.Trace("[PARSING_SERVICE] FindByName split-candidate result: {0}",
                    author != null ? $"'{author.Name}' (ID: {author.Id})" : "NULL");
            }

            if (author == null)
            {
                _logger.Trace("[PARSING_SERVICE] Trying inexact author match for {0}", parsedBookInfo.AuthorName);
                author = _authorService.FindByNameInexact(parsedBookInfo.AuthorName);
                _logger.Trace("[PARSING_SERVICE] FindByNameInexact result: {0}",
                    author != null ? $"'{author.Name}' (ID: {author.Id})" : "NULL");
            }

            if (author == null && authorCandidates.Count > 1)
            {
                author = FindUniqueAuthorCandidate(authorCandidates, candidate => _authorService.FindByNameInexact(candidate));
                _logger.Trace("[PARSING_SERVICE] FindByNameInexact split-candidate result: {0}",
                    author != null ? $"'{author.Name}' (ID: {author.Id})" : "NULL");
            }

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

        private static List<string> GetAuthorCandidates(string authorName)
        {
            if (string.IsNullOrWhiteSpace(authorName))
            {
                return new List<string>();
            }

            var candidates = new List<string> { authorName.Trim() };
            var splitCandidates = Regex.Split(authorName, @"\s*(?:,|&|\band\b|;)\s*", RegexOptions.IgnoreCase)
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Select(candidate => candidate.Trim());

            foreach (var candidate in splitCandidates)
            {
                if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        private static Author FindUniqueAuthorCandidate(IEnumerable<string> candidates, Func<string, Author> lookup)
        {
            var matches = candidates
                .Skip(1)
                .Select(lookup)
                .Where(author => author != null)
                .GroupBy(author => author.Id)
                .Select(group => group.First())
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        public ParsedBookInfo ParseBookTitleFuzzy(string title)
        {
            var bestScore = 0.0;

            Author bestAuthor = null;
            Book bestBook = null;

            // Enhanced title preprocessing with smart cleaning
            var originalTitle = title;
            var cleanedTitle = CleanTitleCruft.Replace(title, "").Trim();
            var normalizedTitle = NormalizeForComparison(cleanedTitle);

            var titleVariants = new[] { originalTitle, cleanedTitle, normalizedTitle }
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            _logger.Debug("Fuzzy parsing with title variants: {0}", string.Join(" | ", titleVariants));

            var possibleAuthors = _authorService.GetReportCandidates(title);

            foreach (var author in possibleAuthors)
            {
                _logger.Trace($"Trying possible author {author}");

                // Enhanced author matching with best title variant
                var bestAuthorMatch = 0.0;
                foreach (var variant in titleVariants)
                {
                    if (variant.Equals(author.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        bestAuthorMatch = 1.0;
                        break;
                    }
                }

                var possibleBooks = _bookService.GetCandidates(author.Id, title);

                foreach (var book in possibleBooks)
                {
                    // Enhanced book matching with exact vs containment preference
                    var bestBookMatch = 0.0;
                    var hasExactMatch = false;

                    foreach (var variant in titleVariants)
                    {
                        // Check for exact match first (highest priority)
                        if (IsEnhancedTitleMatch(book.Title, variant))
                        {
                            if (NormalizeForComparison(book.Title) == NormalizeForComparison(variant))
                            {
                                bestBookMatch = 1.0; // Perfect exact match
                                hasExactMatch = true;
                                break;
                            }
                        }

                        if (!hasExactMatch)
                        {
                            if (variant.Equals(book.Title, StringComparison.OrdinalIgnoreCase))
                            {
                                bestBookMatch = 1.0;
                            }
                        }
                    }

                    // Boost score for exact matches
                    var score = hasExactMatch ?
                        ((bestAuthorMatch + bestBookMatch) / 2) + 0.1 : // Exact match bonus
                        (bestAuthorMatch + bestBookMatch) / 2;

                    _logger.Trace($"Book {book} has score {score} (exact: {hasExactMatch})");

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAuthor = author;
                        bestBook = book;
                    }
                }

                var possibleEditions = _editionService.GetCandidates(author.Id, title);
                foreach (var edition in possibleEditions)
                {
                    // Enhanced edition matching with exact vs containment preference
                    var bestEditionMatch = 0.0;
                    var hasExactMatch = false;

                    foreach (var variant in titleVariants)
                    {
                        // Check for exact match first (highest priority)
                        if (IsEnhancedTitleMatch(edition.Title, variant))
                        {
                            if (NormalizeForComparison(edition.Title) == NormalizeForComparison(variant))
                            {
                                bestEditionMatch = 1.0; // Perfect exact match
                                hasExactMatch = true;
                                break;
                            }
                        }

                        if (!hasExactMatch)
                        {
                            if (variant.Equals(edition.Title, StringComparison.OrdinalIgnoreCase))
                            {
                                bestEditionMatch = 1.0;
                            }
                        }
                    }

                    // Boost score for exact matches
                    var score = hasExactMatch ?
                        ((bestAuthorMatch + bestEditionMatch) / 2) + 0.1 : // Exact match bonus
                        (bestAuthorMatch + bestEditionMatch) / 2;

                    _logger.Trace($"Edition {edition} has score {score} (exact: {hasExactMatch})");

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAuthor = author;
                        bestBook = edition.Book;
                    }
                }
            }

            _logger.Trace($"Best match: {bestAuthor} {bestBook} with score {bestScore}");

            if (bestAuthor != null && bestBook != null)
            {
                return Parser.ParseBookTitleWithSearchCriteria(title, bestAuthor, new List<Book> { bestBook });
            }

            return null;
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

        /// <summary>
        /// Cross-field validation helper for use by other parsing components
        /// </summary>
        public bool ValidateAuthorInMetadata(string authorName, string audioFilePath)
        {
            if (string.IsNullOrWhiteSpace(authorName) || string.IsNullOrWhiteSpace(audioFilePath))
            {
                return false;
            }

            try
            {
                var allFields = _audioTagService.ReadAllTags(audioFilePath);

                foreach (var field in allFields.Values.SelectMany(v => v))
                {
                    if (!string.IsNullOrWhiteSpace(field) && field.Contains(authorName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Debug("Cross-field validation SUCCESS in ParsingService: Author '{0}' found in ID3 field: '{1}'", authorName, field);
                        return true;
                    }
                }

                _logger.Debug("Cross-field validation FAILED in ParsingService: Author '{0}' not found in any ID3 field", authorName);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error during cross-field validation for author: {0}", authorName);
                return false;
            }
        }
    }
}
