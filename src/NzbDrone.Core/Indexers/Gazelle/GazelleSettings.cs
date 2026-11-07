using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Gazelle
{
    public class GazelleSettingsValidator : AbstractValidator<GazelleSettings>
    {
        public GazelleSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.Username).NotEmpty();
            RuleFor(c => c.Password).NotEmpty();

            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator());
        }
    }

    public class GazelleSettings : ITorrentIndexerSettings
    {
        private static readonly GazelleSettingsValidator Validator = new GazelleSettingsValidator();

        public GazelleSettings()
        {
            MinimumSeeders = IndexerDefaults.MINIMUM_SEEDERS;
        }

        public string AuthKey;
        public string PassKey;

        [FieldDefinition(0, Label = "provider.field.fe7a241debad5", Advanced = true, HelpText = "provider.field.ff76d14398efa")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Username", HelpText = "Username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, HelpText = "Password", Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [NotSensitive]
        [FieldDefinition(3, Type = FieldType.Checkbox, Label = "provider.field.ff65364a098fd", HelpText = "provider.field.f87dcdd74eed8", Advanced = true)]
        public bool UseFreeleechToken { get; set; }

        [FieldDefinition(4, Type = FieldType.Number, Label = "provider.field.fa4001360c131", Unit = "days", HelpText = "provider.field.f8ef2df0a1d96", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(5, Type = FieldType.Textbox, Label = "provider.field.fd783e4d6293b", HelpText = "provider.field.fb8265760181c", Advanced = true)]
        public int MinimumSeeders { get; set; }

        [FieldDefinition(6)]
        public SeedCriteriaSettings SeedCriteria { get; set; } = new SeedCriteriaSettings();

        [FieldDefinition(7, Type = FieldType.Checkbox, Label = "provider.field.feaf93d215894", HelpText = "provider.field.f0af80d5651c2", Advanced = true)]
        public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
