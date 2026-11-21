using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class GroupDurationEvidenceFixture
    {
        [TestCase(false, 36000)]
        [TestCase(true, null)]
        public void incomplete_group_duration_must_not_be_replaced_with_one_chapters_duration(bool missingLastDuration, int? expected)
        {
            var chapters = Enumerable.Range(1, 30).Select(index => new DiscoveredFileWithMetadata
            {
                Path = $"/downloads/Dune/Dune {index:00}.mp3",
                DurationSeconds = missingLastDuration && index == 30 ? null : 1200
            }).ToList();
            var calculate = typeof(FileMatchingService).GetMethod("TryCalculateTotalDurationSeconds", BindingFlags.Static | BindingFlags.NonPublic);
            var resolve = typeof(FileMatchingService).GetMethod("ResolveGroupedMatchDurationSeconds", BindingFlags.Static | BindingFlags.NonPublic);
            var total = (int?)calculate.Invoke(null, new object[] { chapters });
            var matchDuration = (int?)resolve.Invoke(null, new object[] { chapters[0], total, true });

            Assert.That(matchDuration, Is.EqualTo(expected), "An unavailable total is unknown, not the first chapter's 20 minutes");
        }

        [TestCase(20, 20)]
        [TestCase(432001, 432001)]
        [TestCase(1008000, 1008000)]
        [TestCase(0, null)]
        [TestCase(-1, null)]
        public void positive_duration_must_not_be_discarded_by_an_arbitrary_floor_or_ceiling(long seconds, int? expected)
        {
            var normalize = typeof(FileMatchingService).GetMethod("NormalizeTotalDurationSeconds", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(normalize.Invoke(null, new object[] { seconds }), Is.EqualTo(expected));
        }
    }
}
