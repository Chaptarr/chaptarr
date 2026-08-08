using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MetadataSource.Goodreads
{
    public interface IGoodreadsAutocompleteFallback
    {
        /// <summary>
        /// Attempts to create a book from Goodreads autocomplete data as a last resort
        /// when BookInfo API doesn't have the book.
        /// </summary>
        /// <param name="bookTitle">The title to search for</param>
        /// <param name="author">The existing author in the database</param>
        /// <returns>The created book or null if validation fails</returns>
        Book TryCreateBookFromAutocomplete(string bookTitle, Author author);

        /// <summary>
        /// Validates that an autocomplete result matches the expected author
        /// and meets quality thresholds.
        /// </summary>
        /// <param name="result">The autocomplete search result</param>
        /// <param name="author">The expected author</param>
        /// <returns>True if the result is valid for book creation</returns>
        bool ValidateAutocompleteResult(SearchJsonResource result, Author author);

        /// <summary>
        /// Checks if a book matching the autocomplete result already exists
        /// in the author's book collection using multiple strategies.
        /// </summary>
        /// <param name="result">The autocomplete search result</param>
        /// <param name="authorBooks">All books by the author</param>
        /// <returns>The existing book if found, null otherwise</returns>
        Book CheckForExistingBook(SearchJsonResource result, List<Book> authorBooks);

        /// <summary>
        /// Searches Goodreads autocomplete for a specific book by an author.
        /// </summary>
        /// <param name="bookTitle">The title to search for</param>
        /// <param name="authorName">The author name to include in search</param>
        /// <returns>Autocomplete results or empty list if none found</returns>
        List<SearchJsonResource> SearchAutocomplete(string bookTitle, string authorName);
    }
}
