using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.MediaFiles
{
    public static class TrackedFileDeletionCheck
    {
        // A loose read match is useful for playback, but never authorizes a destructive write.
        // Preview and execution use the same check, without changing the stored path.
        public static string GetRetentionReason(IDiskProvider disk, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "The stored file path is empty.";
            try
            {
                if (disk.FileExistsCanonical(path)) return null;
                if (disk.FileExists(path)) return "Only a near-identical file name could be found. Rename or remove it manually.";

                // File.Exists can return false for access failures. A successful directory read
                // distinguishes an absent file from a directory that cannot be inspected.
                disk.GetFiles(Path.GetDirectoryName(path), false).Any();
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return "The file could not be accessed: " + error.Message;
            }
        }
    }
}
