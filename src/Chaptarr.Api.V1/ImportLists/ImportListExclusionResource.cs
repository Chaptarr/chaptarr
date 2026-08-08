using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.REST;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;

namespace Chaptarr.Api.V1.ImportLists
{
    public class ImportListExclusionResource : RestResource
    {
        public string ForeignId { get; set; }
        public string AuthorName { get; set; }
        public string MediaType { get; set; }
    }

    public static class ImportListExclusionResourceMapper
    {
        public static ImportListExclusionResource ToResource(this ImportListExclusion model)
        {
            if (model == null)
            {
                return null;
            }

            return new ImportListExclusionResource
            {
                Id = model.Id,
                ForeignId = model.ForeignId,
                AuthorName = model.Name,
                MediaType = model.MediaType.HasValue ? MediaTypeParameterParser.ToApiValue(model.MediaType.Value) : null,
            };
        }

        public static ImportListExclusion ToModel(this ImportListExclusionResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            return new ImportListExclusion
            {
                Id = resource.Id,
                ForeignId = ImportListProviderIdHelper.Normalize(resource.ForeignId, null),
                Name = resource.AuthorName,
                MediaType = MediaTypeParameterParser.ParseOptional(resource.MediaType, allowAll: true)
            };
        }

        public static List<ImportListExclusionResource> ToResource(this IEnumerable<ImportListExclusion> filters)
        {
            return filters.Select(ToResource).ToList();
        }
    }
}
