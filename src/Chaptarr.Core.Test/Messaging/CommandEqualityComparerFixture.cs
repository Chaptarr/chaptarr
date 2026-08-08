using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Messaging
{
    [TestFixture]
    public class CommandEqualityComparerFixture
    {
        private sealed class TestCommand : Command
        {
            public string DownloadId { get; set; }
            public List<string> DownloadIds { get; set; }
            public string Label { get; set; }
        }

        [Test]
        public void should_compare_later_properties_when_first_property_is_null_for_both_commands()
        {
            var first = new TestCommand { DownloadIds = new List<string> { "one" } };
            var second = new TestCommand { DownloadIds = new List<string> { "two" } };

            Assert.That(CommandEqualityComparer.Instance.Equals(first, second), Is.False);
        }

        [Test]
        public void should_compare_string_properties_as_strings_not_unordered_characters()
        {
            var first = new TestCommand { Label = "abc" };
            var second = new TestCommand { Label = "cba" };

            Assert.That(CommandEqualityComparer.Instance.Equals(first, second), Is.False);
        }

        [Test]
        public void should_treat_collection_properties_as_unordered_sets()
        {
            var first = new TestCommand { DownloadIds = new List<string> { "one", "two" } };
            var second = new TestCommand { DownloadIds = new List<string> { "two", "one" } };

            Assert.That(CommandEqualityComparer.Instance.Equals(first, second), Is.True);
        }
    }
}
