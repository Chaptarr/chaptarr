using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Torrentleech
{
    public class TorrentleechSettingsValidator : AbstractValidator<TorrentleechSettings>
    {
        public TorrentleechSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.ApiKey).NotEmpty();

            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator());
        }
    }

    public class TorrentleechSettings : ITorrentIndexerSettings
    {
        private static readonly TorrentleechSettingsValidator Validator = new TorrentleechSettingsValidator();

        public TorrentleechSettings()
        {
            BaseUrl = "http://rss.torrentleech.org";
            MinimumSeeders = IndexerDefaults.MINIMUM_SEEDERS;
        }

        [FieldDefinition(0, Label = "provider.field.feb650fa1243e")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; }

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
