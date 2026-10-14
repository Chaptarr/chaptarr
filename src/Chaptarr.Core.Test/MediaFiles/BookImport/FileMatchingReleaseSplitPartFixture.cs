using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class FileMatchingReleaseSplitPartFixture
    {
        // GraphicAudio's album tag for the second download of book 1 in the series.
        [TestCase("A Court of Thorns and Roses 1: A Court of Thorns and Roses 2 of 2", 2, 2, "A Court of Thorns and Roses 1: A Court of Thorns and Roses")]
        [TestCase("Iron Flame Part 1 of 2", 1, 2, "Iron Flame")]
        [TestCase("Iron Flame (Pt. 2/2)", 2, 2, "Iron Flame ( )")]
        [TestCase("Shadow's Edge Part 2", 2, 0, "Shadow's Edge")]
        public void should_not_read_the_grabbed_split_part_as_a_series_position(string value, int part, int count, string expected)
        {
            Assert.That(FileMatchingService.RemoveReleaseSplitPartPhrase(value, new ReleaseSplitPart(part, count)), Is.EqualTo(expected));
        }

        [TestCase("Mistborn Book 2 of 3", 2, 3)]
        [TestCase("Mistborn Vol. 2/3", 2, 3)]
        [TestCase("A Court of Mist and Fury (2 of 2)", 1, 2)]
        [TestCase("The Way of Kings 12 of 25", 2, 5)]
        public void should_leave_other_numbers_alone(string value, int part, int count)
        {
            Assert.That(FileMatchingService.RemoveReleaseSplitPartPhrase(value, new ReleaseSplitPart(part, count)), Is.EqualTo(value));
        }

        [Test]
        public void should_change_nothing_when_the_grab_was_not_a_split_part()
        {
            const string value = "A Court of Thorns and Roses 1: A Court of Thorns and Roses 2 of 2";

            Assert.That(FileMatchingService.RemoveReleaseSplitPartPhrase(value, null), Is.EqualTo(value));
        }
    }
}
