using System.Text.RegularExpressions;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
namespace NzbDrone.Core.Indexers.Nyaa
{
    public class NyaaSettingsValidator : AbstractValidator<NyaaSettings>
    {
        public NyaaSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.AdditionalParameters).Matches("(&[a-z]+=[a-z0-9_]+)*", RegexOptions.IgnoreCase);

            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator());
        }
    }

    public class NyaaSettings : ITorrentIndexerSettings
    {
        private static readonly NyaaSettingsValidator Validator = new NyaaSettingsValidator();

        public NyaaSettings()
        {
            BaseUrl = "";
            AdditionalParameters = "&cats=3_0&filter=1";
            MinimumSeeders = IndexerDefaults.MINIMUM_SEEDERS;
        }

        [FieldDefinition(0, Label = "provider.field.feb650fa1243e")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "provider.field.f8d4ecad7c7e2", Advanced = true, HelpText = "provider.field.f5a2ce2a83c0e")]
        public string AdditionalParameters { get; set; }

        [FieldDefinition(2, Type = FieldType.Number, Label = "provider.field.fa4001360c131", Unit = "days", HelpText = "provider.field.f8ef2df0a1d96", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(3, Type = FieldType.Textbox, Label = "provider.field.fd783e4d6293b", HelpText = "provider.field.fb8265760181c", Advanced = true)]
        public int MinimumSeeders { get; set; }

        [FieldDefinition(4)]
        public SeedCriteriaSettings SeedCriteria { get; set; } = new SeedCriteriaSettings();

        [FieldDefinition(5, Type = FieldType.Checkbox, Label = "provider.field.feaf93d215894", HelpText = "provider.field.f0af80d5651c2", Advanced = true)]
        public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
