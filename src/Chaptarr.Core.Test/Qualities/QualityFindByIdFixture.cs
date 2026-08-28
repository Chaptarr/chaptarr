using System;
using NUnit.Framework;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Qualities
{
    [TestFixture]
    public class QualityFindByIdFixture
    {
        [Test]
        public void should_return_known_qualities_by_id()
        {
            Assert.That(Quality.FindById(0), Is.EqualTo(Quality.Unknown));
            Assert.That(Quality.FindById(Quality.EPUB.Id), Is.EqualTo(Quality.EPUB));
        }

        [Test]
        public void should_throw_argument_exception_for_id_just_past_the_lookup_bound()
        {
            // The guard must be inclusive: an id equal to the lookup length is out of
            // bounds and previously surfaced as IndexOutOfRangeException instead of the
            // intended ArgumentException.
            Assert.Throws<ArgumentException>(() => Quality.FindById(Quality.AllLookup.Length));
        }

        [Test]
        public void should_throw_argument_exception_for_id_far_past_the_lookup_bound()
        {
            Assert.Throws<ArgumentException>(() => Quality.FindById(Quality.AllLookup.Length + 100));
        }
    }
}
