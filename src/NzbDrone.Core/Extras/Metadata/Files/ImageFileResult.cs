namespace NzbDrone.Core.Extras.Metadata.Files
{
    public class ImageFileResult
    {
        public string RelativePath { get; set; }
        public string Url { get; set; }
        public bool OverwriteExisting { get; set; }

        public ImageFileResult(string relativePath, string url, bool overwriteExisting = false)
        {
            RelativePath = relativePath;
            Url = url;
            OverwriteExisting = overwriteExisting;
        }
    }
}
