using NUnit.Framework;
using NzbDrone.Core;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class FluentTruncateFixture
    {
        [Test]
        public void should_return_null_for_null_input()
        {
            Assert.That(((string)null).Truncate(100), Is.Null);
        }

        [Test]
        public void should_return_empty_for_empty_input()
        {
            Assert.That(string.Empty.Truncate(100), Is.EqualTo(string.Empty));
        }

        [Test]
        public void should_return_short_input_unchanged()
        {
            Assert.That("The Pursuit of the Pankera".Truncate(100), Is.EqualTo("The Pursuit of the Pankera"));
        }

        [Test]
        public void should_return_input_of_exactly_max_length_unchanged()
        {
            var value = new string('a', 10);

            Assert.That(value.Truncate(10), Is.EqualTo(value));
        }

        [Test]
        public void should_truncate_long_input_to_max_length()
        {
            var value = new string('a', 250);

            Assert.That(value.Truncate(100), Is.EqualTo(new string('a', 100)));
        }

        [Test]
        public void should_truncate_multi_byte_input_on_a_byte_budget()
        {
            var truncated = new string('é', 20).Truncate(10);

            Assert.That(truncated, Is.EqualTo(new string('é', 5)));
        }
    }
}
