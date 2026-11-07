using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download.Clients;
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

    public class QBittorrentSettings : IProviderConfig, IBookDownloadCategorySettings
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

        [FieldDefinition(2, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.f43ff64c5a04b")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "provider.field.fafbbcc619aac", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.fed7a4fd6fcef")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "ApiKey", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.fdc7b7a265acf")]
        public string ApiKey { get; set; }

        [FieldDefinition(5, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(6, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(7, Label = "provider.field.fb7445013fe90", Type = FieldType.Textbox, HelpText = "provider.field.ffe3afd1a7d4a")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(8, Label = "provider.field.f007be7ed5f4e", Type = FieldType.Textbox, HelpText = "provider.field.ffe3afd1a7d4a")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "provider.field.f292c06f0045a", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(9, Label = "provider.field.fd10fdb1ebee6", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.f48d8951e889b")]
        public string AudiobookImportedCategory
        {
            get => _audiobookImportedCategory ?? MusicImportedCategory;
            set => _audiobookImportedCategory = value;
        }

        [FieldDefinition(10, Label = "provider.field.f068da7e9da75", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.f77b1eeefbe89")]
        public string EbookImportedCategory
        {
            get => _ebookImportedCategory ?? MusicImportedCategory;
            set => _ebookImportedCategory = value;
        }

        [FieldDefinition(101, Label = "provider.field.fcf18cb638b27", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicImportedCategory { get; set; }

        [FieldDefinition(11, Label = "provider.field.f1e36708f9861", Type = FieldType.Select, SelectOptions = typeof(QBittorrentPriority), Advanced = true, HelpText = "provider.field.fe0303eb76da9")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(12, Label = "provider.field.faabfc5c5d52d", Type = FieldType.Select, SelectOptions = typeof(QBittorrentPriority), Advanced = true, HelpText = "provider.field.f0a783e1949cd")]
        public int OlderTvPriority { get; set; }

        [FieldDefinition(13, Label = "provider.field.fb80d9a4950db", Type = FieldType.Select, SelectOptions = typeof(QBittorrentState), HelpText = "provider.field.f866e447248db")]
        public int InitialState { get; set; }

        [FieldDefinition(14, Label = "provider.field.fff72ae38d781", Type = FieldType.Checkbox, HelpText = "provider.field.fcf9692863aa6")]
        public bool SequentialOrder { get; set; }

        [FieldDefinition(15, Label = "provider.field.fb0c56c384aa4", Type = FieldType.Checkbox, HelpText = "provider.field.f8952604a5ad2")]
        public bool FirstAndLast { get; set; }

        [FieldDefinition(16, Label = "DownloadClientQbittorrentSettingsContentLayout", Type = FieldType.Select, SelectOptions = typeof(QBittorrentContentLayout), HelpText = "DownloadClientQbittorrentSettingsContentLayoutHelpText")]
        public int ContentLayout { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
