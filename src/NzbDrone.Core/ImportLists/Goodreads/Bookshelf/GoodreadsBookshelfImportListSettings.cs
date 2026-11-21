using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public class GoodreadsBookshelfImportListSettingsValidator : AbstractValidator<GoodreadsBookshelfImportListSettings>
    {
        public GoodreadsBookshelfImportListSettingsValidator()
        {
            RuleFor(c => c.UserId)
                .NotEmpty()
                .WithMessage("Goodreads user ID is required");
            RuleFor(c => c.UserId)
                .Must(GoodreadsUserIdParser.IsValidUserId)
                .WithMessage("Goodreads user ID must be a numeric ID (or a profile URL containing it)");
            RuleFor(c => c.BookshelfIds).NotEmpty();
            this.AddDualMediaRules();
            RuleFor(c => c.ImportLimit).GreaterThanOrEqualTo(0).WithMessage("Import limit must be 0 or greater");
        }
    }

    public class GoodreadsBookshelfImportListSettings : DualMediaImportListSettings
    {
        private static readonly GoodreadsBookshelfImportListSettingsValidator Validator = new();

        public GoodreadsBookshelfImportListSettings()
        {
            BaseUrl = "www.goodreads.com";
            BookshelfIds = new string[] { };
        }

        public override string BaseUrl { get; set; }

        [FieldDefinition(0,
            Label = "User ID or URL",
            HelpText = "Numeric Goodreads user ID (from your profile URL), or a full profile URL. Shelves must be public.",
            Placeholder = "12345678 or https://www.goodreads.com/user/show/12345678")]
        public string UserId { get; set; }

        [FieldDefinition(1, Label = "Bookshelves", Type = FieldType.Bookshelf)]
        public IEnumerable<string> BookshelfIds { get; set; }

        [FieldDefinition(4, Label = "Import Limit", Type = FieldType.Number, HelpText = "Maximum number of unique source books to import when work IDs are supplied. RSS fallback counts listed editions. 0 is unlimited.", Advanced = true)]
        public int ImportLimit { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            if (!UserId.IsNullOrWhiteSpace() && GoodreadsUserIdParser.TryParse(UserId, out var normalized))
            {
                UserId = normalized;
            }

            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
