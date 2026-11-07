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
                .Must(userId => userId.IsNullOrWhiteSpace() || GoodreadsUserIdParser.IsValidUserId(userId))
                .WithMessage("Goodreads user ID must be a numeric ID (or a profile URL containing it)");
            RuleFor(c => c)
                .Must(settings => !settings.UserId.IsNullOrWhiteSpace() ||
                                  (!settings.AccessToken.IsNullOrWhiteSpace() && !settings.AccessTokenSecret.IsNullOrWhiteSpace()))
                .WithMessage("Goodreads user ID or existing OAuth credentials are required");
            RuleFor(c => c.AccessToken)
                .NotEmpty()
                .When(c => !c.AccessTokenSecret.IsNullOrWhiteSpace())
                .WithMessage("Goodreads access token is required when an OAuth secret is configured");
            RuleFor(c => c.AccessTokenSecret)
                .NotEmpty()
                .When(c => !c.AccessToken.IsNullOrWhiteSpace())
                .WithMessage("Goodreads OAuth secret is required when an access token is configured");
            RuleFor(c => c.BookshelfIds).NotEmpty();
            RuleFor(c => c.RefreshIntervalMinutes)
                .GreaterThanOrEqualTo(GoodreadsBookshelfImportListSettings.MinimumRefreshIntervalMinutes)
                .WithMessage($"Refresh interval must be at least {GoodreadsBookshelfImportListSettings.MinimumRefreshIntervalMinutes} minutes");
            this.AddDualMediaRules();
        }
    }

    public class GoodreadsBookshelfImportListSettings : GoodreadsSettingsBase<GoodreadsBookshelfImportListSettings>, IGoodreadsDualMediaImportListSettings
    {
        public const int MinimumRefreshIntervalMinutes = 15;

        private static readonly GoodreadsBookshelfImportListSettingsValidator SettingsValidator = new();

        public GoodreadsBookshelfImportListSettings()
        {
            BaseUrl = "www.goodreads.com";
            BookshelfIds = new string[] { };
            MonitorAudiobooks = true;
            MonitorEbooks = true;
            RefreshIntervalMinutes = 720;
        }

        [FieldDefinition(1, Label = "provider.field.f94541466889e", Type = FieldType.Bookshelf)]
        public IEnumerable<string> BookshelfIds { get; set; }

        [FieldDefinition(2,
            Label = "MonitorAudiobooks",
            Type = FieldType.Checkbox,
            HelpText = "provider.field.f5ecacbc7f1fa")]
        public bool MonitorAudiobooks { get; set; }

        [FieldDefinition(3,
            Label = "provider.field.f686d3f04e7e0",
            Type = FieldType.Checkbox,
            HelpText = "provider.field.fea17ca84d52d")]
        public bool MonitorEbooks { get; set; }

        [FieldDefinition(4, Label = "provider.field.f0f701a8deb7c", Type = FieldType.Number, HelpText = "provider.field.fd45ae9f7e9a5", Advanced = true)]
        public int ImportLimit { get; set; }

        [FieldDefinition(5, Label = "AudiobookQualityProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", HelpText = "provider.field.fe51ae2eae9c6")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(6, Label = "EbookQualityProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", HelpText = "provider.field.f5c36c4fb0440")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(7, Label = "AudiobookMetadataProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", HelpText = "provider.field.f0efa2f6e6d2b")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(8, Label = "EbookMetadataProfile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", HelpText = "provider.field.f39bbc1d8b91b")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(9, Label = "AudiobookRootFolder", Type = FieldType.Path, HelpText = "provider.field.f0fe46e7d8d2a")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(10, Label = "EbookRootFolder", Type = FieldType.Path, HelpText = "provider.field.fffc595f7f1a9")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(11, Label = "AudiobookTags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "provider.field.f40547e6a6702")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(12, Label = "EbookTags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "provider.field.f1c419054d0a6")]
        public List<int> EbookTags { get; set; } = new();

        [FieldDefinition(13, Label = "provider.field.ffd06a3b2e208", Type = FieldType.Number, HelpText = "provider.field.f198ab7b8b868", Advanced = true)]
        public int RefreshIntervalMinutes { get; set; }

        protected override AbstractValidator<GoodreadsBookshelfImportListSettings> Validator => SettingsValidator;

        public new NzbDroneValidationResult Validate()
        {
            if (!UserId.IsNullOrWhiteSpace() && GoodreadsUserIdParser.TryParse(UserId, out var normalized))
            {
                UserId = normalized;
            }

            return base.Validate();
        }
    }
}
