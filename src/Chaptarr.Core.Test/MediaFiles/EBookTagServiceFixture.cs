using System;
using System.IO;
using System.IO.Abstractions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using PdfSharpCore.Pdf;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EBookTagServiceFixture
    {
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

                var service = new EBookTagService(null, null, null, null, null, null, LogManager.GetLogger("test"));
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
