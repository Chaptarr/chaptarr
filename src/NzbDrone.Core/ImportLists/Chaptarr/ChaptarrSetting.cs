using System;
using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Chaptarr
{
    public class ChaptarrSettingsValidator : AbstractValidator<ChaptarrSettings>
    {
        public ChaptarrSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.ApiKey).NotEmpty();
        }
    }

    public class ChaptarrSettings : IImportListSettings
    {
        private static readonly ChaptarrSettingsValidator Validator = new ChaptarrSettingsValidator();

        public ChaptarrSettings()
        {
            BaseUrl = "";
            ApiKey = "";
            ProfileIds = Array.Empty<int>();
            TagIds = Array.Empty<int>();
            RootFolderPaths = Array.Empty<string>();
        }

        [FieldDefinition(0, Label = "provider.field.f357d4eccfc02", HelpText = "provider.field.f75157c123901")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.fa09d60a7491d")]
        public string ApiKey { get; set; }

        [FieldDefinition(2, Type = FieldType.Select, SelectOptionsProviderAction = "getProfiles", Label = "Profiles", HelpText = "provider.field.f022785026db1")]
        public IEnumerable<int> ProfileIds { get; set; }

        [FieldDefinition(3, Type = FieldType.Select, SelectOptionsProviderAction = "getTags", Label = "Tags", HelpText = "provider.field.f39b32b0975f2")]
        public IEnumerable<int> TagIds { get; set; }

        [FieldDefinition(4, Type = FieldType.Select, SelectOptionsProviderAction = "getRootFolders", Label = "RootFolders", HelpText = "provider.field.fd4cf989d1fb3")]
        public IEnumerable<string> RootFolderPaths { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
