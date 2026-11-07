using NUnit.Framework;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleasePackDetectorSplitPartFixture
    {
        [TestCase("Author - Title (1 of 2) [M4B]", 1, 2)]
        [TestCase("Author - Title [2 of 3] [MP3]", 2, 3)]
        [TestCase("Author - Title Part 2", 2, 0)]
        [TestCase("Author - Title Pt. 1 of 2", 1, 2)]
        [TestCase("Author - Title Pt.2/3", 2, 3)]
        [TestCase("Author - Title Part Two of Three", 2, 3)]
        [TestCase("Author - Title PART ONE OF TWO", 1, 2)]
        public void should_read_part_position_and_total(string title, int index, int count)
        {
            var result = ReleasePackDetector.Detect(title, null, null);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.SingleBookSplitRelease));
            Assert.That(result.SplitPart, Is.Not.Null);
            Assert.That(result.SplitPart.Index, Is.EqualTo(index));
            Assert.That(result.SplitPart.Count, Is.EqualTo(count));
        }

        [TestCase("Author - Title Part Two")]
        [TestCase("Author - Title")]
        [TestCase("Author - Title (3 of 2)")]
        public void should_not_report_a_position_without_a_usable_marker(string title)
        {
            Assert.That(ReleasePackDetector.ParseSplitPart(title), Is.Null);
        }

        [Test]
        public void should_keep_split_verdict_when_marker_has_no_usable_position()
        {
            var result = ReleasePackDetector.Detect("Author - Title (3 of 2)", null, null);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.SingleBookSplitRelease));
            Assert.That(result.SplitPart, Is.Null);
        }

        [Test]
        public void should_not_read_a_part_from_a_multi_book_pack()
        {
            Assert.That(ReleasePackDetector.ParseSplitPart("Author - Books 1-3 Part 2"), Is.Null);
        }
    }
}
