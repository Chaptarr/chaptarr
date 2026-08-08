using System;
using System.IO;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    [Platform(Include = "Win", Reason = "Exercises the production Windows hardlink-count implementation")]
    public class WindowsDiskProviderLinkCountFixture
    {
        [Test]
        public void production_reader_should_report_both_links()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"chaptarr-windows-links-{Guid.NewGuid():N}");
            var source = Path.Combine(directory, "source.mp3");
            var destination = Path.Combine(directory, "destination.mp3");
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(source, "hardlink test");
                var diskProvider = new NzbDrone.Windows.Disk.DiskProvider();

                Assert.That(diskProvider.TryCreateHardLink(source, destination), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(diskProvider.GetFileLinkCount(source), Is.EqualTo(2));
                    Assert.That(diskProvider.GetFileLinkCount(destination), Is.EqualTo(2));
                });
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }
}
