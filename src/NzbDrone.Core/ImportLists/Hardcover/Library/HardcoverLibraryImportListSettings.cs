using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Hardcover.Library
{
    public class HardcoverLibraryImportListSettingsValidator : AbstractValidator<HardcoverLibraryImportListSettings>
    {
        public HardcoverLibraryImportListSettingsValidator()
        {
            RuleFor(c => c)
                .Must(c => c.MonitorAudiobooks || c.MonitorEbooks)
                .WithMessage("Select at least one media type to monitor");

            RuleFor(c => c)
                .Must(c => c.ImportOwned || c.ImportWantToRead || c.ImportCurrentlyReading || c.ImportRead)
                .WithMessage("Select at least one library section to import");

            RuleFor(c => c.AudiobookRootFolderPath)
                .NotEmpty()
                .When(c => c.MonitorAudiobooks)
                .WithMessage("Audiobook root folder is required when monitoring audiobooks");

            RuleFor(c => c.EbookRootFolderPath)
                .NotEmpty()
                .When(c => c.MonitorEbooks)
                .WithMessage("Ebook root folder is required when monitoring ebooks");
        }
    }

    public class HardcoverLibraryImportListSettings : IImportListSettings, IMediaTypeRootFolderSettings
    {
        private static readonly HardcoverLibraryImportListSettingsValidator Validator = new();

        public HardcoverLibraryImportListSettings()
        {
            BaseUrl = "api.hardcover.app";
            ImportOwned = true;
            ImportWantToRead = true;
            ImportCurrentlyReading = false;
            ImportRead = false;
            MonitorAudiobooks = true;
            MonitorEbooks = true;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "provider.field.f7c6679a4c0b7", Type = FieldType.Checkbox, HelpText = "provider.field.ffb082a613681")]
        public bool ImportOwned { get; set; }

        [FieldDefinition(1, Label = "provider.field.f43d5fbd565ae", Type = FieldType.Checkbox, HelpText = "provider.field.f5f2d90a6787f")]
        public bool ImportWantToRead { get; set; }

        [FieldDefinition(2, Label = "provider.field.fe3017fb82b09", Type = FieldType.Checkbox, HelpText = "provider.field.fd51e797a3412")]
        public bool ImportCurrentlyReading { get; set; }

        [FieldDefinition(3, Label = "provider.field.f3a01afc88e4c", Type = FieldType.Checkbox, HelpText = "provider.field.fde35332a868d")]
        public bool ImportRead { get; set; }

        [FieldDefinition(4, Label = "MonitorAudiobooks", Type = FieldType.Checkbox, HelpText = "provider.field.fd9c3aa57a6fc")]
        public bool MonitorAudiobooks { get; set; }

        [FieldDefinition(5, Label = "provider.field.f686d3f04e7e0", Type = FieldType.Checkbox, HelpText = "provider.field.ff31b483b37fe")]
        public bool MonitorEbooks { get; set; }

        [FieldDefinition(6, Label = "AudiobookQualityProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", HelpText = "provider.field.f121925ef23c3")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(7, Label = "EbookQualityProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", HelpText = "provider.field.f9ad962a99985")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(8, Label = "AudiobookMetadataProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", HelpText = "provider.field.f814e6132049b")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(9, Label = "EbookMetadataProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", HelpText = "provider.field.f02b909aa44ae")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(10, Label = "AudiobookRootFolder", Type = FieldType.Path, HelpText = "provider.field.fd1ea8d092e42")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(11, Label = "EbookRootFolder", Type = FieldType.Path, HelpText = "provider.field.fc3e974e43af9")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(12, Label = "AudiobookTags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "provider.field.f67fe7d50b851")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(13, Label = "EbookTags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "provider.field.f377cf0048e94")]
        public List<int> EbookTags { get; set; } = new();

        [FieldDefinition(14, Label = "provider.field.f4b9a395c8d79", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f646519b156ca")]
        public string ApiToken { get; set; }

        public string CachedUsername { get; set; }

        public string CachedAvatarUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
