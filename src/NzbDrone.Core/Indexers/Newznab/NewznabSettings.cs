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

        [FieldDefinition(0, Label = "provider.field.fe7a241debad5")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "provider.field.fa2802a05b95a", HelpText = "provider.field.f41feb484ad69", Advanced = true)]
        public string ApiPath { get; set; }

        [FieldDefinition(2, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; }

        [FieldDefinition(3, Label = "Categories", Type = FieldType.Select, SelectOptionsProviderAction = "newznabCategories", HelpText = "provider.field.ff8e41f202b43")]
        public IEnumerable<int> Categories { get; set; }

        [FieldDefinition(4, Type = FieldType.Number, Label = "provider.field.fa4001360c131", HelpText = "provider.field.f8ef2df0a1d96", Unit = "days", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(5, Label = "provider.field.f8d4ecad7c7e2", HelpText = "provider.field.f02bb1c85f42f", Advanced = true)]
        public string AdditionalParameters { get; set; }

        [FieldDefinition(6, Type = FieldType.Checkbox, Label = "provider.field.f3364770903f3", HelpText = "provider.field.f0a18711a3171")]
        public bool EnableNarratorMetadata { get; set; }

        [FieldDefinition(7, Label = "provider.field.f8a0beb68a93f", HelpText = "provider.field.fd39d57d52f36")]
        public string NarratorMetadataBaseUrl { get; set; }

        [FieldDefinition(8, Label = "provider.field.f826b98aab5a6", HelpText = "provider.field.f32c4953d91ca", Privacy = PrivacyLevel.ApiKey)]
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
