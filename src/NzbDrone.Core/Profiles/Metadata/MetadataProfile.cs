using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Profiles.Metadata
{
    public enum MetadataProfileType
    {
        General = 0,
        Audiobook = 1,
        Ebook = 2
    }

    public class MetadataProfile : ModelBase
    {
        public string Name { get; set; }
        public MetadataProfileType ProfileType { get; set; }
        public double MinPopularity { get; set; }
        public bool SkipMissingDate { get; set; }
        public bool SkipMissingIsbn { get; set; }
        public bool SkipPartsAndSets { get; set; }
        public bool SkipSeriesSecondary { get; set; }
        public bool SkipMissingIdentifierOmnibus { get; set; }
        public bool SkipOmnibus { get; set; }
        public bool SkipMissingAsin { get; set; }
        public string AllowedLanguages { get; set; }
        public int MinPages { get; set; }
        public List<string> Ignored { get; set; }

        public MetadataProfile()
        {
            Ignored = new List<string>();
            // Don't override ProfileType - let it be set by the database or caller
        }
    }
}
