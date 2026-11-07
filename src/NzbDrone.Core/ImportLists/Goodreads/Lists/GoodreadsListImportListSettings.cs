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

        [FieldDefinition(0, Label = "provider.field.f8cb711d03db7", HelpText = "provider.field.fa09f62eec6d8")]
        public int ListId { get; set; }

        [FieldDefinition(1, Label = "MonitorAudiobooks", Type = FieldType.Checkbox, HelpText = "provider.field.fdfc03b2059db")]
        public bool MonitorAudiobooks { get; set; }

        [FieldDefinition(2, Label = "provider.field.f686d3f04e7e0", Type = FieldType.Checkbox, HelpText = "provider.field.faac2240a8e42")]
        public bool MonitorEbooks { get; set; }

        [FieldDefinition(3, Label = "provider.field.f0f701a8deb7c", Type = FieldType.Number, HelpText = "provider.field.f1a8f7d97f354", Advanced = true)]
        public int ImportLimit { get; set; }

        [FieldDefinition(4, Label = "AudiobookQualityProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", HelpText = "provider.field.f5cfc4f47114e")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(5, Label = "EbookQualityProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", HelpText = "provider.field.fa837f9239f7c")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(6, Label = "AudiobookMetadataProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", HelpText = "provider.field.fe30f57f7c18f")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(7, Label = "EbookMetadataProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", HelpText = "provider.field.f0c83ef4d603a")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(8, Label = "AudiobookRootFolder", Type = FieldType.Path, HelpText = "provider.field.f6126605f5ec8")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(9, Label = "EbookRootFolder", Type = FieldType.Path, HelpText = "provider.field.f6407f6d3c82e")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(10, Label = "AudiobookTags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "provider.field.f59c7aadcca5d")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(11, Label = "EbookTags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "provider.field.f4e9aecfd3b92")]
        public List<int> EbookTags { get; set; } = new();

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
