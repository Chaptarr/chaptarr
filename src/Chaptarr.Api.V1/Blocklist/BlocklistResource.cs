using System;
using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http.REST;
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
    }
}
