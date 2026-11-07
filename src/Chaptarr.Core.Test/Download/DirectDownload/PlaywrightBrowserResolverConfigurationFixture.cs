using NUnit.Framework;
using NzbDrone.Core.Download.Clients.Direct;

namespace Chaptarr.Core.Test.Download.DirectDownload
{
    [TestFixture]
    public class PlaywrightBrowserResolverConfigurationFixture
    {
        [TestCase("/usr/bin/chromium", "/usr/bin/chromium")]
        [TestCase("/opt/browser installation/chromium", "/opt/browser installation/chromium")]
        public void should_use_the_legacy_chromium_executable_override(string configuredPath, string expectedPath)
        {
            Assert.That(PlaywrightBrowserResolver.ResolveChromiumExecutablePath(configuredPath), Is.EqualTo(expectedPath));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void should_use_playwright_default_when_legacy_executable_override_is_empty(string configuredPath)
        {
            Assert.That(PlaywrightBrowserResolver.ResolveChromiumExecutablePath(configuredPath), Is.Null);
        }
    }
}
