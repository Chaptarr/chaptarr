using System.Text.RegularExpressions;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Transmission
{
    public class TransmissionSettingsValidator : AbstractValidator<TransmissionSettings>
    {
        public TransmissionSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);

            RuleFor(c => c.UrlBase).ValidUrlBase();

            RuleFor(c => c.AudiobookCategory).Matches(@"^\.?[-a-z]*$", RegexOptions.IgnoreCase).WithMessage("Allowed characters a-z and -");
            RuleFor(c => c.EbookCategory).Matches(@"^\.?[-a-z]*$", RegexOptions.IgnoreCase).WithMessage("Allowed characters a-z and -");

            RuleFor(c => c.AudiobookCategory).Empty()
                .When(c => c.TvDirectory.IsNotNullOrWhiteSpace())
                .WithMessage("Cannot use Category and Directory");

            RuleFor(c => c.EbookCategory).Empty()
                .When(c => c.TvDirectory.IsNotNullOrWhiteSpace())
                .WithMessage("Cannot use Category and Directory");

            RuleFor(c => c.MusicCategory).Empty()
                .When(c => c.TvDirectory.IsNotNullOrWhiteSpace())
                .WithMessage("Cannot use Category and Directory");
        }
    }

    public class TransmissionSettings : IProviderConfig, IBookDownloadCategorySettings
    {
        private static readonly TransmissionSettingsValidator Validator = new TransmissionSettingsValidator();

        private string _audiobookCategory;
        private string _ebookCategory;

        public TransmissionSettings()
        {
            Host = "localhost";
            Port = 9091;
            UrlBase = "/transmission/";
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.fc03eb4d77a85")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "provider.field.fafbbcc619aac", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.f130b15afdee4")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(5, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(6, Label = "provider.field.fb7445013fe90", Type = FieldType.Textbox, HelpText = "provider.field.f535765ad7944")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(7, Label = "provider.field.f007be7ed5f4e", Type = FieldType.Textbox, HelpText = "provider.field.f535765ad7944")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "provider.field.f292c06f0045a", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(8, Label = "provider.field.fc8f84c3c5378", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.f0ef20a489d17")]
        public string TvDirectory { get; set; }

        [FieldDefinition(9, Label = "provider.field.f1e36708f9861", Type = FieldType.Select, SelectOptions = typeof(TransmissionPriority), Advanced = true, HelpText = "provider.field.fe0303eb76da9")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(10, Label = "provider.field.faabfc5c5d52d", Type = FieldType.Select, SelectOptions = typeof(TransmissionPriority), Advanced = true, HelpText = "provider.field.f0a783e1949cd")]
        public int OlderTvPriority { get; set; }

        [FieldDefinition(11, Label = "provider.field.fee2bb4b2e037", Type = FieldType.Checkbox)]
        public bool AddPaused { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
