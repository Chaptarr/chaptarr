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

        [FieldDefinition(0, Label = "Import Owned", Type = FieldType.Checkbox, HelpText = "Import editions you mark as Owned on Hardcover (edition-level).")]
        public bool ImportOwned { get; set; }

        [FieldDefinition(1, Label = "Import Want to Read", Type = FieldType.Checkbox, HelpText = "Import books on your Hardcover Want to Read shelf (book-level).")]
        public bool ImportWantToRead { get; set; }

        [FieldDefinition(2, Label = "Import Currently Reading", Type = FieldType.Checkbox, HelpText = "Import books on your Hardcover Currently Reading shelf.")]
        public bool ImportCurrentlyReading { get; set; }

        [FieldDefinition(3, Label = "Import Read", Type = FieldType.Checkbox, HelpText = "Import books on your Hardcover Read shelf.")]
        public bool ImportRead { get; set; }

        [FieldDefinition(4, Label = "Monitor Audiobooks", Type = FieldType.Checkbox, HelpText = "When enabled, Chaptarr will monitor audiobooks from your Hardcover library (applies to items without an edition, and audiobook editions).")]
        public bool MonitorAudiobooks { get; set; }

        [FieldDefinition(5, Label = "Monitor Ebooks", Type = FieldType.Checkbox, HelpText = "When enabled, Chaptarr will monitor ebooks from your Hardcover library (applies to items without an edition, and ebook/print editions).")]
        public bool MonitorEbooks { get; set; }

        [FieldDefinition(6, Label = "Audiobook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", HelpText = "Quality profile used when importing audiobooks from Hardcover.")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(7, Label = "Ebook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", HelpText = "Quality profile used when importing ebooks from Hardcover.")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(8, Label = "Audiobook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", HelpText = "Metadata profile used when importing audiobooks from Hardcover.")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(9, Label = "Ebook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", HelpText = "Metadata profile used when importing ebooks from Hardcover.")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(10, Label = "Audiobook Root Folder", Type = FieldType.Path, HelpText = "Root folder used when importing audiobooks from Hardcover.")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(11, Label = "Ebook Root Folder", Type = FieldType.Path, HelpText = "Root folder used when importing ebooks from Hardcover.")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(12, Label = "Audiobook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "Optional: tags to apply when importing audiobooks from Hardcover.")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(13, Label = "Ebook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "Optional: tags to apply when importing ebooks from Hardcover.")]
        public List<int> EbookTags { get; set; } = new();

        [FieldDefinition(14, Label = "Hardcover API Token", Privacy = PrivacyLevel.ApiKey, HelpText = "Optional: use a different Hardcover API token for this import list (multi-account). Leave blank to use the global token from Settings.")]
        public string ApiToken { get; set; }

        public string CachedUsername { get; set; }

        public string CachedAvatarUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
