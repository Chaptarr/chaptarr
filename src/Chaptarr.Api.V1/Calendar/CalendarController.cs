using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaCover;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Calendar
{
    [V1ApiController]
    public class CalendarController : BookControllerWithSignalR
    {
        public CalendarController(IBookService bookService,
                              ISeriesBookLinkService seriesBookLinkService,
                              IAuthorStatisticsService authorStatisticsService,
                              IMapCoversToLocal coverMapper,
                              IUpgradableSpecification upgradableSpecification,
                              IBroadcastSignalRMessage signalRBroadcaster)
        : base(bookService, seriesBookLinkService, authorStatisticsService, coverMapper, upgradableSpecification, signalRBroadcaster)
        {
        }

        [HttpGet]
        public List<BookResource> GetCalendar(DateTime? start, DateTime? end, bool unmonitored = false, bool includeAuthor = false)
        {
            //TODO: Add Book Image support to BookControllerWithSignalR
            var includeBookImages = Request.GetBooleanQueryParameter("includeBookImages");

            var startUse = start ?? DateTime.Today;
            var endUse = end ?? DateTime.Today.AddDays(2);

            var resources = MapToResource(_bookService.BooksBetweenDates(startUse, endUse, unmonitored), includeAuthor);

            return resources.OrderBy(e => e.ReleaseDate).ToList();
        }
    }
}
