using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class TitleBracketCatalogFixture
    {
        [TestCase("Garden (Illustrated)")]
        [TestCase("Garden [Illustrated]")]
        [TestCase("Garden （Illustrated）")]
        [TestCase("Garden ［Illustrated］")]
        [TestCase("Garden 【Illustrated】")]
        [TestCase("Garden {Illustrated}")]
        public void every_bracket_form_is_a_trailing_group(string title)
        {
            var removed = new List<string>();
            Assert.That(TitleBracketCatalog.BaseTitle(title, removed), Is.EqualTo("Garden"));
            Assert.That(removed, Is.EqualTo(new[] { "Illustrated" }));
            Assert.That(TitleBracketCatalog.FullThenBase(title), Is.EqualTo(new[] { title, "Garden" }));
            Assert.That(Regex.Match(title, TitleBracketCatalog.PairedSegmentPattern).Groups["label"].Value, Is.EqualTo("Illustrated"));
            Assert.That(TitleBracketCatalog.TrimUnclosedTail(title.Substring(0, title.Length - 1)), Is.EqualTo("Garden"));
            Assert.That(TitleBracketCatalog.TrimUnclosedTail(title), Is.EqualTo(title));
        }

        [Test]
        public void stacked_groups_are_all_removed_and_reported_last_first()
        {
            var removed = new List<string>();
            Assert.That(TitleBracketCatalog.BaseTitle("Garden (Book 2) 【Illustrated】", removed), Is.EqualTo("Garden"));
            Assert.That(removed, Is.EqualTo(new[] { "Illustrated", "Book 2" }));
            Assert.That(TitleBracketCatalog.BaseTitle("(Etc) (Illustrated)"), Is.EqualTo("(Etc)"));
        }

        [Test]
        public void a_nested_group_of_the_same_kind_is_removed_whole()
        {
            var removed = new List<string>();
            Assert.That(TitleBracketCatalog.BaseTitle("The Sandman #10 (The Sandman (1988-))", removed), Is.EqualTo("The Sandman #10"));
            Assert.That(removed, Is.EqualTo(new[] { "The Sandman (1988-)" }));
        }

        [TestCase("(Illustrated)")]
        [TestCase("Garden ()")]
        [TestCase("Garden (Illustrated]")]
        [TestCase("Garden")]
        public void a_title_is_never_reduced_to_nothing_or_cut_at_a_mismatched_or_empty_group(string title)
        {
            var removed = new List<string>();
            Assert.That(TitleBracketCatalog.BaseTitle(title, removed), Is.EqualTo(title));
            Assert.That(removed, Is.Empty);
            Assert.That(TitleBracketCatalog.FullThenBase(title), Is.EqualTo(new[] { title }));
        }

        [Test]
        public void paired_segment_requires_the_matching_closer()
        {
            Assert.That(Regex.IsMatch("Garden (Illustrated]", TitleBracketCatalog.PairedSegmentPattern), Is.False);
        }

        [TestCase("(Unabr", "(Unabr")]
        [TestCase("Garden (Unabr", "Garden")]
        public void an_unclosed_bracket_is_cut_only_after_some_title_text(string title, string expected)
        {
            Assert.That(TitleBracketCatalog.TrimUnclosedTail(title), Is.EqualTo(expected));
        }
    }
}
