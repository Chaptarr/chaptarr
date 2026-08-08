using System.Collections.Generic;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Security;

namespace NzbDrone.Core.Configuration
{
    public interface IConfigService
    {
        void SaveConfigDictionary(Dictionary<string, object> configValues);

        bool IsDefined(string key);

        //Download Client
        string DownloadClientWorkingFolders { get; set; }
        int DownloadClientHistoryLimit { get; set; }

        //Completed/Failed Download Handling (Download client)
        bool EnableCompletedDownloadHandling { get; set; }
        bool AutoRedownloadFailed { get; set; }
        bool AutoRedownloadFailedFromInteractiveSearch { get; set; }

        //Media Management
        bool AutoUnmonitorPreviouslyDownloadedBooks { get; set; }
        string RecycleBin { get; set; }
        int RecycleBinCleanupDays { get; set; }
        ProperDownloadTypes DownloadPropersAndRepacks { get; set; }
        bool CreateEmptyAuthorFolders { get; set; }
        bool CreateEmptyEbookAuthorFolders { get; set; }
        bool DeleteEmptyFolders { get; set; }
        FileDateType FileDate { get; set; }
        bool SkipFreeSpaceCheckWhenImporting { get; set; }
        int MinimumFreeSpaceWhenImporting { get; set; }
        bool CopyUsingHardlinks { get; set; }
        bool ImportExtraFiles { get; set; }
        string ExtraFileExtensions { get; set; }
        bool WatchLibraryForChanges { get; set; }
        bool GranularFileSystemScanning { get; set; }
        RescanAfterRefreshType RescanAfterRefresh { get; set; }
        AllowFingerprinting AllowFingerprinting { get; set; }
        BookMatchingStrictness BookMatchingStrictness { get; set; }
        bool UsePathAsTagsFallback { get; set; }
        bool AutoAddMissingAuthorsFromCompletedDownloads { get; set; }
        string DefaultAudiobookRootFolderPath { get; set; }
        string DefaultEbookRootFolderPath { get; set; }

        // Conversion
        int AudiobookConversionConcurrentConversions { get; set; }
        int AudiobookConversionMaxBitrate { get; set; }
        int AudiobookConversionMaxCpuThreads { get; set; }
        bool AudiobookConversionNoUpscale { get; set; }
        string AudiobookConversionAudioChannels { get; set; }
        string AudiobookConversionTagMode { get; set; }
        bool EbookConversionEnabled { get; set; }
        string EbookConversionTargetFormat { get; set; }

        //Permissions (Media Management)
        bool SetPermissionsLinux { get; set; }
        string ChmodFolder { get; set; }
        string ChownGroup { get; set; }

        //Indexers
        int Retention { get; set; }
        int RssSyncInterval { get; set; }
        int MaximumSize { get; set; }
        int MinimumAge { get; set; }

        //UI
        int FirstDayOfWeek { get; set; }
        string CalendarWeekColumnHeader { get; set; }

        string ShortDateFormat { get; set; }
        string LongDateFormat { get; set; }
        string TimeFormat { get; set; }
        bool ShowRelativeDates { get; set; }
        bool EnableColorImpairedMode { get; set; }
        int UILanguage { get; set; }
        string AddNewDefaultMediaType { get; set; }

        //Internal
        bool CleanupMetadataImages { get; set; }
        bool AudioProductionCustomFormatsSeeded { get; set; }
        string SeededBuiltInCustomFormatKeys { get; set; }

        string PlexClientIdentifier { get; }

        //Metadata
        string MetadataSource { get; set; }
        string MetadataServerUrl { get; set; }
        string HardcoverApiToken { get; set; }
        bool HardcoverEnabled { get; set; }
        string HardcoverUsername { get; set; }
        string HardcoverUserImageUrl { get; set; }
        string MatchingLogsUploadToken { get; set; }
        WriteAudioTagsType WriteAudioTags { get; set; }
        bool ScrubAudioTags { get; set; }
        WriteBookTagsType WriteBookTags { get; set; }
        bool UpdateCovers { get; set; }
        bool EmbedMetadata { get; set; }

        //Forms Auth
        string RijndaelPassphrase { get; }
        string HmacPassphrase { get; }
        string RijndaelSalt { get; }
        string HmacSalt { get; }

        //Proxy
        bool ProxyEnabled { get; }
        ProxyType ProxyType { get; }
        ProxyMode ProxyMode { get; }
        int? GlobalProxyId { get; set; }
        string ProxyHostname { get; }
        int ProxyPort { get; }
        string ProxyUsername { get; }
        string ProxyPassword { get; }
        string ProxyBypassFilter { get; }
        bool ProxyBypassLocalAddresses { get; }

        // Backups
        string BackupFolder { get; }
        int BackupInterval { get; }
        int BackupRetention { get; }

        CertificateValidationType CertificateValidation { get; }
        string ApplicationUrl { get; }

        // GitHub Updates
        bool UseGitHubUpdates { get; set; }
        string GitHubOwner { get; set; }
        string GitHubRepo { get; set; }
    }
}
