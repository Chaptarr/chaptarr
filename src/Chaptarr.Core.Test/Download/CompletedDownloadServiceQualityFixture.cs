using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class CompletedDownloadServiceQualityFixture
    {
        [Test]
        public void should_update_queue_quality_from_imported_file_not_rejected_duplicate_alternatives()
        {
            var trackedDownload = new TrackedDownload
            {
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.Unknown)
                    }
                }
            };

            var rejectedPdf = new ImportDecision<LocalBook>(new LocalBook
            {
                Quality = new QualityModel(Quality.PDF)
            });
            rejectedPdf.Reject(new Rejection("Skipped duplicate ebook format"));

            var importedAzw3 = new ImportDecision<LocalBook>(new LocalBook
            {
                Quality = new QualityModel(Quality.AZW3)
            });

            CompletedDownloadService.ApplyActualFileQuality(trackedDownload, new List<ImportResult>
            {
                new(rejectedPdf, "Skipped duplicate ebook format"),
                new(importedAzw3)
            });

            Assert.That(trackedDownload.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.AZW3));
        }
    }
}
