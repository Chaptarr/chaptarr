using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class SplitPartFileFilterFixture
    {
        private static RemoteBook Release(int? part)
        {
            return new RemoteBook
            {
                PackDetection = new ReleasePackDetection
                {
                    Verdict = part.HasValue ? ReleasePackDetectionVerdict.SingleBookSplitRelease : ReleasePackDetectionVerdict.None,
                    SplitPart = part.HasValue ? new SplitPart { Index = part.Value, Count = 3 } : null
                }
            };
        }

        private static BookFile[] Files() => new[]
        {
            new BookFile { Id = 1, ReleasePart = 1 },
            new BookFile { Id = 2, ReleasePart = 2 },
            new BookFile { Id = 3, ReleasePart = 0 }
        };

        [Test]
        public void should_ignore_sibling_parts_for_a_part_release()
        {
            var ids = SplitPartFileFilter.ComparableFiles(Release(2), Files()).Select(f => f.Id);

            Assert.That(ids, Is.EquivalentTo(new[] { 2, 3 }));
        }

        [Test]
        public void should_compare_everything_for_a_release_without_a_part()
        {
            Assert.That(SplitPartFileFilter.ComparableFiles(Release(null), Files()).Count(), Is.EqualTo(3));
            Assert.That(SplitPartFileFilter.ComparableFiles(new RemoteBook(), Files()).Count(), Is.EqualTo(3));
        }

        [Test]
        public void should_tolerate_missing_files()
        {
            Assert.That(SplitPartFileFilter.ComparableFiles(Release(1), null), Is.Null);
        }
    }
}
