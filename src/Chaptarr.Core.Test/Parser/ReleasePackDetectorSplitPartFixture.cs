using NUnit.Framework;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleasePackDetectorSplitPartFixture
    {
        // Real GraphicAudio release and file names: each part is a separate download.
        [TestCase("Fourth Wing (1 of 2)", 1, 2)]
        [TestCase("Fourth Wing (2 of 2)", 2, 2)]
        [TestCase("Fourth Wing (Part 2 of 2) (GA-102525)", 2, 2)]
        [TestCase("Iron Flame (Part 1 of 2)", 1, 2)]
        [TestCase("Onyx Storm (Part 1 of 2) (GA) - Rebecca Yarros", 1, 2)]
        [TestCase("The Empyrean [03] Onyx Storm (2 of 2)", 2, 2)]
        [TestCase("Iron Flame (2 of 2) [Dramatized Adaptation]: Book #2, Part 2", 2, 2)]
        [TestCase("The Way of Kings (Graphic Audio)(1 of 5)", 1, 5)]
        [TestCase("The Way of Kings [3 of 5]", 3, 5)]
        [TestCase("Some Book Pt. 2 of 3", 2, 3)]
        [TestCase("Some Book Part 2/3", 2, 3)]
        [TestCase("Some Book: Part Two of Three", 2, 3)]
        [TestCase("SOME BOOK PART ONE OF TWO", 1, 2)]
        public void should_parse_part_of_count(string title, int part, int count)
        {
            var result = ReleasePackDetector.DetectSplitPart(title);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Part, Is.EqualTo(part));
            Assert.That(result.Count, Is.EqualTo(count));
        }

        [TestCase("Shadow's Edge Part 2", 2)]
        [TestCase("Some Book - Pt 1", 1)]
        public void should_parse_part_without_count(string title, int part)
        {
            var result = ReleasePackDetector.DetectSplitPart(title);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Part, Is.EqualTo(part));
            Assert.That(result.Count, Is.EqualTo(0));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Fourth Wing")]
        [TestCase("Brent Weeks - Lightbringer 01 - The Black Prism [GraphicAudio]")]
        [TestCase("The Empyrean [02] Iron Flame {02]")]
        [TestCase("Mistborn (Book 1 of 3)")]
        [TestCase("Disc 2 of 10")]
        [TestCase("Chapter 4 of 30")]
        [TestCase("Counterpart 2 of 3")]
        [TestCase("Some Book (3 of 2)")]
        [TestCase("Some Book (1 of 1)")]
        [TestCase("Some Book (1 of 45)")]
        public void should_not_parse_non_split_titles(string title)
        {
            Assert.That(ReleasePackDetector.DetectSplitPart(title), Is.Null);
        }

        [TestCase("Iron Flame - Part 1 of 2 - 03", 1, 2)]
        [TestCase("EMPYREAN0101-Fourth Wing Part 1 of 2", 1, 2)]
        public void should_parse_explicit_part_of_count_from_file_name(string fileName, int part, int count)
        {
            var result = ReleasePackDetector.DetectSplitPart(fileName, fileName: true);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Part, Is.EqualTo(part));
            Assert.That(result.Count, Is.EqualTo(count));
        }

        // Inside a multi-file release these number tracks, not separate releases.
        [TestCase("Track (3 of 12)")]
        [TestCase("Some Book Part 3")]
        [TestCase("Chapter 1 - Iron Flame - Rebecca Yarros")]
        public void should_not_treat_track_numbering_in_file_names_as_split_parts(string fileName)
        {
            Assert.That(ReleasePackDetector.DetectSplitPart(fileName, fileName: true), Is.Null);
        }

        [Test]
        public void parts_of_the_same_split_should_be_siblings()
        {
            var part1 = new ReleaseSplitPart(1, 2);

            Assert.That(part1.IsSiblingOf(new ReleaseSplitPart(2, 2)), Is.True);
            Assert.That(part1.IsSiblingOf(new ReleaseSplitPart(2, 0)), Is.True);
            Assert.That(part1.IsSiblingOf(new ReleaseSplitPart(1, 2)), Is.False);
            Assert.That(part1.IsSiblingOf(new ReleaseSplitPart(2, 3)), Is.False);
            Assert.That(part1.IsSiblingOf(null), Is.False);
        }

        [TestCase("Rebecca Yarros - Onyx Storm (Part 2 of 2) [GraphicAudio]", 2, 2)]
        [TestCase("The Way of Kings [3 of 5]", 3, 5)]
        [TestCase("Some Book: Part Two of Three", 2, 3)]
        public void detect_should_report_split_part_with_its_verdict(string title, int part, int count)
        {
            var result = ReleasePackDetector.Detect(title, null, null);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.SingleBookSplitRelease));
            Assert.That(result.SplitPart, Is.EqualTo(new ReleaseSplitPart(part, count)));
        }

        [Test]
        public void detect_should_keep_split_verdict_when_numbers_are_implausible()
        {
            var result = ReleasePackDetector.Detect("Some Book (1 of 45)", null, null);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.SingleBookSplitRelease));
            Assert.That(result.SplitPart, Is.Null);
        }

        [Test]
        public void should_not_report_a_split_part_for_multi_book_packs()
        {
            Assert.That(ReleasePackDetector.DetectSplitPart("Mistborn Books 1-3 (Part 1 of 2)"), Is.Null);
        }
    }
}
