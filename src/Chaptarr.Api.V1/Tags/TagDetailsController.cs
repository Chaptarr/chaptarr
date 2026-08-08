using System.Collections.Generic;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Tags;

namespace Chaptarr.Api.V1.Tags
{
    [V1ApiController("tag/detail")]
    public class TagDetailsController : RestController<TagDetailsResource>
    {
        private readonly ITagService _tagService;

        public TagDetailsController(ITagService tagService)
        {
            _tagService = tagService;
        }

        protected override TagDetailsResource GetResourceById(int id)
        {
            return _tagService.Details(id).ToResource();
        }

        [HttpGet]
        public List<TagDetailsResource> GetAll()
        {
            var tags = _tagService.Details().ToResource();

            return tags;
        }
    }
}
