using Chaptarr.Http.REST;

namespace Chaptarr.Api.V1.Books
{
    public class BookImportResource : RestResource
    {
        public string ForeignBookId { get; set; }
        public string ForeignAuthorId { get; set; }
        public string ForeignEditionId { get; set; }
        public string MediaType { get; set; }
        public string RootFolder { get; set; }
        public int QualityProfileId { get; set; }
        public int MetadataProfileId { get; set; }
        public BookImportAuthorMonitoring AuthorMonitoring { get; set; }
    }

    public class BookImportAuthorMonitoring
    {
        public string MonitorExisting { get; set; }
        public bool MonitorFuture { get; set; }
        public bool SearchForMissing { get; set; }
    }
}

