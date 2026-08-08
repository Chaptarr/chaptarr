using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Reflection;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Tags;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    public class SettingsBackupService : ISettingsBackupService
    {
        private const string BackupFileExtension = ".chaptarr-settings-backup.json";
        private const long MaxBackupEnvelopeBytes = 25L * 1024L * 1024L;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly IReadOnlyDictionary<string, Type> CustomFormatSpecificationTypes = BuildCustomFormatSpecificationTypes();

        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly INamingConfigService _namingConfigService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly INotificationFactory _notificationFactory;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly ICustomFormatService _customFormatService;
        private readonly ITagService _tagService;
        private readonly IProxyService _proxyService;
        private readonly IRemotePathMappingService _remotePathMappingService;

        public SettingsBackupService(IAppFolderInfo appFolderInfo,
                                     IDiskProvider diskProvider,
                                     IConfigService configService,
                                     INamingConfigService namingConfigService,
                                     IIndexerFactory indexerFactory,
                                     IDownloadClientFactory downloadClientFactory,
                                     INotificationFactory notificationFactory,
                                     IQualityProfileService qualityProfileService,
                                     IMetadataProfileService metadataProfileService,
                                     ICustomFormatService customFormatService,
                                     ITagService tagService,
                                     IProxyService proxyService,
                                     IRemotePathMappingService remotePathMappingService)
        {
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
            _configService = configService;
            _namingConfigService = namingConfigService;
            _indexerFactory = indexerFactory;
            _downloadClientFactory = downloadClientFactory;
            _notificationFactory = notificationFactory;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _customFormatService = customFormatService;
            _tagService = tagService;
            _proxyService = proxyService;
            _remotePathMappingService = remotePathMappingService;
        }

        public List<SettingsBackupLocation> GetLocations()
        {
            var roots = GetAllowedRoots()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return roots.Select(root =>
            {
                var exists = _diskProvider.FolderExists(root);
                var writable = exists && _diskProvider.FolderWritable(root);

                var warning = root == "/config"
                    ? "Warning: If you delete your /config volume to reset your database, any backups stored in /config may be deleted too. Consider using another mount point."
                    : null;

                return new SettingsBackupLocation
                {
                    Path = root,
                    Exists = exists,
                    Writable = writable,
                    Warning = warning
                };
            }).ToList();
        }

        public List<SettingsBackupFileInfo> GetFiles(string rootFolder)
        {
            var normalizedRoot = NormalizeAndValidateRoot(rootFolder);

            if (!_diskProvider.FolderExists(normalizedRoot))
            {
                return new List<SettingsBackupFileInfo>();
            }

            return _diskProvider.GetFileInfos(normalizedRoot, false)
                .Where(f => f != null && f.FullName.EndsWith(BackupFileExtension, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new SettingsBackupFileInfo
                {
                    Path = f.FullName,
                    Name = f.Name,
                    Size = f.Length,
                    LastWriteTimeUtc = f.LastWriteTimeUtc
                })
                .ToList();
        }

        public SettingsBackupResult CreateBackup(SettingsBackupCreateRequest request)
        {
            if (request == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Request body is required");
            }

            var passphrase = (request.Passphrase ?? string.Empty).Trim();
            if (passphrase.IsNullOrWhiteSpace())
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Passphrase is required");
            }

            if (request.Categories == null || request.Categories.Count == 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "At least one category is required");
            }

            var root = NormalizeAndValidateRoot(request.RootFolder);
            EnsureWritableFolder(root);

            var fileName = NormalizeFileName(request.FileName);
            var targetPath = Path.Combine(root, fileName);

            targetPath = EnsureUniqueOrOverwriteTargetPath(targetPath, request.OverwriteExistingFile);

            var package = BuildPackage(request.Categories);
            var json = JsonSerializer.SerializeToUtf8Bytes(package, STJson.GetSerializerSettings());
            var envelope = SettingsBackupCrypto.Encrypt(json, passphrase);

            _diskProvider.WriteAllText(targetPath, JsonSerializer.Serialize(envelope, STJson.GetSerializerSettings()));

            return new SettingsBackupResult
            {
                Path = targetPath,
                Counts = package.Counts,
                Warnings = package.Warnings
            };
        }

        public SettingsBackupRestoreResult RestoreBackup(SettingsBackupRestoreRequest request)
        {
            if (request == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Request body is required");
            }

            var passphrase = (request.Passphrase ?? string.Empty).Trim();
            if (passphrase.IsNullOrWhiteSpace())
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Passphrase is required");
            }

            if (request.Categories == null || request.Categories.Count == 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "At least one category is required");
            }

            var filePath = NormalizeAndValidateFilePath(request.FilePath);
            if (!_diskProvider.FileExists(filePath))
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Backup file not found");
            }

            var fileSize = _diskProvider.GetFileSize(filePath);
            if (fileSize > MaxBackupEnvelopeBytes)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, $"Backup file is too large ({fileSize} bytes).");
            }

            var envelopeJson = _diskProvider.ReadAllText(filePath);
            var envelope = JsonSerializer.Deserialize<SettingsBackupEnvelope>(envelopeJson, STJson.GetSerializerSettings());

            if (envelope == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Invalid backup file: unable to parse envelope");
            }

            SettingsBackupPackage package;
            try
            {
                var packageJson = SettingsBackupCrypto.Decrypt(envelope, passphrase);
                package = JsonSerializer.Deserialize<SettingsBackupPackage>(packageJson, STJson.GetSerializerSettings());
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException || ex is JsonException || ex is ArgumentException)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Invalid passphrase or corrupted backup file", ex);
            }

            if (package == null || !string.Equals(package.Format, SettingsBackupPackage.PackageFormat, StringComparison.OrdinalIgnoreCase))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Invalid backup file: unrecognized format");
            }

            var categoriesToApply = request.Categories.Intersect(package.Categories).ToHashSet();
            if (categoriesToApply.Count == 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Selected categories are not present in this backup file");
            }

            var result = new SettingsBackupRestoreResult();
            AddLegacySectionWarnings(package, categoriesToApply, result);

            // Tag + CustomFormat mapping is needed for other categories.
            var tagIdMap = RestoreTags(package, categoriesToApply, request.Mode, result);
            var customFormatIdMap = RestoreCustomFormats(package, categoriesToApply, request.Mode, result);

            if (categoriesToApply.Contains(SettingsBackupCategory.Profiles))
            {
                if (request.Mode == SettingsBackupRestoreMode.Overwrite)
                {
                    result.Warnings.Add("Profiles are currently restored via merge/upsert; overwrite does not delete existing profiles.");
                }

                RestoreProfiles(package, customFormatIdMap, request.Mode, result);
                result.Applied.Add("Profiles");
            }

            if (categoriesToApply.Contains(SettingsBackupCategory.MediaManagement))
            {
                RestoreMediaManagement(package, result);
                result.Applied.Add("Media Management");
            }

            if (categoriesToApply.Contains(SettingsBackupCategory.MetadataServer))
            {
                RestoreMetadataServer(package, result);
                result.Applied.Add("Metadata Server");
            }

            Dictionary<int, int> proxyIdMap = null;
            if (categoriesToApply.Contains(SettingsBackupCategory.Proxies))
            {
                proxyIdMap = RestoreProxies(package, request.Mode, result);
                RestoreProxySettings(package, proxyIdMap, categoriesToApply, result);
                result.Applied.Add("Proxy Settings");
            }
            else if (categoriesToApply.Contains(SettingsBackupCategory.Indexers))
            {
                // Best-effort: if we aren't restoring proxies, try to map backup proxy IDs to existing proxies by name.
                // If mapping fails, proxy references will be cleared during indexer restore to avoid broken IDs.
                proxyIdMap = BuildProxyIdMapFromExisting(package);
            }

            if (categoriesToApply.Contains(SettingsBackupCategory.Hardcover))
            {
                RestoreHardcover(package, result);
                result.Applied.Add("Hardcover");
            }

            Dictionary<int, int> downloadClientIdMap = null;
            if (categoriesToApply.Contains(SettingsBackupCategory.DownloadClients))
            {
                downloadClientIdMap = RestoreDownloadClients(package, categoriesToApply, tagIdMap, request.Mode, result);
            }
            else if (categoriesToApply.Contains(SettingsBackupCategory.Indexers))
            {
                // Best-effort: if we aren't restoring download clients, try to map backup download client IDs to existing clients.
                // If mapping fails, references will be cleared during indexer restore to avoid broken IDs.
                downloadClientIdMap = BuildDownloadClientIdMapFromExisting(package);
            }

            if (categoriesToApply.Contains(SettingsBackupCategory.Indexers))
            {
                RestoreIndexers(
                    package,
                    tagIdMap,
                    downloadClientIdMap ?? new Dictionary<int, int>(),
                    proxyIdMap ?? new Dictionary<int, int>(),
                    request.Mode,
                    result);
                result.Applied.Add("Indexers");
            }

            if (categoriesToApply.Contains(SettingsBackupCategory.Connections))
            {
                RestoreConnections(package, tagIdMap, request.Mode, result);
                result.Applied.Add("Connections");
            }

            if (categoriesToApply.Contains(SettingsBackupCategory.RemotePathMappings))
            {
                if (downloadClientIdMap == null)
                {
                    downloadClientIdMap = BuildDownloadClientIdMapFromExisting(package);
                }

                RestoreRemotePathMappings(package, downloadClientIdMap, request.Mode, result);
            }

            return result;
        }

        internal static void AddLegacySectionWarnings(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categoriesToApply, SettingsBackupRestoreResult result)
        {
            if (package?.ExtensionData == null ||
                categoriesToApply?.Contains(SettingsBackupCategory.Profiles) != true ||
                !package.ExtensionData.Keys.Any(key => string.Equals(key, "searchCriteriaProfiles", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            result.Warnings.Add("This backup contains legacy Search Criteria profiles. Search Criteria has been removed; use Quality Profiles and Custom Formats instead. The legacy section was not restored.");
        }

        private SettingsBackupPackage BuildPackage(HashSet<SettingsBackupCategory> categories)
        {
            var package = new SettingsBackupPackage
            {
                CreatedAtUtc = DateTime.UtcNow,
                AppVersion = BuildInfo.Version.ToString(),
                Categories = categories.ToList()
            };

            // Always include tags when any selected category can reference tags.
            if (categories.Contains(SettingsBackupCategory.Indexers) ||
                categories.Contains(SettingsBackupCategory.DownloadClients) ||
                categories.Contains(SettingsBackupCategory.Connections))
            {
                package.Tags = _tagService.All()
                    .Select(t => new TagBackup { Id = t.Id, Label = t.Label })
                    .ToList();

                package.Counts["tags"] = package.Tags.Count;
            }

            if (categories.Contains(SettingsBackupCategory.Indexers))
            {
                var indexers = _indexerFactory.All();
                package.Indexers = indexers.Select(ToBackup).ToList();
                package.Counts["indexers"] = package.Indexers.Count;
            }

            if (categories.Contains(SettingsBackupCategory.DownloadClients))
            {
                var downloadClients = _downloadClientFactory.All();
                package.DownloadClients = downloadClients.Select(ToBackup).ToList();
                package.Counts["downloadClients"] = package.DownloadClients.Count;
            }

            if (categories.Contains(SettingsBackupCategory.RemotePathMappings))
            {
                var mappings = _remotePathMappingService.All();
                var downloadClientsById = _downloadClientFactory.All().ToDictionary(d => d.Id);
                package.RemotePathMappings = mappings.Select(m => new RemotePathMappingBackup
                {
                    OriginalId = m.Id,
                    DownloadClientId = m.DownloadClientId,
                    DownloadClientName = downloadClientsById.GetValueOrDefault(m.DownloadClientId)?.Name,
                    Host = m.Host,
                    RemotePath = m.RemotePath,
                    LocalPath = m.LocalPath
                }).ToList();
                package.Counts["remotePathMappings"] = package.RemotePathMappings.Count;
            }

            if (categories.Contains(SettingsBackupCategory.Connections))
            {
                var notifications = _notificationFactory.All();
                package.Connections = notifications.Select(ToBackup).ToList();
                package.Counts["connections"] = package.Connections.Count;
            }

            if (categories.Contains(SettingsBackupCategory.Proxies))
            {
                var proxies = _proxyService.All();
                package.Proxies = proxies.Select(ToBackup).ToList();
                package.ProxySettings = ToProxySettingsBackup();
                package.Counts["proxies"] = package.Proxies.Count;
            }

            if (categories.Contains(SettingsBackupCategory.Profiles))
            {
                package.CustomFormats = _customFormatService.All().Select(ToBackup).ToList();
                package.QualityProfiles = _qualityProfileService.All().Select(ToBackup).ToList();
                package.MetadataProfiles = _metadataProfileService.All();

                package.Counts["customFormats"] = package.CustomFormats.Count;
                package.Counts["qualityProfiles"] = package.QualityProfiles.Count;
                package.Counts["metadataProfiles"] = package.MetadataProfiles.Count;
            }

            if (categories.Contains(SettingsBackupCategory.MediaManagement))
            {
                package.MediaManagement = new MediaManagementBackup
                {
                    AutoUnmonitorPreviouslyDownloadedBooks = _configService.AutoUnmonitorPreviouslyDownloadedBooks,
                    RecycleBin = _configService.RecycleBin,
                    RecycleBinCleanupDays = _configService.RecycleBinCleanupDays,
                    DownloadPropersAndRepacks = _configService.DownloadPropersAndRepacks,
                    CreateEmptyAuthorFolders = _configService.CreateEmptyAuthorFolders,
                    CreateEmptyEbookAuthorFolders = _configService.CreateEmptyEbookAuthorFolders,
                    DeleteEmptyFolders = _configService.DeleteEmptyFolders,
                    FileDate = _configService.FileDate,
                    WatchLibraryForChanges = _configService.WatchLibraryForChanges,
                    GranularFileSystemScanning = _configService.GranularFileSystemScanning,
                    RescanAfterRefresh = _configService.RescanAfterRefresh,
                    AllowFingerprinting = _configService.AllowFingerprinting,
                    SetPermissionsLinux = _configService.SetPermissionsLinux,
                    ChmodFolder = _configService.ChmodFolder,
                    ChownGroup = _configService.ChownGroup,
                    SkipFreeSpaceCheckWhenImporting = _configService.SkipFreeSpaceCheckWhenImporting,
                    MinimumFreeSpaceWhenImporting = _configService.MinimumFreeSpaceWhenImporting,
                    CopyUsingHardlinks = _configService.CopyUsingHardlinks,
                    ImportExtraFiles = _configService.ImportExtraFiles,
                    ExtraFileExtensions = _configService.ExtraFileExtensions,
                    AudiobookConversionConcurrentConversions = _configService.AudiobookConversionConcurrentConversions,
                    AudiobookConversionMaxBitrate = _configService.AudiobookConversionMaxBitrate,
                    AudiobookConversionMaxCpuThreads = _configService.AudiobookConversionMaxCpuThreads,
                    AudiobookConversionNoUpscale = _configService.AudiobookConversionNoUpscale,
                    AudiobookConversionAudioChannels = _configService.AudiobookConversionAudioChannels,
                    AudiobookConversionTagMode = _configService.AudiobookConversionTagMode,
                    EbookConversionEnabled = _configService.EbookConversionEnabled,
                    EbookConversionTargetFormat = _configService.EbookConversionTargetFormat,
                    NamingConfig = _namingConfigService.GetConfig()
                };

                package.Counts["mediaManagement"] = 1;
            }

            if (categories.Contains(SettingsBackupCategory.MetadataServer))
            {
                package.MetadataServerUrl = _configService.MetadataServerUrl;
                package.MetadataSource = _configService.MetadataSource;
                package.Counts["metadataServer"] = 1;
            }

            if (categories.Contains(SettingsBackupCategory.Hardcover))
            {
                package.Hardcover = new HardcoverSettingsBackup
                {
                    Enabled = _configService.HardcoverEnabled,
                    ApiToken = _configService.HardcoverApiToken,
                    Username = _configService.HardcoverUsername,
                    UserImageUrl = _configService.HardcoverUserImageUrl
                };

                package.Counts["hardcover"] = 1;
            }

            return package;
        }

        private static JsonElement SerializeSettings(IProviderConfig settings)
        {
            if (settings == null)
            {
                return default;
            }

            return JsonSerializer.SerializeToElement(settings, settings.GetType(), STJson.GetSerializerSettings());
        }

        private static IndexerDefinitionBackup ToBackup(IndexerDefinition definition)
        {
            return new IndexerDefinitionBackup
            {
                OriginalId = definition.Id,
                Name = definition.Name,
                Implementation = definition.Implementation,
                ConfigContract = definition.ConfigContract,
                Tags = definition.Tags ?? new HashSet<int>(),
                EnableRss = definition.EnableRss,
                EnableAutomaticSearch = definition.EnableAutomaticSearch,
                EnableInteractiveSearch = definition.EnableInteractiveSearch,
                DownloadClientId = definition.DownloadClientId,
                Protocol = definition.Protocol,
                Priority = definition.Priority,
                ProxyId = definition.ProxyId,
                Settings = SerializeSettings(definition.Settings)
            };
        }

        private static DownloadClientDefinitionBackup ToBackup(DownloadClientDefinition definition)
        {
            return new DownloadClientDefinitionBackup
            {
                OriginalId = definition.Id,
                Name = definition.Name,
                Implementation = definition.Implementation,
                ConfigContract = definition.ConfigContract,
                Enable = definition.Enable,
                Tags = definition.Tags ?? new HashSet<int>(),
                AudiobookTags = definition.AudiobookTags ?? new HashSet<int>(),
                EbookTags = definition.EbookTags ?? new HashSet<int>(),
                Protocol = definition.Protocol,
                Priority = definition.Priority,
                RemoveCompletedDownloads = definition.RemoveCompletedDownloads,
                RemoveFailedDownloads = definition.RemoveFailedDownloads,
                CopyUnmanagedDownloads = definition.CopyUnmanagedDownloads,
                Settings = SerializeSettings(definition.Settings)
            };
        }

        private static NotificationDefinitionBackup ToBackup(NotificationDefinition definition)
        {
            return new NotificationDefinitionBackup
            {
                OriginalId = definition.Id,
                Name = definition.Name,
                Implementation = definition.Implementation,
                ConfigContract = definition.ConfigContract,
                Enable = definition.Enable,
                Tags = definition.Tags ?? new HashSet<int>(),
                OnGrab = definition.OnGrab,
                OnReleaseImport = definition.OnReleaseImport,
                OnUpgrade = definition.OnUpgrade,
                OnRename = definition.OnRename,
                OnAuthorAdded = definition.OnAuthorAdded,
                OnBookAdded = definition.OnBookAdded,
                OnAuthorDelete = definition.OnAuthorDelete,
                OnBookDelete = definition.OnBookDelete,
                OnBookFileDelete = definition.OnBookFileDelete,
                OnBookFileDeleteForUpgrade = definition.OnBookFileDeleteForUpgrade,
                OnHealthIssue = definition.OnHealthIssue,
                OnHealthRestored = definition.OnHealthRestored,
                OnDownloadFailure = definition.OnDownloadFailure,
                OnImportFailure = definition.OnImportFailure,
                OnBookRetag = definition.OnBookRetag,
                OnApplicationUpdate = definition.OnApplicationUpdate,
                Settings = SerializeSettings(definition.Settings)
            };
        }

        private static ProxyDefinitionBackup ToBackup(ProxyDefinition proxy)
        {
            return new ProxyDefinitionBackup
            {
                OriginalId = proxy.Id,
                Name = proxy.Name,
                ProxyType = proxy.ProxyType,
                Hostname = proxy.Hostname,
                Port = proxy.Port,
                Username = proxy.Username,
                Password = proxy.Password,
                BypassLocalAddresses = proxy.BypassLocalAddresses,
                BypassFilter = proxy.BypassFilter
            };
        }

        private ProxySettingsBackup ToProxySettingsBackup()
        {
            return new ProxySettingsBackup
            {
                ProxyMode = _configService.ProxyMode,
                GlobalProxyId = _configService.GlobalProxyId,
                ProxyType = _configService.ProxyType,
                ProxyHostname = _configService.ProxyHostname,
                ProxyPort = _configService.ProxyPort,
                ProxyUsername = _configService.ProxyUsername,
                ProxyPassword = _configService.ProxyPassword,
                ProxyBypassLocalAddresses = _configService.ProxyBypassLocalAddresses,
                ProxyBypassFilter = _configService.ProxyBypassFilter
            };
        }

        private static CustomFormatBackup ToBackup(CustomFormat format)
        {
            return new CustomFormatBackup
            {
                OriginalId = format.Id,
                Name = format.Name,
                IncludeCustomFormatWhenRenaming = format.IncludeCustomFormatWhenRenaming,
                BuiltInKey = format.BuiltInKey,
                AppliesTo = format.AppliesTo,
                Specifications = (format.Specifications ?? new List<ICustomFormatSpecification>())
                    .Where(specification => specification != null)
                    .Select(ToBackup)
                    .ToList()
            };
        }

        private static CustomFormatSpecificationBackup ToBackup(ICustomFormatSpecification specification)
        {
            return new CustomFormatSpecificationBackup
            {
                Implementation = specification.GetType().Name,
                ImplementationName = specification.ImplementationName,
                Name = specification.Name,
                Negate = specification.Negate,
                Required = specification.Required,
                Settings = JsonSerializer.SerializeToElement(specification, specification.GetType(), STJson.GetSerializerSettings())
            };
        }

        private static QualityProfileBackup ToBackup(QualityProfile profile)
        {
            return new QualityProfileBackup
            {
                OriginalId = profile.Id,
                Name = profile.Name,
                ProfileType = profile.ProfileType,
                Cutoff = profile.Cutoff,
                Items = profile.Items ?? new List<QualityProfileQualityItem>(),
                MinFormatScore = profile.MinFormatScore,
                CutoffFormatScore = profile.CutoffFormatScore,
                ConvertMp3ToM4b = profile.ConvertMp3ToM4b,
                ConvertToQualityId = profile.ConvertToQualityId,
                UpgradeAllowed = profile.UpgradeAllowed,
                PreferCustomFormatsOverQuality = profile.ProfileType == ProfileType.Audiobook && profile.PreferCustomFormatsOverQuality,
                FormatItems = (profile.FormatItems ?? new List<NzbDrone.Core.Profiles.ProfileFormatItem>())
                    .Where(item => item?.Format != null)
                    .Select(item => new ProfileFormatItemBackup
                    {
                        Score = item.Score,
                        Format = ToBackup(item.Format)
                    })
                    .ToList()
            };
        }

        private Dictionary<int, int> RestoreTags(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categoriesToApply, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            var map = new Dictionary<int, int>();

            if (!categoriesToApply.Contains(SettingsBackupCategory.Indexers) &&
                !categoriesToApply.Contains(SettingsBackupCategory.DownloadClients) &&
                !categoriesToApply.Contains(SettingsBackupCategory.Connections))
            {
                return map;
            }

            if (package.Tags == null || package.Tags.Count == 0)
            {
                return map;
            }

            // Tags are always merge/restored by label to keep provider references consistent.
            foreach (var tag in package.Tags)
            {
                if (tag?.Label.IsNullOrWhiteSpace() != false)
                {
                    continue;
                }

                var created = _tagService.Add(new Tag { Label = tag.Label });
                map[tag.Id] = created.Id;
            }

            if (package.Tags.Count > 0)
            {
                result.Applied.Add("Tags");
            }

            return map;
        }

        private Dictionary<int, int> RestoreCustomFormats(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categoriesToApply, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            var map = new Dictionary<int, int>();

            if (!categoriesToApply.Contains(SettingsBackupCategory.Profiles))
            {
                return map;
            }

            var customFormats = package.CustomFormats ?? new List<CustomFormatBackup>();
            if (customFormats.Count == 0)
            {
                return map;
            }

            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                // Delete existing custom formats first (they are safe to delete; service removes them from profiles).
                foreach (var existing in _customFormatService.All())
                {
                    try
                    {
                        _customFormatService.Delete(existing.Id);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Settings restore: failed to delete custom format '{0}' (ID: {1})", existing.Name, existing.Id);
                        throw;
                    }
                }
            }

            var existingFormatsByName = _customFormatService.All()
                .Where(f => f?.Name.IsNullOrWhiteSpace() == false)
                .ToLookup(f => f.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var format in customFormats)
            {
                if (format == null || format.Name.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var restored = FromBackup(format, result);

                var existing = existingFormatsByName[restored.Name].FirstOrDefault();
                if (existing != null && mode == SettingsBackupRestoreMode.Merge)
                {
                    restored.Id = existing.Id;
                    _customFormatService.Update(restored);
                    map[format.EffectiveOriginalId] = existing.Id;
                    continue;
                }

                var inserted = _customFormatService.Insert(restored);
                map[format.EffectiveOriginalId] = inserted.Id;
            }

            result.Applied.Add("Custom Formats");
            return map;
        }

        private static CustomFormat FromBackup(CustomFormatBackup backup, SettingsBackupRestoreResult result)
        {
            return new CustomFormat
            {
                Id = 0,
                Name = backup.Name,
                BuiltInKey = backup.BuiltInKey,
                IncludeCustomFormatWhenRenaming = backup.IncludeCustomFormatWhenRenaming,
                AppliesTo = backup.AppliesTo,
                Specifications = (backup.Specifications ?? new List<CustomFormatSpecificationBackup>())
                    .Select(specification => FromBackup(specification, backup.Name, result))
                    .Where(specification => specification != null)
                    .ToList()
            };
        }

        private static ICustomFormatSpecification FromBackup(CustomFormatSpecificationBackup backup, string customFormatName, SettingsBackupRestoreResult result)
        {
            if (backup == null)
            {
                return null;
            }

            var specificationType = ResolveCustomFormatSpecificationType(backup);
            if (specificationType == null)
            {
                result.Warnings.Add($"Skipped unknown custom format specification '{backup.Implementation ?? backup.ImplementationName ?? "(unknown)"}' from '{customFormatName}'.");
                return null;
            }

            try
            {
                var json = GetSpecificationSettingsJson(backup);
                var specification = (ICustomFormatSpecification)JsonSerializer.Deserialize(json, specificationType, STJson.GetSerializerSettings());

                if (backup.Name.IsNotNullOrWhiteSpace())
                {
                    specification.Name = backup.Name;
                }

                if (backup.Negate.HasValue)
                {
                    specification.Negate = backup.Negate.Value;
                }

                if (backup.Required.HasValue)
                {
                    specification.Required = backup.Required.Value;
                }

                return specification;
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is ArgumentException)
            {
                Logger.Warn(ex, "Settings restore: failed to deserialize custom format specification '{0}' for '{1}'", backup.Implementation ?? backup.ImplementationName, customFormatName);
                result.Warnings.Add($"Skipped invalid custom format specification '{backup.Implementation ?? backup.ImplementationName ?? "(unknown)"}' from '{customFormatName}'.");
                return null;
            }
        }

        private static Type ResolveCustomFormatSpecificationType(CustomFormatSpecificationBackup backup)
        {
            if (backup.Implementation.IsNotNullOrWhiteSpace() &&
                CustomFormatSpecificationTypes.TryGetValue(backup.Implementation, out var type))
            {
                return type;
            }

            if (backup.LegacyFields != null &&
                backup.LegacyFields.TryGetValue("implementation", out var implementationElement) &&
                implementationElement.ValueKind == JsonValueKind.String &&
                CustomFormatSpecificationTypes.TryGetValue(implementationElement.GetString(), out type))
            {
                return type;
            }

            if (backup.ImplementationName.IsNullOrWhiteSpace())
            {
                return null;
            }

            return CustomFormatSpecificationTypes.Values.FirstOrDefault(candidate =>
            {
                try
                {
                    var specification = (ICustomFormatSpecification)Activator.CreateInstance(candidate);
                    return string.Equals(specification.ImplementationName, backup.ImplementationName, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        }

        private static string GetSpecificationSettingsJson(CustomFormatSpecificationBackup backup)
        {
            if (backup.Settings.ValueKind == JsonValueKind.Object)
            {
                return backup.Settings.GetRawText();
            }

            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in backup.LegacyFields ?? new Dictionary<string, JsonElement>())
            {
                data[field.Key] = field.Value;
            }

            if (backup.Name.IsNotNullOrWhiteSpace())
            {
                data["name"] = backup.Name;
            }

            if (backup.Negate.HasValue)
            {
                data["negate"] = backup.Negate.Value;
            }

            if (backup.Required.HasValue)
            {
                data["required"] = backup.Required.Value;
            }

            return JsonSerializer.Serialize(data, STJson.GetSerializerSettings());
        }

        private static IReadOnlyDictionary<string, Type> BuildCustomFormatSpecificationTypes()
        {
            var interfaceType = typeof(ICustomFormatSpecification);
            var assembly = interfaceType.GetTypeInfo().Assembly;

            return assembly.GetTypes()
                .Where(t => interfaceType.IsAssignableFrom(t) &&
                            t.IsClass &&
                            !t.IsAbstract &&
                            t.Namespace == interfaceType.Namespace)
                .ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
        }

        private void RestoreProfiles(SettingsBackupPackage package, Dictionary<int, int> customFormatIdMap, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            foreach (var profile in package.MetadataProfiles ?? new List<MetadataProfile>())
            {
                if (profile == null || profile.Name.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var restored = new MetadataProfile
                {
                    Id = 0,
                    Name = profile.Name,
                    ProfileType = profile.ProfileType,
                    MinPopularity = profile.MinPopularity,
                    SkipMissingDate = profile.SkipMissingDate,
                    SkipMissingIsbn = profile.SkipMissingIsbn,
                    SkipPartsAndSets = profile.SkipPartsAndSets,
                    SkipSeriesSecondary = profile.SkipSeriesSecondary,
                    SkipMissingIdentifierOmnibus = profile.SkipMissingIdentifierOmnibus,
                    SkipOmnibus = profile.SkipOmnibus,
                    SkipMissingAsin = profile.SkipMissingAsin,
                    AllowedLanguages = profile.AllowedLanguages,
                    MinPages = profile.MinPages,
                    Ignored = profile.Ignored ?? new List<string>()
                };

                if (restored.Name == MetadataProfileService.NONE_PROFILE_NAME)
                {
                    result.Warnings.Add("Skipping built-in metadata profile 'None' (cannot be modified)");
                    continue;
                }

                var existing = _metadataProfileService.All().FirstOrDefault(p => p.Name == restored.Name && p.ProfileType == restored.ProfileType);
                if (existing != null)
                {
                    restored.Id = existing.Id;
                    _metadataProfileService.Update(restored);
                    continue;
                }

                _metadataProfileService.Add(restored);
            }

            var qualityProfileIdMap = new Dictionary<int, int>();

            foreach (var profile in package.QualityProfiles ?? new List<QualityProfileBackup>())
            {
                if (profile == null || profile.Name.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var restored = CloneQualityProfile(profile, customFormatIdMap);
                QualityProfileService.ReconcileCustomFormatMembership(restored, _customFormatService.All());

                var existing = _qualityProfileService.All().FirstOrDefault(p => p.Name == restored.Name && p.ProfileType == restored.ProfileType);
                if (existing != null)
                {
                    restored.Id = existing.Id;
                    _qualityProfileService.Update(restored);
                    qualityProfileIdMap[profile.EffectiveOriginalId] = existing.Id;
                    continue;
                }

                restored.Id = 0;
                var created = _qualityProfileService.Add(restored);
                qualityProfileIdMap[profile.EffectiveOriginalId] = created.Id;
            }

        }

        private void RestoreMediaManagement(SettingsBackupPackage package, SettingsBackupRestoreResult result)
        {
            if (package.MediaManagement == null)
            {
                result.Warnings.Add("No media management settings present in backup");
                return;
            }

            var mm = package.MediaManagement;
            _configService.AutoUnmonitorPreviouslyDownloadedBooks = mm.AutoUnmonitorPreviouslyDownloadedBooks;
            _configService.RecycleBin = mm.RecycleBin ?? string.Empty;
            _configService.RecycleBinCleanupDays = mm.RecycleBinCleanupDays;
            _configService.DownloadPropersAndRepacks = mm.DownloadPropersAndRepacks;
            _configService.CreateEmptyAuthorFolders = mm.CreateEmptyAuthorFolders;
            _configService.CreateEmptyEbookAuthorFolders = mm.CreateEmptyEbookAuthorFolders;
            _configService.DeleteEmptyFolders = mm.DeleteEmptyFolders;
            _configService.FileDate = mm.FileDate;
            _configService.WatchLibraryForChanges = mm.WatchLibraryForChanges;
            _configService.GranularFileSystemScanning = mm.GranularFileSystemScanning;
            _configService.RescanAfterRefresh = mm.RescanAfterRefresh;
            _configService.AllowFingerprinting = mm.AllowFingerprinting;
            _configService.SetPermissionsLinux = mm.SetPermissionsLinux;
            _configService.ChmodFolder = mm.ChmodFolder ?? string.Empty;
            _configService.ChownGroup = mm.ChownGroup ?? string.Empty;
            _configService.SkipFreeSpaceCheckWhenImporting = mm.SkipFreeSpaceCheckWhenImporting;
            _configService.MinimumFreeSpaceWhenImporting = mm.MinimumFreeSpaceWhenImporting;
            _configService.CopyUsingHardlinks = mm.CopyUsingHardlinks;
            _configService.ImportExtraFiles = mm.ImportExtraFiles;
            _configService.ExtraFileExtensions = mm.ExtraFileExtensions ?? string.Empty;
            var audiobookConversionConcurrentConversions = mm.AudiobookConversionConcurrentConversions > 0
                ? mm.AudiobookConversionConcurrentConversions
                : (mm.AudiobookConversionConcurrentDownloads > 0 ? mm.AudiobookConversionConcurrentDownloads : 1);
            var audiobookConversionMaxCpuThreads = mm.AudiobookConversionMaxCpuThreads > 0 ? mm.AudiobookConversionMaxCpuThreads : (mm.AudiobookConversionThreads > 0 ? mm.AudiobookConversionThreads : 4);
            _configService.AudiobookConversionConcurrentConversions = audiobookConversionConcurrentConversions;
            _configService.AudiobookConversionMaxBitrate = mm.AudiobookConversionMaxBitrate > 0 ? mm.AudiobookConversionMaxBitrate : 64;
            _configService.AudiobookConversionMaxCpuThreads = Math.Max(audiobookConversionConcurrentConversions, audiobookConversionMaxCpuThreads);
            _configService.AudiobookConversionNoUpscale = mm.AudiobookConversionNoUpscale;
            _configService.AudiobookConversionAudioChannels = string.IsNullOrWhiteSpace(mm.AudiobookConversionAudioChannels) ? "source" : mm.AudiobookConversionAudioChannels;
            _configService.AudiobookConversionTagMode = string.IsNullOrWhiteSpace(mm.AudiobookConversionTagMode) ? ConversionTagModes.Preserve : mm.AudiobookConversionTagMode;
            _configService.EbookConversionEnabled = mm.EbookConversionEnabled;
            _configService.EbookConversionTargetFormat = string.IsNullOrWhiteSpace(mm.EbookConversionTargetFormat) ? "epub" : mm.EbookConversionTargetFormat;

            if (mm.NamingConfig != null)
            {
                mm.NamingConfig.Id = _namingConfigService.GetConfig().Id;
                _namingConfigService.Save(mm.NamingConfig);
            }
            else
            {
                result.Warnings.Add("Naming config was missing from media management backup");
            }
        }

        private void RestoreMetadataServer(SettingsBackupPackage package, SettingsBackupRestoreResult result)
        {
            if (!package.MetadataServerUrl.IsNullOrWhiteSpace())
            {
                _configService.MetadataServerUrl = package.MetadataServerUrl;
            }
            else
            {
                result.Warnings.Add("Metadata server URL was missing in backup");
            }

            _configService.MetadataSource = package.MetadataSource ?? string.Empty;
        }

        private void RestoreHardcover(SettingsBackupPackage package, SettingsBackupRestoreResult result)
        {
            var hardcover = package.Hardcover;
            if (hardcover == null)
            {
                result.Warnings.Add("Hardcover settings were missing in backup");
                return;
            }

            _configService.HardcoverEnabled = hardcover.Enabled;
            _configService.HardcoverApiToken = hardcover.ApiToken ?? string.Empty;
            _configService.HardcoverUsername = hardcover.Username ?? string.Empty;
            _configService.HardcoverUserImageUrl = hardcover.UserImageUrl ?? string.Empty;

            if (hardcover.Enabled && hardcover.ApiToken.IsNullOrWhiteSpace())
            {
                result.Warnings.Add("Hardcover was enabled in backup but API token was missing");
            }
        }

        private Dictionary<int, int> RestoreProxies(SettingsBackupPackage package, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            var map = new Dictionary<int, int>();
            var proxies = package.Proxies ?? new List<ProxyDefinitionBackup>();
            if (proxies.Count == 0)
            {
                return map;
            }

            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                var existing = _proxyService.All().Select(p => p.Id).ToList();
                foreach (var id in existing)
                {
                    try
                    {
                        _proxyService.Delete(id);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Settings restore: failed to delete proxy (ID: {0})", id);
                        throw;
                    }
                }
            }

            var existingByName = _proxyService.All()
                .Where(p => p?.Name.IsNullOrWhiteSpace() == false)
                .ToLookup(p => p.Name, StringComparer.OrdinalIgnoreCase);

            var restoredIdsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var backup in proxies)
            {
                if (backup == null || backup.Name.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var normalizedName = backup.Name.Trim();
                if (restoredIdsByName.TryGetValue(normalizedName, out var restoredId))
                {
                    map[backup.OriginalId] = restoredId;
                    continue;
                }

                var restored = new ProxyDefinition
                {
                    Name = normalizedName,
                    ProxyType = backup.ProxyType,
                    Hostname = backup.Hostname ?? string.Empty,
                    Port = backup.Port,
                    Username = backup.Username ?? string.Empty,
                    Password = backup.Password ?? string.Empty,
                    BypassLocalAddresses = backup.BypassLocalAddresses,
                    BypassFilter = backup.BypassFilter ?? string.Empty
                };

                if (mode == SettingsBackupRestoreMode.Merge)
                {
                    var existing = existingByName[restored.Name].FirstOrDefault();
                    if (existing != null)
                    {
                        restored.Id = existing.Id;
                        _proxyService.Update(restored);
                        map[backup.OriginalId] = existing.Id;
                        restoredIdsByName[normalizedName] = existing.Id;
                        continue;
                    }
                }

                restored.Id = 0;
                var created = _proxyService.Add(restored);
                map[backup.OriginalId] = created.Id;
                restoredIdsByName[normalizedName] = created.Id;
            }

            return map;
        }

        private void RestoreProxySettings(SettingsBackupPackage package, Dictionary<int, int> proxyIdMap, HashSet<SettingsBackupCategory> categoriesToApply, SettingsBackupRestoreResult result)
        {
            var proxySettings = package.ProxySettings;
            if (proxySettings != null)
            {
                var mappedGlobalProxyId = MapProxyId(proxySettings.GlobalProxyId, proxyIdMap);

                if (proxySettings.GlobalProxyId.HasValue && !mappedGlobalProxyId.HasValue)
                {
                    result.Warnings.Add("Cleared missing default proxy reference from restored proxy settings.");
                }

                _configService.SaveConfigDictionary(new Dictionary<string, object>
                {
                    { nameof(IConfigService.ProxyMode), proxySettings.ProxyMode },
                    { nameof(IConfigService.GlobalProxyId), mappedGlobalProxyId ?? 0 },
                    { nameof(IConfigService.ProxyType), proxySettings.ProxyType },
                    { nameof(IConfigService.ProxyHostname), proxySettings.ProxyHostname ?? string.Empty },
                    { nameof(IConfigService.ProxyPort), proxySettings.ProxyPort },
                    { nameof(IConfigService.ProxyUsername), proxySettings.ProxyUsername ?? string.Empty },
                    { nameof(IConfigService.ProxyPassword), proxySettings.ProxyPassword ?? string.Empty },
                    { nameof(IConfigService.ProxyBypassLocalAddresses), proxySettings.ProxyBypassLocalAddresses },
                    { nameof(IConfigService.ProxyBypassFilter), proxySettings.ProxyBypassFilter ?? string.Empty }
                });

                return;
            }

            RestoreLegacyProxyRoutingFallback(package, proxyIdMap, categoriesToApply, result);
        }

        private void RestoreLegacyProxyRoutingFallback(SettingsBackupPackage package, Dictionary<int, int> proxyIdMap, HashSet<SettingsBackupCategory> categoriesToApply, SettingsBackupRestoreResult result)
        {
            if (!categoriesToApply.Contains(SettingsBackupCategory.Indexers) ||
                _configService.ProxyMode != ProxyMode.Disabled)
            {
                return;
            }

            var referencedBackupProxyIds = (package.Indexers ?? new List<IndexerDefinitionBackup>())
                .Where(indexer => indexer?.ProxyId.HasValue == true &&
                                  indexer.ProxyId.Value != IndexerDefinition.NoProxyOverride)
                .Select(indexer => indexer.ProxyId.Value)
                .Distinct()
                .ToList();

            if (referencedBackupProxyIds.Count == 0)
            {
                return;
            }

            int? mappedGlobalProxyId = null;
            foreach (var backupProxyId in referencedBackupProxyIds)
            {
                if (proxyIdMap.TryGetValue(backupProxyId, out var mappedProxyId))
                {
                    mappedGlobalProxyId = mappedProxyId;
                    break;
                }
            }

            if (!mappedGlobalProxyId.HasValue)
            {
                return;
            }

            _configService.SaveConfigDictionary(new Dictionary<string, object>
            {
                { nameof(IConfigService.ProxyMode), ProxyMode.IndexerOnly },
                { nameof(IConfigService.GlobalProxyId), mappedGlobalProxyId.Value }
            });

            result.Warnings.Add("Backup did not include proxy routing mode; enabled Indexers only so restored indexer proxy assignments can be used.");
        }

        private static int? MapProxyId(int? originalProxyId, Dictionary<int, int> proxyIdMap)
        {
            if (!originalProxyId.HasValue || originalProxyId.Value <= 0)
            {
                return null;
            }

            if (proxyIdMap != null && proxyIdMap.TryGetValue(originalProxyId.Value, out var mappedProxyId))
            {
                return mappedProxyId;
            }

            return null;
        }

        private Dictionary<int, int> RestoreDownloadClients(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categoriesToApply, Dictionary<int, int> tagIdMap, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            var map = new Dictionary<int, int>();

            if (!categoriesToApply.Contains(SettingsBackupCategory.DownloadClients))
            {
                return map;
            }

            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                var existing = _downloadClientFactory.All().Select(d => d.Id).ToList();
                if (existing.Count > 0)
                {
                    _downloadClientFactory.Delete(existing);
                }
            }

            foreach (var backup in package.DownloadClients ?? new List<DownloadClientDefinitionBackup>())
            {
                var restored = FromBackup(backup, tagIdMap);
                if (restored == null)
                {
                    continue;
                }

                if (mode == SettingsBackupRestoreMode.Merge)
                {
                    var existing = _downloadClientFactory.All().FirstOrDefault(d => d.Implementation == restored.Implementation && d.Name == restored.Name);
                    if (existing != null)
                    {
                        restored.Id = existing.Id;
                        _downloadClientFactory.Update(restored);
                        map[backup.OriginalId] = existing.Id;
                        continue;
                    }
                }

                restored.Id = 0;
                var created = _downloadClientFactory.Create(restored);
                map[backup.OriginalId] = created.Id;
            }

            result.Applied.Add("Download Clients");
            return map;
        }

        private void RestoreRemotePathMappings(SettingsBackupPackage package, Dictionary<int, int> downloadClientIdMap, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            var backups = package?.RemotePathMappings ?? new List<RemotePathMappingBackup>();

            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                var existing = _remotePathMappingService.All();
                foreach (var mapping in existing)
                {
                    _remotePathMappingService.Remove(mapping.Id);
                }
            }

            var existingLookup = mode == SettingsBackupRestoreMode.Merge
                ? _remotePathMappingService.All()
                : new List<RemotePathMapping>();

            foreach (var backup in backups)
            {
                if (backup == null || backup.Host.IsNullOrWhiteSpace() ||
                    backup.RemotePath.IsNullOrWhiteSpace() || backup.LocalPath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var downloadClientId = ResolveRemotePathMappingDownloadClientId(backup, downloadClientIdMap);

                if (mode == SettingsBackupRestoreMode.Merge)
                {
                    var existing = existingLookup
                        .FirstOrDefault(m => MappingMatchesBackup(m, backup, downloadClientId));

                    if (existing != null)
                    {
                        existing.DownloadClientId = downloadClientId;
                        existing.LocalPath = backup.LocalPath;
                        _remotePathMappingService.Update(existing);
                        continue;
                    }
                }

                _remotePathMappingService.Add(new RemotePathMapping
                {
                    DownloadClientId = downloadClientId,
                    Host = backup.Host,
                    RemotePath = backup.RemotePath,
                    LocalPath = backup.LocalPath
                });
            }

            result.Applied.Add("Remote Path Mappings");
        }

        private int ResolveRemotePathMappingDownloadClientId(RemotePathMappingBackup backup, Dictionary<int, int> downloadClientIdMap)
        {
            if (backup.DownloadClientId <= 0)
            {
                return 0;
            }

            if (downloadClientIdMap != null && downloadClientIdMap.TryGetValue(backup.DownloadClientId, out var mappedId))
            {
                return mappedId;
            }

            if (backup.DownloadClientName.IsNotNullOrWhiteSpace())
            {
                var existing = _downloadClientFactory.All()
                    .FirstOrDefault(d => string.Equals(d.Name, backup.DownloadClientName, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    return existing.Id;
                }
            }

            return backup.DownloadClientId;
        }

        private static bool MappingMatchesBackup(RemotePathMapping mapping, RemotePathMappingBackup backup, int downloadClientId)
        {
            if (downloadClientId > 0)
            {
                return mapping.DownloadClientId == downloadClientId &&
                       mapping.RemotePath == backup.RemotePath;
            }

            return mapping.DownloadClientId == 0 &&
                   mapping.Host == backup.Host &&
                   mapping.RemotePath == backup.RemotePath;
        }

        private Dictionary<int, int> BuildDownloadClientIdMapFromExisting(SettingsBackupPackage package)
        {
            var map = new Dictionary<int, int>();

            var backups = package?.DownloadClients ?? new List<DownloadClientDefinitionBackup>();
            if (backups.Count == 0)
            {
                return map;
            }

            var existing = _downloadClientFactory.All();

            foreach (var backup in backups)
            {
                if (backup == null || backup.OriginalId <= 0)
                {
                    continue;
                }

                var existingClient = existing.FirstOrDefault(d =>
                    d != null &&
                    d.Implementation == backup.Implementation &&
                    d.Name == (backup.Name ?? string.Empty));

                if (existingClient != null)
                {
                    map[backup.OriginalId] = existingClient.Id;
                }
            }

            return map;
        }

        private Dictionary<int, int> BuildProxyIdMapFromExisting(SettingsBackupPackage package)
        {
            var map = new Dictionary<int, int>();

            var backups = package?.Proxies ?? new List<ProxyDefinitionBackup>();
            if (backups.Count == 0)
            {
                return map;
            }

            var existingByName = _proxyService.All()
                .Where(p => p?.Name.IsNotNullOrWhiteSpace() == true)
                .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var backup in backups)
            {
                if (backup?.Name.IsNullOrWhiteSpace() != false || backup.OriginalId <= 0)
                {
                    continue;
                }

                if (existingByName.TryGetValue(backup.Name.Trim(), out var proxy))
                {
                    map[backup.OriginalId] = proxy.Id;
                }
            }

            return map;
        }

        private void RestoreIndexers(SettingsBackupPackage package, Dictionary<int, int> tagIdMap, Dictionary<int, int> downloadClientIdMap, Dictionary<int, int> proxyIdMap, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            var clearedDownloadClientRefs = 0;
            var clearedProxyRefs = 0;
            var resetLegacyMamWedgePreferences = 0;

            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                var existing = _indexerFactory.All().Select(i => i.Id).ToList();
                if (existing.Count > 0)
                {
                    _indexerFactory.Delete(existing);
                }
            }

            foreach (var backup in package.Indexers ?? new List<IndexerDefinitionBackup>())
            {
                var restored = FromBackup(backup, tagIdMap, downloadClientIdMap, proxyIdMap);
                if (restored == null)
                {
                    continue;
                }

                if (ResetLegacyMamWedgePreference(package, restored))
                {
                    resetLegacyMamWedgePreferences++;
                }

                if (backup.DownloadClientId > 0 && restored.DownloadClientId <= 0)
                {
                    clearedDownloadClientRefs++;
                }

                if (backup.ProxyId.HasValue && restored.ProxyId == null)
                {
                    clearedProxyRefs++;
                }

                if (mode == SettingsBackupRestoreMode.Merge)
                {
                    var existing = _indexerFactory.All().FirstOrDefault(d => d.Implementation == restored.Implementation && d.Name == restored.Name);
                    if (existing != null)
                    {
                        restored.Id = existing.Id;
                        _indexerFactory.Update(restored);
                        continue;
                    }
                }

                restored.Id = 0;
                _indexerFactory.Create(restored);
            }

            if (clearedDownloadClientRefs > 0)
            {
                result.Warnings.Add($"Cleared missing download client references for {clearedDownloadClientRefs} restored indexer(s).");
            }

            if (clearedProxyRefs > 0)
            {
                result.Warnings.Add($"Cleared missing proxy references for {clearedProxyRefs} restored indexer(s).");
            }

            if (resetLegacyMamWedgePreferences > 0)
            {
                result.Warnings.Add($"Reset freeleech wedge use for {resetLegacyMamWedgePreferences} MyAnonaMouse indexer(s) restored from a legacy backup. Enable Prefer Wedge explicitly if wanted.");
            }
        }

        internal static bool ResetLegacyMamWedgePreference(SettingsBackupPackage package, IndexerDefinition restored)
        {
            if (package?.Version >= SettingsBackupPackage.CurrentVersion ||
                restored?.Settings is not MyAnonaMouseSettings settings ||
                settings.UseFreeleechWedge == (int)MyAnonaMouseFreeleechWedgeAction.Never)
            {
                return false;
            }

            settings.UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Never;
            return true;
        }

        private void RestoreConnections(SettingsBackupPackage package, Dictionary<int, int> tagIdMap, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            var clearedAudioBookShelfLibraryMappings = 0;

            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                var existing = _notificationFactory.All().Select(n => n.Id).ToList();
                if (existing.Count > 0)
                {
                    _notificationFactory.Delete(existing);
                }
            }

            foreach (var backup in package.Connections ?? new List<NotificationDefinitionBackup>())
            {
                var restored = FromBackup(backup, tagIdMap);
                if (restored == null)
                {
                    continue;
                }

                if (ClearNonPortableConnectionSettings(restored))
                {
                    clearedAudioBookShelfLibraryMappings++;
                }

                if (mode == SettingsBackupRestoreMode.Merge)
                {
                    var existing = _notificationFactory.All().FirstOrDefault(d => d.Implementation == restored.Implementation && d.Name == restored.Name);
                    if (existing != null)
                    {
                        restored.Id = existing.Id;
                        _notificationFactory.Update(restored);
                        continue;
                    }
                }

                restored.Id = 0;
                _notificationFactory.Create(restored);
            }

            if (clearedAudioBookShelfLibraryMappings > 0)
            {
                result.Warnings.Add($"Cleared AudioBookShelf library mappings for {clearedAudioBookShelfLibraryMappings} restored connection(s). Root folders are not included in settings backups; review the AudioBookShelf connection after restore.");
            }
        }

        private static HashSet<int> RemapTags(HashSet<int> tagIds, Dictionary<int, int> map)
        {
            if (tagIds == null || tagIds.Count == 0 || map == null || map.Count == 0)
            {
                return tagIds ?? new HashSet<int>();
            }

            return tagIds.Select(id => map.GetValueOrDefault(id, id)).ToHashSet();
        }

        private static DownloadClientDefinition FromBackup(DownloadClientDefinitionBackup backup, Dictionary<int, int> tagIdMap)
        {
            if (backup == null || backup.Implementation.IsNullOrWhiteSpace() || backup.ConfigContract.IsNullOrWhiteSpace())
            {
                return null;
            }

            var settings = DeserializeProviderSettings(backup.ConfigContract, backup.Settings);
            if (settings == null)
            {
                return null;
            }

            return new DownloadClientDefinition
            {
                Name = backup.Name ?? string.Empty,
                Implementation = backup.Implementation,
                ConfigContract = backup.ConfigContract,
                Enable = backup.Enable,
                Tags = RemapTags(backup.Tags ?? new HashSet<int>(), tagIdMap),
                AudiobookTags = RemapTags(backup.AudiobookTags ?? new HashSet<int>(), tagIdMap),
                EbookTags = RemapTags(backup.EbookTags ?? new HashSet<int>(), tagIdMap),
                Protocol = backup.Protocol,
                Priority = backup.Priority,
                RemoveCompletedDownloads = backup.RemoveCompletedDownloads,
                RemoveFailedDownloads = backup.RemoveFailedDownloads,
                CopyUnmanagedDownloads = backup.CopyUnmanagedDownloads,
                Settings = settings
            };
        }

        private static IndexerDefinition FromBackup(IndexerDefinitionBackup backup, Dictionary<int, int> tagIdMap, Dictionary<int, int> downloadClientIdMap, Dictionary<int, int> proxyIdMap)
        {
            if (backup == null || backup.Implementation.IsNullOrWhiteSpace() || backup.ConfigContract.IsNullOrWhiteSpace())
            {
                return null;
            }

            var settings = DeserializeProviderSettings(backup.ConfigContract, backup.Settings);
            if (settings == null)
            {
                return null;
            }

            var mappedDownloadClientId = backup.DownloadClientId;
            if (backup.DownloadClientId > 0 && downloadClientIdMap != null)
            {
                if (downloadClientIdMap.TryGetValue(backup.DownloadClientId, out var newId))
                {
                    mappedDownloadClientId = newId;
                }
                else
                {
                    mappedDownloadClientId = 0;
                }
            }

            int? mappedProxyId = backup.ProxyId;
            if (backup.ProxyId.HasValue &&
                backup.ProxyId.Value != IndexerDefinition.NoProxyOverride &&
                proxyIdMap != null)
            {
                if (proxyIdMap.TryGetValue(backup.ProxyId.Value, out var newProxyId))
                {
                    mappedProxyId = newProxyId;
                }
                else
                {
                    mappedProxyId = null;
                }
            }

            return new IndexerDefinition
            {
                Name = backup.Name ?? string.Empty,
                Implementation = backup.Implementation,
                ConfigContract = backup.ConfigContract,
                Tags = RemapTags(backup.Tags ?? new HashSet<int>(), tagIdMap),
                EnableRss = backup.EnableRss,
                EnableAutomaticSearch = backup.EnableAutomaticSearch,
                EnableInteractiveSearch = backup.EnableInteractiveSearch,
                DownloadClientId = mappedDownloadClientId,
                Protocol = backup.Protocol,
                Priority = backup.Priority,
                ProxyId = mappedProxyId,
                Settings = settings
            };
        }

        private static NotificationDefinition FromBackup(NotificationDefinitionBackup backup, Dictionary<int, int> tagIdMap)
        {
            if (backup == null || backup.Implementation.IsNullOrWhiteSpace() || backup.ConfigContract.IsNullOrWhiteSpace())
            {
                return null;
            }

            var settings = DeserializeProviderSettings(backup.ConfigContract, backup.Settings);
            if (settings == null)
            {
                return null;
            }

            return new NotificationDefinition
            {
                Name = backup.Name ?? string.Empty,
                Implementation = backup.Implementation,
                ConfigContract = backup.ConfigContract,
                Tags = RemapTags(backup.Tags ?? new HashSet<int>(), tagIdMap),
                OnGrab = backup.OnGrab,
                OnReleaseImport = backup.OnReleaseImport,
                OnUpgrade = backup.OnUpgrade,
                OnRename = backup.OnRename,
                OnAuthorAdded = backup.OnAuthorAdded,
                OnBookAdded = backup.OnBookAdded,
                OnAuthorDelete = backup.OnAuthorDelete,
                OnBookDelete = backup.OnBookDelete,
                OnBookFileDelete = backup.OnBookFileDelete,
                OnBookFileDeleteForUpgrade = backup.OnBookFileDeleteForUpgrade,
                OnHealthIssue = backup.OnHealthIssue,
                OnHealthRestored = backup.OnHealthRestored,
                OnDownloadFailure = backup.OnDownloadFailure,
                OnImportFailure = backup.OnImportFailure,
                OnBookRetag = backup.OnBookRetag,
                OnApplicationUpdate = backup.OnApplicationUpdate,
                Settings = settings,
                Enable = backup.Enable
            };
        }

        private static bool ClearNonPortableConnectionSettings(NotificationDefinition definition)
        {
            // Add future notification settings here if they serialize local Chaptarr IDs that cannot be safely restored.
            if (definition?.Settings is AudioBookShelfSettings audioBookShelfSettings)
            {
                return audioBookShelfSettings.ClearLibraryMappings();
            }

            return false;
        }

        private static IProviderConfig DeserializeProviderSettings(string configContract, JsonElement settingsElement)
        {
            var configType = ProviderConfigTypeCache.Find(configContract);
            if (configType == null)
            {
                Logger.Warn("Settings restore: unknown provider config contract '{0}'", configContract);
                return null;
            }

            try
            {
                var json = settingsElement.ValueKind == JsonValueKind.Undefined || settingsElement.ValueKind == JsonValueKind.Null
                    ? "{}"
                    : settingsElement.GetRawText();

                return (IProviderConfig)JsonSerializer.Deserialize(json, configType, STJson.GetSerializerSettings());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Settings restore: failed to deserialize provider settings for contract '{0}'", configContract);
                return null;
            }
        }

        private static QualityProfile CloneQualityProfile(QualityProfileBackup profile, Dictionary<int, int> customFormatIdMap)
        {
            var restored = new QualityProfile
            {
                Id = 0,
                Name = profile.Name,
                ProfileType = profile.ProfileType,
                Cutoff = profile.Cutoff,
                Items = profile.Items ?? new List<QualityProfileQualityItem>(),
                MinFormatScore = profile.MinFormatScore,
                CutoffFormatScore = profile.CutoffFormatScore,
                ConvertMp3ToM4b = profile.ConvertMp3ToM4b,
                ConvertToQualityId = profile.ConvertToQualityId,
                UpgradeAllowed = profile.UpgradeAllowed,
                PreferCustomFormatsOverQuality = profile.ProfileType == ProfileType.Audiobook && profile.PreferCustomFormatsOverQuality,
                FormatItems = new List<NzbDrone.Core.Profiles.ProfileFormatItem>()
            };

            foreach (var item in profile.FormatItems ?? new List<ProfileFormatItemBackup>())
            {
                if (item?.Format == null)
                {
                    continue;
                }

                var oldId = item.Format.EffectiveOriginalId;
                var newId = customFormatIdMap != null && customFormatIdMap.TryGetValue(oldId, out var mapped) ? mapped : oldId;

                restored.FormatItems.Add(new NzbDrone.Core.Profiles.ProfileFormatItem
                {
                    Score = item.Score,
                    Format = new CustomFormat { Id = newId, Name = item.Format.Name }
                });
            }

            return restored;
        }

        private IEnumerable<string> GetAllowedRoots()
        {
            // Minimal allowlist: common container mount points + appdata.
            var defaults = new List<string>
            {
                "/config",
                "/downloads",
                "/audiobooks",
                "/ebooks",
                _appFolderInfo.GetAppDataPath()
            };

            var env = Environment.GetEnvironmentVariable("CHAPTARR_SETTINGS_BACKUP_ALLOWED_ROOTS");
            if (!env.IsNullOrWhiteSpace())
            {
                defaults.AddRange(env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            return defaults.Where(p => p.IsNotNullOrWhiteSpace());
        }

        private string NormalizeAndValidateRoot(string rootFolder)
        {
            rootFolder = (rootFolder ?? string.Empty).Trim();

            if (rootFolder.IsNullOrWhiteSpace())
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Root folder is required");
            }

            if (!Path.IsPathRooted(rootFolder))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Root folder must be an absolute path");
            }

            var normalized = Path.GetFullPath(rootFolder);
            if (!IsAllowedPath(normalized))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, $"Root folder '{rootFolder}' is not an allowed backup location");
            }

            return normalized;
        }

        private string NormalizeAndValidateFilePath(string filePath)
        {
            filePath = (filePath ?? string.Empty).Trim();

            if (filePath.IsNullOrWhiteSpace())
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Backup file path is required");
            }

            if (!Path.IsPathRooted(filePath))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Backup file path must be an absolute path");
            }

            if (!filePath.EndsWith(BackupFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, $"Backup file must end with '{BackupFileExtension}'");
            }

            var normalized = Path.GetFullPath(filePath);
            if (!IsAllowedPath(normalized))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, $"Backup file path '{filePath}' is not an allowed backup location");
            }

            return normalized;
        }

        private bool IsAllowedPath(string fullPath)
        {
            var roots = GetAllowedRoots()
                .Select(root =>
                {
                    try
                    {
                        return Path.GetFullPath(root);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(root => root.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // First gate: lexical containment (fast) so obviously unrelated paths are rejected.
            if (roots.None(root => IsPathWithinRoot(fullPath, root)))
            {
                return false;
            }

            // Second gate: symlink-aware containment. Accept if the resolved path lives under ANY resolved allowed root.
            // This prevents symlink traversal escapes, while allowing common container patterns like:
            //   /config/backups -> /downloads/backups (both roots are allowlisted)
            string resolvedPath;
            try
            {
                resolvedPath = GetSymlinkAwareFullPath(fullPath);
            }
            catch
            {
                // Best-effort only; fall back to lexical containment to avoid breaking NAS/Docker edge cases.
                return true;
            }

            if (resolvedPath.IsNullOrWhiteSpace())
            {
                return true;
            }

            var resolvedRoots = roots
                .Select(root =>
                {
                    try
                    {
                        return GetSymlinkAwareFullPath(root);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(root => root.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return resolvedRoots.Any(resolvedRoot => IsPathWithinRoot(resolvedPath, resolvedRoot));
        }

        private static string GetSymlinkAwareFullPath(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            path = Path.GetFullPath(path);
            var root = Path.GetPathRoot(path);

            if (root.IsNullOrWhiteSpace())
            {
                return null;
            }

            var remainder = path.Substring(root.Length);
            var segments = remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var current = root;

            for (var i = 0; i < segments.Length; i++)
            {
                var next = Path.Combine(current, segments[i]);

                if (!Directory.Exists(next) && !File.Exists(next))
                {
                    // Can't resolve further; append remaining segments to the resolved prefix.
                    for (var j = i; j < segments.Length; j++)
                    {
                        current = Path.Combine(current, segments[j]);
                    }

                    return current;
                }

                var isDir = Directory.Exists(next);
                FileSystemInfo info = isDir ? new DirectoryInfo(next) : new FileInfo(next);

                try
                {
                    var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                    current = resolved?.FullName ?? info.FullName;
                }
                catch
                {
                    current = info.FullName;
                }
            }

            return current;
        }

        private static bool IsPathWithinRoot(string fullPath, string rootPath)
        {
            if (fullPath.IsNullOrWhiteSpace() || rootPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            rootPath = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (fullPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFileName(string fileName)
        {
            var name = (fileName ?? string.Empty).Trim();
            if (name.IsNullOrWhiteSpace())
            {
                name = $"chaptarr_settings_{DateTime.UtcNow:yyyyMMdd_HHmmss}{BackupFileExtension}";
            }

            if (!name.EndsWith(BackupFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                name += BackupFileExtension;
            }

            // Prevent path traversal via filename.
            name = Path.GetFileName(name);

            return name;
        }

        private void EnsureWritableFolder(string folder)
        {
            if (!_diskProvider.FolderExists(folder))
            {
                _diskProvider.EnsureFolder(folder);
            }

            if (!_diskProvider.FolderWritable(folder))
            {
                throw new NzbDroneClientException(HttpStatusCode.Forbidden, $"Backup folder '{folder}' is not writable");
            }
        }

        private string EnsureUniqueOrOverwriteTargetPath(string targetPath, bool overwrite)
        {
            if (!_diskProvider.FileExists(targetPath))
            {
                return targetPath;
            }

            if (overwrite)
            {
                return targetPath;
            }

            var dir = Path.GetDirectoryName(targetPath);
            var baseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(targetPath));
            var suffix = Path.GetFileName(targetPath).Substring(baseName.Length);

            for (var i = 2; i < 1000; i++)
            {
                var candidate = Path.Combine(dir ?? string.Empty, $"{baseName}-{i}{suffix}");
                if (!_diskProvider.FileExists(candidate))
                {
                    return candidate;
                }
            }

            throw new NzbDroneClientException(HttpStatusCode.Conflict, "Unable to find a unique filename for backup");
        }
    }
}
