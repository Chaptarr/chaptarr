namespace NzbDrone.Core.Extras.Metadata.Files
{
    public class MetadataFileResult
    {
        public string RelativePath { get; set; }
        public string Contents { get; set; }
        public bool OverwriteExisting { get; set; }

        public MetadataFileResult(string relativePath, string contents, bool overwriteExisting = false)
        {
            RelativePath = relativePath;
            Contents = contents;
            OverwriteExisting = overwriteExisting;
        }
    }
}
