using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download.Clients;
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

    public class DelugeSettings : IProviderConfig, IBookDownloadCategorySettings
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

        [FieldDefinition(2, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.f66d7a2535164")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "provider.field.fafbbcc619aac", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.f5948d0bfdf6a")]
        public string UrlBase { get; set; }

        [FieldDefinition(3, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(5, Label = "provider.field.fb7445013fe90", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", HelpText = "provider.field.ffe3afd1a7d4a")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(6, Label = "provider.field.f007be7ed5f4e", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", HelpText = "provider.field.ffe3afd1a7d4a")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "provider.field.f292c06f0045a", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(7, Label = "provider.field.fd10fdb1ebee6", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Advanced = true, HelpText = "provider.field.f48d8951e889b")]
        public string AudiobookImportedCategory
        {
            get => _audiobookImportedCategory ?? MusicImportedCategory;
            set => _audiobookImportedCategory = value;
        }

        [FieldDefinition(8, Label = "provider.field.f068da7e9da75", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Advanced = true, HelpText = "provider.field.f77b1eeefbe89")]
        public string EbookImportedCategory
        {
            get => _ebookImportedCategory ?? MusicImportedCategory;
            set => _ebookImportedCategory = value;
        }

        [FieldDefinition(101, Label = "provider.field.fcf18cb638b27", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Hidden = HiddenType.Hidden)]
        public string MusicImportedCategory { get; set; }

        [FieldDefinition(9, Label = "provider.field.f1e36708f9861", Type = FieldType.Select, SelectOptions = typeof(DelugePriority), Advanced = true, HelpText = "provider.field.fd73d1889cb3d")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(10, Label = "provider.field.faabfc5c5d52d", Type = FieldType.Select, SelectOptions = typeof(DelugePriority), Advanced = true, HelpText = "provider.field.fcaeb44897776")]
        public int OlderTvPriority { get; set; }

        [FieldDefinition(11, Label = "provider.field.fee2bb4b2e037", Type = FieldType.Checkbox)]
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
