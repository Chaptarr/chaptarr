using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Api.V1.Books
{
    internal static class BookLocalInstanceLookup
    {
        public static Dictionary<Book, List<Book>> FindExistingBooks(IBookService bookService, IProviderAliasService providerAliasService,
            IEnumerable<Book> searchBooks, IEnumerable<Book> loadedLocalBooks = null)
        {
            IEqualityComparer<Book> comparer = ReferenceEqualityComparer.Instance;
            var books = searchBooks.Distinct(comparer).ToList();
            var result = books.ToDictionary(book => book, _ => new List<Book>(), comparer);
            if (bookService == null || books.Count == 0)
            {
                return result;
            }

            var lookups = books.ToDictionary(book => book, book => new (string Scope, string Provider, string Id)[]
            {
                ("work", "hc", book.HardcoverBookId),
                ("work", "gr", book.GoodreadsWorkId),
                ("edition", "gr", BookEditionIdentity.GetGoodreadsEditionProviderId(book)),
                ("work", "ol", book.OpenLibraryWorkId),
                ("edition", "gb", BookEditionIdentity.GetGoogleBooksEditionId(book)),
                ("edition", "az", BookEditionIdentity.GetAsin(book)),
                ("edition", "az", BookEditionIdentity.GetAudibleAsin(book))
            }.Where(key => !string.IsNullOrWhiteSpace(key.Id))
                .Select(key => (key.Scope, key.Provider, Id: NormalizeLookupId(key.Provider, key.Id)))
                .Where(key => key.Id != null)
                .Concat(GetBookProviderAliasLookups(book, providerAliasService)).Distinct().ToList(), comparer);
            var matches = bookService.FindBookIdsByProviderIds(lookups.Values.SelectMany(keys => keys));
            var idsByBook = books.ToDictionary(book => book,
                book => lookups[book].SelectMany(key => matches[key]).ToHashSet(), comparer);

            var ids = idsByBook.Values.SelectMany(values => values).Distinct().ToArray();
            if (ids.Length == 0)
            {
                return result;
            }

            var localBooks = (loadedLocalBooks ?? Enumerable.Empty<Book>()).DistinctBy(book => book.Id).ToDictionary(book => book.Id);
            var missingIds = ids.Where(id => !localBooks.ContainsKey(id)).ToArray();
            foreach (var book in missingIds.Length > 0 ? bookService.GetBooksForLookup(missingIds) : new List<Book>())
            {
                localBooks[book.Id] = book;
            }

            foreach (var book in books)
            {
                result[book] = idsByBook[book].Where(localBooks.ContainsKey).OrderBy(id => id).Select(id => localBooks[id]).ToList();
            }

            return result;
        }

        private static string NormalizeLookupId(string provider, string id)
        {
            try
            {
                return ProviderIdHelper.Canonicalize(id, provider);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static IEnumerable<(string Scope, string Provider, string Id)> GetBookProviderAliasLookups(Book book,
            IProviderAliasService providerAliasService)
        {
            if (providerAliasService == null)
            {
                return Enumerable.Empty<(string Scope, string Provider, string Id)>();
            }

            // Work and edition numbers can collide. Preserve the source field's
            // scope through the shared batch instead of testing each ID in both namespaces.
            var works = BookEditionIdentity.GetCanonicalWorkProviderIds(book)
                .Concat(new[] { book.BaseBookId })
                .Concat(book.RemoteProviderIds ?? Enumerable.Empty<string>());
            var editions = BookEditionIdentity.GetCanonicalEditionProviderIds(book)
                .Concat((book.Editions ?? new List<Edition>()).SelectMany(BookEditionIdentity.GetEditionProviderIds));
            return providerAliasService.NormalizeProviderIds(works)
                .Select(key => (Scope: "work", key.Provider, Id: $"{key.Provider}:{key.NormalizedProviderId}"))
                .Concat(providerAliasService.NormalizeProviderIds(editions)
                    .Select(key => (Scope: "edition", key.Provider, Id: $"{key.Provider}:{key.NormalizedProviderId}")));
        }
    }
}
