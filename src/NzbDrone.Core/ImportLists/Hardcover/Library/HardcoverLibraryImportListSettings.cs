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
            this.AddDualMediaRules();

            RuleFor(c => c)
                .Must(c => c.ImportOwned || c.ImportWantToRead || c.ImportCurrentlyReading || c.ImportRead)
                .WithMessage("Select at least one library section to import");

        }
    }

    public class HardcoverLibraryImportListSettings : DualMediaImportListSettings
    {
        private static readonly HardcoverLibraryImportListSettingsValidator Validator = new();

        public HardcoverLibraryImportListSettings()
        {
            BaseUrl = "api.hardcover.app";
            ImportOwned = true;
            ImportWantToRead = true;
            ImportCurrentlyReading = false;
            ImportRead = false;
        }

        public override string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Import Owned", Type = FieldType.Checkbox, HelpText = "Import editions you mark as Owned on Hardcover (edition-level).")]
        public bool ImportOwned { get; set; }

        [FieldDefinition(1, Label = "Import Want to Read", Type = FieldType.Checkbox, HelpText = "Import books on your Hardcover Want to Read shelf (book-level).")]
        public bool ImportWantToRead { get; set; }

        [FieldDefinition(2, Label = "Import Currently Reading", Type = FieldType.Checkbox, HelpText = "Import books on your Hardcover Currently Reading shelf.")]
        public bool ImportCurrentlyReading { get; set; }

        [FieldDefinition(3, Label = "Import Read", Type = FieldType.Checkbox, HelpText = "Import books on your Hardcover Read shelf.")]
        public bool ImportRead { get; set; }

        [FieldDefinition(14, Label = "Hardcover API Token", Privacy = PrivacyLevel.ApiKey, HelpText = "Optional: use a different Hardcover API token for this import list (multi-account). Leave blank to use the global token from Settings.")]
        public string ApiToken { get; set; }

        public string CachedUsername { get; set; }

        public string CachedAvatarUrl { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
