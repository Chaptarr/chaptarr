using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.FileList
{
    public class FileListSettingsValidator : AbstractValidator<FileListSettings>
    {
        public FileListSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.Username).NotEmpty();
            RuleFor(c => c.Passkey).NotEmpty();

            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator());
        }
    }

    public class FileListSettings : ITorrentIndexerSettings
    {
        private static readonly FileListSettingsValidator Validator = new FileListSettingsValidator();

        public FileListSettings()
        {
            BaseUrl = "https://filelist.io";
            MinimumSeeders = IndexerDefaults.MINIMUM_SEEDERS;

            Categories = new int[]
            {
                (int)FileListCategories.Docs
            };
        }

        [FieldDefinition(0, Label = "Username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(1, Label = "provider.field.f904c9aa9baae", Privacy = PrivacyLevel.ApiKey)]
        public string Passkey { get; set; }

        [FieldDefinition(3, Label = "provider.field.fed650c75c5c8", Advanced = true, HelpText = "provider.field.ffc3a81f414e1")]
        public string BaseUrl { get; set; }

        [FieldDefinition(4, Label = "Categories", Type = FieldType.Select, SelectOptions = typeof(FileListCategories), HelpText = "provider.field.f5a22d4632457")]
        public IEnumerable<int> Categories { get; set; }

        [FieldDefinition(5, Type = FieldType.Number, Label = "provider.field.fa4001360c131", Unit = "days", HelpText = "provider.field.f8ef2df0a1d96", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(6, Type = FieldType.Number, Label = "provider.field.fd783e4d6293b", HelpText = "provider.field.fb8265760181c", Advanced = true)]
        public int MinimumSeeders { get; set; }

        [FieldDefinition(7)]
        public SeedCriteriaSettings SeedCriteria { get; set; } = new SeedCriteriaSettings();

        [FieldDefinition(8, Type = FieldType.Checkbox, Label = "provider.field.feaf93d215894", HelpText = "provider.field.f0af80d5651c2", Advanced = true)]
        public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }

    public enum FileListCategories
    {
        [FieldOption(Label = "provider.option.o7af023c43013")]
        Docs = 16,
    }
}
