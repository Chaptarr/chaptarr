using System.Linq;
using System.Collections.Generic;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("book/editor")]
    public class BookEditorController : Controller
    {
        private readonly IBookService _bookService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public BookEditorController(
            IBookService bookService,
            IManageCommandQueue commandQueueManager,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
        {
            _bookService = bookService;
            _commandQueueManager = commandQueueManager;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        [HttpPut] // nosemgrep: csharp.dotnet.security.mvc-missing-antiforgery.mvc-missing-antiforgery -- API fallback auth accepts API keys or bearer tokens, never browser cookies.
        [Consumes("application/json")]
        public IActionResult SaveAll([FromBody] BookEditorResource resource)
        {
            // Validate mediaType up front: absent = legacy (per-book), audiobook/ebook = scoped,
            // anything else (including "all") returns 400 instead of silently crossing the
            // audiobook/ebook boundary.
            var effectiveMediaType = resource.MediaType ?? HttpContext?.GetReadarrFacadeContext()?.MediaType;
            var mediaType = MediaTypeParameterParser.ParseOptional(effectiveMediaType, allowAll: false);

            var requestedBookIds = resource.BookIds ?? new List<int>();
            var bookIds = HttpContext.ResolveBookshelfLegacyBookIds(_legacyIdentityMapService, requestedBookIds, mediaType);
            var booksToUpdate = _bookService.GetBooks(bookIds);

            foreach (var book in booksToUpdate)
            {
                if (!resource.Monitored.HasValue)
                {
                    continue;
                }

                switch (mediaType ?? book.MediaType)
                {
                    case BookMediaType.Audiobook:
                        book.AudiobookMonitored = resource.Monitored.Value;
                        break;
                    case BookMediaType.Ebook:
                        book.EbookMonitored = resource.Monitored.Value;
                        break;
                }
            }

            _bookService.UpdateMany(booksToUpdate);
            var facadeContext = HttpContext?.GetReadarrFacadeContext();
            var resources = booksToUpdate
                .Select(book => book.ToResource(new BookResourceMappingOptions { FacadeContext = facadeContext }))
                .ToList();
            BookResourceMapper.WarnFacadeIdentityGaps(resources, facadeContext, "book editor response");
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true && _legacyIdentityMapService != null)
            {
                var mappingsByTargetId = _legacyIdentityMapService.GetMappingsByTargetIds("Book", booksToUpdate.Select(book => book.Id))
                    .GroupBy(mapping => mapping.TargetId)
                    .ToDictionary(group => group.Key, group => group.First());
                var legacyMetadataIdByAuthorId = _legacyIdentityMapService.GetMappingsByTargetIds(
                        "AuthorMetadata",
                        resources.Where(resource => resource != null).Select(resource => resource.AuthorId))
                    .GroupBy(mapping => mapping.TargetId)
                    .Where(group => group.Select(mapping => mapping.SourceId).Distinct().Count() == 1)
                    .ToDictionary(group => group.Key, group => group.First().SourceId);
                resources = resources
                    .Where(resource => resource != null)
                    .Select(resource =>
                    {
                        if (mappingsByTargetId.TryGetValue(resource.Id, out var mapping))
                        {
                            resource.Id = mapping.SourceId;
                            resource.LocalBookId = mapping.SourceId.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                            resource.MediaType = null;
                            resource.AudiobookMonitored = null;
                            resource.EbookMonitored = null;
                            resource.LocalAudiobookBooks = null;
                            resource.LocalEbookBooks = null;
                        }

                        if (legacyMetadataIdByAuthorId.TryGetValue(resource.AuthorId, out var legacyMetadataId))
                        {
                            resource.AuthorMetadataId = legacyMetadataId;
                            if (resource.Author != null)
                            {
                                resource.Author.AuthorMetadataId = legacyMetadataId;
                            }
                        }

                        return resource;
                    })
                    .GroupBy(resource => resource.Id)
                    .Select(group =>
                    {
                        var resource = group.First();
                        resource.Monitored = group.Any(item => item.Monitored);
                        resource.HasFiles = group.Any(item => item.HasFiles);
                        return resource;
                    })
                    .ToList();
            }

            return Accepted(resources);
        }

        [HttpDelete]
        [Consumes("application/json")]
        public void DeleteBook([FromBody] BookEditorResource resource)
        {
            var effectiveMediaType = resource.MediaType ?? HttpContext?.GetReadarrFacadeContext()?.MediaType;
            var requestedBookIds = resource.BookIds ?? new List<int>();
            var bookIds = HttpContext.ResolveBookshelfLegacyBookIds(
                _legacyIdentityMapService,
                requestedBookIds,
                MediaTypeParameterParser.ParseOptional(effectiveMediaType));

            foreach (var bookId in bookIds)
            {
                _bookService.DeleteBook(bookId, resource.DeleteFiles ?? false, resource.AddImportListExclusion ?? false);
            }
        }
    }
}
