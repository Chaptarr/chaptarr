using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// Represents a discovered file with its extracted metadata.
    /// This is the natural output after the extraction phase - a file enriched with its metadata.
    /// </summary>
    public class DiscoveredFileWithMetadata
    {
        // Basic file info
        public string Path { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }

        // Extracted metadata
        public string ExtractedAuthor { get; set; }  // Primary author for compatibility
        public string ExtractedBook { get; set; }
        public string ISBN { get; set; }
        public string ASIN { get; set; }
        public string Narrator { get; set; }
        public Dictionary<string, List<string>> AllTags { get; set; }
        public IReadOnlyList<Dictionary<string, List<string>>> GroupMemberTags { get; set; }
        public QualityModel Quality { get; set; }
        public int? DurationSeconds { get; set; }
        
        // Multi-author support for FTS matching
        public List<Author> ExtractedAuthors { get; set; }

        // Default constructor
        public DiscoveredFileWithMetadata()
        {
            AllTags = new Dictionary<string, List<string>>();
            ExtractedAuthors = new List<Author>();
        }
    }
}
