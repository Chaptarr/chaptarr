using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class LocalProviderWorkBoundaryResolver
    {
        internal static bool TryResolve(
            IBookService bookService,
            Author author,
            string providerWorkId,
            BookMediaType mediaType,
            Logger logger,
            string logContext,
            out List<Book> books,
            out string reason)
        {
            books = new List<Book>();
            reason = null;

            if (bookService == null ||
                author?.Id <= 0 ||
                !ProviderIdHelper.TryNormalize(providerWorkId, defaultPrefix: null, out var normalizedWorkId))
            {
                reason = "V5_LOCAL_WORK_IDENTITY_INVALID";
                return false;
            }

            try
            {
                var separator = normalizedWorkId.IndexOf(':');
                books = bookService.FindAllByWorkProviderId(
                        normalizedWorkId.Substring(0, separator),
                        ProviderIdHelper.StripPrefix(normalizedWorkId),
                        mediaType)
                    .Where(book => book != null &&
                                   book.Id > 0 &&
                                   book.AuthorId == author.Id &&
                                   book.MediaType == mediaType)
                    .GroupBy(book => book.Id)
                    .Select(group => group.First())
                    .OrderBy(book => book.Id)
                    .ToList();

                if (books.Count == 0)
                {
                    reason = "V5_WORK_NOT_LOCAL";
                    return false;
                }

                var first = books[0];
                if (BookEditionIdentity.GetCanonicalWorkProviderIds(first).Count == 0 ||
                    books.Skip(1).Any(candidate => !WorkIdMatcher.WorkIdMatches(first, candidate)))
                {
                    books = new List<Book>();
                    reason = "V5_WORK_ALIAS_AMBIGUOUS";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger?.Debug(
                    ex,
                    "[{0}] Failed to resolve local provider-work boundary for '{1}'",
                    logContext ?? "PROVIDER-WORK",
                    providerWorkId);
                books = new List<Book>();
                reason = "V5_LOCAL_WORK_LOOKUP_FAILED";
                return false;
            }
        }
    }
}
