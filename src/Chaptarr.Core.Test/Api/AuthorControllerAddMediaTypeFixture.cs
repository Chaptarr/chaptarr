using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerAddMediaTypeFixture
    {
        [Test]
        public void should_create_media_type_when_root_folder_supplied()
        {
            Assert.That(AuthorController.ShouldCreateMediaType("/library/ebooks", null, null, null, null, false), Is.True);
        }

        [TestCase(5, null, null, null, false)]
        [TestCase(null, 3, null, null, false)]
        [TestCase(null, null, true, null, false)]
        [TestCase(null, null, null, MonitorTypes.All, false)]
        [TestCase(null, null, null, null, true)]
        public void should_create_media_type_when_ebook_settings_are_supplied_without_a_root_folder(
            int? quality, int? metadata, bool? monitored, MonitorTypes? mode, bool targetsMedia)
        {
            Assert.That(AuthorController.ShouldCreateMediaType(null, quality, metadata, monitored, mode, targetsMedia), Is.True);
        }

        [TestCase(null, null, null, null, false)]
        [TestCase(0, 0, false, null, false)]
        public void should_not_create_media_type_when_nothing_requests_it(
            int? quality, int? metadata, bool? monitored, MonitorTypes? mode, bool targetsMedia)
        {
            Assert.That(AuthorController.ShouldCreateMediaType("  ", quality, metadata, monitored, mode, targetsMedia), Is.False);
        }
    }
}
