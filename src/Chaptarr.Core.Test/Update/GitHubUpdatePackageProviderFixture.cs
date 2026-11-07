using NUnit.Framework;
using NzbDrone.Core.Update;

namespace Chaptarr.Core.Test.Update
{
    [TestFixture]
    public class GitHubUpdatePackageProviderFixture
    {
        [Test]
        public void release_feed_should_default_to_the_chaptarrng_organization_repository()
        {
            Assert.That(
                GitHubUpdatePackageProvider.BuildReleasesResource(null, null),
                Is.EqualTo("repos/snapetech-org/chaptarrng/releases"));
        }

        [Test]
        public void release_feed_should_use_configured_repository_and_escape_each_path_segment()
        {
            Assert.That(
                GitHubUpdatePackageProvider.BuildReleasesResource("custom owner", "bookshelf/ng"),
                Is.EqualTo("repos/custom%20owner/bookshelf%2Fng/releases"));
        }
    }
}
