using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Organizer;

namespace Chaptarr.Api.V1.Author
{
    [V1ApiController("author/lookup")]
    public class AuthorLookupController : Controller
    {
        private readonly ISearchForNewAuthor _searchProxy;
        private readonly IBuildFileNames _fileNameBuilder;
        private readonly IMapCoversToLocal _coverMapper;

        public AuthorLookupController(ISearchForNewAuthor searchProxy, IBuildFileNames fileNameBuilder, IMapCoversToLocal coverMapper)
        {
            _searchProxy = searchProxy;
            _fileNameBuilder = fileNameBuilder;
            _coverMapper = coverMapper;
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<AuthorResource>), 200)]
        public ActionResult<List<AuthorResource>> Search([FromQuery] string term)
        {
            var searchResults = _searchProxy.SearchForNewAuthor(term);
            var facadeContext = HttpContext.GetReadarrFacadeContext();
            var resources = MapToResource(searchResults, facadeContext).ToList();
            AuthorResourceMapper.WarnFacadeIdentityGaps(resources, facadeContext, "author lookup response");
            return resources;
        }

        private IEnumerable<AuthorResource> MapToResource(IEnumerable<NzbDrone.Core.Books.Author> author, ReadarrFacadeContext facadeContext)
        {
            foreach (var currentAuthor in author)
            {
                var resource = currentAuthor.ToResource(facadeContext);

                _coverMapper.ConvertToLocalUrls(resource.Id, MediaCoverEntity.Author, resource.Images, resource.SelectedPosterHash);

                var poster = currentAuthor.Images.FirstOrDefault(c => c.CoverType == MediaCoverTypes.Poster);

                if (poster != null)
                {
                    resource.RemotePoster = poster.Url;
                }

                resource.Folder = _fileNameBuilder.GetAuthorFolder(currentAuthor);

                yield return resource;
            }
        }
    }
}
