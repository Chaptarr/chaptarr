using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaCover;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Wanted
{
    [V1ApiController("wanted/missing")]
    public class MissingController : BookControllerWithSignalR
    {
        public MissingController(IBookService bookService,
                             ISeriesBookLinkService seriesBookLinkService,
                             IAuthorStatisticsService authorStatisticsService,
                             IMapCoversToLocal coverMapper,
                             IUpgradableSpecification upgradableSpecification,
                             IBroadcastSignalRMessage signalRBroadcaster)
        : base(bookService, seriesBookLinkService, authorStatisticsService, coverMapper, upgradableSpecification, signalRBroadcaster)
        {
        }

        [HttpGet]
        public PagingResource<BookResource> GetMissingBooks([FromQuery] PagingRequestResource paging, bool includeAuthor = false, bool monitored = true, [FromQuery] string mediaType = null)
        {
            var requestedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
            var pagingResource = new PagingResource<BookResource>(paging);
            var pagingSpec = new PagingSpec<Book>
            {
                Page = pagingResource.Page,
                PageSize = pagingResource.PageSize,
                SortKey = pagingResource.SortKey,
                SortDirection = pagingResource.SortDirection
            };

            // Apply media-type specific monitoring filter
            pagingSpec.FilterExpressions.Add(AuthorExtensions.GetBookMonitoringFilter(requestedMediaType, monitored));

            return pagingSpec.ApplyToPage(_bookService.BooksWithoutFiles, v => MapToResource(v, includeAuthor));
        }
    }
}
