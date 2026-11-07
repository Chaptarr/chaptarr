using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.NzbVortex
{
    public class NzbVortexSettingsValidator : AbstractValidator<NzbVortexSettings>
    {
        public NzbVortexSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());

            RuleFor(c => c.ApiKey).NotEmpty()
                                  .WithMessage("API Key is required");

            RuleFor(c => c).Must(c =>
                    c.AudiobookCategory.IsNotNullOrWhiteSpace() ||
                    c.EbookCategory.IsNotNullOrWhiteSpace() ||
                    c.MusicCategory.IsNotNullOrWhiteSpace())
                .WithMessage("A category is recommended")
                .AsWarning();
        }
    }

    public class NzbVortexSettings : IProviderConfig, IBookDownloadCategorySettings
    {
        private static readonly NzbVortexSettingsValidator Validator = new NzbVortexSettingsValidator();

        private string _audiobookCategory;
        private string _ebookCategory;

        public NzbVortexSettings()
        {
            Host = "localhost";
            Port = 4321;
            MusicCategory = "Chaptarr";
            RecentTvPriority = (int)NzbVortexPriority.Normal;
            OlderTvPriority = (int)NzbVortexPriority.Normal;
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "provider.field.fafbbcc619aac", Type = FieldType.Textbox, Advanced = true, HelpText = "provider.field.fad474080a315")]
        public string UrlBase { get; set; }

        [FieldDefinition(3, Label = "ApiKey", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; }

        [FieldDefinition(4, Label = "provider.field.f7ab097af902c", Type = FieldType.Textbox, HelpText = "provider.field.ffe3afd1a7d4a")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(5, Label = "provider.field.fb0bc267591a6", Type = FieldType.Textbox, HelpText = "provider.field.ffe3afd1a7d4a")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "Group", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(6, Label = "provider.field.f1e36708f9861", Type = FieldType.Select, SelectOptions = typeof(NzbVortexPriority), Advanced = true, HelpText = "provider.field.fe0303eb76da9")]
        public int RecentTvPriority { get; set; }

        [FieldDefinition(7, Label = "provider.field.faabfc5c5d52d", Type = FieldType.Select, SelectOptions = typeof(NzbVortexPriority), Advanced = true, HelpText = "provider.field.f0a783e1949cd")]
        public int OlderTvPriority { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
