using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Messaging
{
    [TestFixture]
    public class CommandEqualityComparerFixture
    {
        [Test]
        public void book_search_sweep_does_not_deduplicate_an_explicit_selection()
        {
            var selection = new BookSearchCommand(new List<int> { 1, 2 });
            var sweep = new BookSearchCommand(new List<int> { 1, 2 }) { MonitoredOnly = true };

            Assert.That(CommandEqualityComparer.Instance.Equals(selection, sweep), Is.False);
        }

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
