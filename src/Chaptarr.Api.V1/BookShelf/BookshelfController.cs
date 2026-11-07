using System;
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
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public BookshelfController(
            IAuthorService authorService,
            IBookMonitoredService bookMonitoredService,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
        {
            _authorService = authorService;
            _bookMonitoredService = bookMonitoredService;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        [HttpGet("legacy-identities")]
        public IActionResult GetLegacyIdentities([FromQuery] string entityType, [FromQuery] List<int> sourceIds)
        {
            var allowedEntityTypes = new HashSet<string>(new[] { "Author", "AuthorMetadata", "Book", "Edition", "BookFile" }, StringComparer.OrdinalIgnoreCase);
            if (!allowedEntityTypes.Contains(entityType ?? string.Empty))
            {
                return BadRequest("entityType must be Author, AuthorMetadata, Book, Edition, or BookFile.");
            }

            sourceIds = sourceIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (sourceIds.Count == 0 || sourceIds.Count > 500)
            {
                return BadRequest("Pass between 1 and 500 positive sourceIds.");
            }

            var mappings = _legacyIdentityMapService?.GetMappings(entityType, sourceIds) ?? new List<BookshelfLegacyIdentityMapEntry>();
            return Ok(mappings.Select(mapping => new BookshelfLegacyIdentityResource
            {
                Source = mapping.Source,
                EntityType = mapping.EntityType,
                SourceId = mapping.SourceId,
                TargetId = mapping.TargetId,
                MediaType = mapping.MediaType.HasValue
                    ? (mapping.MediaType.Value == (int)BookMediaType.Ebook ? "ebook" : "audiobook")
                    : null
            }).ToList());
        }

        [HttpPost] // nosemgrep: csharp.dotnet.security.mvc-missing-antiforgery.mvc-missing-antiforgery -- API fallback auth accepts API keys or bearer tokens, never browser cookies.
        [Consumes("application/json")]
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

            if (monitoringOptions.Monitor == MonitorTypes.SpecificBook && _legacyIdentityMapService != null)
            {
                var numericBookIds = monitoringOptions.BooksToMonitor
                    .Select(id => int.TryParse(id, out var numericId) ? numericId : 0)
                    .Where(id => id > 0)
                    .ToList();
                var otherBookIds = monitoringOptions.BooksToMonitor
                    .Where(id => !int.TryParse(id, out var numericId) || numericId <= 0)
                    .ToList();
                monitoringOptions.BooksToMonitor = _legacyIdentityMapService
                    .ResolveBookIds(numericBookIds, monitoringOptions.MediaType)
                    .Select(id => id.ToString(global::System.Globalization.CultureInfo.InvariantCulture))
                    .Concat(otherBookIds)
                    .Distinct()
                    .ToList();
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
