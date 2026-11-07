using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Books
{
    public class AuthorMediaMoveFile
    {
        public string FileType { get; set; }
        public int BookFileId { get; set; }
        public int ExtraFileId { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public bool SourceExists { get; set; }
        public bool DestinationExists { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string Status { get; set; }
    }

    public class AuthorMediaMovePreview
    {
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Format { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string PreviewToken { get; set; }
        public long TotalSize { get; set; }
        public long RequiredCopyBytes { get; set; }
        public long? AvailableSpace { get; set; }
        public int MediaFileCount { get; set; }
        public int SidecarFileCount { get; set; }
        public int MissingFileCount => Files.Count(file => file.Status == "missing");
        public int AlreadyAtDestinationCount => Files.Count(file => file.Status == "alreadyAtDestination");
        public bool CanMove { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Conflicts { get; set; } = new List<string>();
        public List<AuthorMediaMoveFile> Files { get; set; } = new List<AuthorMediaMoveFile>();
    }

    public class AuthorMediaMoveBatchPreview
    {
        public string Format { get; set; }
        public string DestinationRootPath { get; set; }
        public string PreviewToken { get; set; }
        public int AuthorCount => Authors.Count;
        public int MediaFileCount => Authors.Sum(author => author.MediaFileCount);
        public int SidecarFileCount => Authors.Sum(author => author.SidecarFileCount);
        public int MissingFileCount => Authors.Sum(author => author.MissingFileCount);
        public long TotalSize => Authors.Sum(author => author.TotalSize);
        public long RequiredCopyBytes { get; set; }
        public long? AvailableSpace { get; set; }
        public bool CanMove { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Conflicts { get; set; } = new List<string>();
        public List<AuthorMediaMovePreview> Authors { get; set; } = new List<AuthorMediaMovePreview>();
    }

    public class AuthorMediaMoveBatchItem
    {
        public int AuthorId { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public List<AuthorMediaMoveFile> Files { get; set; } = new List<AuthorMediaMoveFile>();
    }
}
