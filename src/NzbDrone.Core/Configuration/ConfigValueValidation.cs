using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Configuration
{
    public static class ConfigValueValidation
    {
        public static List<ValidationFailure> ValidateProxy(ProxyDefinition model)
        {
            var validator = new InlineValidator<ProxyDefinition>();
            validator.RuleFor(value => value.Name).NotEmpty();
            validator.RuleFor(value => value.Hostname).NotEmpty();
            validator.RuleFor(value => value.Port).InclusiveBetween(1, 65535);
            validator.RuleFor(value => value.ProxyType).IsInEnum();
            return validator.Validate(model).Errors.ToList();
        }

        public static List<ValidationFailure> ValidateDelayProfile(DelayProfile model)
        {
            var validator = new InlineValidator<DelayProfile>();
            validator.RuleFor(value => value.Tags).NotEmpty().When(value => value.Id != 1);
            validator.RuleFor(value => value.Tags).Empty().When(value => value.Id == 1);
            validator.RuleFor(value => value.UsenetDelay).GreaterThanOrEqualTo(0);
            validator.RuleFor(value => value.TorrentDelay).GreaterThanOrEqualTo(0);
            validator.RuleFor(value => value).Must(value => value.EnableUsenet || value.EnableTorrent)
                .WithMessage("Either Usenet or Torrent should be enabled");
            return validator.Validate(model).Errors.ToList();
        }

        public static List<ValidationFailure> ValidateReleaseProfile(ReleaseProfile model)
        {
            var validator = new InlineValidator<ReleaseProfile>();
            validator.RuleFor(value => value).Must(value => value.Required?.Any() == true || value.Ignored?.Any() == true)
                .WithMessage("Either 'Must contain' or 'Must not contain' is required");
            return validator.Validate(model).Errors.ToList();
        }

        public static List<ValidationFailure> ValidateNaming(NamingConfig model)
        {
            var validator = new InlineValidator<NamingConfig>();
            validator.RuleFor(value => value.StandardBookFormat).ValidBookFormat();
            validator.RuleFor(value => value.AuthorFolderFormat).ValidAuthorFolderFormat();
            validator.RuleFor(value => value.EbookStandardBookFormat).ValidBookFormat().When(value => value.EbookStandardBookFormat != null);
            validator.RuleFor(value => value.EbookAuthorFolderFormat).ValidAuthorFolderFormat().When(value => value.EbookAuthorFolderFormat != null);
            return validator.Validate(model).Errors.ToList();
        }

        public static List<ValidationFailure> ValidateMetadataProfile(MetadataProfile model)
        {
            var validator = new InlineValidator<MetadataProfile>();
            validator.RuleFor(value => value.Name).NotEqual("None").WithMessage("'None' is a reserved profile name").NotEmpty();
            validator.RuleFor(value => value.ProfileType).IsInEnum();
            validator.RuleFor(value => value.MinPopularity).GreaterThanOrEqualTo(0);
            validator.RuleFor(value => value.MinPages).GreaterThanOrEqualTo(0);
            validator.RuleFor(value => value.AllowedLanguages).Must(value => value.Trim(',').Split(',').Select(language => language.Trim())
                .Where(language => !string.IsNullOrWhiteSpace(language)).All(language =>
                    language.Equals("null", StringComparison.OrdinalIgnoreCase) || language.Equals("unknown", StringComparison.OrdinalIgnoreCase) || language.CanonicalizeLanguage().IsNotNullOrWhiteSpace()))
                .When(value => value.AllowedLanguages.IsNotNullOrWhiteSpace()).WithMessage("Unknown languages");
            return validator.Validate(model).Errors.ToList();
        }

        public static bool IsRssInterval(int value) => value == 0 || value is >= 10 and <= 120;

        public static bool HasChanged(object proposed, object stored) => !Equals(proposed, stored);

        public static List<ValidationFailure> Validate(object settings, object stored = null)
        {
            var errors = new List<ValidationFailure>();
            if (settings == null)
            {
                return errors;
            }
            var properties = (settings is IConfigService ? typeof(IConfigService) : settings.GetType()).GetProperties()
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0).ToList();
            var values = properties.ToDictionary(property => property.Name, property => property.GetValue(settings));
            var previous = stored == null ? null : properties.ToDictionary(property => property.Name, property => property.GetValue(stored));
            foreach (var entry in values)
            {
                if (previous != null && !HasChanged(entry.Value, previous[entry.Key]) &&
                    !(entry.Key == nameof(IConfigService.AudiobookConversionMaxCpuThreads) &&
                      HasChanged(values.GetValueOrDefault(nameof(IConfigService.AudiobookConversionConcurrentConversions)), previous.GetValueOrDefault(nameof(IConfigService.AudiobookConversionConcurrentConversions)))))
                    continue;
                var invalid = entry.Key switch
                {
                    nameof(IConfigService.UILanguage) => entry.Value is not int language || language < 1 || !Language.All.Any(item => item.Id == language),
                    nameof(IConfigService.AddNewDefaultMediaType) => entry.Value is string media && !string.IsNullOrWhiteSpace(media) && media.Trim().ToLowerInvariant() is not ("audiobook" or "ebook" or "both"),
                    nameof(IConfigService.RssSyncInterval) => entry.Value is not int interval || !IsRssInterval(interval),
                    nameof(IConfigService.MinimumAge) or nameof(IConfigService.MaximumSize) or nameof(IConfigService.Retention) or nameof(IConfigService.RecycleBinCleanupDays) => entry.Value is not int number || number < 0,
                    nameof(IConfigService.MinimumFreeSpaceWhenImporting) => entry.Value is not int space || space < 100,
                    nameof(IConfigService.AudiobookConversionConcurrentConversions) => entry.Value is not int conversions || conversions is < 1 or > 16,
                    nameof(IConfigService.AudiobookConversionMaxBitrate) => entry.Value is not int bitrate || bitrate is < 16 or > 320,
                    nameof(IConfigService.AudiobookConversionMaxCpuThreads) => entry.Value is not int threads || threads is < 1 or > 64 || threads < (int)values.GetValueOrDefault(nameof(IConfigService.AudiobookConversionConcurrentConversions), 1),
                    nameof(IConfigService.AudiobookConversionAudioChannels) => entry.Value is not ("source" or "mono"),
                    nameof(IConfigService.AudiobookConversionTagMode) => entry.Value is not (ConversionTagModes.Preserve or ConversionTagModes.Clean),
                    nameof(IConfigService.EbookConversionTargetFormat) => entry.Value is not ("epub" or "azw3" or "mobi" or "pdf"),
                    _ => entry.Value is Enum value && !Enum.IsDefined(value.GetType(), value)
                };
                if (invalid)
                {
                    var message = entry.Key switch
                    {
                        nameof(IConfigService.UILanguage) => "Select a language supported by this version of Chaptarr",
                        nameof(IConfigService.AddNewDefaultMediaType) => "Must be audiobook, ebook, or both",
                        nameof(IConfigService.RssSyncInterval) => "Must be 0 to disable or between 10 and 120 minutes",
                        nameof(IConfigService.MinimumAge) or nameof(IConfigService.MaximumSize) or nameof(IConfigService.Retention) or nameof(IConfigService.RecycleBinCleanupDays) => "Must be greater than or equal to 0",
                        nameof(IConfigService.MinimumFreeSpaceWhenImporting) => "Must be greater than or equal to 100 MB",
                        nameof(IConfigService.AudiobookConversionConcurrentConversions) => "Must be between 1 and 16",
                        nameof(IConfigService.AudiobookConversionMaxBitrate) => "Must be between 16 and 320 kbps",
                        nameof(IConfigService.AudiobookConversionMaxCpuThreads) => "Must be between 1 and 64 and at least the number of concurrent conversions",
                        nameof(IConfigService.AudiobookConversionAudioChannels) => "Must be source or mono",
                        nameof(IConfigService.AudiobookConversionTagMode) => "Must be preserve or clean",
                        nameof(IConfigService.EbookConversionTargetFormat) => "Must be epub, azw3, mobi, or pdf",
                        _ => "Select a supported value"
                    };
                    errors.Add(new ValidationFailure(entry.Key, message));
                }
            }
            return errors;
        }
    }
}
