using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class TrackedDownloadSameRecordingFixture
    {
        private static Edition Audiobook(int id, string language, params string[] narrators)
        {
            return new Edition
            {
                Id = id,
                Title = "Project Hail Mary",
                Language = language,
                IsEbook = false,
                ReadingFormatId = 2,
                NarratorNames = new List<string>(narrators)
            };
        }

        [Test]
        public void should_accept_another_listing_of_the_same_narration()
        {
            // Hardcover and Audible list the Ray Porter recording separately
            var grabbed = Audiobook(1, "eng", "Ray Porter");
            var matched = Audiobook(2, "en", "Ray  Porter");

            Assert.That(DownloadedBooksImportService.IsSameRecording(grabbed, matched), Is.True);
        }

        [Test]
        public void should_reject_a_different_narrator()
        {
            Assert.That(DownloadedBooksImportService.IsSameRecording(Audiobook(1, "eng", "Jim Dale"), Audiobook(2, "eng", "Stephen Fry")), Is.False);
        }

        [Test]
        public void should_reject_another_language()
        {
            Assert.That(DownloadedBooksImportService.IsSameRecording(Audiobook(1, "eng", "Ray Porter"), Audiobook(2, "ita", "Ray Porter")), Is.False);
        }

        [Test]
        public void should_reject_when_language_is_unknown()
        {
            Assert.That(DownloadedBooksImportService.IsSameRecording(Audiobook(1, null, "Ray Porter"), Audiobook(2, null, "Ray Porter")), Is.False);
            Assert.That(DownloadedBooksImportService.IsSameRecording(Audiobook(1, "eng", "Ray Porter"), Audiobook(2, null, "Ray Porter")), Is.False);
        }

        [Test]
        public void should_not_judge_editions_without_narrators_or_with_a_full_cast_label()
        {
            Assert.That(DownloadedBooksImportService.IsSameRecording(Audiobook(1, "eng"), Audiobook(2, "eng")), Is.False);
            Assert.That(DownloadedBooksImportService.IsSameRecording(Audiobook(1, "eng", "Full Cast"), Audiobook(2, "eng", "Full Cast")), Is.False);
        }

        [Test]
        public void should_not_apply_to_ebooks()
        {
            var grabbed = new Edition { Id = 1, IsEbook = true, Language = "eng", NarratorNames = new List<string> { "Ray Porter" } };
            var matched = new Edition { Id = 2, IsEbook = true, Language = "eng", NarratorNames = new List<string> { "Ray Porter" } };

            Assert.That(DownloadedBooksImportService.IsSameRecording(grabbed, matched), Is.False);
        }
    }
}
