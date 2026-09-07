using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class MatchEvidenceValueBuilderFixture
    {
        [Test]
        public void supplementary_letters_should_distinguish_proof_and_preserve_highlights()
        {
            const string raw = "🌶️ Astorias𠀀Smut’s edition";
            var sut = new MatchEvidenceValueBuilder();
            sut.AddPhrase("embedded_tag", "TITLE", raw, "Astorias𠀀Smuts", "supporting", "title", "book", "Title proof");
            var value = sut.Build().Single();
            Assert.That(Slice(value, value.Ranges.Single()), Is.EqualTo("Astorias𠀀Smut’s"));

            var other = new MatchEvidenceValueBuilder();
            other.AddPhrase("embedded_tag", "TITLE", raw, "Astorias𠀁Smuts", "supporting", "title", "book", "Title proof");
            Assert.That(other.Build().SelectMany(v => v.Ranges), Is.Empty);
        }

        [TestCase("Ready Player 𝟚", "Ready Player Two")]
        [TestCase("Ready Player ②", "Ready Player Two")]
        [TestCase("23 1/2 Lies", "23 ½ Lies")]
        [TestCase("The 7 ½ Deaths of Evelyn Hardcastle", "The 7½ Deaths of Evelyn Hardcastle")]
        public void numeric_title_proof_should_highlight_the_original_spelling(string raw, string title)
        {
            var sut = new MatchEvidenceValueBuilder();
            sut.AddPhrase("embedded_tag", "TITLE", raw, title, "supporting", "title", "book", "Title proof");
            var value = sut.Build().Single();
            Assert.That(Slice(value), Is.EqualTo(raw));
        }

        [Test]
        public void a_different_numeric_title_should_not_be_highlighted_as_proof()
        {
            var sut = new MatchEvidenceValueBuilder();
            sut.AddPhrase("embedded_tag", "TITLE", "Ready Player ②", "Ready Player One", "supporting", "title", "book", "Title proof");
            Assert.That(sut.Build(), Is.Empty);
        }

        [Test]
        public void should_group_duplicate_extractor_fields_by_the_exact_raw_value()
        {
            const string rawValue = "The Deal (Off-Campus #1)";
            var sut = new MatchEvidenceValueBuilder();

            sut.AddPhrase("embedded_tag", "TITLE", rawValue, "The Deal", "supporting", "title", "book", "Title proof");
            sut.AddPhrase("embedded_tag", "MP4:©nam", rawValue, "The Deal", "supporting", "title", "book", "Title proof");

            var result = sut.Build();

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result.Single().Value, Is.EqualTo(rawValue));
            Assert.That(result.Single().Fields, Is.EquivalentTo(new[] { "TITLE", "MP4:©nam" }));
            Assert.That(result.Single().Ranges, Has.Count.EqualTo(1));
            Assert.That(Slice(result.Single()), Is.EqualTo("The Deal"));
        }

        [Test]
        public void should_retain_decision_time_support_conflict_and_neutral_ranges_against_raw_text()
        {
            const string rawValue = "The Deal (Off-Campus #1) (Unabridged)";
            var sut = new MatchEvidenceValueBuilder();

            sut.AddPhrase("embedded_tag", "TITLE", rawValue, "The Deal", "supporting", "title", "book", "Title proof");
            sut.AddMatchingTokens("embedded_tag", "TITLE", rawValue, new[] { "Off", "Campus" }, "supporting", "series_name", "book", "Series support");
            sut.AddLiteral("embedded_tag", "TITLE", rawValue, "1", "conflicting", "series_position", "book", "Position conflict");
            sut.AddNeutralRemainder("embedded_tag", "TITLE", rawValue, "book", "Tolerated text");

            var result = sut.Build().Single();

            Assert.That(result.Ranges.All(range => range.Start >= 0 && range.End <= result.Value.Length), Is.True);
            Assert.That(result.Ranges.Where(range => range.Type == "title").Select(range => Slice(result, range)), Is.EquivalentTo(new[] { "The Deal" }));
            Assert.That(result.Ranges.Where(range => range.Type == "series_name").Select(range => Slice(result, range)), Is.EquivalentTo(new[] { "Off-Campus" }));
            Assert.That(result.Ranges.Single(range => range.Type == "series_position").Disposition, Is.EqualTo("conflicting"));
            Assert.That(Slice(result, result.Ranges.Single(range => range.Type == "series_position")), Is.EqualTo("1"));
            Assert.That(result.Ranges.Where(range => range.Disposition == "neutral").Select(range => Slice(result, range)), Does.Contain("Unabridged"));
        }

        [TestCase("M02 Harry Potter and the Goblet of Fire - Unabridged", "Harry Potter and the Goblet of Fire")]
        [TestCase("M02 Harry Potter Goblet of Fire - Unabridged", "Harry Potter Goblet of Fire")]
        public void should_highlight_the_complete_accepted_title_span(string rawValue, string expectedSpan)
        {
            var sut = new MatchEvidenceValueBuilder();

            sut.AddPhrase("embedded_tag", "TITLE", rawValue, "Harry Potter and the Goblet of Fire", "supporting", "title", "book", "Title proof", allowNearExact: true);
            sut.AddNeutralRemainder("embedded_tag", "TITLE", rawValue, "book", "Tolerated text");

            var result = sut.Build().Single();

            Assert.That(Slice(result, result.Ranges.Single(range => range.Type == "title")), Is.EqualTo(expectedSpan));
            Assert.That(result.Ranges.Where(range => range.Disposition == "neutral").Select(range => Slice(result, range)), Is.EquivalentTo(new[] { "M02", "Unabridged" }));
        }

        [Test]
        public void truncation_should_keep_the_annotated_text_and_rebase_offsets()
        {
            var rawValue = new string('x', 620) + " The Deal " + new string('y', 620);
            var sut = new MatchEvidenceValueBuilder();

            sut.AddPhrase("embedded_tag", "TITLE", rawValue, "The Deal", "supporting", "title", "book", "Title proof");

            var result = sut.Build().Single();
            var range = result.Ranges.Single();

            Assert.That(result.Value.Length, Is.LessThanOrEqualTo(500));
            Assert.That(result.Value, Does.StartWith("…"));
            Assert.That(result.Value, Does.EndWith("…"));
            Assert.That(range.Start, Is.GreaterThanOrEqualTo(0));
            Assert.That(range.End, Is.LessThanOrEqualTo(result.Value.Length));
            Assert.That(Slice(result, range), Is.EqualTo("The Deal"));
        }

        private static string Slice(MatchEvidenceValue value)
        {
            return Slice(value, value.Ranges.Single());
        }

        private static string Slice(MatchEvidenceValue value, MatchEvidenceRange range)
        {
            return value.Value.Substring(range.Start, range.End - range.Start);
        }
    }
}
