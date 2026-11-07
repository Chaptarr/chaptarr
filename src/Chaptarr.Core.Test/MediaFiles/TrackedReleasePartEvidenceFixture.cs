using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class TrackedReleasePartEvidenceFixture
    {
        private static LocalBook Apply(string releaseTitle)
        {
            var local = new LocalBook { Path = "/downloads/book.m4b" };
            var remote = new RemoteBook
            {
                Release = new ReleaseInfo { Title = releaseTitle },
                ParsedBookInfo = new ParsedBookInfo()
            };

            DownloadedBooksImportService.ApplyTrackedReleaseEvidence(
                new[] { new ImportDecision<LocalBook>(local) },
                remote,
                new DownloadClientItem { Title = releaseTitle });

            return local;
        }

        [Test]
        public void should_carry_part_position_from_release_title()
        {
            var local = Apply("Author - Title (2 of 3) [M4B]");

            Assert.That(local.ReleasePart, Is.EqualTo(2));
            Assert.That(local.ReleasePartCount, Is.EqualTo(3));
        }

        [Test]
        public void should_leave_part_unset_for_whole_book_release()
        {
            var local = Apply("Author - Title [M4B]");

            Assert.That(local.ReleasePart, Is.EqualTo(0));
            Assert.That(local.ReleasePartCount, Is.EqualTo(0));
        }
    }
}
