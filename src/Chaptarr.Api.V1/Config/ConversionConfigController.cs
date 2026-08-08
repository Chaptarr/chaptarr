using System.Linq;
using Chaptarr.Http;
using FluentValidation;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/conversion")]
    public class ConversionConfigController : ConfigController<ConversionConfigResource>
    {
        private static readonly string[] ValidEbookTargetFormats =
        {
            "epub",
            "azw3",
            "mobi",
            "pdf"
        };

        private static readonly string[] ValidAudioChannels =
        {
            "source",
            "mono"
        };

        private static readonly string[] ValidTagModes =
        {
            ConversionTagModes.Preserve,
            ConversionTagModes.Clean
        };

        public ConversionConfigController(IConfigService configService)
            : base(configService)
        {
            SharedValidator.RuleFor(c => c.AudiobookConversionConcurrentConversions)
                .InclusiveBetween(1, 16);

            SharedValidator.RuleFor(c => c.AudiobookConversionMaxBitrate)
                .InclusiveBetween(16, 320);

            SharedValidator.RuleFor(c => c.AudiobookConversionMaxCpuThreads)
                .InclusiveBetween(1, 64);

            SharedValidator.RuleFor(c => c.AudiobookConversionMaxCpuThreads)
                .GreaterThanOrEqualTo(c => c.AudiobookConversionConcurrentConversions)
                .WithMessage("Max CPU threads must be greater than or equal to concurrent conversions");

            SharedValidator.RuleFor(c => c.AudiobookConversionAudioChannels)
                .Must(value => value != null && ValidAudioChannels.Contains(value))
                .WithMessage("Invalid audiobook channel mode");

            SharedValidator.RuleFor(c => c.AudiobookConversionTagMode)
                .Must(value => value != null && ValidTagModes.Contains(value))
                .WithMessage("Invalid audiobook tag mode");

            SharedValidator.RuleFor(c => c.EbookConversionTargetFormat)
                .Must(value => value != null && ValidEbookTargetFormats.Contains(value))
                .WithMessage("Invalid ebook conversion target format");
        }

        protected override ConversionConfigResource ToResource(IConfigService model)
        {
            return ConversionConfigResourceMapper.ToResource(model);
        }
    }
}
