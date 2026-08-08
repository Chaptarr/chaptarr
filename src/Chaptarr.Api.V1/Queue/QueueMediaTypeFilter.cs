using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;

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
                .Where(q => q?.Book == null || q.Book.MediaType == parsedMediaType.Value);
        }
    }
}
