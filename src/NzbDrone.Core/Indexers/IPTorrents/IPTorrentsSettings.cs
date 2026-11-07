using System.Text.RegularExpressions;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.IPTorrents
{
    public class IPTorrentsSettingsValidator : AbstractValidator<IPTorrentsSettings>
    {
        public IPTorrentsSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();

            RuleFor(c => c.BaseUrl).Matches(@"(?:/|t\.)rss\?.+$");

            RuleFor(c => c.BaseUrl).Matches(@"(?:/|t\.)rss\?.+;download(?:;|$)")
                .WithMessage("Use Direct Download Url (;download)")
                .When(v => v.BaseUrl.IsNotNullOrWhiteSpace() && Regex.IsMatch(v.BaseUrl, @"(?:/|t\.)rss\?.+$"));

            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator());
        }
    }

    public class IPTorrentsSettings : ITorrentIndexerSettings
    {
        private static readonly IPTorrentsSettingsValidator Validator = new IPTorrentsSettingsValidator();

        public IPTorrentsSettings()
        {
            MinimumSeeders = IndexerDefaults.MINIMUM_SEEDERS;
        }

        [FieldDefinition(0, Label = "provider.field.fb623f8b84569", HelpText = "provider.field.f12af4c995bd9")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Type = FieldType.Number, Label = "provider.field.fa4001360c131", Unit = "days", HelpText = "provider.field.f8ef2df0a1d96", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(2, Type = FieldType.Textbox, Label = "provider.field.fd783e4d6293b", HelpText = "provider.field.fb8265760181c", Advanced = true)]
        public int MinimumSeeders { get; set; }

        [FieldDefinition(3)]
        public SeedCriteriaSettings SeedCriteria { get; set; } = new SeedCriteriaSettings();

        [FieldDefinition(4, Type = FieldType.Checkbox, Label = "provider.field.feaf93d215894", HelpText = "provider.field.f0af80d5651c2", Advanced = true)]
        public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
