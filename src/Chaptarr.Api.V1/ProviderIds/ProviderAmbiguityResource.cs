using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Api.V1.ProviderIds
{
    public class ProviderAmbiguityResource
    {
        public string Error { get; set; } = "Ambiguous provider identity";
        public string Message { get; set; }
        public string EntityType { get; set; }
        public string Field { get; set; }
        public string ProviderId { get; set; }
        public string MediaType { get; set; }
        public List<ProviderAmbiguityCandidateResource> Candidates { get; set; } = new List<ProviderAmbiguityCandidateResource>();
    }

    public class ProviderAmbiguityCandidateResource
    {
        public int Id { get; set; }
        public string EntityType { get; set; }
        public string Title { get; set; }
        public string AuthorName { get; set; }
        public string TitleSlug { get; set; }
        public string ForeignAuthorId { get; set; }
        public string MediaType { get; set; }
        public string Narrator { get; set; }
        public string Disambiguation { get; set; }
        public bool? Monitored { get; set; }
        public bool? HasFiles { get; set; }
    }

    public static class ProviderAmbiguityHelper
    {
        public const int StatusCode = 409;

        public static ProviderAmbiguityResource GetAuthorAmbiguity(
            IAuthorService authorService,
            IProviderAliasService providerAliasService,
            string prefixedProviderId,
            string field,
            Logger logger,
            string operation = null)
        {
            var (provider, rawId) = SplitProviderId(prefixedProviderId);
            return GetAuthorAmbiguity(authorService, providerAliasService, provider, rawId, field, logger, operation);
        }

        public static ProviderAmbiguityResource GetAuthorAmbiguity(
            IAuthorService authorService,
            IProviderAliasService providerAliasService,
            string provider,
            string providerId,
            string field,
            Logger logger,
            string operation = null)
        {
            var matches = FindAuthorProviderMatches(authorService, providerAliasService, provider, providerId, logger);
            if (matches.Count <= 1)
            {
                return null;
            }

            var normalizedProviderId = NormalizeProviderId(provider, providerId);
            logger?.Warn("[API-AMBIGUOUS-PROVIDER] {0}={1} matched {2} local authors{3}; refusing to choose one",
                field,
                normalizedProviderId,
                matches.Count,
                FormatOperation(operation));

            return new ProviderAmbiguityResource
            {
                Message = $"{field} '{normalizedProviderId}' resolves to multiple local authors. Retry with a local author id or a more specific provider identity.",
                EntityType = "author",
                Field = field,
                ProviderId = normalizedProviderId,
                Candidates = matches.Select(ToAuthorCandidate).ToList()
            };
        }

        public static ProviderAmbiguityResource GetBookAmbiguity(
            IBookService bookService,
            Book book,
            string field,
            Logger logger,
            string operation = null)
        {
            if (book == null)
            {
                return null;
            }

            foreach (var (provider, id) in GetBookProviderLookups(book))
            {
                var ambiguity = GetBookAmbiguity(bookService, provider, id, book.MediaType, field, logger, operation);
                if (ambiguity != null)
                {
                    return ambiguity;
                }
            }

            return null;
        }

        public static ProviderAmbiguityResource GetBookAmbiguity(
            IBookService bookService,
            string prefixedProviderId,
            BookMediaType mediaType,
            string field,
            Logger logger,
            string operation = null)
        {
            var (provider, rawId) = SplitProviderId(prefixedProviderId);
            return GetBookAmbiguity(bookService, provider, rawId, mediaType, field, logger, operation);
        }

        public static ProviderAmbiguityResource GetBookAmbiguity(
            IBookService bookService,
            string provider,
            string providerId,
            BookMediaType mediaType,
            string field,
            Logger logger,
            string operation = null)
        {
            var matches = FindBookProviderMatches(bookService, provider, providerId, mediaType);
            if (matches.Count <= 1)
            {
                return null;
            }

            var normalizedProviderId = NormalizeProviderId(provider, providerId);
            logger?.Warn("[API-AMBIGUOUS-PROVIDER] {0}={1} mediaType={2} matched {3} local books{4}; refusing to choose one",
                field,
                normalizedProviderId,
                mediaType,
                matches.Count,
                FormatOperation(operation));

            var mediaTypeValue = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";

            return new ProviderAmbiguityResource
            {
                Message = $"{field} '{normalizedProviderId}' resolves to multiple local {mediaType} book rows. Retry with a local row id or a more specific scoped identity.",
                EntityType = "book",
                Field = field,
                ProviderId = normalizedProviderId,
                MediaType = mediaTypeValue,
                Candidates = matches.Select(ToBookCandidate).ToList()
            };
        }

        public static List<NzbDrone.Core.Books.Author> FindAuthorProviderMatches(
            IAuthorService authorService,
            IProviderAliasService providerAliasService,
            string provider,
            string providerId,
            Logger logger = null)
        {
            var matches = new Dictionary<int, NzbDrone.Core.Books.Author>();
            var normalizedProviderId = NormalizeProviderId(provider, providerId);

            if (string.IsNullOrWhiteSpace(normalizedProviderId))
            {
                return new List<NzbDrone.Core.Books.Author>();
            }

            var rawId = ProviderIdHelper.StripPrefix(normalizedProviderId);
            var direct = authorService?.FindByProviderId(provider?.Trim().ToLowerInvariant(), rawId);
            if (direct != null && direct.Id > 0)
            {
                matches[direct.Id] = direct;
            }

            if (providerAliasService != null)
            {
                try
                {
                    foreach (var authorId in providerAliasService.FindAuthorIds(new[] { normalizedProviderId }).Distinct())
                    {
                        if (authorId <= 0 || matches.ContainsKey(authorId))
                        {
                            continue;
                        }

                        var author = authorService?.GetAuthor(authorId);
                        if (author != null)
                        {
                            matches[author.Id] = author;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, "Unable to check provider alias ambiguity for author provider id {0}", normalizedProviderId);
                }
            }

            return matches.Values.OrderBy(a => a.Id).ToList();
        }

        public static List<Book> FindBookProviderMatches(IBookService bookService, string provider, string providerId, BookMediaType mediaType)
        {
            var normalizedProviderId = NormalizeProviderId(provider, providerId);
            if (string.IsNullOrWhiteSpace(normalizedProviderId))
            {
                return new List<Book>();
            }

            return (bookService?.FindAllByProviderId(provider?.Trim().ToLowerInvariant(), ProviderIdHelper.StripPrefix(normalizedProviderId), mediaType) ?? new List<Book>())
                .Where(x => x != null)
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .OrderBy(x => x.Id)
                .ToList();
        }

        public static List<(string Provider, string Id)> GetBookProviderLookups(Book book)
        {
            var lookups = new List<(string Provider, string Id)>();

            void Add(string provider, string id)
            {
                if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(id))
                {
                    lookups.Add((provider, id));
                }
            }

            Add("hc", book?.HardcoverBookId);
            Add("gr", book?.GoodreadsWorkId);
            Add("gr", BookEditionIdentity.GetGoodreadsEditionProviderId(book));
            Add("ol", book?.OpenLibraryWorkId);
            Add("gb", BookEditionIdentity.GetGoogleBooksEditionId(book));
            Add("az", BookEditionIdentity.GetAsin(book));
            Add("az", BookEditionIdentity.GetAudibleAsin(book));

            return lookups
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => $"{x.Provider}:{x.Id}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        public static (string Provider, string Id) SplitProviderId(string prefixedId)
        {
            if (string.IsNullOrWhiteSpace(prefixedId))
            {
                return (null, null);
            }

            var trimmed = prefixedId.Trim().Trim('{', '}');
            var idx = trimmed.IndexOf(':');
            if (idx <= 0 || idx == trimmed.Length - 1)
            {
                return (string.Empty, trimmed);
            }

            return (trimmed.Substring(0, idx), trimmed.Substring(idx + 1));
        }

        private static string NormalizeProviderId(string provider, string providerId)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.WithPrefix(provider.Trim().ToLowerInvariant(), providerId.Trim());
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static ProviderAmbiguityCandidateResource ToAuthorCandidate(NzbDrone.Core.Books.Author author)
        {
            var resource = author.ToResource();
            return new ProviderAmbiguityCandidateResource
            {
                Id = author.Id,
                EntityType = "author",
                AuthorName = author.Name,
                TitleSlug = author.TitleSlug,
                ForeignAuthorId = resource.ForeignAuthorId
            };
        }

        private static ProviderAmbiguityCandidateResource ToBookCandidate(Book book)
        {
            var local = book.ToLocalInstanceResource();
            return new ProviderAmbiguityCandidateResource
            {
                Id = local.Id,
                EntityType = "book",
                Title = local.Title,
                TitleSlug = local.TitleSlug,
                MediaType = local.MediaType,
                Narrator = local.Narrator,
                Disambiguation = local.Disambiguation,
                Monitored = local.Monitored,
                HasFiles = local.HasFiles
            };
        }

        private static string FormatOperation(string operation)
        {
            return operation.IsNullOrWhiteSpace() ? string.Empty : $" while {operation}";
        }
    }
}
