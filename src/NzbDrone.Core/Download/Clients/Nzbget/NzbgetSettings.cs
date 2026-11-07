using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Nzbget
{
    public class NzbgetSettingsValidator : AbstractValidator<NzbgetSettings>
    {
        public NzbgetSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());

            RuleFor(c => c.Username).NotEmpty().When(c => !string.IsNullOrWhiteSpace(c.Password));
            RuleFor(c => c.Password).NotEmpty().When(c => !string.IsNullOrWhiteSpace(c.Username));

            RuleFor(c => c).Must(c =>
                    c.AudiobookCategory.IsNotNullOrWhiteSpace() ||
                    c.EbookCategory.IsNotNullOrWhiteSpace() ||
                    c.MusicCategory.IsNotNullOrWhiteSpace())
                .WithMessage("A category is recommended")
                .AsWarning();
        }
    }

    public class NzbgetSettings : IProviderConfig, IBookDownloadCategorySettings
    {
        private static readonly NzbgetSettingsValidator Validator = new NzbgetSettingsValidator();

        private string _audiobookCategory;
        private string _ebookCategory;

        public NzbgetSettings()
        {
            Host = "localhost";
            Port = 6789;
            MusicCategory = "Chaptarr";
            Username = "nzbget";
            RecentTvPriority = (int)NzbgetPriority.Normal;
            OlderTvPriority = (int)NzbgetPriority.Normal;
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.fa6dda8c6f419")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "provider.field.fafbbcc619aac", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.f100fa980fa39")]
        public string UrlBase { get; set; }

        [FieldDefinition(3, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(5, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(6, Label = "provider.field.fb7445013fe90", Type = FieldType.Textbox, HelpText = "provider.field.ffe3afd1a7d4a")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(7, Label = "provider.field.f007be7ed5f4e", Type = FieldType.Textbox, HelpText = "provider.field.ffe3afd1a7d4a")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "provider.field.f292c06f0045a", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(8, Label = "provider.field.f1e36708f9861", Type = FieldType.Select, SelectOptions = typeof(NzbgetPriority), Advanced = true, HelpText = "provider.field.fe0303eb76da9")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(9, Label = "provider.field.faabfc5c5d52d", Type = FieldType.Select, SelectOptions = typeof(NzbgetPriority), Advanced = true, HelpText = "provider.field.f0a783e1949cd")]
        public int OlderTvPriority { get; set; }

        [FieldDefinition(10, Label = "provider.field.fee2bb4b2e037", Type = FieldType.Checkbox, HelpText = "provider.field.fcbf8b70ead77")]
        public bool AddPaused { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
