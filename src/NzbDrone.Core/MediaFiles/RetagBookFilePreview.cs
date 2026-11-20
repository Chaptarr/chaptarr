using System;
using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles
{
    public enum RetagSkipReason
    {
        NotInCalibre,
        MissingCalibreBook,
        LibraryUnavailable,
        MissingRootFolder,
        InvalidCalibreSettings,
        LibraryPreviewFailed,
        PreviewFailed,
        RootNotLinkedToCalibre
    }

    public class RetagBookFilePreview
    {
        public int AuthorId { get; set; }
        public int BookId { get; set; }
        public List<int> TrackNumbers { get; set; } = new List<int>();
        public int BookFileId { get; set; }
        public string Title { get; set; }
        public string Path { get; set; }
        public bool IsFileSymlink { get; set; }
        public bool IsCalibreFormat { get; set; }
        public RetagSkipReason? SkipReason { get; set; }
        public string RootFolderPath { get; set; }
        public string CalibreLibrary { get; set; }
        public DateTime? RetryAfter { get; set; }
        public bool RetryNow { get; set; }
        public Dictionary<string, Tuple<string, string>> Changes { get; set; } = new();
    }
}
