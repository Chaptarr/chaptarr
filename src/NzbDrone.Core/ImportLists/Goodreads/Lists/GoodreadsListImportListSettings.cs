using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public class GoodreadsListImportListValidator : AbstractValidator<GoodreadsListImportListSettings>
    {
        public GoodreadsListImportListValidator()
        {
            RuleFor(c => c.ListId).GreaterThan(0);
            this.AddDualMediaRules();
        }
    }

    public class GoodreadsListImportListSettings : IGoodreadsDualMediaImportListSettings
    {
        private static readonly GoodreadsListImportListValidator Validator = new();

        public GoodreadsListImportListSettings()
        {
            BaseUrl = "www.goodreads.com";
            MonitorAudiobooks = true;
            MonitorEbooks = true;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "List ID", HelpText = "Goodreads list ID")]
        public int ListId { get; set; }

        [FieldDefinition(1, Label = "Monitor Audiobooks", Type = FieldType.Checkbox, HelpText = "When enabled, audiobook items will be created/monitored for books from this list.")]
        public bool MonitorAudiobooks { get; set; }

        [FieldDefinition(2, Label = "Monitor Ebooks", Type = FieldType.Checkbox, HelpText = "When enabled, ebook items will be created/monitored for books from this list.")]
        public bool MonitorEbooks { get; set; }

        [FieldDefinition(3, Label = "Import Limit", Type = FieldType.Number, HelpText = "Maximum number of unique source books to import from this list. 0 is unlimited.", Advanced = true)]
        public int ImportLimit { get; set; }

        [FieldDefinition(4, Label = "Audiobook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", HelpText = "Quality profile used when importing audiobooks from this list.")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(5, Label = "Ebook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", HelpText = "Quality profile used when importing ebooks from this list.")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(6, Label = "Audiobook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", HelpText = "Metadata profile used when importing audiobooks from this list.")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(7, Label = "Ebook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", HelpText = "Metadata profile used when importing ebooks from this list.")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(8, Label = "Audiobook Root Folder", Type = FieldType.Path, HelpText = "Root folder used when importing audiobooks from this list.")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(9, Label = "Ebook Root Folder", Type = FieldType.Path, HelpText = "Root folder used when importing ebooks from this list.")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(10, Label = "Audiobook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "Optional: tags to apply when importing audiobooks from this list.")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(11, Label = "Ebook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "Optional: tags to apply when importing ebooks from this list.")]
        public List<int> EbookTags { get; set; } = new();

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
