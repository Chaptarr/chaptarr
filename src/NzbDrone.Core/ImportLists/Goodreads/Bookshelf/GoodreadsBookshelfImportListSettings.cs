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
            RuleFor(c => c.RefreshIntervalMinutes)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Refresh interval must be at least 1 minute");
            this.AddDualMediaRules();
        }
    }

    public class GoodreadsBookshelfImportListSettings : IGoodreadsDualMediaImportListSettings
    {
        private static readonly GoodreadsBookshelfImportListSettingsValidator Validator = new();

        public GoodreadsBookshelfImportListSettings()
        {
            BaseUrl = "www.goodreads.com";
            BookshelfIds = new string[] { };
            MonitorAudiobooks = true;
            MonitorEbooks = true;
            RefreshIntervalMinutes = 15;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0,
            Label = "User ID or URL",
            HelpText = "Numeric Goodreads user ID (from your profile URL), or a full profile URL. Shelves must be public.",
            Placeholder = "12345678 or https://www.goodreads.com/user/show/12345678")]
        public string UserId { get; set; }

        [FieldDefinition(1, Label = "Bookshelves", Type = FieldType.Bookshelf)]
        public IEnumerable<string> BookshelfIds { get; set; }

        [FieldDefinition(2,
            Label = "Monitor Audiobooks",
            Type = FieldType.Checkbox,
            HelpText = "When enabled, audiobook items will be created/monitored for books from these shelves.")]
        public bool MonitorAudiobooks { get; set; }

        [FieldDefinition(3,
            Label = "Monitor Ebooks",
            Type = FieldType.Checkbox,
            HelpText = "When enabled, ebook items will be created/monitored for books from these shelves.")]
        public bool MonitorEbooks { get; set; }

        [FieldDefinition(4, Label = "Import Limit", Type = FieldType.Number, HelpText = "Maximum number of unique source books to import from these shelves. 0 is unlimited.", Advanced = true)]
        public int ImportLimit { get; set; }

        [FieldDefinition(5, Label = "Audiobook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", HelpText = "Quality profile used when importing audiobooks from these shelves.")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(6, Label = "Ebook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", HelpText = "Quality profile used when importing ebooks from these shelves.")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(7, Label = "Audiobook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", HelpText = "Metadata profile used when importing audiobooks from these shelves.")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(8, Label = "Ebook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", HelpText = "Metadata profile used when importing ebooks from these shelves.")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(9, Label = "Audiobook Root Folder", Type = FieldType.Path, HelpText = "Root folder used when importing audiobooks from these shelves.")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(10, Label = "Ebook Root Folder", Type = FieldType.Path, HelpText = "Root folder used when importing ebooks from these shelves.")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(11, Label = "Audiobook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "Optional: tags to apply when importing audiobooks from these shelves.")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(12, Label = "Ebook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "Optional: tags to apply when importing ebooks from these shelves.")]
        public List<int> EbookTags { get; set; } = new();

        [FieldDefinition(13, Label = "Refresh Interval (Minutes)", Type = FieldType.Number, HelpText = "How often these shelves are checked for new books, in minutes. Short intervals mean more requests to Goodreads.", Advanced = true)]
        public int RefreshIntervalMinutes { get; set; }

        public NzbDroneValidationResult Validate()
        {
            if (!UserId.IsNullOrWhiteSpace() && GoodreadsUserIdParser.TryParse(UserId, out var normalized))
            {
                UserId = normalized;
            }

            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
