using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    internal class SettingsBackupPackage
    {
        public const string PackageFormat = "chaptarr.settings-backup";
        public const int CurrentVersion = 2;

        public string Format { get; set; } = PackageFormat;
        public int Version { get; set; } = CurrentVersion;
        public string AppVersion { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public List<SettingsBackupCategory> Categories { get; set; } = new();

        public List<TagBackup> Tags { get; set; } = new();

        public List<IndexerDefinitionBackup> Indexers { get; set; } = new();
        public List<DownloadClientDefinitionBackup> DownloadClients { get; set; } = new();
        public List<NotificationDefinitionBackup> Connections { get; set; } = new();
        public List<ProxyDefinitionBackup> Proxies { get; set; } = new();
        public ProxySettingsBackup ProxySettings { get; set; }

        public List<CustomFormatBackup> CustomFormats { get; set; } = new();
        public List<QualityProfileBackup> QualityProfiles { get; set; } = new();
        public List<MetadataProfile> MetadataProfiles { get; set; } = new();

        public MediaManagementBackup MediaManagement { get; set; }
        public string MetadataServerUrl { get; set; }
        public string MetadataSource { get; set; }
        public HardcoverSettingsBackup Hardcover { get; set; }

        public List<RemotePathMappingBackup> RemotePathMappings { get; set; } = new();

        public Dictionary<string, int> Counts { get; set; } = new();
        public List<string> Warnings { get; set; } = new();

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal class SettingsBackupEnvelope
    {
        public string Format { get; set; } = SettingsBackupCrypto.EnvelopeFormat;
        public int Version { get; set; } = 1;
        public string Cipher { get; set; } = "aes-256-gcm";
        public int KdfIterations { get; set; } = SettingsBackupCrypto.DefaultKdfIterations;
        public string SaltB64 { get; set; }
        public string NonceB64 { get; set; }
        public string TagB64 { get; set; }
        public string CiphertextB64 { get; set; }
    }

    internal class TagBackup
    {
        public int Id { get; set; }
        public string Label { get; set; }
    }

    internal class IndexerDefinitionBackup
    {
        public int OriginalId { get; set; }
        public string Name { get; set; }
        public string Implementation { get; set; }
        public string ConfigContract { get; set; }
        public HashSet<int> Tags { get; set; } = new();
        public bool EnableRss { get; set; }
        public bool EnableAutomaticSearch { get; set; }
        public bool EnableInteractiveSearch { get; set; }
        public int DownloadClientId { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public int Priority { get; set; }
        public int? ProxyId { get; set; }
        public JsonElement Settings { get; set; }
    }

    internal class DownloadClientDefinitionBackup
    {
        public int OriginalId { get; set; }
        public string Name { get; set; }
        public string Implementation { get; set; }
        public string ConfigContract { get; set; }
        public bool Enable { get; set; }
        public HashSet<int> Tags { get; set; } = new();
        public HashSet<int> AudiobookTags { get; set; } = new();
        public HashSet<int> EbookTags { get; set; } = new();
        public DownloadProtocol Protocol { get; set; }
        public int Priority { get; set; }
        public bool RemoveCompletedDownloads { get; set; }
        public bool RemoveFailedDownloads { get; set; }
        public bool CopyUnmanagedDownloads { get; set; }
        public JsonElement Settings { get; set; }
    }

    internal class NotificationDefinitionBackup
    {
        public int OriginalId { get; set; }
        public string Name { get; set; }
        public string Implementation { get; set; }
        public string ConfigContract { get; set; }
        public bool Enable { get; set; }
        public HashSet<int> Tags { get; set; } = new();

        public bool OnGrab { get; set; }
        public bool OnReleaseImport { get; set; }
        public bool OnUpgrade { get; set; }
        public bool OnRename { get; set; }
        public bool OnAuthorAdded { get; set; }
        public bool OnBookAdded { get; set; }
        public bool OnAuthorDelete { get; set; }
        public bool OnBookDelete { get; set; }
        public bool OnBookFileDelete { get; set; }
        public bool OnBookFileDeleteForUpgrade { get; set; }
        public bool OnHealthIssue { get; set; }
        public bool OnHealthRestored { get; set; }
        public bool OnDownloadFailure { get; set; }
        public bool OnImportFailure { get; set; }
        public bool OnBookRetag { get; set; }
        public bool OnApplicationUpdate { get; set; }

        public JsonElement Settings { get; set; }
    }

    internal class ProxyDefinitionBackup
    {
        public int OriginalId { get; set; }
        public string Name { get; set; }
        public ProxyType ProxyType { get; set; }
        public string Hostname { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool BypassLocalAddresses { get; set; }
        public string BypassFilter { get; set; }
    }

    internal class ProxySettingsBackup
    {
        public ProxyMode ProxyMode { get; set; }
        public int? GlobalProxyId { get; set; }
        public ProxyType ProxyType { get; set; }
        public string ProxyHostname { get; set; }
        public int ProxyPort { get; set; }
        public string ProxyUsername { get; set; }
        public string ProxyPassword { get; set; }
        public bool ProxyBypassLocalAddresses { get; set; }
        public string ProxyBypassFilter { get; set; }
    }

    internal class HardcoverSettingsBackup
    {
        public bool Enabled { get; set; }
        public string ApiToken { get; set; }
        public string Username { get; set; }
        public string UserImageUrl { get; set; }
    }

    internal class RemotePathMappingBackup
    {
        public int OriginalId { get; set; }
        public int DownloadClientId { get; set; }
        public string DownloadClientName { get; set; }
        public string Host { get; set; }
        public string RemotePath { get; set; }
        public string LocalPath { get; set; }
    }

    internal class CustomFormatBackup
    {
        public int OriginalId { get; set; }

        // Legacy backups serialized CustomFormat directly, so the old ID is named "id".
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Id { get; set; }

        public string Name { get; set; }
        public bool IncludeCustomFormatWhenRenaming { get; set; }
        public string BuiltInKey { get; set; }
        public CustomFormatMediaType AppliesTo { get; set; }
        public List<CustomFormatSpecificationBackup> Specifications { get; set; } = new();

        [JsonIgnore]
        public int EffectiveOriginalId => OriginalId > 0 ? OriginalId : Id;
    }

    internal class CustomFormatSpecificationBackup
    {
        public string Implementation { get; set; }
        public string ImplementationName { get; set; }
        public string Name { get; set; }
        public bool? Negate { get; set; }
        public bool? Required { get; set; }
        public JsonElement Settings { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> LegacyFields { get; set; } = new();
    }

    internal class QualityProfileBackup
    {
        public int OriginalId { get; set; }

        // Legacy backups serialized QualityProfile directly, so the old ID is named "id".
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Id { get; set; }

        public string Name { get; set; }
        public ProfileType ProfileType { get; set; }
        public bool UpgradeAllowed { get; set; }
        public bool PreferCustomFormatsOverQuality { get; set; }
        public bool ConvertMp3ToM4b { get; set; }
        public int? ConvertToQualityId { get; set; }
        public int Cutoff { get; set; }
        public int MinFormatScore { get; set; }
        public int CutoffFormatScore { get; set; }
        public List<ProfileFormatItemBackup> FormatItems { get; set; } = new();
        public List<QualityProfileQualityItem> Items { get; set; }

        [JsonIgnore]
        public int EffectiveOriginalId => OriginalId > 0 ? OriginalId : Id;
    }

    internal class ProfileFormatItemBackup
    {
        public CustomFormatBackup Format { get; set; }
        public int Score { get; set; }
    }

    internal class MediaManagementBackup
    {
        public bool AutoUnmonitorPreviouslyDownloadedBooks { get; set; }
        public string RecycleBin { get; set; }
        public int RecycleBinCleanupDays { get; set; }
        public ProperDownloadTypes DownloadPropersAndRepacks { get; set; }
        public bool CreateEmptyAuthorFolders { get; set; }
        public bool CreateEmptyEbookAuthorFolders { get; set; }
        public bool DeleteEmptyFolders { get; set; }
        public FileDateType FileDate { get; set; }
        public bool WatchLibraryForChanges { get; set; }
        public bool GranularFileSystemScanning { get; set; }
        public RescanAfterRefreshType RescanAfterRefresh { get; set; }
        public AllowFingerprinting AllowFingerprinting { get; set; }

        public bool SetPermissionsLinux { get; set; }
        public string ChmodFolder { get; set; }
        public string ChownGroup { get; set; }

        public bool SkipFreeSpaceCheckWhenImporting { get; set; }
        public int MinimumFreeSpaceWhenImporting { get; set; }
        public bool CopyUsingHardlinks { get; set; }
        public bool ImportExtraFiles { get; set; }
        public string ExtraFileExtensions { get; set; }

        public int AudiobookConversionConcurrentConversions { get; set; }
        public int AudiobookConversionMaxBitrate { get; set; }
        public int AudiobookConversionMaxCpuThreads { get; set; }
        public bool AudiobookConversionNoUpscale { get; set; }
        public string AudiobookConversionAudioChannels { get; set; }
        public string AudiobookConversionTagMode { get; set; }
        // Legacy restore shim for backups created before the setting was renamed.
        public int AudiobookConversionConcurrentDownloads { get; set; }
        // Legacy restore shim for backups created before AudiobookConversionMaxCpuThreads.
        public int AudiobookConversionThreads { get; set; }
        public bool EbookConversionEnabled { get; set; }
        public string EbookConversionTargetFormat { get; set; }

        public NamingConfig NamingConfig { get; set; }
    }
}
