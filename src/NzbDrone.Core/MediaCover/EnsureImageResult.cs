namespace NzbDrone.Core.MediaCover
{
    public class EnsureImageResult
    {
        public string State { get; set; } // "downloaded", "pending", "error"
        public string Path { get; set; }
        public string ErrorCode { get; set; }
    }
}