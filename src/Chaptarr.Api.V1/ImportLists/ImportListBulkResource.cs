using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Api.V1.ImportLists
{
    public class ImportListBulkResource : ProviderBulkResource<ImportListBulkResource>
    {
        public bool? EnableAutomaticAdd { get; set; }
        public string RootFolderPath { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
    }

    public class ImportListBulkResourceMapper : ProviderBulkResourceMapper<ImportListBulkResource, ImportListDefinition>
    {
        private readonly Lazy<IQualityProfileService> _qualityProfileService;
        private readonly Lazy<IMetadataProfileService> _metadataProfileService;
        private readonly Lazy<IRootFolderService> _rootFolderService;

        public ImportListBulkResourceMapper(Lazy<IQualityProfileService> qualityProfileService,
            Lazy<IMetadataProfileService> metadataProfileService, Lazy<IRootFolderService> rootFolderService)
        {
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _rootFolderService = rootFolderService;
        }

        public override List<ImportListDefinition> UpdateModel(ImportListBulkResource resource, List<ImportListDefinition> existingDefinitions)
        {
            if (resource == null)
            {
                return new List<ImportListDefinition>();
            }

            var quality = resource.QualityProfileId.GetValueOrDefault() > 0
                ? _qualityProfileService.Value.All().SingleOrDefault(p => p.Id == resource.QualityProfileId) : null;
            var metadata = resource.MetadataProfileId.GetValueOrDefault() > 0
                ? _metadataProfileService.Value.All().SingleOrDefault(p => p.Id == resource.MetadataProfileId) : null;
            var root = !string.IsNullOrWhiteSpace(resource.RootFolderPath)
                ? _rootFolderService.Value.GetBestRootFolder(resource.RootFolderPath, _rootFolderService.Value.All()) : null;
            var audioRoot = root?.FolderType is FolderType.Mixed or FolderType.Audiobook;
            var ebookRoot = root?.FolderType is FolderType.Mixed or FolderType.Ebook;
            var audioQuality = resource.QualityProfileId == 0 || quality?.ProfileType == ProfileType.Audiobook;
            var ebookQuality = resource.QualityProfileId == 0 || quality?.ProfileType == ProfileType.Ebook;
            var audioMetadata = resource.MetadataProfileId == 0 || metadata != null && DualMediaImportListActions.MetadataProfileMatches(metadata.ProfileType, MetadataProfileType.Audiobook);
            var ebookMetadata = resource.MetadataProfileId == 0 || metadata != null && DualMediaImportListActions.MetadataProfileMatches(metadata.ProfileType, MetadataProfileType.Ebook);
            var failures = new List<ValidationFailure>();
            foreach (var definition in existingDefinitions)
            {
                var settings = definition.Settings as DualMediaImportListSettings;
                bool Compatible(bool audiobook, bool ebook) => settings != null &&
                    ((settings.MonitorAudiobooks && audiobook) || (settings.MonitorEbooks && ebook));
                if (resource.RootFolderPath != null && !Compatible(audioRoot, ebookRoot))
                {
                    failures.Add(new ValidationFailure(nameof(resource.RootFolderPath), $"{definition.Name}: choose an existing root folder compatible with an enabled format"));
                }
                else if (resource.RootFolderPath != null &&
                    ((settings.MonitorAudiobooks && audioRoot && !RootFolderSettingsResolver.HasRequiredProfiles(root.GetAudiobookSettings())) ||
                     (settings.MonitorEbooks && ebookRoot && !RootFolderSettingsResolver.HasRequiredProfiles(root.GetEbookSettings()))))
                {
                    failures.Add(new ValidationFailure(nameof(resource.RootFolderPath), $"{definition.Name}: the selected root folder must have defaults configured for the formats being changed"));
                }
                if (resource.QualityProfileId.HasValue && !Compatible(audioQuality, ebookQuality))
                {
                    failures.Add(new ValidationFailure(nameof(resource.QualityProfileId), $"{definition.Name}: choose an existing quality profile compatible with an enabled format"));
                }
                if (resource.MetadataProfileId.HasValue && !Compatible(audioMetadata, ebookMetadata))
                {
                    failures.Add(new ValidationFailure(nameof(resource.MetadataProfileId), $"{definition.Name}: choose an existing General or enabled-format metadata profile"));
                }
            }
            if (failures.Any())
            {
                throw new ValidationException(failures);
            }

            foreach (var definition in existingDefinitions)
            {
                definition.EnableAutomaticAdd = resource.EnableAutomaticAdd ?? definition.EnableAutomaticAdd;
                if (definition.Settings is not DualMediaImportListSettings settings)
                {
                    continue;
                }
                if (settings.MonitorAudiobooks)
                {
                    if (audioRoot) settings.AudiobookRootFolderPath = resource.RootFolderPath;
                    if (audioQuality) settings.AudiobookQualityProfileId = resource.QualityProfileId.Value;
                    if (audioMetadata) settings.AudiobookMetadataProfileId = resource.MetadataProfileId.Value;
                }
                if (settings.MonitorEbooks)
                {
                    if (ebookRoot) settings.EbookRootFolderPath = resource.RootFolderPath;
                    if (ebookQuality) settings.EbookQualityProfileId = resource.QualityProfileId.Value;
                    if (ebookMetadata) settings.EbookMetadataProfileId = resource.MetadataProfileId.Value;
                }
            }
            var creationFailures = existingDefinitions.Where(definition => definition.Enable)
                .SelectMany(definition => DualMediaImportListActions.ValidateCreationSettings(definition)
                    .Select(failure => new ValidationFailure(failure.PropertyName, $"{definition.Name}: {failure.ErrorMessage}")))
                .ToList();
            if (creationFailures.Count > 0)
            {
                throw new ValidationException(creationFailures);
            }

            return existingDefinitions;
        }
    }
}
