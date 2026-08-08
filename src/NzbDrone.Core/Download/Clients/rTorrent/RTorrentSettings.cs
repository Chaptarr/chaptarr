using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.RTorrent
{
    public class RTorrentSettingsValidator : AbstractValidator<RTorrentSettings>
    {
        public RTorrentSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c).Must(c =>
                    c.AudiobookCategory.IsNotNullOrWhiteSpace() ||
                    c.EbookCategory.IsNotNullOrWhiteSpace() ||
                    c.MusicCategory.IsNotNullOrWhiteSpace())
                .WithMessage("A category is recommended")
                .AsWarning();
        }
    }

    public class RTorrentSettings : IProviderConfig
    {
        private static readonly RTorrentSettingsValidator Validator = new RTorrentSettingsValidator();

        private string _audiobookCategory;
        private string _ebookCategory;
        private string _audiobookImportedCategory;
        private string _ebookImportedCategory;

        public RTorrentSettings()
        {
            Host = "localhost";
            Port = 8080;
            UrlBase = "RPC2";
            MusicCategory = "chaptarr";
            OlderTvPriority = (int)RTorrentPriority.Normal;
            RecentTvPriority = (int)RTorrentPriority.Normal;
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "Use SSL", Type = FieldType.Checkbox, HelpText = "Use secure connection when connecting to rTorrent")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "Url Path", Type = FieldType.Textbox, HelpText = "Path to the XMLRPC endpoint, see http(s)://[host]:[port]/[urlPath]. This is usually RPC2 or [path to ruTorrent]/plugins/rpc/rpc.php when using ruTorrent.")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(5, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(6, Label = "Audiobook Category", Type = FieldType.Textbox, HelpText = "Adding a category specific to Chaptarr avoids conflicts with unrelated non-Chaptarr downloads. Using a category is optional, but strongly recommended.")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(7, Label = "Ebook Category", Type = FieldType.Textbox, HelpText = "Adding a category specific to Chaptarr avoids conflicts with unrelated non-Chaptarr downloads. Using a category is optional, but strongly recommended.")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "Category", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(8, Label = "Audiobook Post-Import Category", Type = FieldType.Textbox, Advanced = true, HelpText = "After import, move audiobook torrents to this category and leave them there. Chaptarr will not delete torrents in this category. Leave blank if you want Chaptarr to delete torrents after seeding is done.")]
        public string AudiobookImportedCategory
        {
            get => _audiobookImportedCategory ?? MusicImportedCategory;
            set => _audiobookImportedCategory = value;
        }

        [FieldDefinition(9, Label = "Ebook Post-Import Category", Type = FieldType.Textbox, Advanced = true, HelpText = "After import, move ebook torrents to this category and leave them there. Chaptarr will not delete torrents in this category. Leave blank if you want Chaptarr to delete torrents after seeding is done.")]
        public string EbookImportedCategory
        {
            get => _ebookImportedCategory ?? MusicImportedCategory;
            set => _ebookImportedCategory = value;
        }

        [FieldDefinition(101, Label = "Post-Import Category", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicImportedCategory { get; set; }

        [FieldDefinition(10, Label = "Directory", Type = FieldType.Textbox, Advanced = true, HelpText = "Optional location to put downloads in, leave blank to use the default rTorrent location")]
        public string MusicDirectory { get; set; }

        [FieldDefinition(11, Label = "Recent Priority", Type = FieldType.Select, SelectOptions = typeof(RTorrentPriority), Advanced = true, HelpText = "Priority to use when grabbing books released within the last 14 days")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(12, Label = "Older Priority", Type = FieldType.Select, SelectOptions = typeof(RTorrentPriority), Advanced = true, HelpText = "Priority to use when grabbing books released over 14 days ago")]
        public int OlderTvPriority { get; set; }

        [FieldDefinition(13, Label = "Add Stopped", Type = FieldType.Checkbox, HelpText = "Enabling will add torrents and magnets to rTorrent in a stopped state. This may break magnet files.")]
        public bool AddStopped { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
