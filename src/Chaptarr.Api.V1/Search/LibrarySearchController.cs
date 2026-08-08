using Chaptarr.Http;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Api.V1.Search
{
    [V1ApiController("library/search")]
    public class LibrarySearchController : Controller
    {
        private readonly ILibrarySearchService _librarySearchService;
        private readonly IMapCoversToLocal _coverMapper;

        public LibrarySearchController(ILibrarySearchService librarySearchService, IMapCoversToLocal coverMapper)
        {
            _librarySearchService = librarySearchService;
            _coverMapper = coverMapper;
        }

        [HttpGet]
        public LibrarySearchResult Search([FromQuery] string term, [FromQuery] int limit = 10)
        {
            var results = _librarySearchService.Search(term, limit);

            foreach (var author in results.Authors)
            {
                author.Images ??= new List<MediaCover>();
                _coverMapper.ConvertToLocalUrls(author.Id, MediaCoverEntity.Author, author.Images, author.SelectedPosterHash);
            }

            foreach (var book in results.Books)
            {
                book.Images ??= new List<MediaCover>();
                _coverMapper.ConvertToLocalUrls(book.Id, MediaCoverEntity.Book, book.Images);
            }

            return results;
        }
    }
}
