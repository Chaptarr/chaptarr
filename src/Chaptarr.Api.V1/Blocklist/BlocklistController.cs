using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http;
using Chaptarr.Http.Extensions;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Blocklist
{
    [V1ApiController]
    public class BlocklistController : Controller
    {
        private readonly IBlocklistService _blocklistService;
        private readonly IAuthorService _authorService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IBookService _bookService;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public BlocklistController(IBlocklistService blocklistService,
                                   IAuthorService authorService,
                                   ICustomFormatCalculationService formatCalculator,
                                   IBookService bookService,
                                   IBookshelfLegacyIdentityMapService legacyIdentityMapService)
        {
            _blocklistService = blocklistService;
            _authorService = authorService;
            _formatCalculator = formatCalculator;
            _bookService = bookService;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        [HttpGet]
        [Produces("application/json")]
        public PagingResource<BlocklistResource> GetBlocklist([FromQuery] PagingRequestResource paging)
        {
            var pagingResource = new PagingResource<BlocklistResource>(paging);
            var pagingSpec = pagingResource.MapToPagingSpec<BlocklistResource, NzbDrone.Core.Blocklisting.Blocklist>("date", SortDirection.Descending);

            Dictionary<string, NzbDrone.Core.Books.Author> authorsByProviderId = null;

            NzbDrone.Core.Books.Author ResolveAuthor(List<string> providerIds)
            {
                if (providerIds == null || providerIds.Count == 0)
                {
                    return null;
                }

                authorsByProviderId ??= BuildAuthorByProviderIdMap();

                foreach (var providerId in providerIds)
                {
                    if (string.IsNullOrWhiteSpace(providerId))
                    {
                        continue;
                    }

                    if (authorsByProviderId.TryGetValue(providerId.Trim(), out var author))
                    {
                        return author;
                    }
                }

                return null;
            }

            var page = _blocklistService.Paged(pagingSpec);
            var models = page.Records ?? new List<NzbDrone.Core.Blocklisting.Blocklist>();
            var facadeContext = HttpContext?.GetReadarrFacadeContext();
            var bookshelfCompatibility = facadeContext?.IsBookshelfCompatibility == true;

            var authors = models
                .Select(model => ResolveAuthor(model.AuthorProviderIds))
                .Where(author => author != null)
                .DistinctBy(author => author.Id)
                .ToList();
            var authorIds = authors.Select(author => author.Id).ToList();
            var legacyAuthorIdByTargetId = bookshelfCompatibility
                ? GetUniqueSourceIds(_legacyIdentityMapService?.GetMappingsByTargetIds("Author", authorIds))
                : new Dictionary<int, int>();
            var legacyMetadataIdByTargetId = bookshelfCompatibility
                ? GetUniqueSourceIds(_legacyIdentityMapService?.GetMappingsByTargetIds("AuthorMetadata", authorIds))
                : new Dictionary<int, int>();

            var targetBookIdsByProviderId = bookshelfCompatibility
                ? BuildTargetBookIdsByProviderId(models.SelectMany(model => model.BookProviderIds ?? Enumerable.Empty<string>()))
                : new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var allTargetBookIds = targetBookIdsByProviderId.Values.SelectMany(ids => ids).Distinct().ToList();
            var legacyBookIdsByTargetId = bookshelfCompatibility
                ? (_legacyIdentityMapService?.GetMappingsByTargetIds("Book", allTargetBookIds) ?? new List<BookshelfLegacyIdentityMapEntry>())
                    .GroupBy(mapping => mapping.TargetId)
                    .ToDictionary(group => group.Key, group => group.Select(mapping => mapping.SourceId).Distinct().OrderBy(id => id).ToList())
                : new Dictionary<int, List<int>>();

            var records = models.Select(model =>
            {
                var author = ResolveAuthor(model.AuthorProviderIds);
                var authorId = author != null && legacyAuthorIdByTargetId.TryGetValue(author.Id, out var legacyAuthorId)
                    ? (int?)legacyAuthorId
                    : null;
                var metadataId = author != null && legacyMetadataIdByTargetId.TryGetValue(author.Id, out var legacyMetadataId)
                    ? (int?)legacyMetadataId
                    : null;
                var legacyBookIds = GetLegacyBookIds(model.BookProviderIds, targetBookIdsByProviderId, legacyBookIdsByTargetId);
                var resource = BlocklistResourceMapper.MapToResource(
                    model,
                    author,
                    facadeContext,
                    authorId,
                    metadataId,
                    legacyBookIds);

                if (author != null)
                {
                    resource.CustomFormats = _formatCalculator.ParseCustomFormat(model, author).ToResource(includeDetails: false);
                }

                return resource;
            }).ToList();

            return new PagingResource<BlocklistResource>
            {
                Page = page.Page,
                PageSize = page.PageSize,
                SortKey = page.SortKey,
                SortDirection = page.SortDirection,
                TotalRecords = page.TotalRecords,
                Records = records
            };
        }

        private Dictionary<string, List<int>> BuildTargetBookIdsByProviderId(IEnumerable<string> providerIds)
        {
            var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            if (_bookService == null)
            {
                return result;
            }

            var distinctProviderIds = providerIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>();
            foreach (var providerId in distinctProviderIds)
            {
                string normalized;
                try
                {
                    normalized = ProviderIdHelper.Normalize(providerId, defaultPrefix: null);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                var separator = normalized.IndexOf(':');
                var provider = normalized.Substring(0, separator);
                var targetIds = new List<int>();
                foreach (var mediaType in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    targetIds.AddRange((_bookService.FindAllByProviderId(provider, normalized, mediaType) ?? new List<Book>())
                        .Where(book => book != null)
                        .Select(book => book.Id));
                }

                result[normalized] = targetIds.Distinct().OrderBy(id => id).ToList();
            }

            return result;
        }

        private static List<int> GetLegacyBookIds(
            IEnumerable<string> providerIds,
            IReadOnlyDictionary<string, List<int>> targetBookIdsByProviderId,
            IReadOnlyDictionary<int, List<int>> legacyBookIdsByTargetId)
        {
            var result = new List<int>();
            foreach (var providerId in providerIds ?? Enumerable.Empty<string>())
            {
                string normalized;
                try
                {
                    normalized = ProviderIdHelper.Normalize(providerId, defaultPrefix: null);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(normalized) || !targetBookIdsByProviderId.TryGetValue(normalized, out var targetIds))
                {
                    continue;
                }

                foreach (var targetId in targetIds)
                {
                    if (legacyBookIdsByTargetId.TryGetValue(targetId, out var sourceIds))
                    {
                        result.AddRange(sourceIds);
                    }
                }
            }

            return result.Distinct().ToList();
        }

        private static Dictionary<int, int> GetUniqueSourceIds(IEnumerable<BookshelfLegacyIdentityMapEntry> mappings)
        {
            return (mappings ?? Enumerable.Empty<BookshelfLegacyIdentityMapEntry>())
                .GroupBy(mapping => mapping.TargetId)
                .Where(group => group.Select(mapping => mapping.SourceId).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().SourceId);
        }

        private Dictionary<string, NzbDrone.Core.Books.Author> BuildAuthorByProviderIdMap()
        {
            var map = new Dictionary<string, NzbDrone.Core.Books.Author>(StringComparer.OrdinalIgnoreCase);
            var authors = _authorService.GetAllAuthors() ?? new List<NzbDrone.Core.Books.Author>();

            foreach (var author in authors)
            {
                Add(author?.GoodreadsAuthorId);
                Add(author?.HardcoverAuthorId);
                Add(author?.OpenLibraryAuthorId);
                Add(author?.AudnexusAuthorId);
                Add(author?.GoogleBooksAuthorId);
                foreach (var providerId in author?.RemoteProviderIds ?? Enumerable.Empty<string>())
                {
                    Add(providerId);
                }

                void Add(string providerId)
                {
                    if (string.IsNullOrWhiteSpace(providerId))
                    {
                        return;
                    }

                    var key = providerId.Trim();
                    if (!map.ContainsKey(key))
                    {
                        map[key] = author;
                    }
                }
            }

            return map;
        }

        [RestDeleteById]
        public void DeleteBlocklist(int id)
        {
            _blocklistService.Delete(id);
        }

        [HttpDelete("bulk")]
        public object Remove([FromBody] BlocklistBulkResource resource)
        {
            _blocklistService.Delete(resource.Ids);

            return new { };
        }
    }
}
