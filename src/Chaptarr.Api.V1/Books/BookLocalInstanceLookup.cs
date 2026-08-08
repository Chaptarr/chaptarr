using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Api.V1.Books
{
    internal static class BookLocalInstanceLookup
    {
        public static List<Book> FindExistingBooks(IBookService bookService, IProviderAliasService providerAliasService, Book searchBook, BookMediaType mediaType)
        {
            if (bookService == null || searchBook == null)
            {
                return new List<Book>();
            }

            var lookups = new (string Provider, string Id)[]
            {
                ("hc", searchBook.HardcoverBookId),
                ("gr", searchBook.GoodreadsWorkId),
                ("gr", BookEditionIdentity.GetGoodreadsEditionProviderId(searchBook)),
                ("ol", searchBook.OpenLibraryWorkId),
                ("gb", BookEditionIdentity.GetGoogleBooksEditionId(searchBook)),
                ("az", BookEditionIdentity.GetAsin(searchBook)),
                ("az", BookEditionIdentity.GetAudibleAsin(searchBook))
            };

            var results = new List<Book>();
            var seen = new HashSet<int>();

            if (providerAliasService != null)
            {
                try
                {
                    var aliasLookups = GetBookProviderAliasLookups(searchBook);
                    var aliasBookIds = providerAliasService.FindBookIds("work", aliasLookups)
                        .Concat(providerAliasService.FindBookIds("edition", aliasLookups))
                        .Distinct()
                        .ToList();

                    if (aliasBookIds.Count > 0)
                    {
                        foreach (var match in bookService.GetBooks(aliasBookIds).Where(b => b?.MediaType == mediaType))
                        {
                            if (match != null && seen.Add(match.Id))
                            {
                                results.Add(match);
                            }
                        }
                    }
                }
                catch
                {
                    // Alias index is an optimization over the legacy provider lookup path; never make search fail because it is unavailable.
                }
            }

            foreach (var (provider, id) in lookups)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                try
                {
                    var existing = bookService.FindAllByProviderId(provider, id, mediaType) ?? new List<Book>();
                    foreach (var match in existing)
                    {
                        if (match != null && seen.Add(match.Id))
                        {
                            results.Add(match);
                        }
                    }
                }
                catch
                {
                    // Ignore lookup failures; other provider aliases can still identify the local instance.
                }
            }

            if (BookEditionIdentity.GetAsin(searchBook) is string asin && !string.IsNullOrEmpty(asin))
            {
                try
                {
                    var existing = bookService.FindAllByProviderId("az", asin, mediaType) ?? new List<Book>();
                    foreach (var match in existing)
                    {
                        if (match != null && seen.Add(match.Id))
                        {
                            results.Add(match);
                        }
                    }
                }
                catch
                {
                    // Ignore lookup failures.
                }
            }

            return results
                .OrderBy(b => b.Id)
                .ToList();
        }

        private static List<string> GetBookProviderAliasLookups(Book searchBook)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string id)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id.Trim());
                }
            }

            Add(searchBook?.ForeignEditionId);
            Add(searchBook?.BaseBookId);
            Add(searchBook?.HardcoverBookId);
            Add(searchBook?.GoodreadsWorkId);
            Add(searchBook?.OpenLibraryWorkId);
            Add(searchBook?.GoogleBooksId);
            Add(searchBook?.ASIN);
            Add(searchBook?.AudibleASIN);

            foreach (var id in BookEditionIdentity.GetCanonicalWorkProviderIds(searchBook))
            {
                Add(id);
            }

            foreach (var id in BookEditionIdentity.GetCanonicalEditionProviderIds(searchBook))
            {
                Add(id);
            }

            if (searchBook?.RemoteProviderIds != null)
            {
                foreach (var id in searchBook.RemoteProviderIds)
                {
                    Add(id);
                }
            }

            return ids.ToList();
        }
    }
}
