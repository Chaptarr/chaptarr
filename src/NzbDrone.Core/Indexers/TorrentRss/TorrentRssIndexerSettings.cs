using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.TorrentRss
{
    public class TorrentRssIndexerSettingsValidator : AbstractValidator<TorrentRssIndexerSettings>
    {
        public TorrentRssIndexerSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();

            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator());
        }
    }

    public class TorrentRssIndexerSettings : ITorrentIndexerSettings
    {
        private static readonly TorrentRssIndexerSettingsValidator Validator = new TorrentRssIndexerSettingsValidator();

        public TorrentRssIndexerSettings()
        {
            BaseUrl = string.Empty;
            AllowZeroSize = false;
            MinimumSeeders = IndexerDefaults.MINIMUM_SEEDERS;
        }

        [FieldDefinition(0, Label = "provider.field.fa5ccef673f2f")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "provider.field.f45823eaac0c8", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f741c6140ac25")]
        public string Cookie { get; set; }

        [FieldDefinition(2, Type = FieldType.Checkbox, Label = "provider.field.f3dd97a6dcd0a", HelpText = "provider.field.f3f2ee6aff77b")]
        public bool AllowZeroSize { get; set; }

        [FieldDefinition(3, Type = FieldType.Number, Label = "provider.field.fa4001360c131", Unit = "days", HelpText = "provider.field.f8ef2df0a1d96", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(4, Type = FieldType.Textbox, Label = "provider.field.fd783e4d6293b", HelpText = "provider.field.fb8265760181c", Advanced = true)]
        public int MinimumSeeders { get; set; }

        [FieldDefinition(5)]
        public SeedCriteriaSettings SeedCriteria { get; set; } = new SeedCriteriaSettings();

        [FieldDefinition(6, Type = FieldType.Checkbox, Label = "provider.field.feaf93d215894", HelpText = "provider.field.f0af80d5651c2", Advanced = true)]
        public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
