using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportJunkFileFixture
    {
        [Test]
        public void discovery_should_reuse_junk_exclusions_for_every_supported_media_extension()
        {
            var folder = Path.Combine(Path.GetTempPath(), "Dune");
            var fileSystem = new FileSystem();
            var media = MediaFileExtensions.AllExtensions.Select(extension => Path.Combine(folder, "Part 01" + extension)).ToList();
            var junk = media.Select(path => Path.Combine(folder, "._" + Path.GetFileName(path)))
                .Concat(new[] { "Thumbs.db", ".DS_Store", "Part 02.mp3.partial~" }.Select(name => Path.Combine(folder, name))).ToList();
            var files = media.Concat(junk).Select(path => (IFileInfo)fileSystem.FileInfo.FromFileName(path)).ToList();
            var disk = Proxy<IDiskProvider>((method, args) => method.Name == nameof(IDiskProvider.GetFileInfos)
                ? files : throw new NotImplementedException(method.Name));
            var roots = Proxy<IRootFolderService>((method, args) => method.Name == nameof(IRootFolderService.GetBestRootFolder)
                ? null : throw new NotImplementedException(method.Name));
            var service = new DiskScanService(null, disk, null, null, null, null, null, null, roots, null, null, null,
                LogManager.GetCurrentClassLogger());

            Assert.That(service.GetBookFiles(folder).Select(file => file.FullName), Is.EquivalentTo(media));
            Assert.That(service.FilterFiles(folder, files).Select(file => file.FullName), Is.EquivalentTo(media));
            Assert.That(service.FilterPaths(folder, media.Concat(junk)), Is.EquivalentTo(media));
        }
    }
}
