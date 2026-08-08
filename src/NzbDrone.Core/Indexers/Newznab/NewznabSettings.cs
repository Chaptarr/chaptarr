using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Newznab
{
    public class NewznabSettingsValidator : AbstractValidator<NewznabSettings>
    {
        private static readonly string[] ApiKeyWhiteList =
        {
            "nzbs.org",
            "nzb.su",
            "nzb.life",
            "dognzb.cr",
            "nzbplanet.net",
            "nzbid.org",
            "nzbndx.com",
            "nzbindex.in"
        };

        private static bool ShouldHaveApiKey(NewznabSettings settings)
        {
            return settings.BaseUrl != null && ApiKeyWhiteList.Any(c => settings.BaseUrl.ToLowerInvariant().Contains(c));
        }

        private static readonly Regex AdditionalParametersRegex = new Regex(@"(&.+?\=.+?)+", RegexOptions.Compiled);

        private static bool IsValidRootUrlOrEmpty(string url)
        {
            return url.IsNullOrWhiteSpace() || url.IsValidHttpUrl();
        }

        public NewznabSettingsValidator()
        {
            RuleFor(c => c).Custom((c, context) =>
            {
                if (c.Categories.Empty())
                {
                    context.AddFailure("'Categories' must be provided");
                }

                if (c.EnableNarratorMetadata)
                {
                    var effectiveApiKey = c.NarratorMetadataApiKey.IsNotNullOrWhiteSpace() ? c.NarratorMetadataApiKey : c.ApiKey;
                    if (effectiveApiKey.IsNullOrWhiteSpace())
                    {
                        context.AddFailure("'Narrator Metadata API Key' must be provided when narrator metadata fetching is enabled");
                    }
                }
            });

            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.ApiPath).ValidUrlBase("/api");
            RuleFor(c => c.ApiKey).NotEmpty().When(ShouldHaveApiKey);
            RuleFor(c => c.NarratorMetadataBaseUrl).Must(IsValidRootUrlOrEmpty).WithMessage("must be valid URL that starts with http(s)://");
            RuleFor(c => c.AdditionalParameters).Matches(AdditionalParametersRegex)
                                                .When(c => !c.AdditionalParameters.IsNullOrWhiteSpace());
        }
    }

    public class NewznabSettings : IIndexerSettings
    {
        private static readonly NewznabSettingsValidator Validator = new NewznabSettingsValidator();

        public NewznabSettings()
        {
            ApiPath = "/api";
            Categories = new[] { 3030, 7020, 8010 };
        }

        [FieldDefinition(0, Label = "URL")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "API Path", HelpText = "Path to the api, usually /api", Advanced = true)]
        public string ApiPath { get; set; }

        [FieldDefinition(2, Label = "API Key", Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; }

        [FieldDefinition(3, Label = "Categories", Type = FieldType.Select, SelectOptionsProviderAction = "newznabCategories", HelpText = "Select every category this indexer should search. At search time Chaptarr routes by media type — audiobook searches use only your selected Audio (3000-3999) categories, ebook searches use only your selected Books (7000-7999) categories.")]
        public IEnumerable<int> Categories { get; set; }

        [FieldDefinition(4, Type = FieldType.Number, Label = "Early Download Limit", HelpText = "Time before release date Chaptarr will download from this indexer, empty is no limit", Unit = "days", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(5, Label = "Additional Parameters", HelpText = "Additional Newznab parameters", Advanced = true)]
        public string AdditionalParameters { get; set; }

        [FieldDefinition(6, Type = FieldType.Checkbox, Label = "Fetch Narrator Metadata", HelpText = "When a narrator-specific edition is selected, Chaptarr can use extra indexer API requests to populate narrator metadata before custom format scoring.")]
        public bool EnableNarratorMetadata { get; set; }

        [FieldDefinition(7, Label = "Narrator Metadata URL", HelpText = "Only needed when this indexer was added via Prowlarr. Leave blank to use the main URL.")]
        public string NarratorMetadataBaseUrl { get; set; }

        [FieldDefinition(8, Label = "Narrator Metadata API Key", HelpText = "Only needed when this indexer was added via Prowlarr. Leave blank to use the main API key.", Privacy = PrivacyLevel.ApiKey)]
        public string NarratorMetadataApiKey { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool EnableEnhancedSearching
        {
            get => false;
            set
            {
                if (value)
                {
                    EnableNarratorMetadata = true;
                }
            }
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string EnhancedSearchingBaseUrl
        {
            get => null;
            set
            {
                if (value.IsNotNullOrWhiteSpace())
                {
                    NarratorMetadataBaseUrl = value;
                }
            }
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string EnhancedSearchingApiKey
        {
            get => null;
            set
            {
                if (value.IsNotNullOrWhiteSpace())
                {
                    NarratorMetadataApiKey = value;
                }
            }
        }

        // If you add more fields here, keep TorznabSettings field indexes in sync.
        public virtual NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
