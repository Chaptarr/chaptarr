using System;
using System.IO;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using SkiaSharp;

namespace NzbDrone.Core.MediaCover
{
    public interface IImageResizer
    {
        void Resize(string source, string destination, int height);
    }

    public class ImageResizer : IImageResizer
    {
        private static readonly SKSamplingOptions ResizeSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
        private readonly IDiskProvider _diskProvider;
        private readonly bool _enabled;

        public ImageResizer(IDiskProvider diskProvider, IPlatformInfo platformInfo)
        {
            _diskProvider = diskProvider;
            // SkiaSharp has no FreeBSD native assets. The cover endpoint can
            // serve the original image when a resized rendition is unavailable.
            _enabled = !OperatingSystem.IsFreeBSD();
        }

        public void Resize(string source, string destination, int height)
        {
            if (!_enabled)
            {
                throw new PlatformNotSupportedException("Cover resizing is unavailable on FreeBSD because SkiaSharp native assets are not shipped for that platform.");
            }

            try
            {
                var extension = Path.GetExtension(destination).ToLowerInvariant();
                var format = extension switch
                {
                    ".jpg" or ".jpeg" or ".jfif" => SKEncodedImageFormat.Jpeg,
                    ".png" => SKEncodedImageFormat.Png,
                    ".webp" => SKEncodedImageFormat.Webp,
                    _ => (SKEncodedImageFormat?)null
                };

                // Keep unknown formats byte-for-byte. This covers formats for
                // which SkiaSharp does not provide an encoder and preserves the
                // original encoding used by the cover endpoint.
                if (!format.HasValue)
                {
                    File.Copy(source, destination, true);
                    return;
                }

                using var image = SKBitmap.Decode(source);
                if (image == null)
                {
                    throw new InvalidDataException($"Could not decode image: {source}");
                }

                var targetWidth = Math.Max(1, (int)Math.Round((double)image.Width * height / image.Height));
                using var surface = SKSurface.Create(new SKImageInfo(targetWidth, height));
                if (surface == null)
                {
                    throw new InvalidDataException("Could not allocate a resized cover image.");
                }

                surface.Canvas.DrawBitmap(image, new SKRect(0, 0, targetWidth, height), ResizeSampling);
                using var resizedImage = surface.Snapshot();
                using var encodedImage = resizedImage.Encode(format.Value, format.Value == SKEncodedImageFormat.Jpeg ? 92 : 100);
                if (encodedImage == null)
                {
                    throw new InvalidDataException($"Could not encode cover image as {format.Value}.");
                }

                using var output = File.Create(destination);
                encodedImage.SaveTo(output);
            }
            catch
            {
                if (_diskProvider != null && _diskProvider.FileExists(destination))
                {
                    _diskProvider.DeleteFile(destination);
                }
                else if (_diskProvider == null && File.Exists(destination))
                {
                    File.Delete(destination);
                }

                throw;
            }
        }
    }
}
