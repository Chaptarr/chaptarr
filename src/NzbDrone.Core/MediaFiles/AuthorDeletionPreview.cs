using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles
{
    public sealed class AuthorDeletionPreview
    {
        public string RecycleBin { get; set; }
        public List<AuthorDeletionFolder> Folders { get; set; } = new();
        public List<AuthorDeletionFile> Files { get; set; } = new();
        public List<AuthorDeletionOtherAuthor> OtherAuthors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public sealed class AuthorDeletionFolder
    {
        public string Path { get; set; }
        public bool DeleteContents { get; set; }
        public string Reason { get; set; }
        public int BookFileCount { get; set; }
        public long Size { get; set; }
        public List<string> OtherFiles { get; set; } = new();
    }

    public sealed class AuthorDeletionFile
    {
        public string Path { get; set; }
        public string Reason { get; set; }
    }

    public sealed class AuthorDeletionOtherAuthor
    {
        public int AuthorId { get; set; }
        public string Name { get; set; }
        public int FileCount { get; set; }
    }
}
