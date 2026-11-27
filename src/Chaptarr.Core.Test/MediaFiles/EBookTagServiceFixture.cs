using System;
using System.IO;
using System.IO.Abstractions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Configuration;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using PdfSharpCore.Pdf;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EBookTagServiceFixture
    {
        [Test]
        public void file_without_calibre_id_should_return_before_root_lookup()
        {
            var config = Proxy<IConfigService>((_, _) => false);
            var service = new EBookTagService(null, null, config, null, null, LogManager.GetLogger("test"));
            Assert.DoesNotThrow(() => service.WriteTags(new BookFile { Path = Path.Combine(Path.GetTempPath(), "untracked.epub") }, false, true));
        }

        [Test]
        public void pdf_metadata_should_be_read_with_the_current_image_dependency()
        {
            var path = Path.Combine(Path.GetTempPath(), "chaptarr-pdf-metadata-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                using (var document = new PdfDocument())
                {
                    document.AddPage();
                    document.Info.Title = "Metadata Compatibility";
                    document.Info.Author = "Test Author";
                    document.Save(path);
                }

                var service = new EBookTagService(null, null, null, null, null, LogManager.GetLogger("test"));
                var tags = service.ReadAllTags(new FileInfoWrapper(new FileSystem(), new FileInfo(path)));

                Assert.That(tags["title"], Is.EqualTo(new[] { "Metadata Compatibility" }));
                Assert.That(tags["authors"], Is.EqualTo(new[] { "Test Author" }));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
