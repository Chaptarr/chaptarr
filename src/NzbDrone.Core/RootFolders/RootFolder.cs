using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private int? _legacyMonitorExisting;

        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }

        // Author-level monitoring is a binary media-side gate. NULL means that this
        // root has not supplied a default for the side.
        public bool? Monitored { get; set; }

        // One-time seed for book rows present when an author is first inserted from
        // this root. It is independent from the author gate and later-row policy.
        public MonitorTypes? MonitorExistingMode { get; set; }

        // Compatibility for root JSON written before the one-time seed became a
        // full MonitorTypes value. New JSON writes only MonitorExistingMode.
        [JsonIgnore]
        public bool? MonitorExistingBooks
        {
            get => MonitorExistingMode switch
            {
                MonitorTypes.All => true,
                MonitorTypes.None => false,
                _ => null
            };
            set
            {
                if (value.HasValue)
                {
                    MonitorExistingMode = value.Value ? MonitorTypes.All : MonitorTypes.None;
                }
            }
        }

        // Read-only in-memory compatibility for callers still constructing the old
        // 0/1/2-shaped setting. It is never written to root JSON.
        [JsonIgnore]
        public int? MonitorExisting
        {
            get => _legacyMonitorExisting ?? Monitored switch
            {
                true => MonitorExistingMode == MonitorTypes.All ? 1 : 2,
                false => 0,
                _ => MonitorExistingMode switch
                {
                    MonitorTypes.All => 1,
                    MonitorTypes.None => 0,
                    _ => null
                }
            };
            set
            {
                _legacyMonitorExisting = value;
                if (value.HasValue)
                {
                    MonitorExistingMode = value.Value == 1 ? MonitorTypes.All : MonitorTypes.None;
                }
            }
        }

        // Ongoing policy for catalog rows discovered after the author is configured.
        public NewItemMonitorTypes? MonitorNewItems { get; set; }

        // Read-only compatibility for pre-binary root JSON. New settings never write
        // this field; the migration/repair path converts it to the fields above.
        [JsonIgnore]
        public bool? MonitorFuture { get; set; }
        public bool WriteAudioBookShelfMetadataJson { get; set; }
        public bool WriteAudioBookShelfCover { get; set; }
        public List<int> Tags { get; set; } = new List<int>();

        public void ApplyLegacyMonitoringSettings(int? monitorExisting, bool? monitorFuture)
        {
            if (!monitorExisting.HasValue && !monitorFuture.HasValue)
            {
                return;
            }

            bool? monitored = null;
            MonitorTypes? monitorExistingMode = null;
            NewItemMonitorTypes? monitorNewItems = null;

            if (monitorExisting == 1)
            {
                monitored = true;
                monitorExistingMode = MonitorTypes.All;
                monitorNewItems = NewItemMonitorTypes.All;
            }
            else if (monitorExisting == 2)
            {
                monitored = true;
                monitorExistingMode = MonitorTypes.None;
                monitorNewItems = monitorFuture == true ? NewItemMonitorTypes.New : NewItemMonitorTypes.None;
            }
            else if (monitorFuture == true)
            {
                monitored = true;
                monitorExistingMode = MonitorTypes.None;
                monitorNewItems = NewItemMonitorTypes.New;
            }
            else if (monitorExisting == 0)
            {
                monitored = false;
            }

            Monitored ??= monitored;
            MonitorExistingMode ??= monitorExistingMode;
            MonitorNewItems ??= monitorNewItems;
        }

        internal void SetLegacyCompatibilityValues(int? monitorExisting, bool? monitorFuture)
        {
            // Keep the old properties available only to in-process compatibility
            // callers; serialization uses the canonical fields above.
            _legacyMonitorExisting = monitorExisting;
            MonitorFuture = monitorFuture;
        }
    }

    public class ResolvedRootFolderSettings
    {
        private int? _legacyMonitorExisting;

        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
        public bool? Monitored { get; set; }
        public MonitorTypes? MonitorExistingMode { get; set; }
        [JsonIgnore]
        public bool? MonitorExistingBooks
        {
            get => MonitorExistingMode switch
            {
                MonitorTypes.All => true,
                MonitorTypes.None => false,
                _ => null
            };
            set
            {
                if (value.HasValue)
                {
                    MonitorExistingMode = value.Value ? MonitorTypes.All : MonitorTypes.None;
                }
            }
        }
        [JsonIgnore]
        public int? MonitorExisting
        {
            get => _legacyMonitorExisting ?? MonitorExistingMode switch
            {
                MonitorTypes.All => 1,
                MonitorTypes.None => 0,
                _ => null
            };
            set
            {
                _legacyMonitorExisting = value;
                if (value.HasValue)
                {
                    MonitorExistingMode = value.Value == 1 ? MonitorTypes.All : MonitorTypes.None;
                }
            }
        }
        public NewItemMonitorTypes? MonitorNewItems { get; set; }
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
                return DeserializeMediaTypeSettings(AudiobookSettings);
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
                return DeserializeMediaTypeSettings(EbookSettings);
            }
            catch
            {
                return null;
            }
        }

        public void SetAudiobookSettings(MediaTypeSettings settings)
        {
            AudiobookSettings = SerializeMediaTypeSettings(settings);
        }

        public void SetEbookSettings(MediaTypeSettings settings)
        {
            EbookSettings = SerializeMediaTypeSettings(settings);
        }

        private static MediaTypeSettings DeserializeMediaTypeSettings(string json)
        {
            var settings = JsonConvert.DeserializeObject<MediaTypeSettings>(json);
            var payload = JObject.Parse(json);
            var legacyExisting = payload.TryGetValue("MonitorExisting", StringComparison.OrdinalIgnoreCase, out var existingToken) &&
                                 existingToken.Type != JTokenType.Null
                ? existingToken.Value<int?>()
                : null;
            var legacyFuture = payload.TryGetValue("MonitorFuture", StringComparison.OrdinalIgnoreCase, out var futureToken) &&
                               futureToken.Type != JTokenType.Null
                ? futureToken.Value<bool?>()
                : null;
            var legacyMonitorExistingBooks = payload.TryGetValue("MonitorExistingBooks", StringComparison.OrdinalIgnoreCase, out var existingBooksToken) &&
                                             existingBooksToken.Type != JTokenType.Null
                ? existingBooksToken.Value<bool?>()
                : null;

            if (!settings.MonitorExistingMode.HasValue && legacyMonitorExistingBooks.HasValue)
            {
                settings.MonitorExistingMode = legacyMonitorExistingBooks.Value ? MonitorTypes.All : MonitorTypes.None;
            }

            settings.ApplyLegacyMonitoringSettings(legacyExisting, legacyFuture);
            settings.SetLegacyCompatibilityValues(legacyExisting, legacyFuture);
            return settings;
        }

        private static string SerializeMediaTypeSettings(MediaTypeSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            settings.ApplyLegacyMonitoringSettings(settings.MonitorExisting, settings.MonitorFuture);
            return JsonConvert.SerializeObject(settings);
        }
    }
}
