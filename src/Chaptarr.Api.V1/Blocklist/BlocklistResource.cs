using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.Blocklist
{
    public class BlocklistResource : RestResource
    {
        // Provider-prefixed IDs for multi-provider support
        public List<string> AuthorProviderIds { get; set; }
        public List<string> BookProviderIds { get; set; }
        
        public string SourceTitle { get; set; }
        public QualityModel Quality { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public DateTime Date { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public string Indexer { get; set; }
        public string Message { get; set; }

        // Legacy fields for API compatibility - will be empty
        public int? AuthorId { get; set; }
        public List<int> BookIds { get; set; }
        public AuthorResource Author { get; set; }
    }

    public static class BlocklistResourceMapper
    {
        public static BlocklistResource MapToResource(this NzbDrone.Core.Blocklisting.Blocklist model)
        {
            if (model == null)
            {
                return null;
            }

            return new BlocklistResource
            {
                Id = model.Id,

                // New provider ID fields
                AuthorProviderIds = model.AuthorProviderIds,
                BookProviderIds = model.BookProviderIds,
                
                // Legacy fields - left null/empty for backward compatibility
                AuthorId = null,
                BookIds = new List<int>(),
                Author = null,
                
                SourceTitle = model.SourceTitle,
                Quality = model.Quality,
                CustomFormats = new List<CustomFormatResource>(),
                Date = model.Date,
                Protocol = model.Protocol,
                Indexer = model.Indexer,
                Message = model.Message
            };
        }

        public static BlocklistResource MapToResource(
            NzbDrone.Core.Blocklisting.Blocklist model,
            NzbDrone.Core.Books.Author author,
            ReadarrFacadeContext facadeContext,
            int? legacyAuthorId,
            int? legacyAuthorMetadataId,
            IEnumerable<int> legacyBookIds)
        {
            var resource = MapToResource(model);
            if (resource == null)
            {
                return null;
            }

            if (author != null)
            {
                resource.AuthorId = facadeContext?.IsBookshelfCompatibility == true
                    ? legacyAuthorId ?? author.Id
                    : author.Id;
                resource.Author = AuthorResourceMapper.ToResource(author, facadeContext);
                if (facadeContext?.IsBookshelfCompatibility == true && legacyAuthorMetadataId.HasValue)
                {
                    resource.Author.AuthorMetadataId = legacyAuthorMetadataId.Value;
                }
            }

            if (facadeContext?.IsBookshelfCompatibility == true)
            {
                resource.BookIds = (legacyBookIds ?? Enumerable.Empty<int>())
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
            }

            return resource;
        }
    }
}
