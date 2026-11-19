using System;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Utilities
{
    [TestFixture]
    public class UnicodeCasefoldLookupFixture
    {
        [TestCase(0x269, "i")]
        [TestCase(0x3c2, "σ")]
        [TestCase(0x3f2, "σ")]
        [TestCase(0x1c80, "в")]
        [TestCase(0x1c81, "д")]
        [TestCase(0x1c82, "о")]
        [TestCase(0x1c83, "с")]
        [TestCase(0x1c84, "т")]
        [TestCase(0x1c85, "т")]
        [TestCase(0x1c86, "ъ")]
        [TestCase(0x1c87, "ѣ")]
        [TestCase(0x1c88, "ꙋ")]
        [TestCase(0x1e9b, "s")]
        [TestCase(0x1d6d3, "σ")]
        [TestCase(0x1d70d, "σ")]
        [TestCase(0x1d747, "σ")]
        [TestCase(0x1d781, "σ")]
        [TestCase(0x1d7bb, "σ")]
        public void canonical_letter_case_should_share_the_existing_fold_in_every_comparison_form(int codepoint, string expected)
        {
            var input = new System.Text.Rune(codepoint).ToString();
            var mapped = NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeWordsWithSourceSpans(input, null);
            Assert.Multiple(() =>
            {
                Assert.That(NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeKey(input), Is.EqualTo(expected));
                Assert.That(NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo(expected));
                Assert.That(NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeText(input), Is.EqualTo(expected));
                Assert.That(NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeTextWithSourceSpans(input).Text, Is.EqualTo(expected));
                Assert.That(mapped.Text, Is.EqualTo(expected));
                Assert.That(mapped.SourceSpans, Is.EqualTo(new[] { (0, input.Length) }));
                Assert.That(NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeKey(expected), Is.EqualTo(expected));
            });
        }

        [Test]
        public void canonical_letter_case_should_preserve_explicit_case_and_nonletter_marks()
        {
            const string input = "Σς ɩ ẛ 1\u0345";
            Assert.Multiple(() =>
            {
                Assert.That(NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeText(input, preserveCase: true), Is.EqualTo(input));
                Assert.That(NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeWords(input), Is.EqualTo("σσ i s 1\u0345"));
                Assert.That(NzbDrone.Core.Utilities.UnicodeComparisonNormalizer.NormalizeKey("Ɩ"), Is.EqualTo("i"));
            });
        }


        [TestCase(false)]
        [TestCase(true)]
        public void case_equivalent_narrator_name_should_resolve_the_stored_row(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var metadataRepository = new NarratorMetadataRepository(database, events, LogManager.GetCurrentClassLogger());
                var narratorRepository = new NarratorRepository(database, events);
                var metadata = metadataRepository.Insert(new NarratorMetadata { Name = "οδυσσεας", TitleSlug = "sigma-probe" });
                var narrator = narratorRepository.Insert(new Narrator
                {
                    NarratorMetadataId = metadata.Id, CleanName = metadata.Name.CleanNarratorName(), Added = DateTime.UtcNow
                });
                var exact = narratorRepository.FindByCleanNames(new[] { metadata.Name.CleanNarratorName() });
                var capitalKey = "ΟΔΥΣΣΕΑΣ".CleanNarratorName();
                Assert.Multiple(() =>
                {
                    Assert.That(exact.Select(row => row.Id), Is.EqualTo(new[] { narrator.Id }));
                    Assert.That(string.Equals(capitalKey, narrator.CleanName, StringComparison.OrdinalIgnoreCase), Is.True);
                    Assert.That(narratorRepository.FindByCleanNames(new[] { capitalKey }).Select(row => row.Id),
                        Is.EqualTo(new[] { narrator.Id }));
                });
            });
        }
    }
}
