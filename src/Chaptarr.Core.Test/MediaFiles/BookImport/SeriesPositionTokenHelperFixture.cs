using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class SeriesPositionTokenHelperFixture
    {
        [Test]
        public void should_expand_first_position_forms()
        {
            var tokens = SeriesPositionTokenHelper.GetPositionTokens("1");

            Assert.That(tokens, Does.Contain("1"));
            Assert.That(tokens, Does.Contain("01"));
            Assert.That(tokens, Does.Contain("i"));
            Assert.That(tokens, Does.Contain("one"));
            Assert.That(tokens, Does.Contain("first"));
        }

        [Test]
        public void should_expand_word_position_forms_back_to_number()
        {
            var tokens = SeriesPositionTokenHelper.GetPositionTokens("first");

            Assert.That(tokens, Does.Contain("1"));
            Assert.That(tokens, Does.Contain("01"));
            Assert.That(tokens, Does.Contain("i"));
            Assert.That(tokens, Does.Contain("one"));
            Assert.That(tokens, Does.Contain("first"));
        }

        [TestCase("Volume II")]
        [TestCase("Parte 2")]
        [TestCase("第2巻")]
        public void should_detect_compact_position_identity_without_english_only_labels(string value)
        {
            Assert.That(SeriesPositionTokenHelper.HasPositionIdentity(value), Is.True);
        }

        [TestCase("Train Your Mind for Extraordinary Performance")]
        [TestCase("Entrena tu mente para un rendimiento extraordinario")]
        [TestCase("7 Habits of Highly Effective People")]
        public void should_not_treat_descriptive_subtitles_as_position_identity(string value)
        {
            Assert.That(SeriesPositionTokenHelper.HasPositionIdentity(value), Is.False);
        }

        [TestCase("II", true)]
        [TestCase("XIV", true)]
        [TestCase("MIX", true)]
        [TestCase("VI", true)]
        [TestCase("Mild", false)]
        [TestCase("Civil", false)]
        [TestCase("IL", false)]
        [TestCase("IM", false)]
        [TestCase("", false)]
        public void should_classify_only_canonical_roman_tokens(string value, bool expected)
        {
            Assert.That(SeriesPositionTokenHelper.LooksLikeRomanNumeralToken(value), Is.EqualTo(expected));
        }
    }
}
