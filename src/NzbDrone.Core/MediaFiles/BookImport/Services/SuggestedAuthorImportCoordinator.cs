using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public sealed class SuggestedAuthorImportConfigRequest
    {
        public string AuthorName { get; set; }
        public IEnumerable<string> FilePaths { get; set; } = Array.Empty<string>();
        public RootFolder FixedRootFolder { get; set; }
        public BookMediaType? ForceMediaType { get; set; }
        public string RequestedBy { get; set; }
        public bool QueueIfUnavailable { get; set; }
        public bool IsManualAddition { get; set; }
        public bool UseConfiguredDefaultRoots { get; set; }
        public bool ResolveRootFromFilePathFirst { get; set; }
        public bool AllowAmbiguousRootFallback { get; set; }
        public bool AllowMissingRootFolder { get; set; }
        public bool AllowMissingMediaSettings { get; set; }
        public bool IncludeRootDefaultTags { get; set; }
        public bool PreserveDiscoveredAuthorFolder { get; set; }
    }

    public static class SuggestedAuthorImportCoordinator
    {
        public static bool TryBuildMonitoringConfig(
            SuggestedAuthorImportConfigRequest request,
            IRootFolderService rootFolderService,
            IConfigService configService,
            IAuthorFolderMatchingService authorFolderMatchingService,
            out MonitoringConfig config,
            out string error)
        {
            config = null;
            error = null;

            if (request == null)
            {
                error = "Suggested author import request was not provided";
                return false;
            }

            var filePaths = request.FilePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(PathEqualityComparer.Instance).ToList()
                            ?? new List<string>();

            var wantsAudiobook = request.ForceMediaType == BookMediaType.Audiobook ||
                                 (!request.ForceMediaType.HasValue && filePaths.Any(IsAudioPath));
            var wantsEbook = request.ForceMediaType == BookMediaType.Ebook ||
                             (!request.ForceMediaType.HasValue && filePaths.Any(IsEbookPath));

            if (!wantsAudiobook && !wantsEbook)
            {
                error = "No supported audio or ebook files were found for the suggested author import";
                return false;
            }

            if (request.FixedRootFolder != null)
            {
                wantsAudiobook &= RootFolderDefaultResolver.IsCompatibleRootFolder(request.FixedRootFolder, FolderType.Audiobook);
                wantsEbook &= RootFolderDefaultResolver.IsCompatibleRootFolder(request.FixedRootFolder, FolderType.Ebook);

                if (!wantsAudiobook && !wantsEbook)
                {
                    error = $"Root folder '{request.FixedRootFolder.Path}' is not compatible with the detected file types";
                    return false;
                }
            }

            config = new MonitoringConfig
            {
                AuthorName = request.AuthorName,
                CreateAudiobook = wantsAudiobook,
                CreateEbook = wantsEbook,
                QueueIfUnavailable = request.QueueIfUnavailable,
                RequestedBy = request.RequestedBy,
                IsManualAddition = request.IsManualAddition
            };

            RootFolder audiobookRoot = null;
            RootFolder ebookRoot = null;

            if (wantsAudiobook &&
                !TryApplyRootSettings(request, rootFolderService, configService, filePaths, FolderType.Audiobook, config, out audiobookRoot, out error))
            {
                config = null;
                return false;
            }

            if (wantsEbook &&
                !TryApplyRootSettings(request, rootFolderService, configService, filePaths, FolderType.Ebook, config, out ebookRoot, out error))
            {
                config = null;
                return false;
            }

            if (!config.CreateAudiobook && !config.CreateEbook)
            {
                config = null;
                error = "No selected root folder has complete quality and metadata profile defaults for the detected media types";
                return false;
            }

            if (request.PreserveDiscoveredAuthorFolder &&
                authorFolderMatchingService != null &&
                filePaths.Count > 0 &&
                !string.IsNullOrWhiteSpace(request.AuthorName))
            {
                var rootFolder = audiobookRoot ?? ebookRoot;
                if (rootFolder != null && !string.IsNullOrWhiteSpace(rootFolder.Path))
                {
                    try
                    {
                        var discoveredAuthorFolder = authorFolderMatchingService.FindAuthorFolderByWalkingUp(
                            filePaths[0],
                            rootFolder.Path,
                            new Author { Name = request.AuthorName });

                        if (!string.IsNullOrWhiteSpace(discoveredAuthorFolder))
                        {
                            config.DiscoveredAuthorFolderPath = discoveredAuthorFolder;
                        }
                    }
                    catch
                    {
                        // Folder preservation is best-effort; root/config resolution already succeeded.
                    }
                }
            }

            return true;
        }

        private static bool TryApplyRootSettings(
            SuggestedAuthorImportConfigRequest request,
            IRootFolderService rootFolderService,
            IConfigService configService,
            List<string> filePaths,
            FolderType mediaType,
            MonitoringConfig config,
            out RootFolder rootFolder,
            out string error)
        {
            rootFolder = null;
            error = null;

            if (!TryResolveRootFolder(request, rootFolderService, configService, filePaths, mediaType, out rootFolder, out error))
            {
                return false;
            }

            if (rootFolder == null)
            {
                return true;
            }

            if (mediaType == FolderType.Audiobook)
            {
                var settings = rootFolder.GetAudiobookSettings();
                if (!RootFolderSettingsResolver.HasRequiredProfiles(settings))
                {
                    if (request.AllowMissingMediaSettings)
                    {
                        config.CreateAudiobook = false;
                        rootFolder = null;
                        return true;
                    }

                    error = $"Root folder '{rootFolder.Path}' is missing complete audiobook quality and metadata profile defaults";
                    return false;
                }

                config.AudiobookRootFolderPath = rootFolder.Path;
                config.AudiobookQualityProfileId = settings.QualityProfileId;
                config.AudiobookMetadataProfileId = settings.MetadataProfileId;
                config.AudiobookMonitorExistingMode = RootFolderSettingsResolver.ResolveInitialMonitorMode(settings.MonitorExistingMode);
                config.AudiobookMonitored = settings.Monitored;
                config.AudiobookMonitorNewItems = settings.MonitorNewItems;
                AddTags(config, rootFolder, settings.Tags, request.IncludeRootDefaultTags);

                return true;
            }

            var ebookSettings = rootFolder.GetEbookSettings();
            if (!RootFolderSettingsResolver.HasRequiredProfiles(ebookSettings))
            {
                if (request.AllowMissingMediaSettings)
                {
                    config.CreateEbook = false;
                    rootFolder = null;
                    return true;
                }

                error = $"Root folder '{rootFolder.Path}' is missing complete ebook quality and metadata profile defaults";
                return false;
            }

            config.EbookRootFolderPath = rootFolder.Path;
            config.EbookQualityProfileId = ebookSettings.QualityProfileId;
            config.EbookMetadataProfileId = ebookSettings.MetadataProfileId;
            config.EbookMonitorExistingMode = RootFolderSettingsResolver.ResolveInitialMonitorMode(ebookSettings.MonitorExistingMode);
            config.EbookMonitored = ebookSettings.Monitored;
            config.EbookMonitorNewItems = ebookSettings.MonitorNewItems;
            AddTags(config, rootFolder, ebookSettings.Tags, request.IncludeRootDefaultTags);

            return true;
        }

        private static bool TryResolveRootFolder(
            SuggestedAuthorImportConfigRequest request,
            IRootFolderService rootFolderService,
            IConfigService configService,
            List<string> filePaths,
            FolderType mediaType,
            out RootFolder rootFolder,
            out string error)
        {
            rootFolder = null;
            error = null;

            if (request.FixedRootFolder != null)
            {
                if (RootFolderDefaultResolver.IsCompatibleRootFolder(request.FixedRootFolder, mediaType))
                {
                    rootFolder = request.FixedRootFolder;
                    return true;
                }

                error = $"Root folder '{request.FixedRootFolder.Path}' is not compatible with {mediaType.ToString().ToLowerInvariant()} imports";
                return false;
            }

            if (request.ResolveRootFromFilePathFirst && rootFolderService != null)
            {
                foreach (var path in filePaths)
                {
                    try
                    {
                        var bestRoot = rootFolderService.GetBestRootFolder(path);
                        if (bestRoot != null && RootFolderDefaultResolver.IsCompatibleRootFolder(bestRoot, mediaType))
                        {
                            rootFolder = bestRoot;
                            return true;
                        }
                    }
                    catch
                    {
                        // Keep root resolution best-effort; the default-root fallback below preserves existing caller behavior.
                    }
                }
            }

            if (rootFolderService == null)
            {
                if (request.AllowMissingRootFolder)
                {
                    return true;
                }

                error = "Root folder service was not provided";
                return false;
            }

            var rootFolders = (rootFolderService.All() ?? Enumerable.Empty<RootFolder>()).ToList();
            var defaultRootPath = GetConfiguredDefaultRootPath(request, configService, mediaType);

            if (!RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(
                    rootFolders,
                    mediaType,
                    defaultRootPath,
                    out rootFolder,
                    out error,
                    request.AllowAmbiguousRootFallback))
            {
                if (request.AllowMissingRootFolder)
                {
                    error = null;
                    return true;
                }

                return false;
            }

            return true;
        }

        private static string GetConfiguredDefaultRootPath(
            SuggestedAuthorImportConfigRequest request,
            IConfigService configService,
            FolderType mediaType)
        {
            if (!request.UseConfiguredDefaultRoots || configService == null)
            {
                return null;
            }

            return mediaType == FolderType.Audiobook
                ? configService.DefaultAudiobookRootFolderPath
                : configService.DefaultEbookRootFolderPath;
        }

        private static bool IsAudioPath(string path)
        {
            var extension = Path.GetExtension(path);
            return !string.IsNullOrWhiteSpace(extension) &&
                   MediaFileExtensions.AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsEbookPath(string path)
        {
            var extension = Path.GetExtension(path);
            return !string.IsNullOrWhiteSpace(extension) &&
                   MediaFileExtensions.TextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static void AddTags(MonitoringConfig config, RootFolder rootFolder, IEnumerable<int> settingTags, bool includeRootDefaultTags)
        {
            void AddTag(int tag)
            {
                config.Tags ??= new HashSet<int>();
                config.Tags.Add(tag);
            }

            if (includeRootDefaultTags && rootFolder.DefaultTags != null)
            {
                foreach (var tag in rootFolder.DefaultTags)
                {
                    AddTag(tag);
                }
            }

            if (settingTags == null)
            {
                return;
            }

            foreach (var tag in settingTags)
            {
                AddTag(tag);
            }
        }
    }
}
