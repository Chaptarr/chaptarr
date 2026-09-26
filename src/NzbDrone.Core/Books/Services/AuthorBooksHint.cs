using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NzbDrone.Core.Books
{
    /// <summary>
    /// The author's local books as loaded for one author refresh, plus the work groups derived from them.
    /// One instance belongs to exactly one refresh on one thread (RefreshBookService keeps it in an AsyncLocal),
    /// so concurrent RefreshAuthor commands for different authors never share it.
    /// </summary>
    internal sealed class AuthorBooksHint
    {
        private string _signature;
        private List<List<int>> _groupIds;

        public AuthorBooksHint(IEnumerable<Book> books)
        {
            Books = new List<Book>(books ?? Enumerable.Empty<Book>());
        }

        public List<Book> Books { get; }

        public void Remove(int bookId)
        {
            Books.RemoveAll(book => book?.Id == bookId);
            _signature = null;
            _groupIds = null;
        }

        /// <summary>
        /// Returns the work groups for <paramref name="booksById"/>, reusing the previous grouping only while the
        /// identity of every book that grouping depends on is unchanged. The cached membership is stored as book ids
        /// and mapped back onto the instances passed in now, so a group never holds a stale copy of a book.
        /// </summary>
        public List<List<Book>> GetWorkGroups(Dictionary<int, Book> booksById, Func<List<Book>, List<List<Book>>> build)
        {
            var signature = ComputeSignature(booksById.Values);
            if (_groupIds != null && string.Equals(_signature, signature, StringComparison.Ordinal))
            {
                return _groupIds.Select(ids => ids.Select(id => booksById[id]).ToList()).ToList();
            }

            var groups = build(booksById.Values.ToList());
            _groupIds = groups.Select(group => group.Select(book => book.Id).ToList()).ToList();
            _signature = signature;
            return groups;
        }

        private static string ComputeSignature(IEnumerable<Book> books)
        {
            var builder = new StringBuilder();
            foreach (var book in books)
            {
                builder.Append(book.Id).Append('|').Append((int)book.MediaType).Append('|');
                Append(builder, BookIdentity.GetStableWorkProviderIdentityTokens(book));
                Append(builder, BookIdentity.GetEditionProviderIdentityTokens(book));
                builder.Append(book.HardcoverBookId).Append('|').Append(book.GoodreadsWorkId).Append('|').Append(book.OpenLibraryWorkId).Append('|');
                Append(builder, book.RemoteProviderIds);
                builder.Append(';');
            }

            return builder.ToString();
        }

        private static void Append(StringBuilder builder, IEnumerable<string> values)
        {
            if (values != null)
            {
                foreach (var value in values.Where(v => v != null).OrderBy(v => v, StringComparer.Ordinal))
                {
                    builder.Append(value).Append(',');
                }
            }

            builder.Append('|');
        }
    }
}
