using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;

namespace NzbDrone.Api.V1.Editions
{
    [V1ApiController]
    public class EditionController : Controller
    {
        private readonly IWantedEditionCopyFinder _copyFinder;
        private readonly IMediaCoverProxy _mediaCoverProxy;

        public EditionController(IWantedEditionCopyFinder copyFinder, IMediaCoverProxy mediaCoverProxy)
        {
            _copyFinder = copyFinder;
            _mediaCoverProxy = mediaCoverProxy;
        }

        [HttpGet]
        public List<EditionResource> GetEditions([FromQuery] List<int> bookId)
        {
            var lookup = _copyFinder.FindWantedEditionCopies(bookId);
            var editions = lookup.Books.SelectMany(book => book.Editions).ToList();
            var resources = editions.ToResource(HttpContext.GetReadarrFacadeContext());

            foreach (var resource in resources)
            {
                _mediaCoverProxy.ProxyRemoteUrls(resource.Images);
                if (lookup.ExistingCopies.TryGetValue(resource.Id, out var copy))
                {
                    resource.ExistingCopyId = copy.Id;
                    resource.ExistingCopyMonitored = copy.IsMonitored();
                }
            }

            return resources;
        }
    }
}
