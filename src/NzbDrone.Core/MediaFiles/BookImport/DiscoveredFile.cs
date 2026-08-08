namespace NzbDrone.Core.MediaFiles.BookImport
{
    public class DiscoveredFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public System.DateTime Modified { get; set; }
    }
}

