using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.ImportLists
{
    public abstract class DualMediaImportListSettings : IImportListSettings
    {
        public abstract string BaseUrl { get; set; }

        [FieldDefinition(50, Label = "Monitor Audiobooks", Type = FieldType.Checkbox, HelpText = "Apply this list to audiobooks.")]
        public bool MonitorAudiobooks { get; set; } = true;

        [FieldDefinition(51, Label = "Monitor Ebooks", Type = FieldType.Checkbox, HelpText = "Apply this list to ebooks.")]
        public bool MonitorEbooks { get; set; } = true;

        [FieldDefinition(52, Label = "Audiobook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", Advanced = true, HelpText = "Optional override for an author's first audiobook setup.")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(53, Label = "Ebook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", Advanced = true, HelpText = "Optional override for an author's first ebook setup.")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(54, Label = "Audiobook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", Advanced = true, HelpText = "Optional override for an author's first audiobook setup.")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(55, Label = "Ebook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", Advanced = true, HelpText = "Optional override for an author's first ebook setup.")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(56, Label = "Audiobook Root Folder", Type = FieldType.Path, HelpText = "Used for an author's first audiobook setup.")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(57, Label = "Ebook Root Folder", Type = FieldType.Path, HelpText = "Used for an author's first ebook setup.")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(58, Label = "Audiobook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getLocalTags", HelpText = "Optional tags for an author's first audiobook setup.")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(59, Label = "Ebook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getLocalTags", HelpText = "Optional tags for an author's first ebook setup.")]
        public List<int> EbookTags { get; set; } = new();

        public abstract NzbDrone.Core.Validation.NzbDroneValidationResult Validate();
    }

    public static class DualMediaImportListSettingsValidator
    {
        public static void AddDualMediaRules<TSettings>(this AbstractValidator<TSettings> validator)
            where TSettings : DualMediaImportListSettings
        {
            validator.RuleFor(c => c)
                .Must(c => c.MonitorAudiobooks || c.MonitorEbooks)
                .WithMessage("Select at least one media type to apply the list to");
        }
    }

    public static class DualMediaImportListActions
    {
        public static List<ValidationFailure> ValidateCreationSettings(ImportListDefinition definition)
        {
            var failures = new List<ValidationFailure>();
            if (definition?.Settings is not DualMediaImportListSettings settings ||
                !definition.GetMonitoringOptions().AddMissingAuthors)
            {
                return failures;
            }

            var validator = new InlineValidator<DualMediaImportListSettings>();
            validator.RuleFor(value => value.AudiobookRootFolderPath).Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Root folder is required").IsValidPath().When(value => value.MonitorAudiobooks);
            validator.RuleFor(value => value.EbookRootFolderPath).Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Root folder is required").IsValidPath().When(value => value.MonitorEbooks);
            return validator.Validate(settings).Errors.ToList();
        }

        public static object HandleRequestAction(
            string action,
            Lazy<IQualityProfileService> qualityProfileService,
            Lazy<IMetadataProfileService> metadataProfileService,
            Lazy<ITagService> tagService)
        {
            if (action.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (action == "getAudiobookQualityProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(qualityProfileService.Value.GetByType(ProfileType.Audiobook).Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getEbookQualityProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(qualityProfileService.Value.GetByType(ProfileType.Ebook).Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getAudiobookMetadataProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(metadataProfileService.Value.All()
                        .Where(p => MetadataProfileMatches(p.ProfileType, MetadataProfileType.Audiobook))
                        .Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getEbookMetadataProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(metadataProfileService.Value.All()
                        .Where(p => MetadataProfileMatches(p.ProfileType, MetadataProfileType.Ebook))
                        .Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action is "getLocalTags" or "getTags")
            {
                return new
                {
                    options = tagService.Value.All()
                        .OrderBy(t => t.Label)
                        .Select(t => new
                        {
                            Value = t.Id,
                            Name = t.Label
                        })
                        .ToList()
                };
            }

            return null;
        }

        public static List<ValidationFailure> ValidateCreationProfiles(ImportListDefinition definition,
            Lazy<IQualityProfileService> qualityProfileService,
            Lazy<IMetadataProfileService> metadataProfileService)
        {
            var failures = new List<ValidationFailure>();
            if (definition?.Settings is not DualMediaImportListSettings settings || !definition.GetMonitoringOptions().AddMissingAuthors)
            {
                return failures;
            }

            var qualityOverrides = new[]
            {
                (Field: nameof(settings.AudiobookQualityProfileId), Id: settings.AudiobookQualityProfileId, Enabled: settings.MonitorAudiobooks, Type: ProfileType.Audiobook),
                (Field: nameof(settings.EbookQualityProfileId), Id: settings.EbookQualityProfileId, Enabled: settings.MonitorEbooks, Type: ProfileType.Ebook)
            }.Where(p => p.Enabled && p.Id != 0).ToList();
            var qualityProfiles = qualityOverrides.Any(p => p.Id > 0) ? qualityProfileService.Value.All().ToDictionary(p => p.Id) : new Dictionary<int, QualityProfile>();
            foreach (var profile in qualityOverrides)
            {
                if (!qualityProfiles.TryGetValue(profile.Id, out var stored) || stored.ProfileType != profile.Type)
                {
                    failures.Add(new ValidationFailure(profile.Field, $"Choose an existing {profile.Type} quality profile or use root folder defaults"));
                }
            }

            var metadataOverrides = new[]
            {
                (Field: nameof(settings.AudiobookMetadataProfileId), Id: settings.AudiobookMetadataProfileId, Enabled: settings.MonitorAudiobooks, Type: MetadataProfileType.Audiobook),
                (Field: nameof(settings.EbookMetadataProfileId), Id: settings.EbookMetadataProfileId, Enabled: settings.MonitorEbooks, Type: MetadataProfileType.Ebook)
            }.Where(p => p.Enabled && p.Id != 0).ToList();
            var metadataProfiles = metadataOverrides.Any(p => p.Id > 0) ? metadataProfileService.Value.All().ToDictionary(p => p.Id) : new Dictionary<int, MetadataProfile>();
            foreach (var profile in metadataOverrides)
            {
                if (!metadataProfiles.TryGetValue(profile.Id, out var stored) || !MetadataProfileMatches(stored.ProfileType, profile.Type))
                {
                    failures.Add(new ValidationFailure(profile.Field, $"Choose an existing General or {profile.Type} metadata profile or use root folder defaults"));
                }
            }

            return failures;
        }

        public static bool MetadataProfileMatches(MetadataProfileType stored, MetadataProfileType requested) =>
            stored == MetadataProfileType.General || stored == requested;

        public static IEnumerable<ValidationFailure> TestRootFolderConfig(
            ImportListDefinition definition,
            IRootFolderService rootFolderService,
            IRootFolderSettingsResolver rootFolderSettingsResolver)
        {
            var failures = ValidateCreationSettings(definition);

            if (definition?.Settings is not DualMediaImportListSettings settings ||
                !definition.GetMonitoringOptions().AddMissingAuthors || failures.Count > 0)
            {
                return failures;
            }

            var roots = (settings.MonitorAudiobooks && settings.AudiobookRootFolderPath.IsNotNullOrWhiteSpace()) ||
                (settings.MonitorEbooks && settings.EbookRootFolderPath.IsNotNullOrWhiteSpace())
                ? rootFolderService.All() : new List<RootFolder>();

            if (settings.MonitorAudiobooks)
            {
                failures.AddIfNotNull(TestRootFolder(
                    nameof(settings.AudiobookRootFolderPath),
                    settings.AudiobookRootFolderPath,
                    BookMediaType.Audiobook,
                    settings.AudiobookQualityProfileId,
                    settings.AudiobookMetadataProfileId,
                    rootFolderService,
                    rootFolderSettingsResolver,
                    roots));
            }

            if (settings.MonitorEbooks)
            {
                failures.AddIfNotNull(TestRootFolder(
                    nameof(settings.EbookRootFolderPath),
                    settings.EbookRootFolderPath,
                    BookMediaType.Ebook,
                    settings.EbookQualityProfileId,
                    settings.EbookMetadataProfileId,
                    rootFolderService,
                    rootFolderSettingsResolver,
                    roots));
            }

            return failures;
        }

        private static ValidationFailure TestRootFolder(
            string fieldName,
            string rootFolderPath,
            BookMediaType mediaType,
            int overrideQualityProfileId,
            int overrideMetadataProfileId,
            IRootFolderService rootFolderService,
            IRootFolderSettingsResolver rootFolderSettingsResolver,
            List<RootFolder> roots)
        {
            var rootFolder = rootFolderService.GetBestRootFolder(rootFolderPath, roots);
            if (rootFolder == null)
            {
                return new ValidationFailure(fieldName, $"Root folder '{rootFolderPath}' is not configured in Chaptarr");
            }

            if (mediaType == BookMediaType.Audiobook && rootFolder.FolderType == FolderType.Ebook)
            {
                return new ValidationFailure(fieldName, "Selected root folder is Ebook-only; choose an Audiobook or Mixed root folder");
            }

            if (mediaType == BookMediaType.Ebook && rootFolder.FolderType == FolderType.Audiobook)
            {
                return new ValidationFailure(fieldName, "Selected root folder is Audiobook-only; choose an Ebook or Mixed root folder");
            }

            var resolved = rootFolderSettingsResolver.ResolveSettings(rootFolder, mediaType);
            if (resolved == null || !resolved.IsConfigured)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' does not have {mediaType} defaults configured");
            }

            if (overrideQualityProfileId <= 0 && (resolved.QualityProfileId ?? 0) <= 0)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' is missing a {mediaType} quality profile default");
            }

            if (overrideMetadataProfileId <= 0 && (resolved.MetadataProfileId ?? 0) <= 0)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' is missing a {mediaType} metadata profile default");
            }

            return null;
        }

        private static List<object> BuildSelectOptions(IEnumerable<(int id, string name)> items, bool includeDefault)
        {
            var options = new List<object>();

            if (includeDefault)
            {
                options.Add(new
                {
                    Value = 0,
                    Name = "Use root folder defaults",
                    LocalizationKey = "UseRootFolderDefaults"
                });
            }

            options.AddRange(items
                .OrderBy(i => i.name)
                .Select(i => new
                {
                    Value = i.id,
                    Name = i.name
                }));

            return options;
        }
    }
}
