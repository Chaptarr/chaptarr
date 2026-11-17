using NzbDrone.Common.Disk;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace NzbDrone.Core.MediaCover
{
    public interface IImageResizer
    {
        void Resize(string source, string destination, int height);
    }

    public class ImageResizer : IImageResizer
    {
        private readonly IDiskProvider _diskProvider;

        public ImageResizer(IDiskProvider diskProvider)
        {
            _diskProvider = diskProvider;

            // Thumbnails don't need super high quality
            SixLabors.ImageSharp.Configuration.Default.ImageFormatsManager.SetEncoder(JpegFormat.Instance, new JpegEncoder
            {
                Quality = 95,
                ColorType = JpegColorType.YCbCrRatio444
            });
        }

        public void Resize(string source, string destination, int height)
        {
            using var image = Image.Load(source);
            image.Mutate(x => x.Resize(0, height));

            // The temporary path retains the destination extension for ImageSharp's encoder.
            _diskProvider.WriteFileAtomically(destination, temporaryPath => image.Save(temporaryPath));
        }
    }
}
