using System.Collections.Generic;

namespace NzbDrone.Core.ImportLists.Chaptarr
{
    public class ChaptarrAuthor
    {
        public string AuthorName { get; set; }
        public int Id { get; set; }
        public string ForeignAuthorId { get; set; }
        public string Overview { get; set; }
        public List<MediaCover.MediaCover> Images { get; set; }
        public bool Monitored { get; set; }
        public int QualityProfileId { get; set; }
        public string RootFolderPath { get; set; }
        public HashSet<int> Tags { get; set; }
    }

    public class ChaptarrEdition
    {
        public string Title { get; set; }
        public string ForeignEditionId { get; set; }
        public string Overview { get; set; }
        public List<MediaCover.MediaCover> Images { get; set; }
        public bool Monitored { get; set; }
    }

    public class ChaptarrBook
    {
        public string Title { get; set; }
        public string ForeignBookId { get; set; }
        public string ForeignEditionId { get; set; }
        public string Overview { get; set; }
        public List<MediaCover.MediaCover> Images { get; set; }
        public bool Monitored { get; set; }
        public ChaptarrAuthor Author { get; set; }
        public int AuthorId { get; set; }
        public List<ChaptarrEdition> Editions { get; set; }
    }

    public class ChaptarrProfile
    {
        public string Name { get; set; }
        public int Id { get; set; }
    }

    public class ChaptarrTag
    {
        public string Label { get; set; }
        public int Id { get; set; }
    }

    public class ChaptarrRootFolder
    {
        public string Path { get; set; }
        public int Id { get; set; }
    }
}
