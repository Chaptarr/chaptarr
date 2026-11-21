using System;
using System.IO;
using System.Text;
using NzbDrone.Core.MediaCover;
using NUnit.Framework;

namespace NzbDrone.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverResponseValidationFixture
    {
        // Captured file headers, not claims that ImageSharp can decode the full images.
        // Hardcover books/312460/473128015012398.heic and libavif tests/data/white_1x1.avif.
        [TestCase("000000206674797068656963000000006d6966316d6961664d6948426865696300000ced6d657461000000000000002168646c72000000000000000070696374", "image/heic")]
        [TestCase("00000020667479706176696600000000617669666d6966316d6961664d413141000000f26d657461000000000000002868646c72000000000000000070696374", "application/octet-stream")]
        public void captured_iso_image_headers_preserve_original_fallback(string hex, string contentType)
        {
            Assert.DoesNotThrow(() => MediaCoverRendition.ValidateImageResponse(Convert.FromHexString(hex), contentType));
        }

        [TestCase("avif")]
        [TestCase("avis")]
        [TestCase("heic")]
        [TestCase("heix")]
        [TestCase("mif1")]
        [TestCase("msf1")]
        public void iso_image_brands_are_accepted(string brand)
        {
            var header = Convert.FromHexString("00000010667479700000000000000000");
            Encoding.ASCII.GetBytes(brand).CopyTo(header, 8);
            Assert.DoesNotThrow(() => MediaCoverRendition.ValidateImageResponse(header, "text/plain"));
        }

        [TestCase("000000106674797069736f6d00000000")]
        [TestCase("00000010667479706d70343200000000")]
        [TestCase("000000106e6f70656865696300000000")]
        [TestCase("000000106674797068656963")]
        public void video_wrong_box_and_truncated_headers_are_rejected(string hex)
        {
            Assert.Throws<InvalidDataException>(() => MediaCoverRendition.ValidateImageResponse(Convert.FromHexString(hex), "image/heic"));
        }
    }
}
