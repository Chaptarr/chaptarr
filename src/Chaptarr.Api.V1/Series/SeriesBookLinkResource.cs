using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;
using Swashbuckle.AspNetCore.Annotations;

namespace Chaptarr.Api.V1.Series
{
    public class SeriesBookLinkResource : RestResource
    {
        public string Position { get; set; }
        [SwaggerSchema("Presentation order derived from the original series position.", ReadOnly = true)]
        public decimal PositionOrder { get; set; }
        public int SeriesPosition { get; set; }
        public int SeriesId { get; set; }
        public int BookId { get; set; }
    }

    public static class SeriesBookLinkResourceMapper
    {
        public static SeriesBookLinkResource ToResource(this SeriesBookLink model)
        {
            return new SeriesBookLinkResource
            {
                Id = model.Id,
                Position = model.Position,
                PositionOrder = BookSeries.PositionOrder(model.Position),
                SeriesPosition = model.SeriesPosition,
                SeriesId = model.SeriesId,
                BookId = model.BookId
            };
        }

        public static List<SeriesBookLinkResource> ToResource(this IEnumerable<SeriesBookLink> models)
        {
            return models?.Select(ToResource).ToList();
        }
    }
}
