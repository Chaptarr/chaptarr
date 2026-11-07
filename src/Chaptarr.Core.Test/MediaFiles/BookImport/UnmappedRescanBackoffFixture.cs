using System;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class UnmappedRescanBackoffFixture
    {
        private static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static string Sig(long size = 10, DateTime? stamp = null)
        {
            return UnmappedRescanBackoff.BuildSignature(
                new[] { new BookFile { Path = "/a/1.m4b", Size = size, Modified = Start } },
                stamp);
        }

        [Test]
        public void should_allow_the_first_attempt_and_block_an_identical_repeat()
        {
            var sut = new UnmappedRescanBackoff();

            Assert.That(sut.ShouldRescan("1|/a", Sig(), Start), Is.True);
            Assert.That(sut.ShouldRescan("1|/a", Sig(), Start.AddMinutes(5)), Is.False);
        }

        [Test]
        public void should_back_off_progressively_up_to_a_day()
        {
            var sut = new UnmappedRescanBackoff();
            var now = Start;

            Assert.That(sut.ShouldRescan("k", Sig(), now), Is.True);
            now = now.AddHours(1);
            Assert.That(sut.ShouldRescan("k", Sig(), now), Is.True);
            Assert.That(sut.ShouldRescan("k", Sig(), now.AddHours(5)), Is.False);
            now = now.AddHours(6);
            Assert.That(sut.ShouldRescan("k", Sig(), now), Is.True);
            Assert.That(sut.ShouldRescan("k", Sig(), now.AddHours(23)), Is.False);
            now = now.AddHours(24);
            Assert.That(sut.ShouldRescan("k", Sig(), now), Is.True);
            Assert.That(sut.ShouldRescan("k", Sig(), now.AddHours(23)), Is.False);
            Assert.That(sut.ShouldRescan("k", Sig(), now.AddHours(24)), Is.True);
        }

        [Test]
        public void should_allow_immediately_when_the_unmapped_set_or_catalog_changes()
        {
            var sut = new UnmappedRescanBackoff();

            Assert.That(sut.ShouldRescan("k", Sig(10), Start), Is.True);
            Assert.That(sut.ShouldRescan("k", Sig(11), Start.AddMinutes(1)), Is.True);
            Assert.That(sut.ShouldRescan("k", Sig(11, Start), Start.AddMinutes(2)), Is.True);
        }

        [Test]
        public void should_track_each_author_folder_separately_and_reset_on_clear()
        {
            var sut = new UnmappedRescanBackoff();

            Assert.That(sut.ShouldRescan("1|/a", Sig(), Start), Is.True);
            Assert.That(sut.ShouldRescan("2|/a", Sig(), Start), Is.True);
            sut.Clear("1|/a");
            Assert.That(sut.ShouldRescan("1|/a", Sig(), Start.AddMinutes(1)), Is.True);
        }
    }
}
