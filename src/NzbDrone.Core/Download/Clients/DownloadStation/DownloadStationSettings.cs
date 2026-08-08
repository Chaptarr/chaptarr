using System.Linq;
using System.Text.RegularExpressions;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.DownloadStation
{
    public class DownloadStationSettingsValidator : AbstractValidator<DownloadStationSettings>
    {
        public DownloadStationSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);

            // Download Station paths are remote and may be entered with or without a leading '/'.
            // Internally we normalize with TrimStart('/'), so treat leading '/' as valid input.
            RuleFor(c => c.TvDirectory).Must(dir => IsSafeDownloadStationDirectory(dir))
                                       .When(c => c.TvDirectory.IsNotNullOrWhiteSpace())
                                       .WithMessage("Invalid directory");

            RuleFor(c => c.AudiobookCategory).Matches(@"^\.?[-a-z]*$", RegexOptions.IgnoreCase).WithMessage("Allowed characters a-z and -");
            RuleFor(c => c.EbookCategory).Matches(@"^\.?[-a-z]*$", RegexOptions.IgnoreCase).WithMessage("Allowed characters a-z and -");

            RuleFor(c => c.AudiobookCategory).Empty()
                                      .When(c => c.TvDirectory.IsNotNullOrWhiteSpace())
                                      .WithMessage("Cannot use Category and Directory");

            RuleFor(c => c.EbookCategory).Empty()
                                      .When(c => c.TvDirectory.IsNotNullOrWhiteSpace())
                                      .WithMessage("Cannot use Category and Directory");

            RuleFor(c => c.MusicCategory).Empty()
                                      .When(c => c.TvDirectory.IsNotNullOrWhiteSpace())
                                      .WithMessage("Cannot use Category and Directory");
        }

        private static bool IsSafeDownloadStationDirectory(string directory)
        {
            if (directory.IsNullOrWhiteSpace())
            {
                return true;
            }

            // Allow optional leading '/', but reject traversal or empty after trimming.
            var trimmed = directory.Trim();
            var normalized = trimmed.TrimStart('/');

            if (normalized.IsNullOrWhiteSpace())
            {
                return false;
            }

            // Reject any path traversal segments.
            var parts = normalized.Split('\\', '/');
            return !parts.Any(p => p == "..");
        }
    }

    public class DownloadStationSettings : IProviderConfig
    {
        private static readonly DownloadStationSettingsValidator Validator = new DownloadStationSettingsValidator();

        private string _audiobookCategory;
        private string _ebookCategory;

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "Use SSL", Type = FieldType.Checkbox, HelpText = "Use secure connection when connecting to Download Station")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(4, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(5, Label = "Audiobook Category", Type = FieldType.Textbox, HelpText = "Adding a category specific to Chaptarr avoids conflicts with unrelated non-Chaptarr downloads. Using a category is optional, but strongly recommended. Creates a [category] subdirectory in the output directory.")]
        public string AudiobookCategory
        {
            get => _audiobookCategory ?? MusicCategory;
            set => _audiobookCategory = value;
        }

        [FieldDefinition(6, Label = "Ebook Category", Type = FieldType.Textbox, HelpText = "Adding a category specific to Chaptarr avoids conflicts with unrelated non-Chaptarr downloads. Using a category is optional, but strongly recommended. Creates a [category] subdirectory in the output directory.")]
        public string EbookCategory
        {
            get => _ebookCategory ?? MusicCategory;
            set => _ebookCategory = value;
        }

        [FieldDefinition(100, Label = "Category", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string MusicCategory { get; set; }

        [FieldDefinition(7, Label = "Directory", Type = FieldType.Textbox, HelpText = "Optional shared folder to put downloads into, leave blank to use the default Download Station location")]
        public string TvDirectory { get; set; }

        public DownloadStationSettings()
        {
            Host = "127.0.0.1";
            Port = 5000;
        }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
