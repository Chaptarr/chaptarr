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

        [FieldDefinition(0, Label = "Staging Folder", Type = FieldType.Path, HelpText = "Local folder where Chaptarr stages Direct downloads before import.")]
        public string StagingFolder { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
