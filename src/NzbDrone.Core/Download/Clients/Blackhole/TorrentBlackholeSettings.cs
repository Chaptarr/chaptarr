using System.ComponentModel;
using FluentValidation;
using Newtonsoft.Json;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Blackhole
{
    public class TorrentBlackholeSettingsValidator : AbstractValidator<TorrentBlackholeSettings>
    {
        public TorrentBlackholeSettingsValidator()
        {
            //Todo: Validate that the path actually exists
            RuleFor(c => c.TorrentFolder).IsValidPath();
            RuleFor(c => c.MagnetFileExtension).NotEmpty();
        }
    }

    public class TorrentBlackholeSettings : IProviderConfig
    {
        public TorrentBlackholeSettings()
        {
            MagnetFileExtension = ".magnet";
            ReadOnly = true;
        }

        private static readonly TorrentBlackholeSettingsValidator Validator = new TorrentBlackholeSettingsValidator();

        [FieldDefinition(0, Label = "provider.field.f974e20cd8550", Type = FieldType.Path, HelpText = "provider.field.f848a6d927677")]
        public string TorrentFolder { get; set; }

        [FieldDefinition(1, Label = "provider.field.fdfec772dc118", Type = FieldType.Path, HelpText = "provider.field.f85efd9cda753")]
        public string WatchFolder { get; set; }

        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        [FieldDefinition(2, Label = "provider.field.f892a299aa334", Type = FieldType.Checkbox, HelpText = "provider.field.f0fafe62ec9fe")]
        public bool SaveMagnetFiles { get; set; }

        [FieldDefinition(3, Label = "provider.field.f375ebd2cea28", Type = FieldType.Textbox, HelpText = "provider.field.f0d53d88c6792")]
        public string MagnetFileExtension { get; set; }

        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        [FieldDefinition(4, Label = "provider.field.fca1048507ded", Type = FieldType.Checkbox, HelpText = "provider.field.f89119c9c0b2b")]
        public bool ReadOnly { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
