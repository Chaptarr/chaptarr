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
            RuleFor(c => c.ImportLimit).GreaterThanOrEqualTo(0).WithMessage("Import limit must be 0 or greater");
        }
    }

    public class GoodreadsSeriesImportListSettings : DualMediaImportListSettings
    {
        private static readonly GoodreadsSeriesImportListValidator Validator = new();

        public GoodreadsSeriesImportListSettings()
        {
            BaseUrl = "www.goodreads.com";
        }

        public override string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Series ID", HelpText = "Goodreads series ID")]
        public int SeriesId { get; set; }

        [FieldDefinition(3, Label = "Import Limit", Type = FieldType.Number, HelpText = "Maximum number of unique source books to import from this series. 0 is unlimited.", Advanced = true)]
        public int ImportLimit { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
