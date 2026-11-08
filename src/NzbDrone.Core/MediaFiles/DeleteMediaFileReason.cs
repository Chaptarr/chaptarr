namespace NzbDrone.Core.MediaFiles
{
    public enum DeleteMediaFileReason
    {
        MissingFromDisk,
        Manual,
        Upgrade,
        NoLinkedEpisodes,
        ManualOverride
    }

    public static class DeleteMediaFileReasonExtensions
    {
        // Losing tracking or discovering a missing primary never authorizes deleting other files.
        public static bool RemovesFiles(this DeleteMediaFileReason reason) =>
            reason == DeleteMediaFileReason.Manual || reason == DeleteMediaFileReason.ManualOverride ||
            reason == DeleteMediaFileReason.Upgrade;
    }
}
