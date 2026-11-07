using System;
using System.Collections.Generic;
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
        private SeedCriteriaSettings _seedCriteria = new SeedCriteriaSettings();
        private bool _neverMoveOnImport;
        private int? _seedTimeHours;
        private bool _seedTimeComesFromHours;

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
            SearchInSeries = true;
            SearchLanguages = Array.Empty<int>();
        }

        [FieldDefinition(0, Label = "provider.field.feb650fa1243e", Hidden = HiddenType.Hidden)]
        public string BaseUrl { get; set; }

        private string _mamId;

        [FieldDefinition(1, Label = "provider.field.fa5d0dbd394ef", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "provider.field.f77f1b50c39ac")]
        public string MamId
        {
            get => _mamId;
            set => _mamId = string.IsNullOrEmpty(value) ? value : value.Trim();
        }

        [FieldDefinition(1, Label = "provider.field.f7b6980f35f3c", Type = FieldType.Checkbox, HelpText = "provider.field.f965f27b9207d", Advanced = true)]
        public bool MamSsl { get; set; }

        [FieldDefinition(2, Type = FieldType.Select, Label = "provider.field.f515ca00fdbc1", SelectOptions = typeof(MyAnonaMouseFreeleechWedgeAction), HelpText = "provider.field.f4c3929f32227")]
        public int UseFreeleechWedge { get; set; }

        [FieldDefinition(3, Type = FieldType.Checkbox, Label = "provider.field.fb3ba54306f30", HelpText = "provider.field.f5d22c0abb0f6")]
        public bool UseFreeleechOnlyForAudiobooks { get; set; }

        [FieldDefinition(4, Type = FieldType.Textbox, Label = "provider.field.fd783e4d6293b", HelpText = "provider.field.fb8265760181c")]
        public int MinimumSeeders { get; set; }

        [FieldDefinition(5, Type = FieldType.Checkbox, Label = "provider.field.fd1d33dec9414", HelpText = "provider.field.fda201a931c3d", Hidden = HiddenType.Hidden)]
        public bool EnableDeepSearch { get; set; }

        [FieldDefinition(6, Type = FieldType.Checkbox, Label = "provider.field.f5269857c4a80", HelpText = "provider.field.fddbb45739308", Hidden = HiddenType.Hidden)]
        public bool IntegrateWithAbs { get; set; }

        [FieldDefinition(7, Type = FieldType.Number, Label = "IndexerSettingsSeedTime", Unit = "hours", HelpText = "provider.field.f47441f72616d")]
        public int? SeedTimeHours
        {
            get => _seedTimeHours;
            set
            {
                _seedTimeHours = value;
                if (value is > 0)
                {
                    _seedCriteria.SeedTime = value.Value * 60;
                    _seedTimeComesFromHours = true;
                }
                else if (_seedTimeComesFromHours)
                {
                    _seedCriteria.SeedTime = null;
                    _seedTimeComesFromHours = false;
                }
            }
        }

        [FieldDefinition(8, Type = FieldType.Number, Label = "provider.field.fa4001360c131", Unit = "days", HelpText = "provider.field.f8ef2df0a1d96", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        // VIP Status - populated during connection test, hidden from editing (display-only elsewhere)
        [FieldDefinition(9, Type = FieldType.Textbox, Label = "provider.field.fb02d807fec76", HelpText = "provider.field.f3bd6c86c637a", Hidden = HiddenType.Hidden)]
        public string UserClass { get; set; }

        [FieldDefinition(10, Type = FieldType.Checkbox, Label = "provider.field.f7e906a8a7a41", HelpText = "provider.field.f3bd6c86c637a", Hidden = HiddenType.Hidden)]
        public bool IsVip { get; set; }

        [FieldDefinition(11, Type = FieldType.Checkbox, Label = "provider.field.fa29bea30de58", HelpText = "provider.field.fcc2215a99125", Advanced = true)]
        public bool ProtectUnsatisfiedSlots { get; set; }

        [FieldDefinition(12, Type = FieldType.Number, Label = "provider.field.fececc0ba3a53", Unit = "slots", HelpText = "provider.field.fd2727e11e19f")]
        public int UnsatisfiedSlotReserve { get; set; }

        [FieldDefinition(13, Type = FieldType.Number, Label = "provider.field.f5cb8dfbc2510", Unit = "slots", HelpText = "provider.field.f00d387283c10")]
        public int ManualGrabBuffer { get; set; }

        [FieldDefinition(14, Type = FieldType.Checkbox, Label = "IndexerSettingsNeverMoveOnImport", HelpText = "IndexerSettingsNeverMoveOnImportHelpText")]
        public bool NeverMoveOnImport
        {
            get => _neverMoveOnImport;
            set
            {
                _neverMoveOnImport = value;
                _seedCriteria.NeverMoveOnImport = value;
            }
        }

        // BookshelfNG exposed these MAM browse filters. Keep their serialized values and apply
        // them to Chaptarr's richer JSON search so existing indexer settings remain effective.
        [FieldDefinition(15, Type = FieldType.Select, Label = "provider.field.f9a57b04a9014", SelectOptions = typeof(MyAnonaMouseSearchType), HelpText = "provider.field.f5da30e901390")]
        public int? SearchType { get; set; }

        [FieldDefinition(16, Type = FieldType.Checkbox, Label = "provider.field.f7d8829f167e7")]
        public bool SearchInDescription { get; set; }

        [FieldDefinition(17, Type = FieldType.Checkbox, Label = "provider.field.f492adad97f7b")]
        public bool SearchInSeries { get; set; }

        [FieldDefinition(18, Type = FieldType.Checkbox, Label = "provider.field.f5bc9475e1083")]
        public bool SearchInFilenames { get; set; }

        [FieldDefinition(19, Type = FieldType.Select, Label = "provider.field.f794ff3ca7f46", SelectOptions = typeof(MyAnonaMouseSearchLanguages), HelpText = "provider.field.f38207e4f9b29")]
        public IEnumerable<int> SearchLanguages { get; set; }

        // Persisted provider snapshot used by the grab guard. These are not user-editable.
        public int? UnsatisfiedCount { get; set; }
        public int? UnsatisfiedLimit { get; set; }
        public DateTime? UnsatisfiedSnapshotUtc { get; set; }
        public DateTime? UnsatisfiedStatusRefreshedUtc { get; set; }

        // Keep the legacy seed criteria in the persisted settings and provider schema. The
        // SeedTimeHours field remains the native MAM control; this nested shape keeps existing
        // BookshelfNG settings and API clients round-trippable.
        [FieldDefinition(20)]
        public SeedCriteriaSettings SeedCriteria
        {
            get
            {
                if (_seedCriteria == null)
                {
                    _seedCriteria = new SeedCriteriaSettings();
                }

                if (!_seedCriteria.SeedTime.HasValue && SeedTimeHours is > 0)
                {
                    _seedCriteria.SeedTime = SeedTimeHours.Value * 60;
                }

                _seedCriteria.NeverMoveOnImport = NeverMoveOnImport;

                return _seedCriteria;
            }

            set
            {
                _seedCriteria = value ?? new SeedCriteriaSettings();
                _neverMoveOnImport = _seedCriteria.NeverMoveOnImport;
                _seedTimeComesFromHours = _seedTimeHours is > 0 &&
                                          _seedCriteria.SeedTime == _seedTimeHours.Value * 60;
            }
        }

        [FieldDefinition(21, Type = FieldType.Checkbox, Label = "provider.field.feaf93d215894", HelpText = "provider.field.f0af80d5651c2", Advanced = true)]
        public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }

    public enum MyAnonaMouseSearchType
    {
        [FieldOption(Label = "provider.option.o3e6bbeeff4ef", Hint = "provider.option.o415155739590")]
        All = 0,

        [FieldOption(Label = "provider.option.oc83a723bae52", Hint = "provider.option.o99a1dca2bd66")]
        Active = 1,

        [FieldOption(Label = "provider.option.o294a0c3d28c4", Hint = "provider.option.oaab534cd510a")]
        Freeleech = 2,

        [FieldOption(Label = "provider.option.o72a071498ead", Hint = "provider.option.ob31cf1a5e75d")]
        FreeleechOrVip = 3,

        [FieldOption(Label = "provider.option.o986e2890737d", Hint = "provider.option.o7c3056a206f0")]
        Vip = 4,

        [FieldOption(Label = "provider.option.obff67db96859", Hint = "provider.option.o7a21be239c11")]
        NotVip = 5
    }

    public enum MyAnonaMouseSearchLanguages
    {
        [FieldOption(Label = "provider.option.oba118bf7fc9c")] English = 1,
        [FieldOption(Label = "provider.option.o6884e4dc7300")] Afrikaans = 17,
        [FieldOption(Label = "provider.option.obafb66f32cd7")] Arabic = 32,
        [FieldOption(Label = "provider.option.oe7f4fe2631bb")] Bengali = 35,
        [FieldOption(Label = "provider.option.o1d25285d7182")] Bosnian = 51,
        [FieldOption(Label = "provider.option.oecd986094baf")] Bulgarian = 18,
        [FieldOption(Label = "provider.option.o2e9fa8e62006")] Burmese = 6,
        [FieldOption(Label = "provider.option.oad83a00f609f")] Cantonese = 44,
        [FieldOption(Label = "provider.option.o843c489421f4")] Catalan = 19,
        [FieldOption(Label = "provider.option.o8a9779f42128")] Chinese = 2,
        [FieldOption(Label = "provider.option.o754787c2f88c")] Croatian = 49,
        [FieldOption(Label = "provider.option.o8b463ea23a7e")] Czech = 20,
        [FieldOption(Label = "provider.option.ofe4338ad620c")] Danish = 21,
        [FieldOption(Label = "provider.option.o45f16144c18c")] Dutch = 22,
        [FieldOption(Label = "provider.option.o4d8bdb8972a2")] Estonian = 61,
        [FieldOption(Label = "provider.option.oe4d40d318e99")] Farsi = 39,
        [FieldOption(Label = "provider.option.o5710b435f90a")] Finnish = 23,
        [FieldOption(Label = "provider.option.o7458199fe97a")] French = 36,
        [FieldOption(Label = "provider.option.oa659b60d246d")] German = 37,
        [FieldOption(Label = "provider.option.o5c74e5b8100c")] Greek = 26,
        [FieldOption(Label = "provider.option.o60a7067462bf")] GreekAncient = 59,
        [FieldOption(Label = "provider.option.oaa1324f5df47")] Gujarati = 3,
        [FieldOption(Label = "provider.option.o33e00df2aac3")] Hebrew = 27,
        [FieldOption(Label = "provider.option.o63c2dc51d089")] Hindi = 8,
        [FieldOption(Label = "provider.option.o58801e6907f0")] Hungarian = 28,
        [FieldOption(Label = "provider.option.o2bca68c58bb4")] Icelandic = 63,
        [FieldOption(Label = "provider.option.o4fe1bb67f3b9")] Indonesian = 53,
        [FieldOption(Label = "provider.option.od65819e43e55")] Irish = 56,
        [FieldOption(Label = "provider.option.ofe1d9c7db376")] Italian = 43,
        [FieldOption(Label = "provider.option.obe4ee906d3ef")] Japanese = 38,
        [FieldOption(Label = "provider.option.o1d8b8d4e409f")] Javanese = 12,
        [FieldOption(Label = "provider.option.o4829406f52d7")] Kannada = 5,
        [FieldOption(Label = "provider.option.o04656c6fe003")] Korean = 41,
        [FieldOption(Label = "provider.option.o8625f6a2065b")] Lithuanian = 50,
        [FieldOption(Label = "provider.option.o2d38c456a2ec")] Latin = 46,
        [FieldOption(Label = "provider.option.o28fa038bb8bc")] Latvian = 62,
        [FieldOption(Label = "provider.option.ocd43b2d3b020")] Malay = 33,
        [FieldOption(Label = "provider.option.o2247ef59ce64")] Malayalam = 58,
        [FieldOption(Label = "provider.option.o54b0bff947fb")] Manx = 57,
        [FieldOption(Label = "provider.option.o6bbef3a79c78")] Marathi = 9,
        [FieldOption(Label = "provider.option.o71f4cf5c7a23")] Norwegian = 48,
        [FieldOption(Label = "provider.option.o770341d6e89e")] Polish = 45,
        [FieldOption(Label = "provider.option.o98b01b890687")] Portuguese = 34,
        [FieldOption(Label = "provider.option.o911140b89c71")] BrazilianPortuguese = 52,
        [FieldOption(Label = "provider.option.o9e61f8c4fbca")] Punjabi = 14,
        [FieldOption(Label = "provider.option.o096b861df653")] Romanian = 30,
        [FieldOption(Label = "provider.option.o5bcc40adf6e0")] Russian = 16,
        [FieldOption(Label = "provider.option.o1435d07cdf1c")] ScottishGaelic = 24,
        [FieldOption(Label = "provider.option.oa3ab29b2b806")] Sanskrit = 60,
        [FieldOption(Label = "provider.option.ob4a077dadb30")] Serbian = 31,
        [FieldOption(Label = "provider.option.odea070feef80")] Slovenian = 54,
        [FieldOption(Label = "provider.option.o3411059cb8e0")] Spanish = 4,
        [FieldOption(Label = "provider.option.o7285d46b982d")] CastilianSpanish = 55,
        [FieldOption(Label = "provider.option.oa89a9329d3d4")] Swedish = 40,
        [FieldOption(Label = "provider.option.o605eac303049")] Tagalog = 29,
        [FieldOption(Label = "provider.option.o135d78ac8f56")] Tamil = 11,
        [FieldOption(Label = "provider.option.oa4504b3d0ff7")] Telugu = 10,
        [FieldOption(Label = "provider.option.oad705ee19c04")] Thai = 7,
        [FieldOption(Label = "provider.option.o6e7aac65db01")] Turkish = 42,
        [FieldOption(Label = "provider.option.o6f424303fa12")] Ukrainian = 25,
        [FieldOption(Label = "provider.option.o2c6ef73fc9e2")] Urdu = 15,
        [FieldOption(Label = "provider.option.of25499e9df5c")] Vietnamese = 13,
        [FieldOption(Label = "Other")] Other = 47
    }

    public enum MyAnonaMouseFreeleechWedgeAction
    {
        [FieldOption(Label = "provider.option.o687198870652", Hint = "provider.option.od54cdf586b7b")]
        Never = 0,

        [FieldOption(Label = "provider.option.o6c3105944211", Hint = "provider.option.o9cb1fa5198db")]
        Preferred = 1,

        [FieldOption(Label = "provider.option.o0be971351722", Hint = "provider.option.o836f9584c3e0")]
        Required = 2,
    }
}
