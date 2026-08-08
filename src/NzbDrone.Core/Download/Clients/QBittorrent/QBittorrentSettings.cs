using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    public class QBittorrentSettingsValidator : AbstractValidator<QBittorrentSettings>
    {
        public QBittorrentSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());

            RuleFor(c => c.Username).Empty()
                .WithMessage("Username must be empty when using API Key.")
                .When(c => c.ApiKey.IsNotNullOrWhiteSpace());
            RuleFor(c => c.Password).Empty()
                .WithMessage("Password must be empty when using API Key.")
                .When(c => c.ApiKey.IsNotNullOrWhiteSpace());

            RuleFor(c => c.AudiobookCategory).Matches(@"^([^\\\/](\/?[^\\\/])*)?$").WithMessage(@"Can not contain '\', '//', or start/end with '/'");
            RuleFor(c => c.EbookCategory).Matches(@"^([^\\\/](\/?[^\\\/])*)?$").WithMessage(@"Can not contain '\', '//', or start/end with '/'");
            RuleFor(c => c.AudiobookImportedCategory).Matches(@"^([^\\\/](\/?[^\\\/])*)?$").WithMessage(@"Can not contain '\', '//', or start/end with '/'");
            RuleFor(c => c.EbookImportedCategory).Matches(@"^([^\\\/](\/?[^\\\/])*)?$").WithMessage(@"Can not contain '\', '//', or start/end with '/'");
        }
    }

    public class QBittorrentSettings : IProviderConfig
    {
        private static readonly QBittorrentSettingsValidator Validator = new QBittorrentSettingsValidator();

        private string _audiobookCategory;
        private string _ebookCategory;
        private string _audiobookImportedCategory;
        private string _ebookImportedCategory;

        public QBittorrentSettings()
        {
            Host = "localhost";
            Port = 8080;
            MusicCategory = "chaptarr";
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "Use SSL", Type = FieldType.Checkbox, HelpText = "Use a secure connection. See Options -> Web UI -> 'Use HTTPS instead of HTTP' in qBittorrent.")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "Url Base", Type = FieldType.Textbox, Advanced = true, HelpText = "Adds a prefix to the qBittorrent url, e.g. http://[host]:[port]/[urlBase]/api")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "Requires qBittorrent 5.2 or newer. Older qBittorrent versions ignore the Bearer header and the connection will fail.")]
        public string ApiKey { get; set; }

        [FieldDefinition(5, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(6, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(7, Label = "Audiobook Category", Type = FieldType.Textbox, HelpText = "Adding a category specific to Chaptarr avoids conflicts with unrelated non-Chaptarr downloads. Using a category is optional, but strongly recommended.")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(8, Label = "Ebook Category", Type = FieldType.Textbox, HelpText = "Adding a category specific to Chaptarr avoids conflicts with unrelated non-Chaptarr downloads. Using a category is optional, but strongly recommended.")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "Category", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(9, Label = "Audiobook Post-Import Category", Type = FieldType.Textbox, Advanced = true, HelpText = "After import, move audiobook torrents to this category and leave them there. Chaptarr will not delete torrents in this category. Leave blank if you want Chaptarr to delete torrents after seeding is done.")]
        public string AudiobookImportedCategory
        {
            get => _audiobookImportedCategory ?? MusicImportedCategory;
            set => _audiobookImportedCategory = value;
        }

        [FieldDefinition(10, Label = "Ebook Post-Import Category", Type = FieldType.Textbox, Advanced = true, HelpText = "After import, move ebook torrents to this category and leave them there. Chaptarr will not delete torrents in this category. Leave blank if you want Chaptarr to delete torrents after seeding is done.")]
        public string EbookImportedCategory
        {
            get => _ebookImportedCategory ?? MusicImportedCategory;
            set => _ebookImportedCategory = value;
        }

        [FieldDefinition(101, Label = "Post-Import Category", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicImportedCategory { get; set; }

        [FieldDefinition(11, Label = "Recent Priority", Type = FieldType.Select, SelectOptions = typeof(QBittorrentPriority), Advanced = true, HelpText = "Priority to use when grabbing books released within the last 14 days")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(12, Label = "Older Priority", Type = FieldType.Select, SelectOptions = typeof(QBittorrentPriority), Advanced = true, HelpText = "Priority to use when grabbing books released over 14 days ago")]
        public int OlderTvPriority { get; set; }

        [FieldDefinition(13, Label = "Initial State", Type = FieldType.Select, SelectOptions = typeof(QBittorrentState), HelpText = "Initial state for torrents added to qBittorrent. Note that Forced Torrents do not abide by seed restrictions")]
        public int InitialState { get; set; }

        [FieldDefinition(14, Label = "Sequential Order", Type = FieldType.Checkbox, HelpText = "Download in sequential order (qBittorrent 4.1.0+)")]
        public bool SequentialOrder { get; set; }

        [FieldDefinition(15, Label = "First and Last First", Type = FieldType.Checkbox, HelpText = "Download first and last pieces first (qBittorrent 4.1.0+)")]
        public bool FirstAndLast { get; set; }

        [FieldDefinition(16, Label = "DownloadClientQbittorrentSettingsContentLayout", Type = FieldType.Select, SelectOptions = typeof(QBittorrentContentLayout), HelpText = "DownloadClientQbittorrentSettingsContentLayoutHelpText")]
        public int ContentLayout { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
