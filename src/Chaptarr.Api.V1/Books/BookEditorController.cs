using System.Collections.Generic;
using System.Linq;
using System.Net;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NzbDrone.Core.Exceptions;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("book/editor")]
    public class BookEditorController : Controller
    {
        private readonly IBookService _bookService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IMapCoversToLocal _coverMapper;

        public BookEditorController(IBookService bookService, IManageCommandQueue commandQueueManager, IMapCoversToLocal coverMapper)
        {
            _bookService = bookService;
            _commandQueueManager = commandQueueManager;
            _coverMapper = coverMapper;
        }

        [HttpPut]
        public IActionResult SaveAll([FromBody] BookEditorResource resource)
        {
            // Validate mediaType up front: absent = legacy (per-book), audiobook/ebook = scoped,
            // anything else (including "all") returns 400 instead of silently crossing the
            // audiobook/ebook boundary.
            var mediaType = MediaTypeParameterParser.ParseOptional(resource.MediaType, allowAll: false);

            var booksToUpdate = _bookService.GetBooks(resource.BookIds);

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

            _bookService.UpdateManyWithLifecycle(booksToUpdate);

            var resources = booksToUpdate.ToResource();
            _coverMapper.ConvertBookCoversToLocalUrls(resources.Select(book =>
                new KeyValuePair<int, List<MediaCover>>(book.Id, book.Images)));

            return Accepted(resources);
        }

        [HttpDelete]
        [ProducesResponseType(200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ApiErrorResource), 409)]
        public object DeleteBook([FromBody] BookEditorResource resource)
        {
            var scope = MediaTypeParameterParser.ParseDeletionScope(false, HttpContext.GetReadarrFacadeContext()?.MediaType, resource.MediaType);
            if (scope.HasValue && _bookService.GetBooks(resource.BookIds).Any(book => book.MediaType != scope.Value))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict,
                    "The selection contains books of a different media type than the requested scope. Nothing was changed.");
            }

            foreach (var bookId in resource.BookIds)
            {
                _bookService.DeleteBook(bookId, resource.DeleteFiles ?? false, resource.AddImportListExclusion ?? false);
            }

            return new { };
        }
    }
}
