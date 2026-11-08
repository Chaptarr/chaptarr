using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Author
{
    [V1ApiController("author/deletepreview")]
    public class AuthorDeletePreviewController : Controller
    {
        private readonly IAuthorRepository _authors;
        private readonly IBookRepository _books;
        private readonly IDeleteMediaFiles _deletion;

        public AuthorDeletePreviewController(IAuthorRepository authors, IBookRepository books, IDeleteMediaFiles deletion)
        {
            _authors = authors;
            _books = books;
            _deletion = deletion;
        }

        [HttpPost]
        public AuthorDeletionPreview Preview([FromBody] AuthorEditorResource resource)
        {
            var ids = resource?.AuthorIds?.Distinct().ToList();
            if (ids == null || ids.Count == 0 || ids.Any(id => id <= 0))
                throw new BadRequestException("Select authors to preview their deletion.");
            var authors = _authors.Get(ids).ToList();
            if (authors.Count != ids.Count) throw new BadRequestException("A selected author no longer exists. Reload the author list.");
            var scope = MediaTypeParameterParser.ParseDeletionScope(resource.ApplyToBothFormats,
                HttpContext.GetReadarrFacadeContext()?.MediaType, resource.MediaType);
            AuthorDeletionScope.Validate(authors, author => _books.GetBooksByAuthorId(author.Id), scope, resource.ApplyToBothFormats);
            return _deletion.PreviewAuthorFiles(authors, scope);
        }
    }
}
