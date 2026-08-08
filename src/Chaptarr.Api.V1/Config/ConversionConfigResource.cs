using Chaptarr.Http.REST;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Api.V1.Config
{
    public class ConversionConfigResource : RestResource
    {
        public int AudiobookConversionConcurrentConversions { get; set; }
        public int AudiobookConversionMaxBitrate { get; set; }
        public int AudiobookConversionMaxCpuThreads { get; set; }
        public bool AudiobookConversionNoUpscale { get; set; }
        public string AudiobookConversionAudioChannels { get; set; }
        public string AudiobookConversionTagMode { get; set; }

        public bool EbookConversionEnabled { get; set; }
        public string EbookConversionTargetFormat { get; set; }
    }

    public static class ConversionConfigResourceMapper
    {
        public static ConversionConfigResource ToResource(IConfigService model)
        {
            return new ConversionConfigResource
            {
                AudiobookConversionConcurrentConversions = model.AudiobookConversionConcurrentConversions,
                AudiobookConversionMaxBitrate = model.AudiobookConversionMaxBitrate,
                AudiobookConversionMaxCpuThreads = model.AudiobookConversionMaxCpuThreads,
                AudiobookConversionNoUpscale = model.AudiobookConversionNoUpscale,
                AudiobookConversionAudioChannels = model.AudiobookConversionAudioChannels,
                AudiobookConversionTagMode = model.AudiobookConversionTagMode,
                EbookConversionEnabled = model.EbookConversionEnabled,
                EbookConversionTargetFormat = model.EbookConversionTargetFormat
            };
        }
    }
}
