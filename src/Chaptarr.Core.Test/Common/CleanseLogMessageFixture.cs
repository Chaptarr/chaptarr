using NzbDrone.Common.Instrumentation;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class CleanseLogMessageFixture
    {
        [TestCase("http://user:pass@example.com/path", "user:pass")]
        [TestCase("http://user:pass@example.com/path?apikey=abc", "abc")]
        [TestCase("Authorization: Bearer supersecrettoken", "supersecrettoken")]
        [TestCase("Proxy-Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
        [TestCase("Cookie: session=abc123; other=def456", "abc123")]
        [TestCase("Set-Cookie: session=abc123; Path=/; HttpOnly", "abc123")]
        public void should_cleanse_common_secrets(string message, string secret)
        {
            var cleansed = CleanseLogMessage.Cleanse(message);

            Assert.That(cleansed, Does.Not.Contain(secret));
            Assert.That(cleansed, Does.Contain("(removed)"));
        }
    }
}

