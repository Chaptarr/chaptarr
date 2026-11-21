using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleasePackDetectorSceneSeparatorFixture
    {
        private static Book Target()
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };

            return new Book
            {
                Id = 39406, Author = author, Title = "Dune", SeriesName = "Dune", SeriesPosition = "1",
                Editions = new List<Edition> { new Edition { Id = 1, Title = "Dune", Monitored = true } }
            };
        }

        [TestCase("【Books 1-3】", "[Books 1-3]", ReleasePackDetectionVerdict.MultipleBooks)]
        [TestCase("【Dune 1-3】", "[Dune 1-3]", ReleasePackDetectionVerdict.MultipleBooks)]
        [TestCase("【1-3】", "[1-3]", ReleasePackDetectionVerdict.None)]
        [TestCase("［Dune 1-3］", "[Dune 1-3]", ReleasePackDetectionVerdict.MultipleBooks)]
        [TestCase("［1-3］", "[1-3]", ReleasePackDetectionVerdict.None)]
        [TestCase("（1 of 2）", "(1 of 2)", ReleasePackDetectionVerdict.SingleBookSplitRelease)]
        [TestCase("{1 of 2}", "(1 of 2)", ReleasePackDetectionVerdict.SingleBookSplitRelease)]
        [TestCase("{Unabridged}", "(Unabridged)", ReleasePackDetectionVerdict.None)]
        public void catalog_brackets_use_existing_pack_rules_without_changing_the_release(string suffix, string asciiSuffix, ReleasePackDetectionVerdict verdict)
        {
            var title = "Frank Herbert - Dune " + suffix;
            var release = new NzbDrone.Core.Parser.Model.ReleaseInfo { Title = title };
            var actual = ReleasePackDetector.Detect(release, Target());
            var expected = ReleasePackDetector.Detect("Frank Herbert - Dune " + asciiSuffix, Target());

            Assert.That(actual.Verdict, Is.EqualTo(verdict));
            Assert.That(actual.PackType, Is.EqualTo(expected.PackType));
            Assert.That(actual.MatchedValue, Is.EqualTo(expected.MatchedValue));
            Assert.That(release.Title, Is.EqualTo(title));
        }

        [TestCase("Frank.Herbert s.Dune.Saga.Collection .Books.1-6")]
        [TestCase("Frank_Herbert_Dune_Books_1-6")]
        [TestCase("Frank Herbert - Dune - Books 1-6")]
        [TestCase("Frank.Herbert.Dune.Vol.1-6")]
        public void dotted_and_underscored_pack_markers_should_be_detected(string releaseTitle)
        {
            var detection = ReleasePackDetector.Detect(releaseTitle, Target());

            Assert.That(detection.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks), releaseTitle);
        }

        [TestCase("Frank Herbert - Dune (v5.0) (epub)")]
        [TestCase("Frank.Herbert.-.Dune.2026-09-16.retail.epub")]
        [TestCase("Frank Herbert - Dune 1.5 - Some Novella")]
        [TestCase("Frank.Herbert.-.Dune.epub")]
        public void a_single_book_release_should_not_become_a_pack(string releaseTitle)
        {
            var detection = ReleasePackDetector.Detect(releaseTitle, Target());

            Assert.That(detection.Verdict, Is.Not.EqualTo(ReleasePackDetectionVerdict.MultipleBooks), releaseTitle);
        }

        [TestCase("Books.1-6", "Books 1-6")]
        [TestCase("v5.0", "v5.0")]
        [TestCase("1.5", "1.5")]
        [TestCase("2026-09-16", "2026-09-16")]
        [TestCase("Frank.Herbert", "Frank Herbert")]
        [TestCase("Dune_Saga", "Dune Saga")]
        public void separator_normalization_should_preserve_digit_pairs(string input, string expected)
        {
            Assert.That(ReleasePackDetector.NormalizeSceneSeparators(input), Is.EqualTo(expected));
        }
    }
}
