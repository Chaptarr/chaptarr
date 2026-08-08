using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Parser.Model
{
    public class LocalBook
    {
        public string Path { get; set; }
        public int CalibreId { get; set; }
        public int Part { get; set; }
        public int PartCount { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public int? DurationSeconds { get; set; }
        // Field-agnostic raw tags for import path
        public RawFileTags RawTags { get; set; }
        public ParsedBookInfo FolderTrackInfo { get; set; }
        public ParsedBookInfo DownloadClientBookInfo { get; set; }
        public List<string> AcoustIdResults { get; set; }
        public Author Author { get; set; }
        public Book Book { get; set; }
        public Edition Edition { get; set; }
        public QualityModel Quality { get; set; }
        public IndexerFlags IndexerFlags { get; set; }
        public bool ExistingFile { get; set; }
        public bool IsGeneratedConversion { get; set; }
        public List<string> GeneratedConversionSourcePaths { get; set; }
        public string GeneratedConversionOutputPath { get; set; }
        public long? GeneratedConversionOutputSize { get; set; }
        public QualityModel GeneratedConversionSourceQuality { get; set; }
        public string GeneratedConversionTagMode { get; set; }
        public string GeneratedConversionTagManifestJson { get; set; }
        public bool AdditionalFile { get; set; }
        public bool SceneSource { get; set; }
        public string ReleaseGroup { get; set; }
        public bool IsGraphicAudio { get; set; }
        public string AudioProductionType { get; set; }
        public string SceneName { get; set; }
        public string Narrator { get; set; }
        public bool IsInitialImport { get; set; }
        public bool IsManualImport { get; set; }
        public MatchProvenance MatchProvenance { get; set; }

        // Manual import suggestion metadata (V5 match) - must not cause DB side effects during suggestion generation.
        public string SuggestedForeignAuthorId { get; set; }
        public string SuggestedAuthorName { get; set; }
        public string SuggestedForeignBookId { get; set; }
        public string SuggestedBookTitle { get; set; }
        public string SuggestedForeignEditionId { get; set; }
        public string SuggestedEditionTitle { get; set; }

        // Common tags extracted from multi-file book units
        public Dictionary<string, string> CommonTags { get; set; }

        public override string ToString()
        {
            return Path;
        }
    }
}
