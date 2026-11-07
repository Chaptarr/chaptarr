using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BookshelfMatchSettingMigrationFixture
    {
        [TestCase(50, BookMatchingStrictness.Aggressive)]
        [TestCase(66, BookMatchingStrictness.Aggressive)]
        [TestCase(67, BookMatchingStrictness.Balanced)]
        [TestCase(82, BookMatchingStrictness.Balanced)]
        [TestCase(83, BookMatchingStrictness.Strict)]
        [TestCase(99, BookMatchingStrictness.Strict)]
        public void should_map_each_legacy_match_threshold_band(int percent, BookMatchingStrictness expected)
        {
            Assert.That(BookshelfMatchSettingMigration.TryMapMinimumMatchPercent(percent, out var actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase(49)]
        [TestCase(100)]
        public void should_reject_values_outside_the_legacy_setting_range(int percent)
        {
            Assert.That(BookshelfMatchSettingMigration.TryMapMinimumMatchPercent(percent, out _), Is.False);
        }
    }
}
