using System;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MyAnonaMouseSettingsValidator : AbstractValidator<MyAnonaMouseSettings>
    {
        public MyAnonaMouseSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.MamId).NotEmpty();

            RuleFor(c => c.UnsatisfiedSlotReserve).GreaterThanOrEqualTo(0);
            RuleFor(c => c.ManualGrabBuffer).GreaterThanOrEqualTo(0);
            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator(seedTimeMinimum: 72 * 60));
        }
    }

    public class MyAnonaMouseSettings : ITorrentIndexerSettings
    {
        private static readonly MyAnonaMouseSettingsValidator Validator = new MyAnonaMouseSettingsValidator();
        private const int LegacyRequiredWedgeAction = 2;
        private int _useFreeleechWedge;

        public MyAnonaMouseSettings()
        {
            BaseUrl = "https://www.myanonamouse.net";
            MinimumSeeders = IndexerDefaults.MINIMUM_SEEDERS;
            EnableDeepSearch = true; // Default to enabled for better metadata
            IntegrateWithAbs = false; // Default to disabled (hidden in UI)
            UseFreeleechOnlyForAudiobooks = true; // Default to audiobooks-only
            MamSsl = true; // Default to true since most sessions use mam_ssl
            ProtectUnsatisfiedSlots = true;
            UnsatisfiedSlotReserve = 5;
            ManualGrabBuffer = 0;
        }

        [FieldDefinition(0, Label = "Website URL", Hidden = HiddenType.Hidden)]
        public string BaseUrl { get; set; }

        private string _mamId;

        [FieldDefinition(1, Label = "mam_id", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "The mam_id is found under Preferences > Security")]
        public string MamId
        {
            get => _mamId;
            set => _mamId = string.IsNullOrEmpty(value) ? value : value.Trim();
        }

        [FieldDefinition(1, Label = "mam_ssl", Type = FieldType.Checkbox, HelpText = "Set if your MAM session uses the mam_ssl cookie (most accounts do). This adds mam_ssl=1 to requests.", Advanced = true)]
        public bool MamSsl { get; set; }

        [FieldDefinition(2, Type = FieldType.Select, Label = "Freeleech Wedges", SelectOptions = typeof(MyAnonaMouseFreeleechWedgeAction), HelpText = "Follow your MAM site preference, or ask MAM to apply a personal freeleech wedge when grabbing an eligible torrent. Prefer Wedge falls back to a normal download if MAM cannot apply one.")]
        public int UseFreeleechWedge
        {
            get => _useFreeleechWedge;
            set => _useFreeleechWedge = value == LegacyRequiredWedgeAction
                ? (int)MyAnonaMouseFreeleechWedgeAction.Never
                : value;
        }

        [FieldDefinition(3, Type = FieldType.Checkbox, Label = "Only Use Wedges for Audiobooks", HelpText = "Do not request a wedge for ebook torrents. Chaptarr also avoids requesting wedges for torrents already marked free, personal freeleech, or VIP.")]
        public bool UseFreeleechOnlyForAudiobooks { get; set; }

        [FieldDefinition(4, Type = FieldType.Textbox, Label = "Minimum Seeders", HelpText = "Minimum number of seeders required.")]
        public int MinimumSeeders { get; set; }

        [FieldDefinition(5, Type = FieldType.Checkbox, Label = "Enable Deep Search", HelpText = "Use JSON API with enhanced metadata including title, description, narrator, and duration information when available", Hidden = HiddenType.Hidden)]
        public bool EnableDeepSearch { get; set; }

        [FieldDefinition(6, Type = FieldType.Checkbox, Label = "Integrate with Audiobookshelf", HelpText = "If you've connected ABS this will enhance your MAM searching", Hidden = HiddenType.Hidden)]
        public bool IntegrateWithAbs { get; set; }

        [FieldDefinition(7, Type = FieldType.Number, Label = "Seed Time", Unit = "hours", HelpText = "Number of hours to seed after download completes. MAM requires 72 hours within 30 days; values below 72 hours will warn.")]
        public int? SeedTimeHours { get; set; }

        [FieldDefinition(8, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Chaptarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        // VIP Status - populated during connection test, hidden from editing (display-only elsewhere)
        [FieldDefinition(9, Type = FieldType.Textbox, Label = "MAM User Class", HelpText = "Detected on connection test", Hidden = HiddenType.Hidden)]
        public string UserClass { get; set; }

        [FieldDefinition(10, Type = FieldType.Checkbox, Label = "VIP Member", HelpText = "Detected on connection test", Hidden = HiddenType.Hidden)]
        public bool IsVip { get; set; }

        [FieldDefinition(11, Type = FieldType.Checkbox, Label = "Protect Unsatisfied Slots", HelpText = "Pause MAM grabs before your unsatisfied-torrent limit is reached. Disabling this can trigger MAM's 24-hour download suspension.", Advanced = true)]
        public bool ProtectUnsatisfiedSlots { get; set; }

        [FieldDefinition(12, Type = FieldType.Number, Label = "Safety Reserve", Unit = "slots", HelpText = "Slots Chaptarr will always keep free to protect against MAM's reporting delay and downloads started outside Chaptarr. Neither automatic nor Interactive Search grabs can use this reserve.")]
        public int UnsatisfiedSlotReserve { get; set; }

        [FieldDefinition(13, Type = FieldType.Number, Label = "User Buffer", Unit = "slots", HelpText = "Additional slots automatic grabs will keep free for releases you choose in Interactive Search or download outside Chaptarr. Interactive Search can use this buffer, but never the Safety Reserve.")]
        public int ManualGrabBuffer { get; set; }

        [FieldDefinition(14, Type = FieldType.Checkbox, Label = "IndexerSettingsNeverMoveOnImport", HelpText = "IndexerSettingsNeverMoveOnImportHelpText")]
        public bool NeverMoveOnImport { get; set; }

        // Persisted provider snapshot used by the grab guard. These are not user-editable.
        public int? UnsatisfiedCount { get; set; }
        public int? UnsatisfiedLimit { get; set; }
        public DateTime? UnsatisfiedSnapshotUtc { get; set; }
        public DateTime? UnsatisfiedStatusRefreshedUtc { get; set; }

        // Required by ITorrentIndexerSettings but hidden from UI
        public SeedCriteriaSettings SeedCriteria
        {
            get
            {
                var criteria = new SeedCriteriaSettings();
                if (SeedTimeHours.HasValue && SeedTimeHours.Value > 0)
                {
                    criteria.SeedTime = SeedTimeHours.Value * 60;
                }

                criteria.NeverMoveOnImport = NeverMoveOnImport;

                return criteria;
            }

            set
            {
            }
        }

        // Required by ITorrentIndexerSettings but hidden from UI
        public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }

    public enum MyAnonaMouseFreeleechWedgeAction
    {
        [FieldOption(Label = "Follow MAM Preferences", Hint = "Do not force a wedge; let your MAM account preferences decide")]
        Never = 0,

        [FieldOption(Label = "Prefer Wedge", Hint = "Request a personal freeleech wedge when eligible, then fall back to a normal download if unavailable")]
        Preferred = 1,

        [Obsolete("MAM's zero-wedge response is not documented well enough to guarantee required-wedge behavior.")]
        Required = 2,
    }
}
