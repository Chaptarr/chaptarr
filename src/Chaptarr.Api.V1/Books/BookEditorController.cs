using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
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

        public BookEditorController(IBookService bookService, IManageCommandQueue commandQueueManager)
        {
            _bookService = bookService;
            _commandQueueManager = commandQueueManager;
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

            _bookService.UpdateMany(booksToUpdate);
            return Accepted(booksToUpdate.ToResource());
        }

        [HttpDelete]
        public void DeleteBook([FromBody] BookEditorResource resource)
        {
            foreach (var bookId in resource.BookIds)
            {
                _bookService.DeleteBook(bookId, resource.DeleteFiles ?? false, resource.AddImportListExclusion ?? false);
            }
        }
    }
}
