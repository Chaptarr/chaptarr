using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
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
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Releases;
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
        private static readonly StringComparer ProviderNameComparer = StringComparer.InvariantCultureIgnoreCase;
        private static readonly IReadOnlyDictionary<string, Type> CustomFormatSpecificationTypes = BuildCustomFormatSpecificationTypes();

        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly IConfigFileProvider _configFileProvider;
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
        private readonly IDelayProfileService _delayProfileService;
        private readonly IReleaseProfileService _releaseProfileService;
        private readonly IQualityDefinitionService _qualityDefinitionService;
        private readonly MediaManagementPathValidator _mediaPaths;
        private readonly IFilenameValidationService _filenameValidation;

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
                                     IRemotePathMappingService remotePathMappingService,
                                     IConfigFileProvider configFileProvider,
                                     IDelayProfileService delayProfileService,
                                     IReleaseProfileService releaseProfileService,
                                     IQualityDefinitionService qualityDefinitionService,
                                     MediaManagementPathValidator mediaPaths,
                                     IFilenameValidationService filenameValidation)
        {
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
            _configService = configService;
            _configFileProvider = configFileProvider;
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
            _delayProfileService = delayProfileService;
            _releaseProfileService = releaseProfileService;
            _qualityDefinitionService = qualityDefinitionService;
            _mediaPaths = mediaPaths;
            _filenameValidation = filenameValidation;
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
            }).Where(location => location.Exists).ToList();
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
            var timer = Stopwatch.StartNew();
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

            Logger.Info("Settings backup completed: {0} categories, {1} bytes in {2} ms", request.Categories.Count, _diskProvider.GetFileSize(targetPath), timer.ElapsedMilliseconds);
            return new SettingsBackupResult
            {
                Path = targetPath,
                Counts = package.Counts,
                Warnings = package.Warnings
            };
        }

        public SettingsBackupRestoreResult RestoreBackup(SettingsBackupRestoreRequest request)
        {
            var timer = Stopwatch.StartNew();
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
            var existingProviders = ValidateRestoreProviderNames(package, categoriesToApply, request.Mode, result);
            if (request.Mode == SettingsBackupRestoreMode.Merge)
            {
                MergeGeneralSettings(package, categoriesToApply);
            }
            result.Warnings.AddRange(ValidateRestore(package, categoriesToApply, _filenameValidation,
                definition => ApplyRestoreMode(definition, existingProviders.SingleOrDefault(existing =>
                    existing.GetType() == definition.GetType() && ProviderNameComparer.Equals(existing.Name, definition.Name) && existing.Implementation == definition.Implementation), request.Mode)));
            PrepareRestorePaths(package, categoriesToApply, result);
            AddLegacySectionWarnings(package, categoriesToApply, result);
            if (request.Mode == SettingsBackupRestoreMode.Overwrite)
            {
                PreviewOverwriteRemovals(package, categoriesToApply, existingProviders, result);
            }
            if (categoriesToApply.Contains(SettingsBackupCategory.Profiles) && request.Mode == SettingsBackupRestoreMode.Overwrite)
            {
                result.Warnings.Add("Quality and metadata profiles are merged by name. Overwrite replaces custom formats, delay and release profiles; existing custom-format scores are removed. Quality size limits are updated.");
            }
            if (request.Preview) return result;

            var steps = new List<(string Category, string Name, Action Apply)>();
            void Add(SettingsBackupCategory category, string name, Action apply)
            {
                if (categoriesToApply.Contains(category)) steps.Add((CategoryName(category), name, apply));
            }
            Dictionary<int, int> tagIdMap = new(), customFormatIdMap = new(), proxyIdMap = new(), downloadClientIdMap = new();
            var insertedFormatIds = new HashSet<int>();
            Add(SettingsBackupCategory.Indexers, "search settings", () => RestoreIndexerSettings(package, result));
            Add(SettingsBackupCategory.DownloadClients, "download settings", () => RestoreDownloadClientSettings(package, result));
            Add(SettingsBackupCategory.UiSettings, "preferences", () => RestoreUiSettings(package, result));
            if (package.Tags?.Count > 0 && categoriesToApply.Overlaps(new[] { SettingsBackupCategory.Indexers, SettingsBackupCategory.DownloadClients, SettingsBackupCategory.Connections, SettingsBackupCategory.Profiles }))
            {
                steps.Add(("Tags", "tags", () => tagIdMap = RestoreTags(package, categoriesToApply, request.Mode, result)));
            }
            Add(SettingsBackupCategory.Profiles, "custom formats", () => customFormatIdMap = RestoreCustomFormats(package, categoriesToApply, request.Mode, result, insertedFormatIds));
            Add(SettingsBackupCategory.Profiles, "metadata profiles", () => RestoreMetadataProfiles(package, result, request.Mode));
            Add(SettingsBackupCategory.Profiles, "quality profiles", () => RestoreQualityProfiles(package, customFormatIdMap, request.Mode, insertedFormatIds));
            Add(SettingsBackupCategory.MediaManagement, "media management settings", () => RestoreMediaManagement(package, result));
            Add(SettingsBackupCategory.MetadataServer, "metadata server settings", () => RestoreMetadataServer(package, result));
            if (categoriesToApply.Contains(SettingsBackupCategory.Proxies))
            {
                Add(SettingsBackupCategory.Proxies, "proxy definitions", () => proxyIdMap = RestoreProxies(package, request.Mode, result));
                Add(SettingsBackupCategory.Proxies, "proxy settings", () => RestoreProxySettings(package, proxyIdMap, categoriesToApply, result, request.Mode));
            }
            else
            {
                Add(SettingsBackupCategory.Indexers, "proxy references", () => proxyIdMap = BuildProxyIdMapFromExisting(package));
            }
            Add(SettingsBackupCategory.Hardcover, "Hardcover settings", () => RestoreHardcover(package, result));
            if (categoriesToApply.Contains(SettingsBackupCategory.DownloadClients))
            {
                Add(SettingsBackupCategory.DownloadClients, "client definitions", () => downloadClientIdMap = RestoreDownloadClients(package, categoriesToApply, tagIdMap, request.Mode, result));
            }
            else if (categoriesToApply.Contains(SettingsBackupCategory.Indexers) || categoriesToApply.Contains(SettingsBackupCategory.RemotePathMappings))
            {
                var category = categoriesToApply.Contains(SettingsBackupCategory.Indexers) ? SettingsBackupCategory.Indexers : SettingsBackupCategory.RemotePathMappings;
                Add(category, "download client references", () => downloadClientIdMap = BuildDownloadClientIdMapFromExisting(package));
            }
            Add(SettingsBackupCategory.Indexers, "indexer definitions", () => RestoreIndexers(package, tagIdMap, downloadClientIdMap, proxyIdMap,
                request.Mode, result));
            Add(SettingsBackupCategory.Profiles, "delay profiles", () => RestoreDelayProfiles(package, tagIdMap, request.Mode, result));
            Add(SettingsBackupCategory.Profiles, "release profiles", () => RestoreReleaseProfiles(package, tagIdMap, request.Mode, result));
            Add(SettingsBackupCategory.Profiles, "quality sizes", () => RestoreQualitySizes(package, result, request.Mode));
            Add(SettingsBackupCategory.Connections, "connections", () => RestoreConnections(package, tagIdMap, request.Mode, result));
            Add(SettingsBackupCategory.RemotePathMappings, "remote path mappings", () => RestoreRemotePathMappings(package, downloadClientIdMap, request.Mode, result));

            Logger.Info("Settings restore started: {0}, mode {1}", string.Join(", ", categoriesToApply.OrderBy(category => category).Select(CategoryName)), request.Mode);
            for (var index = 0; index < steps.Count; index++)
            {
                try
                {
                    steps[index].Apply();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    var completed = new List<string>();
                    var partial = new List<string>();
                    var unattempted = new List<string>();
                    foreach (var group in steps.Select((step, position) => (step.Category, step.Name, Position: position)).GroupBy(step => step.Category))
                    {
                        if (group.All(step => step.Position < index)) completed.Add(group.Key);
                        else if (group.All(step => step.Position > index)) unattempted.Add(group.Key);
                        else
                        {
                            var done = group.Where(step => step.Position < index).Select(step => step.Name).ToList();
                            var pending = group.Where(step => step.Position > index).Select(step => step.Name).ToList();
                            var failed = group.SingleOrDefault(step => step.Position == index);
                            partial.Add(group.Key + ": " + (done.Count > 0 ? string.Join(", ", done) + " completed; " : "") +
                                (failed.Name != null ? "failed at " + failed.Name + " (may be partially changed); " : "") +
                                (pending.Count > 0 ? string.Join(", ", pending) + " not attempted" : ""));
                        }
                    }
                    string Items(List<string> items) => items.Count == 0 ? "none" : string.Join("; ", items);
                    var message = $"Settings restore stopped at {steps[index].Category}: {steps[index].Name} ({ex.GetType().Name}). " +
                        $"Changes were not rolled back.\nCompleted categories: {Items(completed)}.\nPartly applied categories: {Items(partial)}.\nNot attempted categories: {Items(unattempted)}.";
                    // Provider/database exceptions can contain credentials; report location and error type, not raw values.
                    Logger.Error("{0}", message);
                    throw new NzbDroneClientException(HttpStatusCode.InternalServerError, message);
                }
            }
            result.Applied = result.Applied.Concat(steps.Select(step => step.Category)).Distinct().ToList();
            result.Warnings = result.Warnings.Distinct().ToList();

            Logger.Info("Settings restore completed: {0} categories, {1} warnings, {2} bytes in {3} ms", categoriesToApply.Count, result.Warnings.Count, fileSize, timer.ElapsedMilliseconds);
            return result;
        }

        private void PreviewOverwriteRemovals(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categories,
            List<ProviderDefinition> existingProviders, SettingsBackupRestoreResult result)
        {
            var removedClients = GetRemovedClients(existingProviders.OfType<DownloadClientDefinition>(), package).ToList();
            foreach (var client in removedClients) result.WouldRemove.Add($"Download client: {client.Name}");
            if (removedClients.Count > 0 || categories.Contains(SettingsBackupCategory.RemotePathMappings))
            {
                var mappings = _remotePathMappingService.All();
                var clientIds = removedClients.Select(client => client.Id).ToHashSet();
                var removedMappings = mappings.Where(mapping => clientIds.Contains(mapping.DownloadClientId));
                if (categories.Contains(SettingsBackupCategory.RemotePathMappings))
                    removedMappings = removedMappings.Concat(GetRemovedMappings(mappings, result));
                foreach (var mapping in removedMappings.DistinctBy(mapping => mapping.Id))
                    result.WouldRemove.Add($"Remote path mapping: {mapping.Host}:{mapping.RemotePath} → {mapping.LocalPath}");
            }
            foreach (var indexer in GetRemovedIndexers(existingProviders.OfType<IndexerDefinition>(), package))
                result.WouldRemove.Add($"Indexer: {indexer.Name}");
            foreach (var connection in GetRemovedConnections(existingProviders.OfType<NotificationDefinition>(), package))
                result.WouldRemove.Add($"Connection: {connection.Name}");
            if (categories.Contains(SettingsBackupCategory.Proxies))
                foreach (var proxy in GetRemovedProxies(_proxyService.All(), package))
                    result.WouldRemove.Add($"Proxy: {proxy.Name}");
            if (!categories.Contains(SettingsBackupCategory.Profiles)) return;
            if (package.CustomFormats?.Count > 0)
                foreach (var format in _customFormatService.All()) result.WouldRemove.Add($"Custom format: {format.Name}");

            if (package.DelayProfiles == null && package.ReleaseProfiles == null) return;

            // Preview allocates no real tags. Reproduce TagService.Add's exact lookup/lowercase-insert
            // behavior so duplicate backup labels map to the same identity they will get on restore.
            var currentTags = _tagService.All();
            var labelsById = currentTags.ToDictionary(tag => tag.Id, tag => tag.Label);
            string TagsLabel(IEnumerable<int> ids) => string.Join(", ", (ids ?? Enumerable.Empty<int>()).OrderBy(id => id)
                .Select(id => labelsById.TryGetValue(id, out var label) ? label : $"#{id}"));
            var tagsByLabel = package.Tags?.Count > 0
                ? currentTags.ToDictionary(tag => tag.Label, tag => tag.Id, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);
            var nextTagId = tagsByLabel.Values.DefaultIfEmpty(0).Max();
            var tagIdMap = new Dictionary<int, int>();
            foreach (var tag in package.Tags ?? new())
            {
                if (tag?.Label.IsNullOrWhiteSpace() != false) continue;
                if (!tagsByLabel.TryGetValue(tag.Label, out var id))
                    tagsByLabel[tag.Label.ToLowerInvariant()] = id = ++nextTagId;
                tagIdMap[tag.Id] = id;
            }
            if (package.DelayProfiles != null && ReplacesAllDelayProfiles(package, PrepareDelayProfiles(package, tagIdMap, result)))
                foreach (var profile in _delayProfileService.All().Where(profile => profile.Id != 1))
                    result.WouldRemove.Add($"Delay profile: {TagsLabel(profile.Tags)} (#{profile.Id})");
            if (package.ReleaseProfiles == null) return;

            var indexers = _indexerFactory.All();
            if (categories.Contains(SettingsBackupCategory.Indexers))
            {
                var removed = GetRemovedIndexers(indexers, package).Select(indexer => indexer.Id).ToHashSet();
                indexers = indexers.Where(indexer => !removed.Contains(indexer.Id)).ToList();
                indexers.AddRange((package.Indexers ?? new()).Select(saved => FromBackup(saved, null, null, null)).Where(indexer => indexer != null));
            }
            var indexerIds = indexers.GroupBy(indexer => indexer.Name, ProviderNameComparer)
                .ToDictionary(group => group.Key, group => group.Min(indexer => indexer.Id), ProviderNameComparer);
            if (PrepareReleaseProfiles(package, tagIdMap, indexerIds, result).Count == package.ReleaseProfiles.Count)
            {
                foreach (var profile in _releaseProfileService.All())
                {
                    var labels = new[] { TagsLabel(profile.Tags) }
                        .Concat((profile.Required ?? new List<string>()).Select(term => "+" + term))
                        .Concat((profile.Ignored ?? new List<string>()).Select(term => "−" + term));
                    result.WouldRemove.Add($"Release profile: {string.Join(" / ", labels.Where(label => !string.IsNullOrWhiteSpace(label)))} (#{profile.Id})");
                }
            }
        }

        private static IEnumerable<RemotePathMapping> GetRemovedMappings(IEnumerable<RemotePathMapping> current, SettingsBackupRestoreResult result) =>
            current.Where(mapping => !result.SkippedRemotePathMappings.Any(saved =>
                string.Equals(saved.Host, mapping.Host, StringComparison.OrdinalIgnoreCase) && saved.RemotePath == mapping.RemotePath));

        private static IEnumerable<ProxyDefinition> GetRemovedProxies(IEnumerable<ProxyDefinition> current, SettingsBackupPackage package)
        {
            var names = (package.Proxies ?? new()).Select(proxy => proxy.Name?.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.Count == 0 ? Enumerable.Empty<ProxyDefinition>() : current.Where(proxy => !names.Contains(proxy.Name?.Trim()));
        }

        private void PrepareRestorePaths(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categories, SettingsBackupRestoreResult result)
        {
            if (categories.Contains(SettingsBackupCategory.MediaManagement) && package.MediaManagement != null)
            {
                var path = package.MediaManagement.RecycleBin;
                var errors = _mediaPaths.Validate(new MediaManagementPathSettings
                {
                    RecycleBin = ConfigValueValidation.HasChanged(path, _configService.RecycleBin) ? path : null,
                    ChmodFolder = package.MediaManagement.ChmodFolder
                }).Errors;
                if (errors.Any(error => error.PropertyName != nameof(MediaManagementPathSettings.RecycleBin)))
                    throw new NzbDroneClientException(HttpStatusCode.BadRequest, "No settings were restored. " + string.Join("; ", errors.Select(error => $"{error.PropertyName}: {error.ErrorMessage}")));
                if (errors.Count > 0)
                {
                    result.Warnings.Add($"Kept your Recycling Bin setting. Cannot use '{path}': {string.Join("; ", errors.Select(error => error.ErrorMessage))}");
                    package.MediaManagement.RecycleBin = _configService.RecycleBin;
                }
            }
            if (categories.Contains(SettingsBackupCategory.RemotePathMappings))
            {
                foreach (var mapping in package.RemotePathMappings)
                {
                    var issue = !_diskProvider.FolderExists(mapping.LocalPath) ? "folder doesn't exist here" :
                        !_diskProvider.FolderWritable(mapping.LocalPath) ? "folder isn't writable here" : null;
                    if (issue == null) continue;
                    result.SkippedRemotePathMappings.Add(mapping);
                    result.Warnings.Add($"Skipped remote path mapping {mapping.Host}:{mapping.RemotePath} → {mapping.LocalPath}: {issue}.");
                }
            }
        }

        private List<ProviderDefinition> ValidateRestoreProviderNames(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categories, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            void Check(IEnumerable<string> names, string source, StringComparer comparer)
            {
                var duplicates = names.GroupBy(name => name, comparer).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
                if (duplicates.Count > 0)
                {
                    throw new NzbDroneClientException(HttpStatusCode.BadRequest,
                        $"{source} contain duplicate names: {string.Join(", ", duplicates)}.");
                }
            }

            var providers = new List<ProviderDefinition>();
            void CheckProviders(IEnumerable<(string Name, string Implementation)> incoming, IEnumerable<ProviderDefinition> existing, string section)
            {
                var names = incoming.ToList();
                var current = existing.ToList();
                Check(names.Select(item => item.Name), $"Backup {section}", ProviderNameComparer);
                var incomingNames = names.Select(item => item.Name).ToHashSet(ProviderNameComparer);
                Check(current.Where(item => incomingNames.Contains(item.Name)).Select(item => item.Name), $"Existing {section}", ProviderNameComparer);
                if (mode == SettingsBackupRestoreMode.Merge)
                {
                    foreach (var item in names)
                    {
                        var collision = current.SingleOrDefault(value => ProviderNameComparer.Equals(value.Name, item.Name));
                        if (collision != null && collision.Implementation != item.Implementation)
                        {
                            throw new NzbDroneClientException(HttpStatusCode.BadRequest,
                                $"{section} name '{item.Name}' conflicts with existing '{collision.Name}' ({collision.Implementation}). Rename one connection before merging.");
                        }
                    }
                }
                providers.AddRange(current);
            }
            void CheckCategory(SettingsBackupCategory category, Action check)
            {
                if (!categories.Contains(category)) return;
                try { check(); }
                catch (NzbDroneClientException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
                {
                    categories.Remove(category);
                    result.Warnings.Add($"Skipped {CategoryName(category)}: {ex.Message}");
                }
            }
            CheckCategory(SettingsBackupCategory.DownloadClients, () =>
                CheckProviders((package.DownloadClients ?? new()).Select(item => (item.Name, item.Implementation)), _downloadClientFactory.All(), "download clients"));
            CheckCategory(SettingsBackupCategory.Indexers, () =>
                CheckProviders((package.Indexers ?? new()).Select(item => (item.Name, item.Implementation)), _indexerFactory.All(), "indexers"));
            CheckCategory(SettingsBackupCategory.Connections, () =>
                CheckProviders((package.Connections ?? new()).Select(item => (item.Name, item.Implementation)), _notificationFactory.All(), "connections"));
            CheckCategory(SettingsBackupCategory.Proxies, () =>
            {
                var names = (package.Proxies ?? new()).Select(proxy => proxy.Name?.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Check((package.Proxies ?? new()).Select(proxy => proxy.Name?.Trim()), "Backup proxies", StringComparer.OrdinalIgnoreCase);
                Check(_proxyService.All().Where(proxy => names.Contains(proxy.Name?.Trim())).Select(proxy => proxy.Name?.Trim()), "Existing proxies", StringComparer.OrdinalIgnoreCase);
            });
            return providers;
        }

        private static T ApplyRestoreMode<T>(T saved, T current, SettingsBackupRestoreMode mode) where T : class =>
            mode == SettingsBackupRestoreMode.Merge && saved != null ? (T)FillBlanks(saved, current) : saved;

        private static object FillBlanks(object saved, object current)
        {
            if (current == null || current is string text && string.IsNullOrWhiteSpace(text) ||
                current is IEnumerable values && current is not string && !values.Cast<object>().Any())
                return saved;
            if (saved == null || current.GetType() != saved.GetType() || current is string ||
                current.GetType().IsValueType || current is IEnumerable)
                return current;

            foreach (var property in saved.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0))
            {
                property.SetValue(saved, FillBlanks(property.GetValue(saved), property.GetValue(current)));
            }
            return saved;
        }

        private void MergeGeneralSettings(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categories)
        {
            void MergeConfig(object settings, string prefix = "")
            {
                if (settings == null) return;
                foreach (var property in settings.GetType().GetProperties().Where(property => property.CanRead && property.CanWrite))
                {
                    // Provider IDs are remapped before their settings are merged.
                    if (property.Name == nameof(ProxySettingsBackup.GlobalProxyId)) continue;
                    var source = typeof(IConfigService).GetProperty(prefix + property.Name);
                    if (source != null)
                        property.SetValue(settings, FillBlanks(property.GetValue(settings), source.GetValue(_configService)));
                }
            }
            if (categories.Contains(SettingsBackupCategory.Indexers)) MergeConfig(package.IndexerSettings);
            if (categories.Contains(SettingsBackupCategory.DownloadClients)) MergeConfig(package.DownloadClientSettings);
            if (categories.Contains(SettingsBackupCategory.UiSettings))
            {
                MergeConfig(package.UiSettings);
                if (package.UiSettings != null)
                    package.UiSettings.Theme = (string)FillBlanks(package.UiSettings.Theme, _configFileProvider.Theme);
            }
            if (categories.Contains(SettingsBackupCategory.MediaManagement))
            {
                MergeConfig(package.MediaManagement);
                if (package.MediaManagement?.NamingConfig != null)
                    package.MediaManagement.NamingConfig = ApplyRestoreMode(package.MediaManagement.NamingConfig, _namingConfigService.GetConfig(), SettingsBackupRestoreMode.Merge);
            }
            if (categories.Contains(SettingsBackupCategory.MetadataServer))
            {
                package.MetadataServerUrl = (string)FillBlanks(package.MetadataServerUrl, _configService.MetadataServerUrl);
                package.MetadataSource = (string)FillBlanks(package.MetadataSource, _configService.MetadataSource);
            }
            if (categories.Contains(SettingsBackupCategory.Hardcover)) MergeConfig(package.Hardcover, "Hardcover");
            if (categories.Contains(SettingsBackupCategory.Proxies)) MergeConfig(package.ProxySettings);
        }

        private static string CategoryName(SettingsBackupCategory category) => category switch
        {
            SettingsBackupCategory.DownloadClients => "Download Clients",
            SettingsBackupCategory.MediaManagement => "Media Management",
            SettingsBackupCategory.MetadataServer => "Metadata Server",
            SettingsBackupCategory.RemotePathMappings => "Remote Path Mappings",
            SettingsBackupCategory.UiSettings => "Interface Preferences",
            SettingsBackupCategory.Proxies => "Proxy Settings",
            _ => category.ToString()
        };

        internal static List<string> ValidateRestore(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categories, IFilenameValidationService filenameValidation, Func<ProviderDefinition, ProviderDefinition> prepareProvider = null)
        {
            ArgumentNullException.ThrowIfNull(filenameValidation);
            var errors = new List<string>();
            var warnings = new List<string>();
            void Check(string section, object settings)
            {
                errors.AddRange(ConfigValueValidation.Validate(settings).Select(error => $"{section}.{error.PropertyName}: {error.ErrorMessage}"));
            }
            void Provider(string section, string name, string contract, Func<ProviderDefinition> restore)
            {
                if (ProviderConfigTypeCache.Find(contract) == null)
                {
                    warnings.Add($"Skipped {section.ToLowerInvariant()} '{name}': provider type '{contract}' is not supported by this version.");
                    return;
                }
                var definition = restore();
                if (definition?.Settings == null)
                {
                    errors.Add($"{section} '{name}': unsupported provider settings.");
                    return;
                }
                definition = prepareProvider?.Invoke(definition) ?? definition;
                if (definition.Enable)
                {
                    errors.AddRange(definition.Settings.Validate().Errors.Select(error => $"{section} '{name}'.{error.PropertyName}: {error.ErrorMessage}"));
                }
            }
            if (categories.Contains(SettingsBackupCategory.MetadataServer) && package.MetadataServerUrl.IsNotNullOrWhiteSpace() && !package.MetadataServerUrl.IsValidHttpUrl())
            {
                errors.Add("Metadata server URL: enter a valid absolute URL.");
            }
            if (categories.Contains(SettingsBackupCategory.UiSettings))
            {
                Check("Interface preferences", package.UiSettings);
            }
            if (categories.Contains(SettingsBackupCategory.MediaManagement))
            {
                NormalizeLegacyConversionSettings(package.MediaManagement);
                Check("Media management", package.MediaManagement);
                if (package.MediaManagement?.NamingConfig != null)
                {
                    errors.AddRange(filenameValidation.ValidateNamingConfig(package.MediaManagement.NamingConfig).Select(error => $"Naming.{error.PropertyName}: {error.ErrorMessage}"));
                }
            }
            if (categories.Contains(SettingsBackupCategory.Indexers))
            {
                Check("Indexers", package.IndexerSettings);
                foreach (var item in package.Indexers)
                {
                    Provider("Indexer", item.Name, item.ConfigContract, () => FromBackup(item, null, null, null));
                }
            }
            if (categories.Contains(SettingsBackupCategory.DownloadClients))
            {
                Check("Download clients", package.DownloadClientSettings);
                foreach (var item in package.DownloadClients)
                {
                    Provider("Download client", item.Name, item.ConfigContract, () => FromBackup(item, null));
                }
            }
            if (categories.Contains(SettingsBackupCategory.Connections))
            {
                foreach (var item in package.Connections)
                {
                    Provider("Connection", item.Name, item.ConfigContract, () => FromBackup(item, null));
                }
            }
            if (categories.Contains(SettingsBackupCategory.Profiles))
            {
                foreach (var format in package.CustomFormats ?? new())
                {
                    var converted = FromBackup(format, new SettingsBackupRestoreResult());
                    errors.AddRange(converted.Specifications.SelectMany(specification => specification.Validate().Errors)
                        .Select(error => $"Custom format '{format.Name}'.{error.PropertyName}: {error.ErrorMessage}"));
                }
                foreach (var profile in package.QualityProfiles)
                {
                    errors.AddRange(new QualityProfileValidator().Validate(CloneQualityProfile(profile, null)).Errors.Select(error => $"Quality profile '{profile.Name}'.{error.PropertyName}: {error.ErrorMessage}"));
                }
                foreach (var profile in package.DelayProfiles ?? new())
                {
                    errors.AddRange(ConfigValueValidation.ValidateDelayProfile(profile).Select(error => $"Delay profile {profile.Id}.{error.PropertyName}: {error.ErrorMessage}"));
                }
                foreach (var profile in package.ReleaseProfiles ?? new())
                {
                    errors.AddRange(ConfigValueValidation.ValidateReleaseProfile(profile).Select(error => $"Release profile {profile.Id}.{error.PropertyName}: {error.ErrorMessage}"));
                }
            }
            if (categories.Contains(SettingsBackupCategory.Profiles))
            {
                foreach (var profile in package.MetadataProfiles.Where(profile => !string.Equals(profile.Name, "None", StringComparison.OrdinalIgnoreCase)))
                {
                    errors.AddRange(ConfigValueValidation.ValidateMetadataProfile(profile).Select(error => $"Metadata profile '{profile.Name}'.{error.PropertyName}: {error.ErrorMessage}"));
                }
            }
            if (categories.Contains(SettingsBackupCategory.Proxies))
            {
                Check("Proxy settings", package.ProxySettings);
                foreach (var proxy in package.Proxies)
                {
                    errors.AddRange(ConfigValueValidation.ValidateProxy(new ProxyDefinition { Name = proxy.Name, Hostname = proxy.Hostname, Port = proxy.Port, ProxyType = proxy.ProxyType })
                        .Select(error => $"Proxy '{proxy.Name}'.{error.PropertyName}: {error.ErrorMessage}"));
                }
            }
            if (categories.Contains(SettingsBackupCategory.RemotePathMappings))
            {
                foreach (var mapping in package.RemotePathMappings)
                {
                    try
                    {
                        RemotePathMappingService.ValidateMapping(new(), new RemotePathMapping
                        {
                            DownloadClientId = mapping.DownloadClientId, Host = mapping.Host,
                            RemotePath = mapping.RemotePath, LocalPath = mapping.LocalPath
                        }, null, requireLocalPathExists: false, checkDuplicates: false);
                    }
                    catch (FluentValidation.ValidationException ex)
                    {
                        errors.AddRange(ex.Errors.Select(error => $"Remote path mapping '{mapping.Host}: {mapping.RemotePath}'.{error.PropertyName}: {error.ErrorMessage}"));
                    }
                }
            }
            if (errors.Count > 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "No settings were restored. Correct these backup values: " + string.Join("; ", errors));
            }
            return warnings;
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

            var indexers = categories.Contains(SettingsBackupCategory.Indexers) || categories.Contains(SettingsBackupCategory.Profiles)
                ? _indexerFactory.All() : new List<IndexerDefinition>();
            // Always include tags when any selected category can reference tags.
            if (categories.Contains(SettingsBackupCategory.Indexers) ||
                categories.Contains(SettingsBackupCategory.DownloadClients) ||
                categories.Contains(SettingsBackupCategory.Connections) ||
                categories.Contains(SettingsBackupCategory.Profiles))
            {
                package.Tags = _tagService.All()
                    .Select(t => new TagBackup { Id = t.Id, Label = t.Label })
                    .ToList();

                package.Counts["tags"] = package.Tags.Count;
            }

            if (categories.Contains(SettingsBackupCategory.Indexers))
            {
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
                package.DelayProfiles = _delayProfileService.All();
                package.ReleaseProfiles = _releaseProfileService.All();
                package.QualityDefinitions = _qualityDefinitionService.All();
                package.IndexerNames = indexers.ToDictionary(indexer => indexer.Id, indexer => indexer.Name);
                package.Counts["delayProfiles"] = package.DelayProfiles.Count;
                package.Counts["releaseProfiles"] = package.ReleaseProfiles.Count;
                package.Counts["qualityDefinitions"] = package.QualityDefinitions.Count;

                package.Counts["customFormats"] = package.CustomFormats.Count;
                package.Counts["qualityProfiles"] = package.QualityProfiles.Count;
                package.Counts["metadataProfiles"] = package.MetadataProfiles.Count;
            }

            if (categories.Contains(SettingsBackupCategory.MediaManagement))
            {
                package.MediaManagement = new MediaManagementBackup
                {
                    AutoUnmonitorPreviouslyDownloadedBooks = _configService.AutoUnmonitorPreviouslyDownloadedBooks,
                    WriteAudioTags = _configService.WriteAudioTags,
                    ScrubAudioTags = _configService.ScrubAudioTags,
                    WriteBookTags = _configService.WriteBookTags,
                    UpdateCovers = _configService.UpdateCovers,
                    EmbedMetadata = _configService.EmbedMetadata,
                    CoverDownloadScope = _configService.CoverDownloadScope,
                    BookMatchingStrictness = _configService.BookMatchingStrictness,
                    UsePathAsTagsFallback = _configService.UsePathAsTagsFallback,
                    AutoAddMissingAuthorsFromCompletedDownloads = _configService.AutoAddMissingAuthorsFromCompletedDownloads,
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

            if (categories.Contains(SettingsBackupCategory.Indexers))
            {
                package.IndexerSettings = new IndexerSettingsBackup
                {
                    Retention = _configService.Retention,
                    RssSyncInterval = _configService.RssSyncInterval,
                    MaximumSize = _configService.MaximumSize,
                    MinimumAge = _configService.MinimumAge
                };
            }

            if (categories.Contains(SettingsBackupCategory.DownloadClients))
            {
                package.DownloadClientSettings = new DownloadClientSettingsBackup
                {
                    DownloadClientWorkingFolders = _configService.DownloadClientWorkingFolders,
                    DownloadClientHistoryLimit = _configService.DownloadClientHistoryLimit,
                    EnableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling,
                    AutoRedownloadFailed = _configService.AutoRedownloadFailed,
                    AutoRedownloadFailedFromInteractiveSearch = _configService.AutoRedownloadFailedFromInteractiveSearch
                };
            }

            if (categories.Contains(SettingsBackupCategory.UiSettings))
            {
                package.UiSettings = new UiSettingsBackup
                {
                    FirstDayOfWeek = _configService.FirstDayOfWeek,
                    CalendarWeekColumnHeader = _configService.CalendarWeekColumnHeader,
                    ShortDateFormat = _configService.ShortDateFormat,
                    LongDateFormat = _configService.LongDateFormat,
                    TimeFormat = _configService.TimeFormat,
                    ShowRelativeDates = _configService.ShowRelativeDates,
                    EnableColorImpairedMode = _configService.EnableColorImpairedMode,
                    UILanguage = _configService.UILanguage,
                    Theme = _configFileProvider.Theme,
                    AddNewDefaultMediaType = _configService.AddNewDefaultMediaType
                };

                package.Counts["uiSettings"] = 1;
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
                IncludeHealthWarnings = definition.IncludeHealthWarnings,
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
                ConfirmAutomaticUpgrades = profile.ConfirmAutomaticUpgrades,
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
                !categoriesToApply.Contains(SettingsBackupCategory.Connections) &&
                !categoriesToApply.Contains(SettingsBackupCategory.Profiles))
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

        private Dictionary<int, int> RestoreCustomFormats(SettingsBackupPackage package, HashSet<SettingsBackupCategory> categoriesToApply, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result, ISet<int> insertedFormatIds = null)
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
                    restored = ApplyRestoreMode(restored, existing, mode);
                    restored.Id = existing.Id;
                    _customFormatService.Update(restored);
                    map[format.EffectiveOriginalId] = existing.Id;
                    continue;
                }

                var inserted = _customFormatService.Insert(restored);
                insertedFormatIds?.Add(inserted.Id);
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

        private static List<(DelayProfile Profile, HashSet<int> Tags)> PrepareDelayProfiles(
            SettingsBackupPackage package, Dictionary<int, int> tagIdMap, SettingsBackupRestoreResult result)
        {
            var ready = new List<(DelayProfile Profile, HashSet<int> Tags)>();
            foreach (var saved in package.DelayProfiles.OrderBy(profile => profile.Order))
            {
                var tags = saved.Tags.Select(tag => tagIdMap.GetValueOrDefault(tag)).Where(tag => tag > 0).ToHashSet();
                if (tags.Count != saved.Tags.Count || (saved.Id != 1 && tags.Count == 0))
                {
                    result.Warnings.Add($"Skipped delay profile {saved.Id}: one or more tags were not restored.");
                    continue;
                }
                ready.Add((saved, tags));
            }

            return ready;
        }

        private static bool ReplacesAllDelayProfiles(SettingsBackupPackage package, List<(DelayProfile Profile, HashSet<int> Tags)> ready) =>
            ready.Count == package.DelayProfiles.Count && !ready.Any(left =>
                ready.Any(right => left.Tags.Overlaps(right.Tags) && !left.Tags.SetEquals(right.Tags)));

        private void RestoreDelayProfiles(SettingsBackupPackage package, Dictionary<int, int> tagIdMap, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            if (package.DelayProfiles == null) return;
            var current = _delayProfileService.All();
            var ready = PrepareDelayProfiles(package, tagIdMap, result);

            // Backup IDs belong to another installation. Unresolved references cannot identify a safe deletion target.
            var replaceAll = mode == SettingsBackupRestoreMode.Overwrite && ReplacesAllDelayProfiles(package, ready);
            if (replaceAll)
            {
                _delayProfileService.Delete(current.Where(profile => profile.Id != 1).Select(profile => profile.Id).ToList());
                current.RemoveAll(profile => profile.Id != 1);
            }
            else if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                result.Warnings.Add($"Kept existing delay profiles ({string.Join(", ", current.Select(profile => profile.Id))}): the backup contains unresolved or overlapping tags. Valid profiles update matching entries or are appended; existing priority order is kept.");
            }

            foreach (var entry in ready)
            {
                var saved = entry.Profile;
                saved.Tags = entry.Tags;
                var existing = current.FirstOrDefault(profile => profile.Tags.SetEquals(saved.Tags));
                if (existing == null && current.Any(profile => profile.Tags.Overlaps(saved.Tags)))
                {
                    var labels = package.Tags.Where(tag => saved.Tags.Contains(tagIdMap.GetValueOrDefault(tag.Id))).Select(tag => tag.Label);
                    result.Warnings.Add($"Skipped delay profile {saved.Id} for tags {string.Join(", ", labels)}: its tags overlap an existing delay profile.");
                    continue;
                }
                if (existing != null)
                {
                    ApplyRestoreMode(saved, existing, mode);
                    saved.Id = existing.Id;
                    if (!replaceAll) saved.Order = existing.Order;
                    _delayProfileService.Update(saved);
                    current.Remove(existing);
                    current.Add(saved);
                }
                else
                {
                    saved.Id = 0;
                    var restored = _delayProfileService.Add(saved, replaceAll ? saved.Order : current.Where(profile => profile.Id != 1).Select(profile => profile.Order).DefaultIfEmpty(-1).Max() + 1);
                    current.Add(restored);
                }
            }
            if (mode == SettingsBackupRestoreMode.Merge && package.DelayProfiles.Count > 0)
            {
                result.Applied.Add("Delay profiles: existing priority order kept; new profiles appended in backup order");
            }
        }

        private static List<(ReleaseProfile Profile, HashSet<int> Tags, int IndexerId)> PrepareReleaseProfiles(
            SettingsBackupPackage package, Dictionary<int, int> tagIdMap, Dictionary<string, int> indexers, SettingsBackupRestoreResult result)
        {
            var ready = new List<(ReleaseProfile Profile, HashSet<int> Tags, int IndexerId)>();
            foreach (var saved in package.ReleaseProfiles)
            {
                var tags = saved.Tags.Select(tag => tagIdMap.GetValueOrDefault(tag)).Where(tag => tag > 0).ToHashSet();
                var indexerId = 0;
                if (tags.Count != saved.Tags.Count || (saved.IndexerId != 0 &&
                    (!package.IndexerNames.TryGetValue(saved.IndexerId, out var name) || !indexers.TryGetValue(name, out indexerId))))
                {
                    result.Warnings.Add($"Skipped release profile {saved.Id}: its tags or indexer '{package.IndexerNames.GetValueOrDefault(saved.IndexerId)}' were not restored.");
                    continue;
                }
                ready.Add((saved, tags, indexerId));
            }
            return ready;
        }

        private void RestoreReleaseProfiles(SettingsBackupPackage package, Dictionary<int, int> tagIdMap, SettingsBackupRestoreMode mode, SettingsBackupRestoreResult result)
        {
            if (package.ReleaseProfiles == null) return;
            var current = _releaseProfileService.All();
            var indexers = _indexerFactory.All().GroupBy(indexer => indexer.Name, ProviderNameComparer)
                .ToDictionary(group => group.Key, group => group.Min(indexer => indexer.Id), ProviderNameComparer);
            var ready = PrepareReleaseProfiles(package, tagIdMap, indexers, result);
            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                if (ready.Count == package.ReleaseProfiles.Count)
                {
                    _releaseProfileService.Delete(current.Select(profile => profile.Id).ToList());
                    current.Clear();
                }
                else if (current.Count > 0)
                {
                    result.Warnings.Add($"Kept existing release profiles ({string.Join(", ", current.Select(profile => profile.Id))}): some backup profiles could not be restored. Valid profiles update matching entries or are added.");
                }
            }
            foreach (var entry in ready)
            {
                var saved = entry.Profile;
                saved.Tags = entry.Tags;
                saved.IndexerId = entry.IndexerId;
                var existing = current.FirstOrDefault(profile => profile.IndexerId == saved.IndexerId && profile.Tags.SetEquals(saved.Tags) &&
                    profile.Required.ToHashSet(StringComparer.InvariantCultureIgnoreCase).SetEquals(saved.Required) && profile.Ignored.ToHashSet(StringComparer.InvariantCultureIgnoreCase).SetEquals(saved.Ignored));
                saved.Id = existing?.Id ?? 0;
                if (existing != null)
                {
                    ApplyRestoreMode(saved, existing, mode);
                    _releaseProfileService.Update(saved);
                    current.Remove(existing);
                    current.Add(saved);
                }
                else
                {
                    current.Add(_releaseProfileService.Add(saved));
                }
            }
        }

        private void RestoreQualitySizes(SettingsBackupPackage package, SettingsBackupRestoreResult result, SettingsBackupRestoreMode mode = SettingsBackupRestoreMode.Overwrite)
        {
            if (package.QualityDefinitions != null)
            {
                var current = _qualityDefinitionService.All().ToDictionary(definition => definition.Quality);
                var updates = new List<QualityDefinition>();
                foreach (var saved in package.QualityDefinitions)
                {
                    if (!current.TryGetValue(saved.Quality, out var definition))
                    {
                        result.Warnings.Add($"Skipped unknown quality '{saved.Quality}' from the backup.");
                        continue;
                    }
                    ApplyRestoreMode(saved, definition, mode);
                    saved.Id = definition.Id;
                    updates.Add(saved);
                }
                _qualityDefinitionService.UpdateMany(updates);
            }
        }

        private void RestoreMetadataProfiles(SettingsBackupPackage package, SettingsBackupRestoreResult result, SettingsBackupRestoreMode mode = SettingsBackupRestoreMode.Overwrite)
        {
            var metadataProfiles = _metadataProfileService.All();
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

                if (string.Equals(restored.Name, MetadataProfileService.NONE_PROFILE_NAME, StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add("Skipping built-in metadata profile 'None' (cannot be modified)");
                    continue;
                }

                var existing = metadataProfiles.FirstOrDefault(p => p.Name == restored.Name && p.ProfileType == restored.ProfileType);
                if (existing != null)
                {
                    restored = ApplyRestoreMode(restored, existing, mode);
                    restored.Id = existing.Id;
                    _metadataProfileService.Update(restored);
                    continue;
                }

                metadataProfiles.Add(_metadataProfileService.Add(restored));
            }

        }

        private void RestoreQualityProfiles(SettingsBackupPackage package, Dictionary<int, int> customFormatIdMap, SettingsBackupRestoreMode mode = SettingsBackupRestoreMode.Overwrite, ISet<int> insertedFormatIds = null)
        {
            var qualityProfiles = _qualityProfileService.All();
            var customFormats = _customFormatService.All();
            foreach (var profile in package.QualityProfiles ?? new List<QualityProfileBackup>())
            {
                if (profile == null || profile.Name.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var restored = CloneQualityProfile(profile, customFormatIdMap);
                QualityProfileService.ReconcileCustomFormatMembership(restored, customFormats);

                var existing = qualityProfiles.FirstOrDefault(p => p.Name == restored.Name && p.ProfileType == restored.ProfileType);
                if (existing != null)
                {
                    var newScores = restored.FormatItems
                        .Where(item => insertedFormatIds?.Contains(item.Format.Id) == true)
                        .ToDictionary(item => item.Format.Id, item => item.Score);
                    restored = ApplyRestoreMode(restored, existing, mode);
                    restored.Id = existing.Id;
                    QualityProfileService.ReconcileCustomFormatMembership(restored, customFormats);
                    foreach (var item in restored.FormatItems)
                    {
                        if (newScores.TryGetValue(item.Format.Id, out var score)) item.Score = score;
                    }
                    _qualityProfileService.Update(restored);
                    continue;
                }

                restored.Id = 0;
                qualityProfiles.Add(_qualityProfileService.Add(restored));
            }

        }

        private void RestoreIndexerSettings(SettingsBackupPackage package, SettingsBackupRestoreResult result)
        {
            var settings = package.IndexerSettings;
            if (settings == null)
            {
                return;
            }
            _configService.SaveConfigDictionary(new Dictionary<string, object>
            {
                [nameof(IConfigService.Retention)] = settings.Retention,
                [nameof(IConfigService.RssSyncInterval)] = settings.RssSyncInterval,
                [nameof(IConfigService.MaximumSize)] = settings.MaximumSize,
                [nameof(IConfigService.MinimumAge)] = settings.MinimumAge
            });
        }

        private void RestoreDownloadClientSettings(SettingsBackupPackage package, SettingsBackupRestoreResult result)
        {
            var settings = package.DownloadClientSettings;
            if (settings == null)
            {
                return;
            }
            _configService.SaveConfigDictionary(new Dictionary<string, object>
            {
                [nameof(IConfigService.DownloadClientWorkingFolders)] = settings.DownloadClientWorkingFolders,
                [nameof(IConfigService.DownloadClientHistoryLimit)] = settings.DownloadClientHistoryLimit,
                [nameof(IConfigService.EnableCompletedDownloadHandling)] = settings.EnableCompletedDownloadHandling,
                [nameof(IConfigService.AutoRedownloadFailed)] = settings.AutoRedownloadFailed,
                [nameof(IConfigService.AutoRedownloadFailedFromInteractiveSearch)] = settings.AutoRedownloadFailedFromInteractiveSearch
            });
        }

        private void RestoreUiSettings(SettingsBackupPackage package, SettingsBackupRestoreResult result)
        {
            var settings = package.UiSettings;
            if (settings == null)
            {
                result.Warnings.Add("No interface preferences present in backup");
                return;
            }

            // Older backups omit the theme; preserve the destination's choice in that case.
            if (!string.IsNullOrWhiteSpace(settings.Theme))
            {
                _configFileProvider.SaveConfigDictionary(new Dictionary<string, object>
                {
                    [nameof(IConfigFileProvider.Theme)] = settings.Theme
                });
            }

            _configService.SaveConfigDictionary(new Dictionary<string, object>
            {
                [nameof(IConfigService.FirstDayOfWeek)] = settings.FirstDayOfWeek,
                [nameof(IConfigService.CalendarWeekColumnHeader)] = settings.CalendarWeekColumnHeader,
                [nameof(IConfigService.ShortDateFormat)] = settings.ShortDateFormat,
                [nameof(IConfigService.LongDateFormat)] = settings.LongDateFormat,
                [nameof(IConfigService.TimeFormat)] = settings.TimeFormat,
                [nameof(IConfigService.ShowRelativeDates)] = settings.ShowRelativeDates,
                [nameof(IConfigService.EnableColorImpairedMode)] = settings.EnableColorImpairedMode,
                [nameof(IConfigService.UILanguage)] = settings.UILanguage,
                [nameof(IConfigService.AddNewDefaultMediaType)] = settings.AddNewDefaultMediaType
            });
        }

        private static void NormalizeLegacyConversionSettings(MediaManagementBackup settings)
        {
            if (settings == null) return;
            if (settings.AudiobookConversionConcurrentConversions == 0)
                settings.AudiobookConversionConcurrentConversions = settings.AudiobookConversionConcurrentDownloads > 0 ? settings.AudiobookConversionConcurrentDownloads : 1;
            if (settings.AudiobookConversionMaxCpuThreads == 0)
                settings.AudiobookConversionMaxCpuThreads = Math.Max(settings.AudiobookConversionConcurrentConversions, settings.AudiobookConversionThreads > 0 ? settings.AudiobookConversionThreads : 4);
            if (settings.AudiobookConversionMaxBitrate == 0) settings.AudiobookConversionMaxBitrate = 64;
            if (string.IsNullOrWhiteSpace(settings.AudiobookConversionAudioChannels)) settings.AudiobookConversionAudioChannels = "source";
            if (string.IsNullOrWhiteSpace(settings.AudiobookConversionTagMode)) settings.AudiobookConversionTagMode = ConversionTagModes.Preserve;
            if (string.IsNullOrWhiteSpace(settings.EbookConversionTargetFormat)) settings.EbookConversionTargetFormat = "epub";
        }

        private void RestoreMediaManagement(SettingsBackupPackage package, SettingsBackupRestoreResult result)
        {
            if (package.MediaManagement == null)
            {
                result.Warnings.Add("No media management settings present in backup");
                return;
            }

            var mm = package.MediaManagement;
            NormalizeLegacyConversionSettings(mm);
            mm.AudiobookConversionTagMode = ConversionTagModes.Normalize(mm.AudiobookConversionTagMode);
            var settings = mm.GetType().GetProperties()
                .ToDictionary(property => property.Name, property =>
                    property.GetValue(mm) ?? (property.PropertyType == typeof(string) ? string.Empty : null));

            if (mm.NamingConfig != null)
            {
                mm.NamingConfig.Id = _namingConfigService.GetConfig().Id;
                _namingConfigService.Save(mm.NamingConfig);
            }
            else
            {
                result.Warnings.Add("Naming config was missing from media management backup");
            }

            _configService.SaveConfigDictionary(settings);
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

            var existingProxies = _proxyService.All();
            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                foreach (var proxy in GetRemovedProxies(existingProxies, package))
                {
                    try
                    {
                        _proxyService.Delete(proxy.Id);
                        result.Warnings.Add($"Deleted proxy '{proxy.Name}' because it is absent from the backup; its proxy assignments were cleared.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Settings restore: failed to delete proxy (ID: {0})", proxy.Id);
                        throw;
                    }
                }
            }

            var existingByName = existingProxies
                .Where(p => p?.Name.IsNullOrWhiteSpace() == false)
                .ToLookup(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var backup in proxies)
            {
                if (backup == null || backup.Name.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var restored = new ProxyDefinition
                {
                    Name = backup.Name.Trim(),
                    ProxyType = backup.ProxyType,
                    Hostname = backup.Hostname ?? string.Empty,
                    Port = backup.Port,
                    Username = backup.Username ?? string.Empty,
                    Password = backup.Password ?? string.Empty,
                    BypassLocalAddresses = backup.BypassLocalAddresses,
                    BypassFilter = backup.BypassFilter ?? string.Empty
                };

                var existing = existingByName[restored.Name].SingleOrDefault();
                if (existing != null)
                {
                    restored = ApplyRestoreMode(restored, existing, mode);
                    restored.Id = existing.Id;
                    _proxyService.Update(restored);
                    map[backup.OriginalId] = existing.Id;
                    continue;
                }

                restored.Id = 0;
                var created = _proxyService.Add(restored);
                map[backup.OriginalId] = created.Id;
            }

            return map;
        }

        private void RestoreProxySettings(SettingsBackupPackage package, Dictionary<int, int> proxyIdMap, HashSet<SettingsBackupCategory> categoriesToApply, SettingsBackupRestoreResult result, SettingsBackupRestoreMode mode = SettingsBackupRestoreMode.Overwrite)
        {
            var proxySettings = package.ProxySettings;
            if (proxySettings != null)
            {
                var mappedGlobalProxyId = mode == SettingsBackupRestoreMode.Merge ? _configService.GlobalProxyId ?? MapProxyId(proxySettings.GlobalProxyId, proxyIdMap) : MapProxyId(proxySettings.GlobalProxyId, proxyIdMap);

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

            if (mode == SettingsBackupRestoreMode.Overwrite)
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

            var existingClients = _downloadClientFactory.All();
            var backups = package.DownloadClients ?? new List<DownloadClientDefinitionBackup>();
            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                var removed = GetRemovedClients(existingClients, package).ToList();
                if (removed.Count > 0)
                {
                    _downloadClientFactory.Delete(removed.Select(client => client.Id).ToList());
                    foreach (var client in removed)
                    {
                        result.Warnings.Add($"Deleted download client '{client.Name}' because it is absent from the backup; its client-specific remote path mappings were removed.");
                    }
                }
            }

            foreach (var backup in backups)
            {
                var restored = FromBackup(backup, tagIdMap);
                if (restored == null)
                {
                    continue;
                }

                var existing = existingClients.SingleOrDefault(client => SameDownloadClient(client, backup));
                if (existing != null)
                {
                    restored = ApplyRestoreMode(restored, existing, mode);
                    restored.Id = existing.Id;
                    _downloadClientFactory.Update(restored);
                    map[backup.OriginalId] = existing.Id;
                    continue;
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
                foreach (var mapping in GetRemovedMappings(_remotePathMappingService.All(), result))
                {
                    _remotePathMappingService.Remove(mapping.Id);
                }
            }

            var clientsByName = _downloadClientFactory.All().Where(client => client.Name.IsNotNullOrWhiteSpace()).ToLookup(client => client.Name, ProviderNameComparer);
            var existingLookup = mode == SettingsBackupRestoreMode.Merge
                ? _remotePathMappingService.All()
                : new List<RemotePathMapping>();

            foreach (var backup in backups)
            {
                if (result.SkippedRemotePathMappings.Contains(backup) || backup == null || backup.Host.IsNullOrWhiteSpace() ||
                    backup.RemotePath.IsNullOrWhiteSpace() || backup.LocalPath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var downloadClientId = ResolveRemotePathMappingDownloadClientId(backup, downloadClientIdMap, clientsByName);
                if (!downloadClientId.HasValue)
                {
                    var clientLabel = backup.DownloadClientName.IsNotNullOrWhiteSpace()
                        ? $"'{backup.DownloadClientName}'"
                        : $"with source id {backup.DownloadClientId}";
                    result.Warnings.Add($"Skipped remote path mapping '{backup.RemotePath}' because download client {clientLabel} could not be resolved on this Chaptarr instance.");
                    continue;
                }

                var resolvedDownloadClientId = downloadClientId.Value;

                if (mode == SettingsBackupRestoreMode.Merge)
                {
                    var existing = existingLookup
                        .FirstOrDefault(m => MappingMatchesBackup(m, backup, resolvedDownloadClientId));

                    if (existing != null)
                    {
                        existing.DownloadClientId = resolvedDownloadClientId;
                        existing.LocalPath = (string)FillBlanks(backup.LocalPath, existing.LocalPath);
                        _remotePathMappingService.Update(existing);
                        continue;
                    }
                }

                _remotePathMappingService.Add(new RemotePathMapping
                {
                    DownloadClientId = resolvedDownloadClientId,
                    Host = backup.Host,
                    RemotePath = backup.RemotePath,
                    LocalPath = backup.LocalPath
                });
            }

            result.Applied.Add("Remote Path Mappings");
        }

        private static int? ResolveRemotePathMappingDownloadClientId(RemotePathMappingBackup backup, Dictionary<int, int> downloadClientIdMap, ILookup<string, DownloadClientDefinition> clientsByName)
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
                var candidates = clientsByName[backup.DownloadClientName].Take(2).ToList();
                var existing = candidates.Count == 1 ? candidates[0] : null;

                if (existing != null)
                {
                    return existing.Id;
                }
            }

            return null;
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

                var candidates = existing.Where(client => SameDownloadClient(client, backup)).Take(2).ToList();
                var existingClient = candidates.Count == 1 ? candidates[0] : null;

                if (existingClient != null)
                {
                    map[backup.OriginalId] = existingClient.Id;
                }
            }

            return map;
        }

        private static bool SameDownloadClient(DownloadClientDefinition client, DownloadClientDefinitionBackup backup) =>
            client != null && backup != null && client.Implementation == backup.Implementation && ProviderNameComparer.Equals(client.Name, backup.Name ?? string.Empty);

        private static IEnumerable<DownloadClientDefinition> GetRemovedClients(IEnumerable<DownloadClientDefinition> current, SettingsBackupPackage package) =>
            current.Where(client => !(package.DownloadClients ?? new()).Any(backup => SameDownloadClient(client, backup)));

        private static IEnumerable<IndexerDefinition> GetRemovedIndexers(IEnumerable<IndexerDefinition> current, SettingsBackupPackage package)
        {
            var names = (package.Indexers ?? new()).Select(indexer => indexer.Name).ToHashSet(ProviderNameComparer);
            return current.Where(indexer => !names.Contains(indexer.Name));
        }

        private static IEnumerable<NotificationDefinition> GetRemovedConnections(IEnumerable<NotificationDefinition> current, SettingsBackupPackage package) =>
            current.Where(connection => !(package.Connections ?? new()).Any(backup =>
                backup.Implementation == connection.Implementation && ProviderNameComparer.Equals(backup.Name, connection.Name)));

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
                .Where(group => group.Count() == 1)
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
            var existingIndexers = _indexerFactory.All();

            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                var references = existingIndexers.Count == 0
                    ? new Dictionary<int, List<int>>()
                    : _releaseProfileService.All().GroupBy(profile => profile.IndexerId)
                        .ToDictionary(group => group.Key, group => group.Select(profile => profile.Id).ToList());
                var removedIndexers = GetRemovedIndexers(existingIndexers, package).ToList();
                var removed = removedIndexers.Select(indexer => indexer.Id).ToHashSet();
                foreach (var indexer in removedIndexers)
                {
                    if (references.TryGetValue(indexer.Id, out var profiles))
                    {
                        result.Warnings.Add($"Deleted indexer '{indexer.Name}': current release profile(s) {string.Join(", ", profiles)} reference it. Any kept by this restore will no longer apply to an indexer.");
                    }
                }
                if (removed.Count > 0)
                {
                    _indexerFactory.Delete(removed.ToList());
                    existingIndexers.RemoveAll(indexer => removed.Contains(indexer.Id));
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

                var existing = existingIndexers.FirstOrDefault(indexer => ProviderNameComparer.Equals(indexer.Name, restored.Name));
                if (existing != null)
                {
                    restored = ApplyRestoreMode(restored, existing, mode);
                    restored.Id = existing.Id;
                    _indexerFactory.Update(restored);
                    continue;
                }

                restored.Id = 0;
                existingIndexers.Add(_indexerFactory.Create(restored));
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
            var existingConnections = _notificationFactory.All();

            if (mode == SettingsBackupRestoreMode.Overwrite)
            {
                var existing = GetRemovedConnections(existingConnections, package).Select(n => n.Id).ToList();
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

                var existing = existingConnections.SingleOrDefault(d => d.Implementation == restored.Implementation && ProviderNameComparer.Equals(d.Name, restored.Name));
                if (existing != null)
                {
                    restored = ApplyRestoreMode(restored, existing, mode);
                    restored.Id = existing.Id;
                    _notificationFactory.Update(restored);
                    continue;
                }

                restored.Id = 0;
                existingConnections.Add(_notificationFactory.Create(restored));
            }

            if (clearedAudioBookShelfLibraryMappings > 0)
            {
                result.Warnings.Add($"Discarded backup AudioBookShelf library mappings for {clearedAudioBookShelfLibraryMappings} connection(s). Existing mappings are kept in Merge. Root folders are not included in settings backups; review new or overwritten AudioBookShelf connections after restore.");
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
                IncludeHealthWarnings = backup.IncludeHealthWarnings ?? new NotificationDefinition().IncludeHealthWarnings,
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
                ConfirmAutomaticUpgrades = profile.ConfirmAutomaticUpgrades,
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
                "/data",
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
