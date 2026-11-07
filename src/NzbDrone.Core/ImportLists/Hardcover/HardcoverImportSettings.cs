using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Hardcover
{
    public class HardcoverImportSettingsValidator : AbstractValidator<HardcoverImportSettings>
    {
        public HardcoverImportSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.User).NotEmpty();
            RuleFor(c => c.ApiKey).NotEmpty();
            RuleFor(c => c.ListIds).NotEmpty();
        }
    }

    public class HardcoverImportSettings : IImportListSettings
    {
        private static readonly HardcoverImportSettingsValidator Validator = new ();

        public HardcoverImportSettings()
        {
            BaseUrl = "https://api.hardcover.app";
            ListIds = Array.Empty<string>();
        }

        [FieldDefinition(0, Label = "provider.field.f70589413a3c9", HelpText = "provider.field.f21d73e148c35")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey, HelpText = "provider.field.f8d223bd1b121")]
        public string ApiKey { get; set; }
        [FieldDefinition(2, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "provider.field.fc2ff59e65c06")]
        public string User { get; set; }
        [FieldDefinition(3, Type = FieldType.Select, SelectOptionsProviderAction = "getLists", Label = "Lists", HelpText = "provider.field.f258d6cc199ae")]
        public IEnumerable<string> ListIds { get; set; }

        public string ListId => ListIds?.FirstOrDefault();

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
