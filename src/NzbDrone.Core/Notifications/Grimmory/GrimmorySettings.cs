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

        [FieldDefinition(0, Label = "URL", HelpText = "Grimmory URL, including http(s):// and port, e.g. http://grimmory:6060. Grimmory must see the same files as Chaptarr (shared or identically mounted storage)")]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "Grimmory user with permission to manage libraries")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(3, Label = "Ebook Library", Type = FieldType.Select, SelectOptionsProviderAction = "getLibraries", HelpText = "Grimmory library to refresh when Chaptarr imports, renames or deletes ebook files. Leave unset to ignore ebooks")]
        public long EbookLibraryId { get; set; }

        [FieldDefinition(4, Label = "Audiobook Library", Type = FieldType.Select, SelectOptionsProviderAction = "getLibraries", HelpText = "Grimmory library to refresh when Chaptarr imports, renames or deletes audiobook files. Leave unset to ignore audiobooks")]
        public long AudiobookLibraryId { get; set; }

        [FieldDefinition(5, Label = "Push Metadata", Type = FieldType.Checkbox, HelpText = "Push Chaptarr's metadata for a book to Grimmory, locking the pushed fields there, whenever the book is imported, retagged, or its metadata changes in Chaptarr")]
        public bool PushMetadata { get; set; }

        [FieldDefinition(6, Label = "Push Covers", Type = FieldType.Checkbox, HelpText = "Push Chaptarr's cover image for a book to Grimmory whenever the book is imported, retagged, or its cover changes in Chaptarr")]
        public bool PushCovers { get; set; }

        [FieldDefinition(7, Label = "Forward Grimmory Edits", Type = FieldType.Checkbox, HelpText = "Forward metadata and cover edits made in Grimmory to other connections that accept library edits. Requires Grimmory's sidecar 'write on update' setting so edits appear as sidecar files Chaptarr can watch for. Grimmory only writes sidecars when it runs with DISK_TYPE=LOCAL; on network storage the setting is hidden and nothing is forwarded")]
        public bool ForwardEdits { get; set; }

        [FieldDefinition(8, Label = "Ignore Grimmory Tags", Type = FieldType.Tag, HelpText = "Tags set on the book in Grimmory, not Chaptarr tags, e.g. Processed. Automatic pushes and forwarded edits leave Grimmory books that carry any of these tags untouched. The Grimmory Push dialog and Push Chaptarr Metadata to Grimmory still update them")]
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
