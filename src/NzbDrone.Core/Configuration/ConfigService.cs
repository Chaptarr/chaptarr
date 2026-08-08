using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Security;

namespace NzbDrone.Core.Configuration
{
    public enum ConfigKey
    {
        DownloadedBooksFolder
    }

    public class ConfigService : IConfigService
    {
        private readonly IConfigRepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly IRootFolderService _rootFolderService;
        private readonly Logger _logger;
        private static Dictionary<string, string> _cache;

        public ConfigService(IConfigRepository repository, IEventAggregator eventAggregator, IRootFolderService rootFolderService, Logger logger)
        {
            _repository = repository;
            _eventAggregator = eventAggregator;
            _rootFolderService = rootFolderService;
            _logger = logger;
            _cache = new Dictionary<string, string>();
        }

        private Dictionary<string, object> AllWithDefaults()
        {
            var dict = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);

            var type = GetType();
            var properties = type.GetProperties();

            foreach (var propertyInfo in properties)
            {
                var value = propertyInfo.GetValue(this, null);
                dict.Add(propertyInfo.Name, value);
            }

            return dict;
        }

        public void SaveConfigDictionary(Dictionary<string, object> configValues)
        {
            var allWithDefaults = AllWithDefaults();

            foreach (var configValue in configValues)
            {
                if (!allWithDefaults.TryGetValue(configValue.Key, out var currentValue) ||
                    configValue.Value == null)
                {
                    continue;
                }

                var equal = currentValue != null && configValue.Value.ToString().Equals(currentValue.ToString());

                if (!equal)
                {
                    SetValue(configValue.Key, configValue.Value.ToString());
                }
            }

            _eventAggregator.PublishEvent(new ConfigSavedEvent());
        }

        public bool IsDefined(string key)
        {
            return _repository.Get(key.ToLower()) != null;
        }

        public bool AutoUnmonitorPreviouslyDownloadedBooks
        {
            get { return GetValueBoolean("AutoUnmonitorPreviouslyDownloadedBooks"); }
            set { SetValue("AutoUnmonitorPreviouslyDownloadedBooks", value); }
        }

        public int Retention
        {
            get { return GetValueInt("Retention", 0); }
            set { SetValue("Retention", value); }
        }

        public string RecycleBin
        {
            get
            {
                const string key = "recyclebin";

                EnsureCache();

                if (_cache.TryGetValue(key, out var dbValue))
                {
                    return dbValue ?? string.Empty;
                }
                
                // Default: disabled. New installs should not implicitly enable the recycle bin.
                return string.Empty;
            }
            set { SetValue("RecycleBin", value); }
        }

        public int RecycleBinCleanupDays
        {
            get { return GetValueInt("RecycleBinCleanupDays", 7); }
            set { SetValue("RecycleBinCleanupDays", value); }
        }

        public int RssSyncInterval
        {
            get { return GetValueInt("RssSyncInterval", 15); }

            set { SetValue("RssSyncInterval", value); }
        }

        public int MaximumSize
        {
            get { return GetValueInt("MaximumSize", 0); }

            set { SetValue("MaximumSize", value); }
        }

        public int MinimumAge
        {
            get { return GetValueInt("MinimumAge", 0); }

            set { SetValue("MinimumAge", value); }
        }

        public ProperDownloadTypes DownloadPropersAndRepacks
        {
            get { return GetValueEnum("DownloadPropersAndRepacks", ProperDownloadTypes.PreferAndUpgrade); }

            set { SetValue("DownloadPropersAndRepacks", value); }
        }

        public bool EnableCompletedDownloadHandling
        {
            get { return GetValueBoolean("EnableCompletedDownloadHandling", true); }

            set { SetValue("EnableCompletedDownloadHandling", value); }
        }

        public bool AutoRedownloadFailed
        {
            get { return GetValueBoolean("AutoRedownloadFailed", true); }

            set { SetValue("AutoRedownloadFailed", value); }
        }

        public bool AutoRedownloadFailedFromInteractiveSearch
        {
            get { return GetValueBoolean("AutoRedownloadFailedFromInteractiveSearch", true); }

            set { SetValue("AutoRedownloadFailedFromInteractiveSearch", value); }
        }

        public bool CreateEmptyAuthorFolders
        {
            get { return GetValueBoolean("CreateEmptyAuthorFolders", false); }

            set { SetValue("CreateEmptyAuthorFolders", value); }
        }

        public bool CreateEmptyEbookAuthorFolders
        {
            get
            {
                // Backwards compatibility: if the per-media-type key isn't defined yet,
                // inherit the legacy (audiobook) setting so behavior stays consistent.
                if (!IsDefined("CreateEmptyEbookAuthorFolders"))
                {
                    return CreateEmptyAuthorFolders;
                }

                return GetValueBoolean("CreateEmptyEbookAuthorFolders", false);
            }

            set { SetValue("CreateEmptyEbookAuthorFolders", value); }
        }

        public bool DeleteEmptyFolders
        {
            get { return GetValueBoolean("DeleteEmptyFolders", false); }

            set { SetValue("DeleteEmptyFolders", value); }
        }

        public FileDateType FileDate
        {
            get { return GetValueEnum("FileDate", FileDateType.None); }

            set { SetValue("FileDate", value); }
        }

        public string DownloadClientWorkingFolders
        {
            get { return GetValue("DownloadClientWorkingFolders", "_UNPACK_|_FAILED_"); }
            set { SetValue("DownloadClientWorkingFolders", value); }
        }

        public int DownloadClientHistoryLimit
        {
            get { return GetValueInt("DownloadClientHistoryLimit", 60); }

            set { SetValue("DownloadClientHistoryLimit", value); }
        }

        public bool SkipFreeSpaceCheckWhenImporting
        {
            get { return GetValueBoolean("SkipFreeSpaceCheckWhenImporting", false); }

            set { SetValue("SkipFreeSpaceCheckWhenImporting", value); }
        }

        public int MinimumFreeSpaceWhenImporting
        {
            get { return GetValueInt("MinimumFreeSpaceWhenImporting", 100); }

            set { SetValue("MinimumFreeSpaceWhenImporting", value); }
        }

        public bool CopyUsingHardlinks
        {
            get { return GetValueBoolean("CopyUsingHardlinks", true); }

            set { SetValue("CopyUsingHardlinks", value); }
        }

        public bool ImportExtraFiles
        {
            get { return GetValueBoolean("ImportExtraFiles", false); }

            set { SetValue("ImportExtraFiles", value); }
        }

        public string ExtraFileExtensions
        {
            get { return GetValue("ExtraFileExtensions", "jpg,png,cue,m3u,opf"); }

            set { SetValue("ExtraFileExtensions", value); }
        }

        public bool WatchLibraryForChanges
        {
            get { return GetValueBoolean("WatchLibraryForChanges", true); }

            set { SetValue("WatchLibraryForChanges", value); }
        }

        public bool GranularFileSystemScanning
        {
            get { return GetValueBoolean("GranularFileSystemScanning", true); }

            set { SetValue("GranularFileSystemScanning", value); }
        }

        public RescanAfterRefreshType RescanAfterRefresh
        {
            get { return GetValueEnum("RescanAfterRefresh", RescanAfterRefreshType.Always); }

            set { SetValue("RescanAfterRefresh", value); }
        }

        public AllowFingerprinting AllowFingerprinting
        {
            get { return GetValueEnum("AllowFingerprinting", AllowFingerprinting.NewFiles); }

            set { SetValue("AllowFingerprinting", value); }
        }

        public BookMatchingStrictness BookMatchingStrictness
        {
            get { return GetValueEnum("BookMatchingStrictness", BookMatchingStrictness.Balanced); }

            set { SetValue("BookMatchingStrictness", value); }
        }

        public bool UsePathAsTagsFallback
        {
            get { return GetValueBoolean("UsePathAsTagsFallback", true); }

            set { SetValue("UsePathAsTagsFallback", value); }
        }

        public bool AutoAddMissingAuthorsFromCompletedDownloads
        {
            get { return GetValueBoolean("AutoAddMissingAuthorsFromCompletedDownloads", false); }

            set { SetValue("AutoAddMissingAuthorsFromCompletedDownloads", value); }
        }

        public string DefaultAudiobookRootFolderPath
        {
            get { return GetValue("DefaultAudiobookRootFolderPath", string.Empty); }

            set { SetValue("DefaultAudiobookRootFolderPath", value); }
        }

        public string DefaultEbookRootFolderPath
        {
            get { return GetValue("DefaultEbookRootFolderPath", string.Empty); }

            set { SetValue("DefaultEbookRootFolderPath", value); }
        }

        public int AudiobookConversionConcurrentConversions
        {
            get { return GetValueInt("AudiobookConversionConcurrentConversions", 1); }

            set { SetValue("AudiobookConversionConcurrentConversions", value); }
        }

        public int AudiobookConversionMaxBitrate
        {
            get { return GetValueInt("AudiobookConversionMaxBitrate", 64); }

            set { SetValue("AudiobookConversionMaxBitrate", value); }
        }

        public int AudiobookConversionMaxCpuThreads
        {
            get { return GetValueInt("AudiobookConversionMaxCpuThreads", 4); }

            set { SetValue("AudiobookConversionMaxCpuThreads", value); }
        }

        public bool AudiobookConversionNoUpscale
        {
            get { return GetValueBoolean("AudiobookConversionNoUpscale", true); }

            set { SetValue("AudiobookConversionNoUpscale", value); }
        }

        public string AudiobookConversionAudioChannels
        {
            get { return GetValue("AudiobookConversionAudioChannels", "source"); }

            set { SetValue("AudiobookConversionAudioChannels", value); }
        }

        public string AudiobookConversionTagMode
        {
            get { return ConversionTagModes.Normalize(GetValue("AudiobookConversionTagMode", ConversionTagModes.Preserve)); }

            set { SetValue("AudiobookConversionTagMode", ConversionTagModes.Normalize(value)); }
        }

        public bool EbookConversionEnabled
        {
            get { return GetValueBoolean("EbookConversionEnabled", false); }

            set { SetValue("EbookConversionEnabled", value); }
        }

        public string EbookConversionTargetFormat
        {
            get { return GetValue("EbookConversionTargetFormat", "epub"); }

            set { SetValue("EbookConversionTargetFormat", value); }
        }

        public bool SetPermissionsLinux
        {
            get { return GetValueBoolean("SetPermissionsLinux", false); }

            set { SetValue("SetPermissionsLinux", value); }
        }

        public string ChmodFolder
        {
            get { return GetValue("ChmodFolder", "755"); }

            set { SetValue("ChmodFolder", value); }
        }

        public string ChownGroup
        {
            get { return GetValue("ChownGroup", ""); }

            set { SetValue("ChownGroup", value); }
        }

        public string MetadataSource
        {
            get { return GetValue("MetadataSource", ""); }

            set { SetValue("MetadataSource", value); }
        }

        public string MetadataServerUrl
        {
            get { return GetValue("MetadataServerUrl", "https://api2.chaptarr.com"); }

            set { SetValue("MetadataServerUrl", value); }
        }

        public string HardcoverApiToken
        {
            get { return GetValue("HardcoverApiToken", ""); }

            set { SetValue("HardcoverApiToken", value); }
        }

        public bool HardcoverEnabled
        {
            get { return GetValueBoolean("HardcoverEnabled", false); }

            set { SetValue("HardcoverEnabled", value); }
        }

        public string HardcoverUsername
        {
            get { return GetValue("HardcoverUsername", ""); }

            set { SetValue("HardcoverUsername", value); }
        }

        public string HardcoverUserImageUrl
        {
            get { return GetValue("HardcoverUserImageUrl", ""); }

            set { SetValue("HardcoverUserImageUrl", value); }
        }

        public string MatchingLogsUploadToken
        {
            get { return GetValue("MatchingLogsUploadToken", ""); }

            set { SetValue("MatchingLogsUploadToken", value); }
        }

        public WriteAudioTagsType WriteAudioTags
        {
            get { return GetValueEnum("WriteAudioTags", WriteAudioTagsType.No); }

            set { SetValue("WriteAudioTags", value); }
        }

        public bool ScrubAudioTags
        {
            get { return GetValueBoolean("ScrubAudioTags", false); }

            set { SetValue("ScrubAudioTags", value); }
        }

        public WriteBookTagsType WriteBookTags
        {
            get { return GetValueEnum("WriteBookTags", WriteBookTagsType.NewFiles); }

            set { SetValue("WriteBookTags", value); }
        }

        public bool UpdateCovers
        {
            get { return GetValueBoolean("UpdateCovers", true); }

            set { SetValue("UpdateCovers", value); }
        }

        public bool EmbedMetadata
        {
            get { return GetValueBoolean("EmbedMetadata", false); }

            set { SetValue("EmbedMetadata", value); }
        }

        public int FirstDayOfWeek
        {
            get { return GetValueInt("FirstDayOfWeek", (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek); }

            set { SetValue("FirstDayOfWeek", value); }
        }

        public string CalendarWeekColumnHeader
        {
            get { return GetValue("CalendarWeekColumnHeader", "ddd M/D"); }

            set { SetValue("CalendarWeekColumnHeader", value); }
        }

        public string ShortDateFormat
        {
            get { return GetValue("ShortDateFormat", "MMM D YYYY"); }

            set { SetValue("ShortDateFormat", value); }
        }

        public string LongDateFormat
        {
            get { return GetValue("LongDateFormat", "dddd, MMMM D YYYY"); }

            set { SetValue("LongDateFormat", value); }
        }

        public string TimeFormat
        {
            get { return GetValue("TimeFormat", "h(:mm)a"); }

            set { SetValue("TimeFormat", value); }
        }

        public bool ShowRelativeDates
        {
            get { return GetValueBoolean("ShowRelativeDates", true); }

            set { SetValue("ShowRelativeDates", value); }
        }

        public bool EnableColorImpairedMode
        {
            get { return GetValueBoolean("EnableColorImpairedMode", false); }

            set { SetValue("EnableColorImpairedMode", value); }
        }

        public int UILanguage
        {
            get { return GetValueInt("UILanguage", (int)Language.English); }

            set { SetValue("UILanguage", value); }
        }

        public string AddNewDefaultMediaType
        {
            get { return GetValue("AddNewDefaultMediaType", string.Empty); }
            set { SetValue("AddNewDefaultMediaType", (value ?? string.Empty).Trim().ToLowerInvariant()); }
        }

        public bool CleanupMetadataImages
        {
            get { return GetValueBoolean("CleanupMetadataImages", true); }

            set { SetValue("CleanupMetadataImages", value); }
        }

        public bool AudioProductionCustomFormatsSeeded
        {
            get { return GetValueBoolean("AudioProductionCustomFormatsSeeded", false); }

            set { SetValue("AudioProductionCustomFormatsSeeded", value); }
        }

        public string SeededBuiltInCustomFormatKeys
        {
            get { return GetValue("SeededBuiltInCustomFormatKeys", string.Empty); }

            set { SetValue("SeededBuiltInCustomFormatKeys", value ?? string.Empty); }
        }

        public string PlexClientIdentifier => GetValue("PlexClientIdentifier", Guid.NewGuid().ToString(), true);

        public string RijndaelPassphrase => GetValue("RijndaelPassphrase", Guid.NewGuid().ToString(), true);

        public string HmacPassphrase => GetValue("HmacPassphrase", Guid.NewGuid().ToString(), true);

        public string RijndaelSalt => GetValue("RijndaelSalt", Guid.NewGuid().ToString(), true);

        public string HmacSalt => GetValue("HmacSalt", Guid.NewGuid().ToString(), true);

        public bool ProxyEnabled => ProxyMode != ProxyMode.Disabled;

        public ProxyType ProxyType => GetValueEnum<ProxyType>("ProxyType", ProxyType.Http);

        public ProxyMode ProxyMode => GetValueEnum<ProxyMode>("ProxyMode", ProxyMode.Disabled);

        public int? GlobalProxyId
        {
            get
            {
                var value = GetValueInt("GlobalProxyId", 0);
                return value == 0 ? null : value;
            }
            set => SetValue("GlobalProxyId", value ?? 0);
        }

        public string ProxyHostname => GetValue("ProxyHostname", string.Empty);

        public int ProxyPort => GetValueInt("ProxyPort", 8080);

        public string ProxyUsername => GetValue("ProxyUsername", string.Empty);

        public string ProxyPassword => GetValue("ProxyPassword", string.Empty);

        public string ProxyBypassFilter => GetValue("ProxyBypassFilter", string.Empty);

        public bool ProxyBypassLocalAddresses => GetValueBoolean("ProxyBypassLocalAddresses", true);

        public string BackupFolder => GetValue("BackupFolder", "Backups");

        public int BackupInterval => GetValueInt("BackupInterval", 7);

        public int BackupRetention => GetValueInt("BackupRetention", 28);

        public CertificateValidationType CertificateValidation =>
            GetValueEnum("CertificateValidation", CertificateValidationType.Enabled);

        public string ApplicationUrl => GetValue("ApplicationUrl", string.Empty);

        public bool TrustCgnatIpAddresses
        {
            get { return GetValueBoolean("TrustCgnatIpAddresses", false); }
            set { SetValue("TrustCgnatIpAddresses", value); }
        }

        public bool UseGitHubUpdates
        {
            get { return GetValueBoolean("UseGitHubUpdates", false); }
            set { SetValue("UseGitHubUpdates", value); }
        }

        public string GitHubOwner
        {
            get { return GetValue("GitHubOwner", "chaptarr"); }
            set { SetValue("GitHubOwner", value); }
        }

        public string GitHubRepo
        {
            get { return GetValue("GitHubRepo", "chaptarr"); }
            set { SetValue("GitHubRepo", value); }
        }

        private string GetValue(string key)
        {
            return GetValue(key, string.Empty);
        }

        private bool GetValueBoolean(string key, bool defaultValue = false)
        {
            return Convert.ToBoolean(GetValue(key, defaultValue));
        }

        private int GetValueInt(string key, int defaultValue = 0)
        {
            return Convert.ToInt32(GetValue(key, defaultValue));
        }

        private T GetValueEnum<T>(string key, T defaultValue)
        {
            return (T)Enum.Parse(typeof(T), GetValue(key, defaultValue), true);
        }

        public string GetValue(string key, object defaultValue, bool persist = false)
        {
            key = key.ToLowerInvariant();
            Ensure.That(key, () => key).IsNotNullOrWhiteSpace();

            EnsureCache();

            if (_cache.TryGetValue(key, out var dbValue) && dbValue != null && !string.IsNullOrEmpty(dbValue))
            {
                return dbValue;
            }

            if (IsSensitiveConfigKey(key))
            {
                _logger.Trace("Using default config value for '{0}'", key);
            }
            else
            {
                _logger.Trace("Using default config value for '{0}' defaultValue:'{1}'", key, defaultValue);
            }

            if (persist)
            {
                SetValue(key, defaultValue.ToString());
            }

            return defaultValue.ToString();
        }

        private void SetValue(string key, bool value)
        {
            SetValue(key, value.ToString());
        }

        private void SetValue(string key, int value)
        {
            SetValue(key, value.ToString());
        }

        private void SetValue(string key, Enum value)
        {
            SetValue(key, value.ToString().ToLower());
        }

        private void SetValue(string key, string value)
        {
            key = key.ToLowerInvariant();

            // Normalize proxy hostname - trim whitespace and handle HTTP prefixes
            if (key == "proxyhostname" && !string.IsNullOrEmpty(value))
            {
                var originalValue = value;
                value = value.Trim();

                // Strip HTTP/HTTPS prefixes if present (we'll add them back in the proxy factory)
                if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(7);
                }
                else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(8);
                }

                // Remove trailing slashes
                value = value.TrimEnd('/');

                if (originalValue != value)
                {
                    _logger.Debug("Normalized proxy hostname from '{0}' to '{1}'", originalValue, value);
                }
            }

            // Normalize metadata server URL - trim whitespace and trailing slashes to avoid double-slash requests
            if (key == "metadataserverurl" && !string.IsNullOrEmpty(value))
            {
                var originalValue = value;
                value = value.Trim().TrimEnd('/');

                if (!string.Equals(originalValue, value, StringComparison.Ordinal))
                {
                    _logger.Debug("Normalized metadata server URL from '{0}' to '{1}'", RedactUrlForLogs(originalValue), RedactUrlForLogs(value));
                }
            }

            // Trim proxy username and password
            if ((key == "proxyusername" || key == "proxypassword") && !string.IsNullOrEmpty(value))
            {
                var originalValue = value;
                value = value.Trim();
                if (originalValue != value)
                {
                    _logger.Debug("Trimmed proxy {0}", key);
                }
            }

            if (key == "addnewdefaultmediatype")
            {
                value = (value ?? string.Empty).Trim().ToLowerInvariant();
            }

            if (IsSensitiveConfigKey(key))
            {
                _logger.Trace("Writing Setting to database. Key:'{0}' Value:'(removed)'", key);
            }
            else
            {
                _logger.Trace("Writing Setting to database. Key:'{0}' Value:'{1}'", key, value);
            }
            _repository.Upsert(key, value);

            ClearCache();
        }

        private void EnsureCache()
        {
            lock (_cache)
            {
                if (!_cache.Any())
                {
                    var all = _repository.All();
                    _cache = all.ToDictionary(c => c.Key.ToLower(), c => c.Value);
                }
            }
        }

        private static void ClearCache()
        {
            lock (_cache)
            {
                _cache = new Dictionary<string, string>();
            }
        }

        private static bool IsSensitiveConfigKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            key = key.ToLowerInvariant();

            // Core secrets and credentials; keep broad but avoid false positives on common non-secret keys.
            return key == "metadataserverurl" ||
                   key.Contains("password") ||
                   key.Contains("apikey") ||
                   key.Contains("secret") ||
                   key.Contains("token") ||
                   key.Contains("passphrase");
        }

        private static string RedactUrlForLogs(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return "(invalid_url)";
            }

            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };

            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
    }
}
