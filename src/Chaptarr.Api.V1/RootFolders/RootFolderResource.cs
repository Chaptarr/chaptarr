using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.REST;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Api.V1.RootFolders
{
    public class MediaTypeSettingsResource
    {
        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
        public bool? Monitored { get; set; }
        public MonitorTypes? MonitorExistingMode { get; set; }
        // Deprecated All/None input retained for clients from before the full
        // one-time monitoring selector was restored.
        public bool? MonitorExistingBooks { get; set; }
        // Deprecated 0/1/2 inputs retained for old clients. They are translated
        // with MonitorFuture into the binary gate, current-book seed, and ongoing policy.
        public int? MonitorExisting { get; set; }
        public bool? MonitorFuture { get; set; }
        public NewItemMonitorTypes? MonitorNewItems { get; set; }
        public bool WriteAudioBookShelfMetadataJson { get; set; }
        public bool WriteAudioBookShelfCover { get; set; }
        public List<int> Tags { get; set; } = new List<int>();
    }

    public class RootFolderResource : RestResource
    {
        public string Name { get; set; }
        public string Path { get; set; }
        // DELETED: DefaultMetadataProfileId, DefaultQualityProfileId, DefaultAudiobookMetadataProfileId, DefaultEbookMetadataProfileId - use media-specific settings
        public HashSet<int> DefaultTags { get; set; }
        public bool IsCalibreLibrary { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string UrlBase { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Library { get; set; }
        public string OutputFormat { get; set; }
        public string OutputProfile { get; set; }
        public bool UseSsl { get; set; }
        public int FolderType { get; set; }
        public bool PlaceEbooksWithAudiobooks { get; set; }
        public bool? DefaultSyncMonitoredAcrossFormats { get; set; }
        public bool IsEffectiveDefaultAudiobook { get; set; }
        public bool IsEffectiveDefaultEbook { get; set; }

        // PER-MEDIA-TYPE SETTINGS
        public MediaTypeSettingsResource Audiobook { get; set; }
        public MediaTypeSettingsResource Ebook { get; set; }

        // INDIVIDUAL MONITORING FIELDS (for root-folder defaults form)
        public bool? AudiobookMonitored { get; set; }
        public MonitorTypes? AudiobookMonitorExistingMode { get; set; }
        public bool? AudiobookMonitorExistingBooks { get; set; }
        public int? AudiobookMonitorExisting { get; set; }
        public bool? AudiobookMonitorFuture { get; set; }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }
        public bool? EbookMonitored { get; set; }
        public MonitorTypes? EbookMonitorExistingMode { get; set; }
        public bool? EbookMonitorExistingBooks { get; set; }
        public int? EbookMonitorExisting { get; set; }
        public bool? EbookMonitorFuture { get; set; }
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }
        
        // INDIVIDUAL PROFILE FIELDS (for frontend form)
        public int? AudiobookQualityProfileId { get; set; }
        public int? AudiobookMetadataProfileId { get; set; }
        public int? EbookQualityProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }

        // INDIVIDUAL AUDIOBOOKSHELF SIDECAR FIELDS (for frontend form)
        public bool? AudiobookWriteAudioBookShelfMetadataJson { get; set; }
        public bool? AudiobookWriteAudioBookShelfCover { get; set; }
        public bool? EbookWriteAudioBookShelfMetadataJson { get; set; }
        public bool? EbookWriteAudioBookShelfCover { get; set; }
        
        // INDIVIDUAL TAG FIELDS (for frontend form)
        public List<int> AudiobookTags { get; set; }
        public List<int> EbookTags { get; set; }

        public bool Accessible { get; set; }
        public long? FreeSpace { get; set; }
        public long? TotalSpace { get; set; }
    }

    public static class RootFolderResourceMapper
    {
        public static RootFolderResource ToResource(this RootFolder model)
        {
            if (model == null)
            {
                return null;
            }

            return new RootFolderResource
            {
                Id = model.Id,

                Name = model.Name,
                Path = model.Path.GetCleanPath(),
                // REMOVED: DefaultMetadataProfileId, DefaultQualityProfileId, DefaultAudiobookMetadataProfileId, DefaultEbookMetadataProfileId
                DefaultTags = model.DefaultTags,
                IsCalibreLibrary = model.IsCalibreLibrary,
                Host = model.CalibreSettings?.Host,
                Port = model.CalibreSettings?.Port ?? 0,
                UrlBase = model.CalibreSettings?.UrlBase,
                Username = model.CalibreSettings?.Username,
                Password = string.Empty,
                Library = model.CalibreSettings?.Library,
                OutputFormat = model.CalibreSettings?.OutputFormat,
                OutputProfile = ((CalibreProfile)(model.CalibreSettings?.OutputProfile ?? 0)).ToString(),
                UseSsl = model.CalibreSettings?.UseSsl ?? false,
                FolderType = (int)model.FolderType,
                PlaceEbooksWithAudiobooks = model.PlaceEbooksWithAudiobooks,
                DefaultSyncMonitoredAcrossFormats = model.DefaultSyncMonitoredAcrossFormats,

                // PER-MEDIA-TYPE SETTINGS
                Audiobook = ToMediaTypeSettingsResource(model.GetAudiobookSettings()),
                Ebook = ToMediaTypeSettingsResource(model.GetEbookSettings()),

                // INDIVIDUAL MONITORING FIELDS (for root-folder defaults form)
                AudiobookMonitored = model.GetAudiobookSettings()?.Monitored,
                AudiobookMonitorExistingMode = model.GetAudiobookSettings()?.MonitorExistingMode,
                AudiobookMonitorExistingBooks = model.GetAudiobookSettings()?.MonitorExistingBooks,
                AudiobookMonitorExisting = model.GetAudiobookSettings()?.MonitorExisting,
                AudiobookMonitorFuture = ToLegacyMonitorFuture(model.GetAudiobookSettings()),
                AudiobookMonitorNewItems = model.GetAudiobookSettings()?.MonitorNewItems,
                EbookMonitored = model.GetEbookSettings()?.Monitored,
                EbookMonitorExistingMode = model.GetEbookSettings()?.MonitorExistingMode,
                EbookMonitorExistingBooks = model.GetEbookSettings()?.MonitorExistingBooks,
                EbookMonitorExisting = model.GetEbookSettings()?.MonitorExisting,
                EbookMonitorFuture = ToLegacyMonitorFuture(model.GetEbookSettings()),
                EbookMonitorNewItems = model.GetEbookSettings()?.MonitorNewItems,

                // INDIVIDUAL PROFILE FIELDS (for frontend form)
                AudiobookQualityProfileId = model.GetAudiobookSettings()?.QualityProfileId,
                AudiobookMetadataProfileId = model.GetAudiobookSettings()?.MetadataProfileId,
                EbookQualityProfileId = model.GetEbookSettings()?.QualityProfileId,
                EbookMetadataProfileId = model.GetEbookSettings()?.MetadataProfileId,

                // INDIVIDUAL AUDIOBOOKSHELF SIDECAR FIELDS (for frontend form)
                AudiobookWriteAudioBookShelfMetadataJson = model.GetAudiobookSettings()?.WriteAudioBookShelfMetadataJson ?? false,
                AudiobookWriteAudioBookShelfCover = model.GetAudiobookSettings()?.WriteAudioBookShelfCover ?? false,
                EbookWriteAudioBookShelfMetadataJson = model.GetEbookSettings()?.WriteAudioBookShelfMetadataJson ?? false,
                EbookWriteAudioBookShelfCover = model.GetEbookSettings()?.WriteAudioBookShelfCover ?? false,
                
                // INDIVIDUAL TAG FIELDS (for frontend form)
                AudiobookTags = model.GetAudiobookSettings()?.Tags ?? new List<int>(),
                EbookTags = model.GetEbookSettings()?.Tags ?? new List<int>(),

                Accessible = model.Accessible,
                FreeSpace = model.FreeSpace,
                TotalSpace = model.TotalSpace,
            };
        }

        public static RootFolderResource ToResource(this RootFolder model, IEnumerable<RootFolder> allRootFolders, string defaultAudiobookRootFolderPath, string defaultEbookRootFolderPath)
        {
            var resource = model.ToResource();
            if (resource == null)
            {
                return null;
            }

            resource.IsEffectiveDefaultAudiobook = IsEffectiveDefaultRootFolder(model, allRootFolders, FolderType.Audiobook, defaultAudiobookRootFolderPath);
            resource.IsEffectiveDefaultEbook = IsEffectiveDefaultRootFolder(model, allRootFolders, FolderType.Ebook, defaultEbookRootFolderPath);

            return resource;
        }

        private static bool IsEffectiveDefaultRootFolder(RootFolder model, IEnumerable<RootFolder> allRootFolders, FolderType mediaType, string defaultRootFolderPath)
        {
            return RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(allRootFolders,
                       mediaType,
                       defaultRootFolderPath,
                       out var effectiveRootFolder,
                       out _) &&
                   model.Path.PathEquals(effectiveRootFolder.Path);
        }

        public static RootFolder ToModel(this RootFolderResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            // Validate folderType is a valid enum value
            if (!Enum.IsDefined(typeof(NzbDrone.Core.RootFolders.FolderType), resource.FolderType))
            {
                throw new BadRequestException($"Invalid folder type: {resource.FolderType}. Valid values are 0 (Mixed), 1 (Audiobook), 2 (Ebook).");
            }

            CalibreSettings cs;
            if (resource.IsCalibreLibrary)
            {
                cs = new CalibreSettings
                {
                    Host = resource.Host,
                    Port = resource.Port,
                    UrlBase = resource.UrlBase,
                    Username = resource.Username,
                    Password = resource.Password,
                    Library = resource.Library,
                    OutputFormat = resource.OutputFormat,
                    OutputProfile = (int)Enum.Parse(typeof(CalibreProfile), resource.OutputProfile, true),
                    UseSsl = resource.UseSsl
                };
            }
            else
            {
                cs = null;
            }

            var model = new RootFolder
            {
                Id = resource.Id,
                Name = resource.Name,
                Path = resource.Path,

                // REMOVED: DefaultMetadataProfileId, DefaultQualityProfileId, DefaultAudiobookMetadataProfileId, DefaultEbookMetadataProfileId - DO NOT SET
                // Migration 025 will drop these columns from the database
                DefaultTags = resource.DefaultTags ?? new HashSet<int>(),
                IsCalibreLibrary = resource.IsCalibreLibrary,
                CalibreSettings = cs,
                FolderType = (NzbDrone.Core.RootFolders.FolderType)resource.FolderType,
                PlaceEbooksWithAudiobooks = resource.PlaceEbooksWithAudiobooks,
                DefaultSyncMonitoredAcrossFormats = resource.DefaultSyncMonitoredAcrossFormats
            };

            // Set new per-media-type settings if provided
            // First, handle structured settings objects
            if (resource.Audiobook != null)
            {
                ValidateMediaMonitoring(resource.Audiobook, "audiobook");
                model.SetAudiobookSettings(ToMediaTypeSettings(resource.Audiobook));
            }

            if (resource.Ebook != null)
            {
                ValidateMediaMonitoring(resource.Ebook, "ebook");
                model.SetEbookSettings(ToMediaTypeSettings(resource.Ebook));
            }

            ValidateMediaMonitoring(resource.AudiobookMonitored,
                resource.AudiobookMonitorExistingMode,
                resource.AudiobookMonitorExistingBooks,
                resource.AudiobookMonitorExisting,
                resource.AudiobookMonitorNewItems,
                "audiobook");
            ValidateMediaMonitoring(resource.EbookMonitored,
                resource.EbookMonitorExistingMode,
                resource.EbookMonitorExistingBooks,
                resource.EbookMonitorExisting,
                resource.EbookMonitorNewItems,
                "ebook");

            // Then, handle individual monitoring and profile fields from frontend form
            // Validate based on folder type - defense in depth
            if (model.FolderType == FolderType.Audiobook)
            {
                // Audiobook-only folder should not have ebook settings
                if (HasConfiguredIndividualSettings(resource.EbookMonitored,
                        resource.EbookMonitorExistingMode,
                        resource.EbookMonitorExistingBooks,
                        resource.EbookMonitorExisting,
                        resource.EbookMonitorFuture,
                        resource.EbookMonitorNewItems,
                        resource.EbookQualityProfileId,
                        resource.EbookMetadataProfileId,
                        resource.EbookTags,
                        resource.EbookWriteAudioBookShelfMetadataJson,
                        resource.EbookWriteAudioBookShelfCover) ||
                    HasConfiguredMediaTypeSettings(resource.Ebook))
                {
                    throw new BadRequestException("Audiobook-only folders cannot have ebook settings");
                }
                
                // Process audiobook settings
                if (resource.AudiobookMonitored.HasValue || resource.AudiobookMonitorExistingMode.HasValue || resource.AudiobookMonitorExistingBooks.HasValue || resource.AudiobookMonitorExisting.HasValue || resource.AudiobookMonitorFuture.HasValue || resource.AudiobookMonitorNewItems.HasValue ||
                    resource.AudiobookQualityProfileId.HasValue || resource.AudiobookMetadataProfileId.HasValue ||
                    resource.AudiobookWriteAudioBookShelfMetadataJson.HasValue || resource.AudiobookWriteAudioBookShelfCover.HasValue ||
                    resource.AudiobookTags != null)
                {
                    var existingAudiobook = model.GetAudiobookSettings() ?? new MediaTypeSettings();
                    if (resource.AudiobookMonitored.HasValue)
                        existingAudiobook.Monitored = resource.AudiobookMonitored;
                    if (resource.AudiobookMonitorExistingMode.HasValue)
                        existingAudiobook.MonitorExistingMode = resource.AudiobookMonitorExistingMode;
                    else if (resource.AudiobookMonitorExistingBooks.HasValue)
                        existingAudiobook.MonitorExistingBooks = resource.AudiobookMonitorExistingBooks;
                    else if (resource.AudiobookMonitorExisting.HasValue)
                        existingAudiobook.MonitorExisting = resource.AudiobookMonitorExisting;
                    if (resource.AudiobookMonitorFuture.HasValue)
                        existingAudiobook.MonitorFuture = resource.AudiobookMonitorFuture;
                    if (resource.AudiobookMonitorNewItems.HasValue)
                        existingAudiobook.MonitorNewItems = resource.AudiobookMonitorNewItems;
                    existingAudiobook.ApplyLegacyMonitoringSettings(resource.AudiobookMonitorExisting, resource.AudiobookMonitorFuture);
                    if (resource.AudiobookQualityProfileId.HasValue)
                        existingAudiobook.QualityProfileId = resource.AudiobookQualityProfileId;
                    if (resource.AudiobookMetadataProfileId.HasValue)
                        existingAudiobook.MetadataProfileId = resource.AudiobookMetadataProfileId;
                    if (resource.AudiobookWriteAudioBookShelfMetadataJson.HasValue)
                        existingAudiobook.WriteAudioBookShelfMetadataJson = resource.AudiobookWriteAudioBookShelfMetadataJson.Value;
                    if (resource.AudiobookWriteAudioBookShelfCover.HasValue)
                        existingAudiobook.WriteAudioBookShelfCover = resource.AudiobookWriteAudioBookShelfCover.Value;
                    if (resource.AudiobookTags != null)
                        existingAudiobook.Tags = resource.AudiobookTags;
                    
                    model.SetAudiobookSettings(existingAudiobook);
                }
                
                // Ensure ebook settings are null for inheritance
                model.SetEbookSettings(null);
            }
            else if (model.FolderType == FolderType.Ebook)
            {
                // Ebook-only folder should not have audiobook settings
                if (HasConfiguredIndividualSettings(resource.AudiobookMonitored,
                        resource.AudiobookMonitorExistingMode,
                        resource.AudiobookMonitorExistingBooks,
                        resource.AudiobookMonitorExisting,
                        resource.AudiobookMonitorFuture,
                        resource.AudiobookMonitorNewItems,
                        resource.AudiobookQualityProfileId,
                        resource.AudiobookMetadataProfileId,
                        resource.AudiobookTags,
                        resource.AudiobookWriteAudioBookShelfMetadataJson,
                        resource.AudiobookWriteAudioBookShelfCover) ||
                    HasConfiguredMediaTypeSettings(resource.Audiobook))
                {
                    throw new BadRequestException("Ebook-only folders cannot have audiobook settings");
                }
                
                // Process ebook settings
                if (resource.EbookMonitored.HasValue || resource.EbookMonitorExistingMode.HasValue || resource.EbookMonitorExistingBooks.HasValue || resource.EbookMonitorExisting.HasValue || resource.EbookMonitorFuture.HasValue || resource.EbookMonitorNewItems.HasValue ||
                    resource.EbookQualityProfileId.HasValue || resource.EbookMetadataProfileId.HasValue ||
                    resource.EbookWriteAudioBookShelfMetadataJson.HasValue || resource.EbookWriteAudioBookShelfCover.HasValue ||
                    resource.EbookTags != null)
                {
                    var existingEbook = model.GetEbookSettings() ?? new MediaTypeSettings();
                    if (resource.EbookMonitored.HasValue)
                        existingEbook.Monitored = resource.EbookMonitored;
                    if (resource.EbookMonitorExistingMode.HasValue)
                        existingEbook.MonitorExistingMode = resource.EbookMonitorExistingMode;
                    else if (resource.EbookMonitorExistingBooks.HasValue)
                        existingEbook.MonitorExistingBooks = resource.EbookMonitorExistingBooks;
                    else if (resource.EbookMonitorExisting.HasValue)
                        existingEbook.MonitorExisting = resource.EbookMonitorExisting;
                    if (resource.EbookMonitorFuture.HasValue)
                        existingEbook.MonitorFuture = resource.EbookMonitorFuture;
                    if (resource.EbookMonitorNewItems.HasValue)
                        existingEbook.MonitorNewItems = resource.EbookMonitorNewItems;
                    existingEbook.ApplyLegacyMonitoringSettings(resource.EbookMonitorExisting, resource.EbookMonitorFuture);
                    if (resource.EbookQualityProfileId.HasValue)
                        existingEbook.QualityProfileId = resource.EbookQualityProfileId;
                    if (resource.EbookMetadataProfileId.HasValue)
                        existingEbook.MetadataProfileId = resource.EbookMetadataProfileId;
                    if (resource.EbookWriteAudioBookShelfMetadataJson.HasValue)
                        existingEbook.WriteAudioBookShelfMetadataJson = resource.EbookWriteAudioBookShelfMetadataJson.Value;
                    if (resource.EbookWriteAudioBookShelfCover.HasValue)
                        existingEbook.WriteAudioBookShelfCover = resource.EbookWriteAudioBookShelfCover.Value;
                    if (resource.EbookTags != null)
                        existingEbook.Tags = resource.EbookTags;
                    
                    model.SetEbookSettings(existingEbook);
                }
                
                // Ensure audiobook settings are null for inheritance
                model.SetAudiobookSettings(null);
            }
            else
            {
                // Mixed content folder - process both types
                if (resource.AudiobookMonitored.HasValue || resource.AudiobookMonitorExistingMode.HasValue || resource.AudiobookMonitorExistingBooks.HasValue || resource.AudiobookMonitorExisting.HasValue || resource.AudiobookMonitorFuture.HasValue || resource.AudiobookMonitorNewItems.HasValue ||
                    resource.AudiobookQualityProfileId.HasValue || resource.AudiobookMetadataProfileId.HasValue ||
                    resource.AudiobookWriteAudioBookShelfMetadataJson.HasValue || resource.AudiobookWriteAudioBookShelfCover.HasValue ||
                    resource.AudiobookTags != null)
                {
                    var existingAudiobook = model.GetAudiobookSettings() ?? new MediaTypeSettings();
                    if (resource.AudiobookMonitored.HasValue)
                        existingAudiobook.Monitored = resource.AudiobookMonitored;
                    if (resource.AudiobookMonitorExistingMode.HasValue)
                        existingAudiobook.MonitorExistingMode = resource.AudiobookMonitorExistingMode;
                    else if (resource.AudiobookMonitorExistingBooks.HasValue)
                        existingAudiobook.MonitorExistingBooks = resource.AudiobookMonitorExistingBooks;
                    else if (resource.AudiobookMonitorExisting.HasValue)
                        existingAudiobook.MonitorExisting = resource.AudiobookMonitorExisting;
                    if (resource.AudiobookMonitorFuture.HasValue)
                        existingAudiobook.MonitorFuture = resource.AudiobookMonitorFuture;
                    if (resource.AudiobookMonitorNewItems.HasValue)
                        existingAudiobook.MonitorNewItems = resource.AudiobookMonitorNewItems;
                    existingAudiobook.ApplyLegacyMonitoringSettings(resource.AudiobookMonitorExisting, resource.AudiobookMonitorFuture);
                    if (resource.AudiobookQualityProfileId.HasValue)
                        existingAudiobook.QualityProfileId = resource.AudiobookQualityProfileId;
                    if (resource.AudiobookMetadataProfileId.HasValue)
                        existingAudiobook.MetadataProfileId = resource.AudiobookMetadataProfileId;
                    if (resource.AudiobookWriteAudioBookShelfMetadataJson.HasValue)
                        existingAudiobook.WriteAudioBookShelfMetadataJson = resource.AudiobookWriteAudioBookShelfMetadataJson.Value;
                    if (resource.AudiobookWriteAudioBookShelfCover.HasValue)
                        existingAudiobook.WriteAudioBookShelfCover = resource.AudiobookWriteAudioBookShelfCover.Value;
                    if (resource.AudiobookTags != null)
                        existingAudiobook.Tags = resource.AudiobookTags;
                    
                    model.SetAudiobookSettings(existingAudiobook);
                }
                
                if (resource.EbookMonitored.HasValue || resource.EbookMonitorExistingMode.HasValue || resource.EbookMonitorExistingBooks.HasValue || resource.EbookMonitorExisting.HasValue || resource.EbookMonitorFuture.HasValue || resource.EbookMonitorNewItems.HasValue ||
                    resource.EbookQualityProfileId.HasValue || resource.EbookMetadataProfileId.HasValue ||
                    resource.EbookWriteAudioBookShelfMetadataJson.HasValue || resource.EbookWriteAudioBookShelfCover.HasValue ||
                    resource.EbookTags != null)
                {
                    var existingEbook = model.GetEbookSettings() ?? new MediaTypeSettings();
                    if (resource.EbookMonitored.HasValue)
                        existingEbook.Monitored = resource.EbookMonitored;
                    if (resource.EbookMonitorExistingMode.HasValue)
                        existingEbook.MonitorExistingMode = resource.EbookMonitorExistingMode;
                    else if (resource.EbookMonitorExistingBooks.HasValue)
                        existingEbook.MonitorExistingBooks = resource.EbookMonitorExistingBooks;
                    else if (resource.EbookMonitorExisting.HasValue)
                        existingEbook.MonitorExisting = resource.EbookMonitorExisting;
                    if (resource.EbookMonitorFuture.HasValue)
                        existingEbook.MonitorFuture = resource.EbookMonitorFuture;
                    if (resource.EbookMonitorNewItems.HasValue)
                        existingEbook.MonitorNewItems = resource.EbookMonitorNewItems;
                    existingEbook.ApplyLegacyMonitoringSettings(resource.EbookMonitorExisting, resource.EbookMonitorFuture);
                    if (resource.EbookQualityProfileId.HasValue)
                        existingEbook.QualityProfileId = resource.EbookQualityProfileId;
                    if (resource.EbookMetadataProfileId.HasValue)
                        existingEbook.MetadataProfileId = resource.EbookMetadataProfileId;
                    if (resource.EbookWriteAudioBookShelfMetadataJson.HasValue)
                        existingEbook.WriteAudioBookShelfMetadataJson = resource.EbookWriteAudioBookShelfMetadataJson.Value;
                    if (resource.EbookWriteAudioBookShelfCover.HasValue)
                        existingEbook.WriteAudioBookShelfCover = resource.EbookWriteAudioBookShelfCover.Value;
                    if (resource.EbookTags != null)
                        existingEbook.Tags = resource.EbookTags;
                    
                    model.SetEbookSettings(existingEbook);
                }
            }

            // Only meaningful for mixed roots; force off otherwise.
            if (model.FolderType != FolderType.Mixed)
            {
                model.PlaceEbooksWithAudiobooks = false;
            }

            return model;
        }

        private static bool HasConfiguredMediaTypeSettings(MediaTypeSettingsResource settings)
        {
            return settings != null &&
                   (settings.QualityProfileId.HasValue ||
                    settings.MetadataProfileId.HasValue ||
                    settings.Monitored.HasValue ||
                    settings.MonitorExistingMode.HasValue ||
                    settings.MonitorExistingBooks.HasValue ||
                    settings.MonitorExisting.HasValue ||
                    settings.MonitorFuture.HasValue ||
                    settings.MonitorNewItems.HasValue ||
                    settings.WriteAudioBookShelfMetadataJson ||
                    settings.WriteAudioBookShelfCover ||
                   settings.Tags?.Count > 0);
        }

        private static void ValidateMediaMonitoring(MediaTypeSettingsResource settings, string mediaLabel)
        {
            if (settings != null)
            {
                ValidateMediaMonitoring(settings.Monitored,
                    settings.MonitorExistingMode,
                    settings.MonitorExistingBooks,
                    settings.MonitorExisting,
                    settings.MonitorNewItems,
                    mediaLabel);
            }
        }

        private static void ValidateMediaMonitoring(bool? monitored,
            MonitorTypes? monitorExistingMode,
            bool? monitorExistingBooks,
            int? legacyMonitorExisting,
            NewItemMonitorTypes? monitorNewItems,
            string mediaLabel)
        {
            if (legacyMonitorExisting.HasValue && legacyMonitorExisting.Value is not 0 and not 1 and not 2)
            {
                throw new BadRequestException($"{mediaLabel} MonitorExisting must be 0 (None), 1 (All), 2 (Selected), or null (unconfigured)");
            }

            if (monitorExistingMode.HasValue && monitorExistingMode.Value is not MonitorTypes.All and not MonitorTypes.Missing and not MonitorTypes.Existing and not MonitorTypes.None)
            {
                throw new BadRequestException($"{mediaLabel} MonitorExistingMode must be All, Missing, Existing, None, or null (unconfigured)");
            }

            if (monitorNewItems.HasValue && !Enum.IsDefined(typeof(NewItemMonitorTypes), monitorNewItems.Value))
            {
                throw new BadRequestException($"{mediaLabel} MonitorNewItems must be All, New, or None");
            }
        }

        private static bool HasConfiguredIndividualSettings(bool? monitored,
            MonitorTypes? monitorExistingMode,
            bool? monitorExistingBooks,
            int? monitorExisting,
            bool? monitorFuture,
            NewItemMonitorTypes? monitorNewItems,
            int? qualityProfileId,
            int? metadataProfileId,
            List<int> tags,
            bool? writeAudioBookShelfMetadataJson = null,
            bool? writeAudioBookShelfCover = null)
        {
            return monitored.HasValue ||
                   monitorExistingMode.HasValue ||
                   monitorExistingBooks.HasValue ||
                   monitorExisting.HasValue ||
                   monitorFuture.HasValue ||
                   monitorNewItems.HasValue ||
                   qualityProfileId.HasValue ||
                   metadataProfileId.HasValue ||
                   writeAudioBookShelfMetadataJson == true ||
                   writeAudioBookShelfCover == true ||
                   tags?.Count > 0;
        }

        private static MediaTypeSettingsResource ToMediaTypeSettingsResource(MediaTypeSettings settings)
        {
            if (settings == null)
                return null;

            return new MediaTypeSettingsResource
            {
                QualityProfileId = settings.QualityProfileId,
                MetadataProfileId = settings.MetadataProfileId,
                Monitored = settings.Monitored,
                MonitorExistingMode = settings.MonitorExistingMode,
                MonitorExistingBooks = settings.MonitorExistingBooks,
                MonitorExisting = settings.MonitorExisting,
                MonitorFuture = ToLegacyMonitorFuture(settings),
                MonitorNewItems = settings.MonitorNewItems,
                WriteAudioBookShelfMetadataJson = settings.WriteAudioBookShelfMetadataJson,
                WriteAudioBookShelfCover = settings.WriteAudioBookShelfCover,
                Tags = settings.Tags ?? new List<int>()
            };
        }

        private static MediaTypeSettings ToMediaTypeSettings(MediaTypeSettingsResource resource)
        {
            if (resource == null)
                return null;

            var settings = new MediaTypeSettings
            {
                QualityProfileId = resource.QualityProfileId,
                MetadataProfileId = resource.MetadataProfileId,
                Monitored = resource.Monitored,
                MonitorExistingMode = resource.MonitorExistingMode,
                MonitorNewItems = resource.MonitorNewItems,
                MonitorFuture = resource.MonitorFuture,
                WriteAudioBookShelfMetadataJson = resource.WriteAudioBookShelfMetadataJson,
                WriteAudioBookShelfCover = resource.WriteAudioBookShelfCover,
                Tags = resource.Tags ?? new List<int>()
            };

            if (!resource.MonitorExistingMode.HasValue && resource.MonitorExistingBooks.HasValue)
            {
                settings.MonitorExistingBooks = resource.MonitorExistingBooks;
            }

            settings.ApplyLegacyMonitoringSettings(resource.MonitorExisting, resource.MonitorFuture);
            return settings;
        }

        private static bool? ToLegacyMonitorFuture(MediaTypeSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            return settings.MonitorFuture ?? (settings.MonitorNewItems.HasValue
                ? settings.MonitorNewItems != NewItemMonitorTypes.None
                : null);
        }

        public static List<RootFolderResource> ToResource(this IEnumerable<RootFolder> models)
        {
            return models.Select(ToResource).ToList();
        }

        public static List<RootFolderResource> ToResource(this IEnumerable<RootFolder> models, IEnumerable<RootFolder> allRootFolders, string defaultAudiobookRootFolderPath, string defaultEbookRootFolderPath)
        {
            var allFolders = (allRootFolders ?? Enumerable.Empty<RootFolder>()).ToList();

            return models.Select(model => model.ToResource(allFolders, defaultAudiobookRootFolderPath, defaultEbookRootFolderPath)).ToList();
        }
    }
}
