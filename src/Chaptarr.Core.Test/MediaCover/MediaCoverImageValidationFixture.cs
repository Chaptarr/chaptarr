using System;
using System.IO;
using NUnit.Framework;
using NzbDrone.Core.MediaCover;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverImageValidationFixture
    {
        internal static byte[] Encode(string format)
        {
            using var image = new Image<Rgb24>(120, 160);
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    image[x, y] = new Rgb24((byte)(x * 2), (byte)(y * 1.5), (byte)((x + y) % 256));
                }
            }

            using var data = new MemoryStream();
            switch (format)
            {
                case "png": image.SaveAsPng(data); break;
                case "webp": image.SaveAsWebp(data); break;
                case "progressive": image.SaveAsJpeg(data, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Progressive = true }); break;
                case "restart": image.SaveAsJpeg(data, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { RestartInterval = 4 }); break;
                default: image.SaveAsJpeg(data); break;
            }

            return data.ToArray();
        }

        internal static byte[] Truncate(byte[] data, int length) => data.AsSpan(0, Math.Min(length, data.Length)).ToArray();

        [TestCase("png")]
        [TestCase("jpeg")]
        [TestCase("progressive")]
        [TestCase("restart")]
        [TestCase("webp")]
        public void a_complete_image_passes(string format)
        {
            var data = Encode(format);
            Assert.That(MediaCoverRendition.IsCompleteImage(data), Is.True);
            Assert.DoesNotThrow(() => MediaCoverRendition.ValidateCompleteImage(data, "image/" + format));
        }

        [TestCase("png", 32)]
        [TestCase("jpeg", 32)]
        [TestCase("png", -1)]
        [TestCase("jpeg", -1)]
        [TestCase("progressive", -1)]
        [TestCase("webp", -1)]
        [TestCase("png", -2)]
        [TestCase("jpeg", -2)]
        [TestCase("progressive", -2)]
        [TestCase("restart", -1)]
        [TestCase("restart", -2)]
        [TestCase("webp", -2)]
        public void a_truncated_image_passes_the_header_check_but_not_the_complete_check(string format, int length)
        {
            // -1: cut at 60%; -2: only the final byte missing.
            var full = Encode(format);
            var data = Truncate(full, length > 0 ? length : length == -1 ? full.Length * 3 / 5 : full.Length - 1);
            Assert.DoesNotThrow(() => MediaCoverRendition.ValidateImageResponse(data, "image/" + format), "The header check only reads the first bytes");
            Assert.That(MediaCoverRendition.IsCompleteImage(data), Is.False);
            Assert.Throws<InvalidDataException>(() => MediaCoverRendition.ValidateCompleteImage(data, "image/" + format));
        }

        [Test]
        public void a_jpeg_with_an_exif_thumbnail_is_judged_by_its_own_end_marker()
        {
            // The thumbnail's end marker sits inside the APP1 segment; truncating the main
            // image after it must still fail.
            var main = Encode("jpeg");
            var thumbnail = Encode("jpeg");
            var app1 = new byte[4 + thumbnail.Length];
            app1[0] = 0xFF;
            app1[1] = 0xE1;
            app1[2] = (byte)((thumbnail.Length + 2) >> 8);
            app1[3] = (byte)((thumbnail.Length + 2) & 0xFF);
            thumbnail.CopyTo(app1, 4);
            var withThumbnail = new byte[main.Length + app1.Length];
            main.AsSpan(0, 2).CopyTo(withThumbnail);
            app1.CopyTo(withThumbnail, 2);
            main.AsSpan(2).CopyTo(withThumbnail.AsSpan(2 + app1.Length));
            Assert.That(MediaCoverRendition.IsCompleteImage(withThumbnail), Is.True);
            Assert.That(MediaCoverRendition.IsCompleteImage(Truncate(withThumbnail, withThumbnail.Length - main.Length / 3)), Is.False);
        }

        // Why the structural end checks exist: even in Strict mode the decoder fills in a
        // cut-off JPEG or WebP. If a later ImageSharp rejects these, the checks can go.
        [TestCase("jpeg", -1)]
        [TestCase("jpeg", -2)]
        [TestCase("progressive", -1)]
        [TestCase("webp", -1)]
        public void the_decoder_alone_accepts_these_truncated_images_even_in_strict_mode(string format, int length)
        {
            var full = Encode(format);
            var data = Truncate(full, length == -1 ? full.Length * 3 / 5 : full.Length - 1);
            var options = new SixLabors.ImageSharp.Formats.DecoderOptions { SegmentIntegrityHandling = SixLabors.ImageSharp.Formats.SegmentIntegrityHandling.Strict };
            Assert.DoesNotThrow(() =>
            {
                using var stream = new MemoryStream(data);
                using var image = Image.Load(options, stream);
            });
            Assert.That(MediaCoverRendition.IsCompleteImage(data), Is.False);
        }

        [Test]
        public void a_png_declaring_an_impossibly_long_chunk_is_rejected_without_an_exception()
        {
            var data = Encode("png");
            data[8] = 0xFF;
            data[9] = 0xFF;
            data[10] = 0xFF;
            data[11] = 0xF0;
            Assert.That(MediaCoverRendition.IsCompleteImage(data), Is.False);
        }

        [Test]
        public void a_complete_jpeg_with_bytes_after_its_end_marker_passes()
        {
            var full = Encode("jpeg");
            var padded = new byte[full.Length + 16];
            full.CopyTo(padded, 0);
            Assert.That(MediaCoverRendition.IsCompleteImage(padded), Is.True);
        }

        [Test]
        public void avif_originals_are_still_accepted_without_decoding()
        {
            var avif = new byte[32];
            "ftypavif"u8.CopyTo(avif.AsSpan(4));
            Assert.DoesNotThrow(() => MediaCoverRendition.ValidateCompleteImage(avif, "image/avif"));
        }
    }
}
