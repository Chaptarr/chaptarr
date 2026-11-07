namespace Chaptarr.Api.V1.Bookshelf
{
    public class BookshelfLegacyIdentityResource
    {
        public string Source { get; set; }
        public string EntityType { get; set; }
        public int SourceId { get; set; }
        public int TargetId { get; set; }
        public string MediaType { get; set; }
    }
}
