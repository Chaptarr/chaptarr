using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;

namespace NzbDrone.Core.ImportLists.Exclusions
{
    public static class ImportListExclusionBookMatcher
    {
        public static List<string> GetCanonicalProviderIds(Book book)
        {
            if (book == null)
            {
                return new List<string>();
            }

            var providerIds = new List<string>
            {
                Normalize(book.HardcoverBookId, "hc"),
                Normalize(book.GoodreadsWorkId, "gr"),
                Normalize(book.OpenLibraryWorkId, "ol")
            };

            providerIds.AddRange(BookEditionIdentity.GetCanonicalEditionProviderIds(book));
            providerIds.AddRange(book.RemoteProviderIds ?? Enumerable.Empty<string>());

            return providerIds
                .Where(id => id.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> GetLookupIds(Book book)
        {
            var lookupIds = ImportListProviderIdHelper.GetExclusionLookupIds(GetCanonicalProviderIds(book));

            if (book != null)
            {
                var isbn10 = BookEditionIdentity.GetIsbn10(book);
                if (isbn10.IsNotNullOrWhiteSpace())
                {
                    lookupIds.Add(isbn10.Trim());
                }

                var isbn13 = BookEditionIdentity.GetIsbn13(book);
                if (isbn13.IsNotNullOrWhiteSpace())
                {
                    lookupIds.Add(isbn13.Trim());
                }
            }

            return lookupIds
                .Where(id => id.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool AppliesToBook(ImportListExclusion exclusion, Book book)
        {
            if (exclusion == null || book == null || !AppliesToMediaType(exclusion, book.MediaType))
            {
                return false;
            }

            return GetLookupIds(book).Any(id => ImportListProviderIdHelper.MatchesExclusionId(id, exclusion.ForeignId));
        }

        public static bool AppliesToProviderId(ImportListExclusion exclusion, string providerId, BookMediaType? mediaType)
        {
            if (exclusion == null || providerId.IsNullOrWhiteSpace() || !AppliesToMediaType(exclusion, mediaType))
            {
                return false;
            }

            var normalized = providerId.Contains(":")
                ? ImportListProviderIdHelper.Normalize(providerId, null)
                : providerId.Trim();

            return ImportListProviderIdHelper.MatchesExclusionId(normalized, exclusion.ForeignId);
        }

        public static bool AppliesToMediaType(ImportListExclusion exclusion, BookMediaType? mediaType)
        {
            if (exclusion == null)
            {
                return false;
            }

            return !exclusion.MediaType.HasValue || (mediaType.HasValue && exclusion.MediaType == mediaType.Value);
        }

        public static BookMediaType GetOppositeMediaType(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Audiobook
                ? BookMediaType.Ebook
                : BookMediaType.Audiobook;
        }

        private static string Normalize(string value, string defaultPrefix)
        {
            return ImportListProviderIdHelper.Normalize(value, defaultPrefix);
        }


    }
}
