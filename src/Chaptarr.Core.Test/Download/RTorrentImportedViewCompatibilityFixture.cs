using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.RTorrent;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class RTorrentImportedViewCompatibilityFixture
    {
        [TestCase(true, "readarr_imported")]
        [TestCase(false, "chaptarr_imported")]
        public void post_import_view_should_preserve_migrated_scripts(bool migrated, string expectedView)
        {
            var proxy = new Mock<IRTorrentProxy>();
            var config = new Mock<IConfigService>();
            config.SetupGet(service => service.BookshelfNGDatabaseMigrated).Returns(migrated);
            var settings = new RTorrentSettings { MusicCategory = "readarr" };
            var client = new RTorrent(proxy.Object, null, null, config.Object, null, null, null, null, null,
                LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition { Settings = settings }
            };

            client.MarkItemAsImported(new DownloadClientItem { DownloadId = "AABBCC", Category = "readarr" });

            proxy.Verify(service => service.PushTorrentUniqueView("aabbcc", expectedView, settings), Times.Once);
            proxy.Verify(service => service.SetTorrentLabel(It.IsAny<string>(), It.IsAny<string>(), settings), Times.Never);
        }
    }
}
