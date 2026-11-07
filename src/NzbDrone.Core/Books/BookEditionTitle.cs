using System.Text.RegularExpressions;

namespace NzbDrone.Core.Books
{
    internal static class BookEditionTitle
    {
        private static readonly Regex CollapsedWhitespace = new Regex(@"\s+", RegexOptions.Compiled);

        internal static string GetTitleForBook(Book book, Edition edition)
        {
            if (book == null)
            {
                return string.Empty;
            }

            var editionTitle = edition?.Title;
            if (string.IsNullOrWhiteSpace(editionTitle))
            {
                return book.Title ?? string.Empty;
            }

            return GetCollectedWorkTitle(editionTitle, book.Title) ?? editionTitle;
        }

        /// <summary>
        /// Returns the book's own title when an edition title is a slash-separated
        /// compilation that lists it as one of its works. A slash inside a phrase
        /// remains untouched unless a complete segment equals the book title.
        /// </summary>
        private static string GetCollectedWorkTitle(string editionTitle, string bookTitle)
        {
            if (string.IsNullOrWhiteSpace(editionTitle) || string.IsNullOrWhiteSpace(bookTitle))
            {
                return null;
            }

            var segments = editionTitle.Split('/');
            if (segments.Length < 2)
            {
                return null;
            }

            var normalizedBookTitle = NormalizeTitleSegment(bookTitle);
            if (normalizedBookTitle.Length == 0)
            {
                return null;
            }

            foreach (var segment in segments)
            {
                if (string.Equals(NormalizeTitleSegment(segment), normalizedBookTitle, System.StringComparison.OrdinalIgnoreCase))
                {
                    return bookTitle.Trim();
                }
            }

            return null;
        }

        private static string NormalizeTitleSegment(string value)
        {
            return CollapsedWhitespace.Replace(value ?? string.Empty, " ").Trim();
        }
    }
}
