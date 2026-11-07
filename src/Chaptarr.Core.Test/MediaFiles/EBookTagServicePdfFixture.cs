using System;
using System.IO;
using System.IO.Abstractions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using PdfSharp;
using PdfSharp.Pdf;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EBookTagServicePdfFixture
    {
        [Test]
        public void should_read_pdf_metadata_with_PDFsharp()
        {
            var folder = Path.Combine(Path.GetTempPath(), "chaptarr-pdf-metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "metadata.pdf");
            string expectedProducer;

            try
            {
                PdfSharpLicense.SetAgplLicense();
                using (var document = new PdfDocument())
                {
                    document.Info.Title = "Example PDF title";
                    document.Info.Author = "Example author";
                    document.Info.Subject = "Example subject";
                    document.Info.Keywords = "fiction, mystery";
                    document.Info.Creator = "Example creator";
                    document.AddPage();
                    document.Save(path);
                    expectedProducer = document.Info.Producer;
                }

                var service = new EBookTagService(null, null, null, null, null, null, null, LogManager.GetLogger(nameof(EBookTagServicePdfFixture)));
                var tags = service.ReadAllTags(new FileSystem().FileInfo.New(path));

                Assert.That(tags["title"], Is.EqualTo(new[] { "Example PDF title" }));
                Assert.That(tags["author"], Is.EqualTo(new[] { "Example author" }));
                Assert.That(tags["authors"], Is.EqualTo(new[] { "Example author" }));
                Assert.That(tags["subject"], Is.EqualTo(new[] { "Example subject" }));
                Assert.That(tags["description"], Is.EqualTo(new[] { "Example subject" }));
                Assert.That(tags["keywords"], Is.EqualTo(new[] { "fiction, mystery" }));
                Assert.That(tags["tags"], Is.EqualTo(new[] { "fiction, mystery" }));
                Assert.That(tags["creator"], Is.EqualTo(new[] { "Example creator" }));
                if (!string.IsNullOrWhiteSpace(expectedProducer))
                {
                    Assert.That(tags["producer"], Is.EqualTo(new[] { expectedProducer }));
                }
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
