using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class GrimmorySettingsValidator : AbstractValidator<GrimmorySettings>
    {
        public GrimmorySettingsValidator()
        {
            RuleFor(c => c.Url).NotEmpty().WithMessage("URL cannot be empty");
            RuleFor(c => c.Url).IsValidUrl().When(c => c.Url.IsNotNullOrWhiteSpace());
            RuleFor(c => c.Username).NotEmpty().WithMessage("Username is required");
            RuleFor(c => c.Password).NotEmpty().WithMessage("Password is required");
            RuleFor(c => c.EbookLibraryId)
                .GreaterThan(0)
                .When(c => c.AudiobookLibraryId <= 0)
                .WithMessage("At least one library is required");
        }
    }

    public class GrimmorySettings : IProviderConfig
    {
        private static readonly GrimmorySettingsValidator Validator = new GrimmorySettingsValidator();

        [FieldDefinition(0, Label = "provider.field.fe7a241debad5", HelpText = "provider.field.f9d0dec541ca6")]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "provider.field.fd237f4cb52a2")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(3, Label = "provider.field.f1c9fa4c0dbf4", Type = FieldType.Select, SelectOptionsProviderAction = "getLibraries", HelpText = "provider.field.f624a087218b7")]
        public long EbookLibraryId { get; set; }

        [FieldDefinition(4, Label = "provider.field.f8b02155aecf5", Type = FieldType.Select, SelectOptionsProviderAction = "getLibraries", HelpText = "provider.field.f96c19c28b26b")]
        public long AudiobookLibraryId { get; set; }

        [FieldDefinition(5, Label = "PushMetadata", Type = FieldType.Checkbox, HelpText = "provider.field.f8ef01f9fc65e")]
        public bool PushMetadata { get; set; }

        [FieldDefinition(6, Label = "provider.field.ff2286e3d1df8", Type = FieldType.Checkbox, HelpText = "provider.field.f0d128645275f")]
        public bool PushCovers { get; set; }

        [FieldDefinition(7, Label = "provider.field.feccfce2a7fdf", Type = FieldType.Checkbox, HelpText = "provider.field.f9da80bea42a7")]
        public bool ForwardEdits { get; set; }

        [FieldDefinition(8, Label = "provider.field.feb693dfac8a0", Type = FieldType.Tag, HelpText = "provider.field.f709b8e79d1cd")]
        public IEnumerable<string> IgnoreTags { get; set; } = Array.Empty<string>();

        public bool HasIgnoreTag(IEnumerable<string> tags)
        {
            var ignored = (IgnoreTags ?? Enumerable.Empty<string>())
                .Where(t => t.IsNotNullOrWhiteSpace())
                .Select(t => t.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return ignored.Count > 0 && (tags ?? Enumerable.Empty<string>()).Any(t => t != null && ignored.Contains(t.Trim()));
        }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
