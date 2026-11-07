using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.MediaTypes
{
    public static class BookshelfLegacyIdentityMapExtensions
    {
        public static List<int> ResolveBookshelfLegacyBookIds(
            this HttpContext context,
            IBookshelfLegacyIdentityMapService identityMapService,
            IEnumerable<int> bookIds,
            BookMediaType? mediaType = null)
        {
            var ids = bookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (context?.GetReadarrFacadeContext()?.AcceptsBookshelfLegacyBookIds != true || identityMapService == null)
            {
                return ids;
            }

            return identityMapService.ResolveBookIds(ids, mediaType);
        }
    }
}
