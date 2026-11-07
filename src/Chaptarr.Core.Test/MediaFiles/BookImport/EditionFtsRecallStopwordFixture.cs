using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class EditionFtsRecallStopwordFixture
    {
        [Test]
        public void common_function_words_should_not_be_used_as_recall_keys()
        {
            var kept = EditionFtsRepository.DropRecallStopwords(new List<string> { "the", "hobbit", "and", "there", "and", "back", "again", "of", "1" });

            Assert.That(kept, Is.EqualTo(new[] { "hobbit", "there", "back", "again", "1" }));
        }

        [Test]
        public void series_words_and_digits_should_be_kept()
        {
            var kept = EditionFtsRepository.DropRecallStopwords(new List<string> { "part", "book", "volume", "2" });

            Assert.That(kept, Is.EqualTo(new[] { "part", "book", "volume", "2" }));
        }

        [Test]
        public void a_title_made_only_of_stopwords_should_keep_its_terms()
        {
            var terms = new List<string> { "the", "and" };

            Assert.That(EditionFtsRepository.DropRecallStopwords(terms), Is.EqualTo(terms));
        }

        [Test]
        public void stopwords_should_match_regardless_of_case()
        {
            Assert.That(EditionFtsRepository.DropRecallStopwords(new List<string> { "The", "AND", "Odyssey" }), Is.EqualTo(new[] { "Odyssey" }));
        }

        private static Dictionary<string, int> Frequencies(params (string Lexeme, int Df)[] entries)
        {
            var frequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                frequencies[entry.Lexeme] = entry.Df;
            }

            return frequencies;
        }

        [Test]
        public void common_tokens_should_be_dropped_when_a_rare_token_remains()
        {
            var lexemes = new List<string> { "legend", "drizzt", "book", "passage", "dawn" };
            var frequencies = Frequencies(
                ("legend", 900),
                ("drizzt", 41),
                ("book", 46000),
                ("passage", 2300),
                ("dawn", 5200));

            var kept = EditionFtsRepository.PruneCommonRecallTokens(lexemes, frequencies, 3000);

            Assert.That(kept, Is.EqualTo(new[] { "legend", "drizzt", "passage" }));
        }

        [Test]
        public void a_token_at_the_threshold_should_be_kept()
        {
            var kept = EditionFtsRepository.PruneCommonRecallTokens(
                new List<string> { "hobbit", "volume" },
                Frequencies(("hobbit", 12), ("volume", 3000)),
                3000);

            Assert.That(kept, Is.EqualTo(new[] { "hobbit", "volume" }));
        }

        [Test]
        public void an_unmeasured_token_should_be_treated_as_rare()
        {
            var kept = EditionFtsRepository.PruneCommonRecallTokens(
                new List<string> { "kvothe", "book" },
                Frequencies(("book", 46000)),
                3000);

            Assert.That(kept, Is.EqualTo(new[] { "kvothe" }));
        }

        [Test]
        public void frequencies_should_be_matched_regardless_of_case()
        {
            var kept = EditionFtsRepository.PruneCommonRecallTokens(
                new List<string> { "Drizzt", "Book" },
                Frequencies(("drizzt", 41), ("book", 46000)),
                3000);

            Assert.That(kept, Is.EqualTo(new[] { "Drizzt" }));
        }

        [Test]
        public void all_common_tokens_should_keep_the_least_common_few_in_query_order()
        {
            var lexemes = new List<string> { "book", "one", "part", "volume" };
            var frequencies = Frequencies(
                ("book", 46000),
                ("one", 9000),
                ("part", 21000),
                ("volume", 12000));

            var kept = EditionFtsRepository.PruneCommonRecallTokens(lexemes, frequencies, 3000);

            Assert.That(kept, Is.EqualTo(new[] { "one", "volume" }));
            Assert.That(kept.Count, Is.EqualTo(EditionFtsRepository.MinKeptCommonRecallTokens));
        }

        [Test]
        public void a_single_token_should_never_be_pruned()
        {
            var kept = EditionFtsRepository.PruneCommonRecallTokens(
                new List<string> { "book" },
                Frequencies(("book", 46000)),
                3000);

            Assert.That(kept, Is.EqualTo(new[] { "book" }));
        }

        [Test]
        public void no_measurements_should_leave_every_token_in_place()
        {
            var lexemes = new List<string> { "book", "part" };

            Assert.That(EditionFtsRepository.PruneCommonRecallTokens(lexemes, null, 3000), Is.EqualTo(lexemes));
            Assert.That(EditionFtsRepository.PruneCommonRecallTokens(lexemes, Frequencies(), 3000), Is.EqualTo(lexemes));
        }

        [Test]
        public void an_unknown_threshold_should_disable_pruning()
        {
            var lexemes = new List<string> { "book", "drizzt" };

            Assert.That(
                EditionFtsRepository.PruneCommonRecallTokens(lexemes, Frequencies(("book", 46000)), 0),
                Is.EqualTo(lexemes));
        }

        [Test]
        public void no_lexemes_should_prune_to_an_empty_list()
        {
            Assert.That(EditionFtsRepository.PruneCommonRecallTokens(null, Frequencies(), 3000), Is.Empty);
        }

        [Test]
        public void threshold_should_scale_with_the_library_but_keep_a_floor()
        {
            Assert.That(EditionFtsRepository.RecallDocumentFrequencyThreshold(500000), Is.EqualTo(5000));
            Assert.That(
                EditionFtsRepository.RecallDocumentFrequencyThreshold(1000),
                Is.EqualTo(EditionFtsRepository.MinRecallTokenDocumentFrequencyThreshold));
            Assert.That(EditionFtsRepository.RecallDocumentFrequencyThreshold(0), Is.EqualTo(0));
        }

        [Test]
        public void lexeme_extraction_should_split_punctuation_and_deduplicate()
        {
            var lexemes = EditionFtsRepository.ExtractPostgresLexemes(new[] { "legend-of", "Drizzt", "drizzt", "..." });

            Assert.That(lexemes, Is.EqualTo(new[] { "legend", "of", "Drizzt" }));
        }
    }
}
