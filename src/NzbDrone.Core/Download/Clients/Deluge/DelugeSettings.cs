using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Deluge
{
    public class DelugeSettingsValidator : AbstractValidator<DelugeSettings>
    {
        public DelugeSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);

            RuleFor(c => c.AudiobookCategory).Matches("^[-a-z0-9]*$").WithMessage("Allowed characters a-z, 0-9 and -");
            RuleFor(c => c.EbookCategory).Matches("^[-a-z0-9]*$").WithMessage("Allowed characters a-z, 0-9 and -");
            RuleFor(c => c.AudiobookImportedCategory).Matches("^[-a-z0-9]*$").WithMessage("Allowed characters a-z, 0-9 and -");
            RuleFor(c => c.EbookImportedCategory).Matches("^[-a-z0-9]*$").WithMessage("Allowed characters a-z, 0-9 and -");
        }
    }

    public class DelugeSettings : IProviderConfig
    {
        private static readonly DelugeSettingsValidator Validator = new DelugeSettingsValidator();

        private string _audiobookCategory;
        private string _ebookCategory;
        private string _audiobookImportedCategory;
        private string _ebookImportedCategory;

        public DelugeSettings()
        {
            Host = "localhost";
            Port = 8112;
            MusicCategory = "";
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "Use SSL", Type = FieldType.Checkbox, HelpText = "Use secure connection when connecting to Deluge")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "Url Base", Type = FieldType.Textbox, Advanced = true, HelpText = "Adds a prefix to the deluge json url, see http://[host]:[port]/[urlBase]/json")]
        public string UrlBase { get; set; }

        [FieldDefinition(3, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(5, Label = "Audiobook Category", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", HelpText = "Adding a category specific to Chaptarr avoids conflicts with unrelated non-Chaptarr downloads. Using a category is optional, but strongly recommended.")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(6, Label = "Ebook Category", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", HelpText = "Adding a category specific to Chaptarr avoids conflicts with unrelated non-Chaptarr downloads. Using a category is optional, but strongly recommended.")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "Category", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(7, Label = "Audiobook Post-Import Category", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Advanced = true, HelpText = "After import, move audiobook torrents to this category and leave them there. Chaptarr will not delete torrents in this category. Leave blank if you want Chaptarr to delete torrents after seeding is done.")]
        public string AudiobookImportedCategory
        {
            get => _audiobookImportedCategory ?? MusicImportedCategory;
            set => _audiobookImportedCategory = value;
        }

        [FieldDefinition(8, Label = "Ebook Post-Import Category", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Advanced = true, HelpText = "After import, move ebook torrents to this category and leave them there. Chaptarr will not delete torrents in this category. Leave blank if you want Chaptarr to delete torrents after seeding is done.")]
        public string EbookImportedCategory
        {
            get => _ebookImportedCategory ?? MusicImportedCategory;
            set => _ebookImportedCategory = value;
        }

        [FieldDefinition(101, Label = "Post-Import Category", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Hidden = HiddenType.Hidden)]
        public string MusicImportedCategory { get; set; }

        [FieldDefinition(9, Label = "Recent Priority", Type = FieldType.Select, SelectOptions = typeof(DelugePriority), Advanced = true, HelpText = "Priority to use when grabbing books that were released within the last 14 days")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(10, Label = "Older Priority", Type = FieldType.Select, SelectOptions = typeof(DelugePriority), Advanced = true, HelpText = "Priority to use when grabbing books that were released over 14 days ago")]
        public int OlderTvPriority { get; set; }

        [FieldDefinition(11, Label = "Add Paused", Type = FieldType.Checkbox)]
        public bool AddPaused { get; set; }

        [FieldDefinition(12, Label = "DownloadClientDelugeSettingsDirectory", Type = FieldType.Textbox, Advanced = true, HelpText = "DownloadClientDelugeSettingsDirectoryHelpText")]
        public string DownloadDirectory { get; set; }

        [FieldDefinition(13, Label = "DownloadClientDelugeSettingsDirectoryCompleted", Type = FieldType.Textbox, Advanced = true, HelpText = "DownloadClientDelugeSettingsDirectoryCompletedHelpText")]
        public string CompletedDirectory { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
