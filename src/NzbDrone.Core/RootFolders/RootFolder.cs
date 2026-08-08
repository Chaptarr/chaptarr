using System.Collections.Generic;
using Newtonsoft.Json;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.RootFolders
{
    public enum FolderType
    {
        Mixed = 0,      // Accepts both audiobook and ebook files (was Unknown)
        Audiobook = 1,  // Audiobook-only folder
        Ebook = 2       // Ebook-only folder
    }

    public class MediaTypeSettings
    {
        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
        public int? MonitorExisting { get; set; } // NULL=unconfigured, 0=None, 1=All, 2=Selected
        public bool? MonitorFuture { get; set; }
        public bool WriteAudioBookShelfMetadataJson { get; set; }
        public bool WriteAudioBookShelfCover { get; set; }
        public List<int> Tags { get; set; } = new List<int>();
    }

    public class ResolvedRootFolderSettings
    {
        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
        public int? MonitorExisting { get; set; }
        public bool? MonitorFuture { get; set; }
        public List<int> Tags { get; set; } = new List<int>();
        public bool IsConfigured { get; set; }
        public string Source { get; set; } // "MediaSpecific", "Legacy", "Unconfigured"
    }

    public class RootFolder : ModelBase
    {
        public string Name { get; set; }
        public string Path { get; set; }
        // DELETED - Migration 022 drops generic defaults, Migration 025 drops media-specific defaults
        // public int DefaultMetadataProfileId { get; set; } // REMOVED
        // public int DefaultQualityProfileId { get; set; } // REMOVED
        // public int? DefaultAudiobookMetadataProfileId { get; set; } // REMOVED
        // public int? DefaultEbookMetadataProfileId { get; set; } // REMOVED
        public HashSet<int> DefaultTags { get; set; } = new();
        public bool IsCalibreLibrary { get; set; }
        public bool UseCalibreNaming { get; set; }
        public CalibreSettings CalibreSettings { get; set; }
        public FolderType FolderType { get; set; }
        public bool PlaceEbooksWithAudiobooks { get; set; }
        public bool? DefaultSyncMonitoredAcrossFormats { get; set; }

        // PER-MEDIA-TYPE SETTINGS (JSON)
        // NULL = unconfigured for this media type
        public string AudiobookSettings { get; set; }
        public string EbookSettings { get; set; }

        public bool Accessible { get; set; }
        public long? FreeSpace { get; set; }
        public long? TotalSpace { get; set; }

        // Helper methods to parse JSON settings
        public MediaTypeSettings GetAudiobookSettings()
        {
            if (string.IsNullOrWhiteSpace(AudiobookSettings))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<MediaTypeSettings>(AudiobookSettings);
            }
            catch
            {
                return null;
            }
        }

        public MediaTypeSettings GetEbookSettings()
        {
            if (string.IsNullOrWhiteSpace(EbookSettings))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<MediaTypeSettings>(EbookSettings);
            }
            catch
            {
                return null;
            }
        }

        public void SetAudiobookSettings(MediaTypeSettings settings)
        {
            AudiobookSettings = settings == null ? null : JsonConvert.SerializeObject(settings);
        }

        public void SetEbookSettings(MediaTypeSettings settings)
        {
            EbookSettings = settings == null ? null : JsonConvert.SerializeObject(settings);
        }
    }
}
