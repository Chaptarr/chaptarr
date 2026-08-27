using System.Collections.Generic;
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
            var requestedAuthors = request.Authors ?? new List<BookshelfAuthorResource>();

            if (request.MonitorNewItems.HasValue || requestedAuthors.Any(author => author.Monitored.HasValue))
            {
                return BadRequest("Bookshelf only changes book monitoring. Use Author Editor to change author or future-book monitoring.");
            }

            if (request.MonitoringOptions?.Monitor == MonitorTypes.SpecificBook &&
                (request.MonitoringOptions.BooksToMonitor == null || request.MonitoringOptions.BooksToMonitor.Count == 0))
            {
                return BadRequest("Specific-book monitoring requires at least one book ID.");
            }

            var monitoringOptions = request.MonitoringOptions?.ToModel();
            if (monitoringOptions == null || requestedAuthors.Count == 0)
            {
                return Accepted(request);
            }

            var authorToUpdate = _authorService.GetAuthors(requestedAuthors.Select(author => author.Id));

            foreach (var requestedAuthor in requestedAuthors)
            {
                var author = authorToUpdate.Single(candidate => candidate.Id == requestedAuthor.Id);
                _bookMonitoredService.SetBookMonitoredStatus(author, monitoringOptions);
            }

            return Accepted(request);
        }
    }
}
