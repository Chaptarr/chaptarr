namespace NzbDrone.Core.Download
{
    public static class DownloadIdentity
    {
        // Persist the same Unicode key as download history; database UPPER is not equivalent.
        public static string Key(string downloadId) => string.IsNullOrWhiteSpace(downloadId) ? null : downloadId.ToUpperInvariant();
    }
}
