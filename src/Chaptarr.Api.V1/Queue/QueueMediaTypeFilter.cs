using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.Queue
{
    internal static class QueueMediaTypeFilter
    {
        public static IEnumerable<NzbDrone.Core.Queue.Queue> FilterByMediaType(IEnumerable<NzbDrone.Core.Queue.Queue> queue, string mediaType)
        {
            var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
            if (!parsedMediaType.HasValue)
            {
                return queue ?? Enumerable.Empty<NzbDrone.Core.Queue.Queue>();
            }

            return (queue ?? Enumerable.Empty<NzbDrone.Core.Queue.Queue>())
                .Where(q =>
                {
                    var itemMediaType = q?.Book?.MediaType ?? QualityMediaTypeHelper.GetKnownMediaType(q?.Quality?.Quality);
                    return !itemMediaType.HasValue || itemMediaType.Value == parsedMediaType.Value;
                });
        }
    }
}
