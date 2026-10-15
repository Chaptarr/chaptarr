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
            RuleFor(c => c.ImportLimit).GreaterThanOrEqualTo(0).WithMessage("Import limit must be 0 or greater");
        }
    }

    public class GoodreadsListImportListSettings : DualMediaImportListSettings
    {
        private static readonly GoodreadsListImportListValidator Validator = new();

        public GoodreadsListImportListSettings()
        {
            BaseUrl = "www.goodreads.com";
        }

        public override string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "List ID", HelpText = "Goodreads list ID")]
        public int ListId { get; set; }

        [FieldDefinition(3, Label = "Import Limit", Type = FieldType.Number, HelpText = "Maximum number of unique source books to import from this list. 0 is unlimited.", Advanced = true)]
        public int ImportLimit { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
