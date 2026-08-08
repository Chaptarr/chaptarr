using NUnit.Framework;
using NzbDrone.Common.Extensions;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class UrlExtensionsFixture
    {
        [TestCase("><div class=", false)]
        [TestCase("/MediaCover/Books/1/cover.jpg", false)]
        [TestCase("https://example.com/a.jpg", true)]
        [TestCase("http://example.com/a.jpg", true)]
        [TestCase("ftp://example.com/a.jpg", false)]
        public void is_valid_http_url_should_match_expectation(string url, bool expected)
        {
            Assert.That(url.IsValidHttpUrl(), Is.EqualTo(expected));
        }
    }
}

