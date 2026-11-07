using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public class GoodreadsSeriesImportListValidator : AbstractValidator<GoodreadsSeriesImportListSettings>
    {
        public GoodreadsSeriesImportListValidator()
        {
            RuleFor(c => c.SeriesId).GreaterThan(0);
            this.AddDualMediaRules();
        }
    }

    public class GoodreadsSeriesImportListSettings : IGoodreadsDualMediaImportListSettings
    {
        private static readonly GoodreadsSeriesImportListValidator Validator = new();

        public GoodreadsSeriesImportListSettings()
        {
            BaseUrl = "www.goodreads.com";
            MonitorAudiobooks = true;
            MonitorEbooks = true;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "provider.field.fbe17b52bf053", HelpText = "provider.field.f50daa23e691a")]
        public int SeriesId { get; set; }

        [FieldDefinition(1, Label = "MonitorAudiobooks", Type = FieldType.Checkbox, HelpText = "provider.field.fa207a3b75949")]
        public bool MonitorAudiobooks { get; set; }

        [FieldDefinition(2, Label = "provider.field.f686d3f04e7e0", Type = FieldType.Checkbox, HelpText = "provider.field.f91bc6fc1d38a")]
        public bool MonitorEbooks { get; set; }

        [FieldDefinition(3, Label = "provider.field.f0f701a8deb7c", Type = FieldType.Number, HelpText = "provider.field.ffbbc0487f001", Advanced = true)]
        public int ImportLimit { get; set; }

        [FieldDefinition(4, Label = "AudiobookQualityProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", HelpText = "provider.field.f285fb519aa1a")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(5, Label = "EbookQualityProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", HelpText = "provider.field.ffd272adc2f58")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(6, Label = "AudiobookMetadataProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", HelpText = "provider.field.f29b6b9e083cc")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(7, Label = "EbookMetadataProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", HelpText = "provider.field.f4891b0209fad")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(8, Label = "AudiobookRootFolder", Type = FieldType.Path, HelpText = "provider.field.f1ee0e50bc744")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(9, Label = "EbookRootFolder", Type = FieldType.Path, HelpText = "provider.field.fa4f2502db2e0")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(10, Label = "AudiobookTags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "provider.field.fd296d5ee89d3")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(11, Label = "EbookTags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "provider.field.f1ee868fd9557")]
        public List<int> EbookTags { get; set; } = new();

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
