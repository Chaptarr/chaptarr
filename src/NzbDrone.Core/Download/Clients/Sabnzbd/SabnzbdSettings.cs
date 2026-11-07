using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Sabnzbd
{
    public class SabnzbdSettingsValidator : AbstractValidator<SabnzbdSettings>
    {
        public SabnzbdSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());

            RuleFor(c => c.ApiKey).NotEmpty()
                                  .WithMessage("API Key is required when username/password are not configured")
                                  .When(c => string.IsNullOrWhiteSpace(c.Username));

            RuleFor(c => c.Username).NotEmpty()
                                    .WithMessage("Username is required when API key is not configured")
                                    .When(c => string.IsNullOrWhiteSpace(c.ApiKey));

            RuleFor(c => c.Password).NotEmpty()
                                    .WithMessage("Password is required when API key is not configured")
                                    .When(c => string.IsNullOrWhiteSpace(c.ApiKey));
        }
    }

    public class SabnzbdSettings : IProviderConfig, IBookDownloadCategorySettings
    {
        private static readonly SabnzbdSettingsValidator Validator = new SabnzbdSettingsValidator();

        private string _audiobookCategory;
        private string _ebookCategory;

        public SabnzbdSettings()
        {
            Host = "localhost";
            Port = 8080;
            MusicCategory = "";
            RecentTvPriority = (int)SabnzbdPriority.Default;
            OlderTvPriority = (int)SabnzbdPriority.Default;
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.f71d1d49e8c27")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "provider.field.fafbbcc619aac", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.f980cab606403")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "ApiKey", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; }

        [FieldDefinition(5, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName, Advanced = true)]
        public string Username { get; set; }

        [FieldDefinition(6, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password, Advanced = true)]
        public string Password { get; set; }

        [FieldDefinition(7, Label = "provider.field.fb7445013fe90", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", HelpText = "provider.field.ffe3afd1a7d4a")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(8, Label = "provider.field.f007be7ed5f4e", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", HelpText = "provider.field.ffe3afd1a7d4a")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "provider.field.f292c06f0045a", Type = FieldType.Select, SelectOptionsProviderAction = "getCategories", Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(9, Label = "provider.field.f1e36708f9861", Type = FieldType.Select, SelectOptions = typeof(SabnzbdPriority), Advanced = true, HelpText = "provider.field.fe0303eb76da9")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(10, Label = "provider.field.faabfc5c5d52d", Type = FieldType.Select, SelectOptions = typeof(SabnzbdPriority), Advanced = true, HelpText = "provider.field.f0a783e1949cd")]
        public int OlderTvPriority { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
