using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Blocklist
{
    [V1ApiController]
    public class BlocklistController : Controller
    {
        private readonly IBlocklistService _blocklistService;
        private readonly IAuthorService _authorService;
        private readonly ICustomFormatCalculationService _formatCalculator;

        public BlocklistController(IBlocklistService blocklistService,
                                   IAuthorService authorService,
                                   ICustomFormatCalculationService formatCalculator)
        {
            _blocklistService = blocklistService;
            _authorService = authorService;
            _formatCalculator = formatCalculator;
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

            return pagingSpec.ApplyToPage(_blocklistService.Paged, model =>
            {
                var resource = BlocklistResourceMapper.MapToResource(model);

                var author = ResolveAuthor(model.AuthorProviderIds);
                if (author != null)
                {
                    resource.AuthorId = author.Id;
                    resource.Author = author.ToResource();
                    resource.CustomFormats = _formatCalculator.ParseCustomFormat(model, author).ToResource(includeDetails: false);
                }

                return resource;
            });
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
