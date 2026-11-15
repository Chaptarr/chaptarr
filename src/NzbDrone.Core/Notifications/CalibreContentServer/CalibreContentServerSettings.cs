using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.CalibreContentServer
{
    public class CalibreContentServerSettingsValidator : AbstractValidator<CalibreContentServerSettings>
    {
        public CalibreContentServerSettingsValidator()
        {
            RuleFor(c => c.Url).NotEmpty().WithMessage("URL cannot be empty");
            RuleFor(c => c.Url).IsValidUrl().When(c => c.Url.IsNotNullOrWhiteSpace());
            RuleFor(c => c.Username).NotEmpty().When(c => c.Password.IsNotNullOrWhiteSpace());
            RuleFor(c => c.Password).NotEmpty().When(c => c.Username.IsNotNullOrWhiteSpace());
        }
    }

    public class CalibreContentServerSettings : IProviderConfig
    {
        private static readonly CalibreContentServerSettingsValidator Validator = new CalibreContentServerSettingsValidator();

        [FieldDefinition(0, Label = "URL", HelpText = "Calibre content server URL, including http(s):// and port, e.g. http://localhost:8080", HelpTextWarning = "This connection functions as a one-way mirror: Chaptarr pushes books and its own deletions to this content server. Deleting a book on the content server itself is NOT reflected back into Chaptarr, and the book may be pushed to the server again later.")]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "Optional, only needed if the content server requires authentication")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "Optional, only needed if the content server requires authentication")]
        public string Password { get; set; }

        [FieldDefinition(3, Label = "Allow Edits & Deletes", Type = FieldType.Checkbox, HelpText = "Allow Chaptarr to edit or delete matching books on the content server when books change in Chaptarr or are deleted from Chaptarr")]
        public bool SyncChanges { get; set; }

        [FieldDefinition(4, Label = "Push Library Scan Imports", Type = FieldType.Checkbox, HelpText = "Also push books that arrive via library scans, for example books added through calibre-web, to this content server")]
        public bool PushLibraryImports { get; set; }

        [FieldDefinition(5, Label = "Push Library Edits", Type = FieldType.Checkbox, HelpText = "When Chaptarr changes a book - a calibre push, a retag, or an edit - push the updated record to this content server")]
        public bool PushLibraryEdits { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
