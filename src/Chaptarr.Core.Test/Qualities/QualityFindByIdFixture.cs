using System;
using NUnit.Framework;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Qualities
{
    [TestFixture]
    public class QualityFindByIdFixture
    {
        [Test]
        public void should_return_unknown_for_zero()
        {
            Assert.That(Quality.FindById(0), Is.EqualTo(Quality.Unknown));
        }

        [Test]
        public void should_find_every_known_quality()
        {
            foreach (var quality in Quality.All)
            {
                Assert.That(Quality.FindById(quality.Id), Is.EqualTo(quality));
            }
        }

        [Test]
        public void should_throw_argument_exception_for_ids_outside_lookup()
        {
            var length = Quality.AllLookup.Length;

            Assert.Throws<ArgumentException>(() => Quality.FindById(length));
            Assert.Throws<ArgumentException>(() => Quality.FindById(length + 1));
            Assert.Throws<ArgumentException>(() => Quality.FindById(-1));
        }
    }
}
