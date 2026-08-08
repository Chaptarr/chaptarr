using System.Collections.Generic;

namespace Chaptarr.Api.V1.Author
{
    public class AggregateStatisticsRequest
    {
        public List<int> AuthorIds { get; set; }
        public string MediaType { get; set; }
    }
}