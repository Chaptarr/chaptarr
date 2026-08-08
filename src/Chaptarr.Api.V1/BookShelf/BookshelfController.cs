using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Bookshelf
{
    [V1ApiController]
    public class BookshelfController : Controller
    {
        private readonly IAuthorService _authorService;
        private readonly IBookMonitoredService _bookMonitoredService;

        public BookshelfController(IAuthorService authorService, IBookMonitoredService bookMonitoredService)
        {
            _authorService = authorService;
            _bookMonitoredService = bookMonitoredService;
        }

        [HttpPost]
        public IActionResult UpdateAll([FromBody] BookshelfResource request)
        {
            //Read from request
            var authorToUpdate = _authorService.GetAuthors(request.Authors.Select(s => s.Id));

            foreach (var s in request.Authors)
            {
                var author = authorToUpdate.Single(c => c.Id == s.Id);

                if (s.Monitored.HasValue)
                {
                    author.Monitored = s.Monitored.Value;
                }

                // Legacy behavior: when applying a global "None" monitor option, also unmonitor the author.
                // When a specific media type is provided, only apply book-level changes for that type.
                if (request.MonitoringOptions != null &&
                    request.MonitoringOptions.Monitor == MonitorTypes.None &&
                    request.MonitoringOptions.MediaType == null)
                {
                    author.Monitored = false;
                }

                // TODO: Update for new boolean monitoring system
                // MonitorNewItems is being replaced with per-media-type boolean monitoring
                // if (request.MonitorNewItems.HasValue)
                // {
                //     author.MonitorNewItems = request.MonitorNewItems.Value;
                // }

                _bookMonitoredService.SetBookMonitoredStatus(author, request.MonitoringOptions);
            }

            return Accepted(request);
        }
    }
}
