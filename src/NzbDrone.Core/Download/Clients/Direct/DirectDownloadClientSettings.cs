using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public class DirectDownloadClientSettingsValidator : AbstractValidator<DirectDownloadClientSettings>
    {
        public DirectDownloadClientSettingsValidator()
        {
            RuleFor(settings => settings.StagingFolder).IsValidPath();
        }
    }

    public class DirectDownloadClientSettings : IProviderConfig
    {
        private static readonly DirectDownloadClientSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "provider.field.f9962f2ec3f39", Type = FieldType.Path, HelpText = "provider.field.f1a732caa0d94")]
        public string StagingFolder { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
