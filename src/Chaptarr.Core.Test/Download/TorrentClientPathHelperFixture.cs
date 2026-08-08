using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download.Clients;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TorrentClientPathHelperFixture
    {
        [Test]
        public void should_preserve_windows_base_when_member_uses_forward_slashes()
        {
            var result = TorrentClientPathHelper.CombineClientPath(new OsPath(@"X:\TempBooks"), "Author - Book/file.epub");

            Assert.That(result.FullPath, Is.EqualTo(@"X:\TempBooks\Author - Book\file.epub"));
            Assert.That(result.Kind, Is.EqualTo(OsPathKind.Windows));
        }

        [Test]
        public void should_preserve_unix_base_when_member_uses_backslashes()
        {
            var result = TorrentClientPathHelper.CombineClientPath(new OsPath("/downloads"), @"Author - Book\file.epub");

            Assert.That(result.FullPath, Is.EqualTo("/downloads/Author - Book/file.epub"));
            Assert.That(result.Kind, Is.EqualTo(OsPathKind.Unix));
        }
    }
}
