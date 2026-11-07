using NUnit.Framework;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Profiles.Metadata
{
    [TestFixture]
    public class PreferredEditionTermMatcherFixture
    {
        [Test]
        public void should_return_the_highest_matching_weight_across_edition_fields()
        {
            var score = PreferredEditionTermMatcher.Score(
                "1|paperback\n3|audible, 2|penguin",
                "The Book",
                "Audible Audiobook",
                "Penguin");

            Assert.That(score, Is.EqualTo(1.0));
        }

        [Test]
        public void should_score_a_nonmatching_edition_as_zero()
        {
            var score = PreferredEditionTermMatcher.Score(
                "3|audible\n1|kindle",
                "The Book",
                "Paperback",
                "Penguin");

            Assert.That(score, Is.Zero);
        }

        [Test]
        public void should_ignore_invalid_terms_and_support_case_insensitive_matching()
        {
            var score = PreferredEditionTermMatcher.Score(
                "0|bad\ninvalid\n2 | KINDLE , -1|ignored",
                "The Book",
                "Kindle Edition",
                null);

            Assert.That(score, Is.EqualTo(1.0));
        }

        [Test]
        public void should_normalize_weights_within_the_profile()
        {
            var score = PreferredEditionTermMatcher.Score(
                "10|audible\n5|kindle",
                "The Book",
                "Kindle Edition",
                null);

            Assert.That(score, Is.EqualTo(0.5));
        }

        [Test]
        public void should_return_zero_when_preferences_are_empty_or_invalid()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PreferredEditionTermMatcher.Score(null, "Title", "Format", "Publisher"), Is.Zero);
                Assert.That(PreferredEditionTermMatcher.Score("0|bad\nterm", "Title", "Format", "Publisher"), Is.Zero);
            });
        }
    }
}
