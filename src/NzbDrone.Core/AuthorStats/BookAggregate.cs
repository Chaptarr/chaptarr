namespace NzbDrone.Core.AuthorStats
{
    public class BookAggregate
    {
        public int BookCount { get; set; }
        public int BookFileCount { get; set; }
        public long SizeOnDisk { get; set; }
    }
}