using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Equ;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    public class BookFile : ModelBase
    {
        // these are model properties
        public string Path { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public DateTime DateAdded { get; set; }
        public string OriginalFilePath { get; set; }
        public string SceneName { get; set; }
        public string ReleaseGroup { get; set; }
        public QualityModel Quality { get; set; }
        public IndexerFlags IndexerFlags { get; set; }
        public MediaInfoModel MediaInfo { get; set; }
        public int EditionId { get; set; }
        public int CalibreId { get; set; }
        public int Part { get; set; }

        // GraphicAudio classification fields
        public bool IsGraphicAudio { get; set; }
        public string AudioProductionType { get; set; }

        // Narrator information
        public string Narrator { get; set; }

        // Match confidence tracking
        public DateTime? LastMatchAttempt { get; set; }
        public string MatchDetails { get; set; }

        // Structured explanation for the successful automatic/manual match that linked this file.
        // MatchDetails remains the separate why-unmapped/apply-failure scratchpad.
        public MatchProvenance MatchProvenance { get; set; }

        // Media type tracking
        public string MediaType { get; set; } = "audiobook";

        // Managed replica copies (additional full paths) used for mixed audiobook+ebook colocation.
        // These are additional on-disk copies/hardlinks of the same BookFile content stored elsewhere.
        public List<string> ReplicaPaths { get; set; } = new List<string>();

        // All extracted embedded metadata tags (raw, unfiltered). Persisted as JSON.
        public Dictionary<string, List<string>> AllTags { get; set; }

        // Pre-computed file duration in seconds (from stream header / DURATION tag)
        public int? DurationSeconds { get; set; }

        // These are queried from the database
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<Author> LazyAuthor { get; set; }
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<Edition> LazyEdition { get; set; }

        public Author Author
        {
            get => LazyAuthor?.Value;
            set => LazyAuthor = value;
        }

        public Edition Edition
        {
            get => LazyEdition?.Value;
            set => LazyEdition = value;
        }

        // Calculated manually
        public int PartCount { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}] {1}", Id, Path);
        }

        public string GetSceneOrFileName()
        {
            if (SceneName.IsNotNullOrWhiteSpace())
            {
                return SceneName;
            }

            if (Path.IsNotNullOrWhiteSpace())
            {
                return System.IO.Path.GetFileNameWithoutExtension(Path);
            }

            return string.Empty;
        }

        public static string DetermineMediaType(QualityModel quality)
        {
            return QualityMediaTypeHelper.IsEbookFileQuality(quality.Quality) ? "ebook" : "audiobook";
        }
    }
}
