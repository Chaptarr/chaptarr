using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class HolyGrailTagCategorizationFixture
    {
        [Test]
        public void should_classify_container_prefixed_comment_tags_as_comments()
        {
            var sut = CreateService();

            var method = typeof(FileMatchingService).GetMethod("CategorizeTagsForHolyGrail", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Never Finished: Unshackle Your Mind and Win the War Within" },
                ["MP4:©cmt"] = new List<string> { "Can’t Hurt Me, David Goggins’ smash hit memoir..." },
                ["ID3v2:COMM:eng"] = new List<string> { "Some comment frame" },
                ["MP4:©lyr"] = new List<string> { "Some lyrics" }
            };

            var categorized = (Dictionary<string, List<string>>)method.Invoke(sut, new object[] { tags });

            Assert.That(categorized.ContainsKey("TITLE"), Is.True);
            Assert.That(categorized.ContainsKey("MP4:©cmt"), Is.False);
            Assert.That(categorized.ContainsKey("ID3v2:COMM:eng"), Is.False);
            Assert.That(categorized.ContainsKey("MP4:©lyr"), Is.False);
        }

        [TestCase("Søren", "søren", "soren")]
        [TestCase("Łukasz", "łukasz", "lukasz")]
        [TestCase("Ælfric", "ælfric", "aelfric")]
        [TestCase("Straße", "straße", "strasse")]
        [TestCase("Королёв", "королёв", "королев")]
        [TestCase("がとう", "がとう", "がとう")]
        [TestCase("𠀀Reader", "𠀀reader", "𠀀reader")]
        public void eligible_import_queries_should_use_shared_fold_and_preserve_raw_fields_without_comment_evidence(string spelling, string originalTerm, string foldedTerm)
        {
            var service = CreateService();
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new() { "Neutral Story" },
                ["PUBLISHER"] = new() { spelling + " Press" },
                ["MP4:©cmt"] = new() { "ForbiddenMarker" }
            };
            var categorize = typeof(FileMatchingService).GetMethod("CategorizeTagsForHolyGrail", BindingFlags.Instance | BindingFlags.NonPublic);
            var eligible = (Dictionary<string, List<string>>)categorize.Invoke(service, new object[] { tags });
            var tokenize = typeof(FileMatchingService).GetMethod("TokenizeForHolyGrail", BindingFlags.Instance | BindingFlags.NonPublic);
            var terms = (List<string>)tokenize.Invoke(service, new object[] { eligible });
            var fields = ResidualFields(service, tags, Array.Empty<string>());
            var publisher = fields.Single(field => field.Key == "PUBLISHER[0]");

            Assert.Multiple(() =>
            {
                Assert.That(terms, Does.Contain(foldedTerm));
                Assert.That(terms, Does.Not.Contain("forbiddenmarker"));
                Assert.That(publisher.Terms, Does.Contain(foldedTerm));
                if (originalTerm != foldedTerm)
                {
                    Assert.That(terms, Does.Not.Contain(originalTerm));
                    Assert.That(publisher.Terms, Does.Not.Contain(originalTerm));
                }

                Assert.That(publisher.ResidualValue, Is.EqualTo(spelling + " Press"));
                Assert.That(publisher.SourceFields, Is.EqualTo(new[] { "PUBLISHER[0]" }));
                Assert.That(eligible["PUBLISHER"], Is.EqualTo(new[] { spelling + " Press" }));
                Assert.That(tags["PUBLISHER"], Is.EqualTo(new[] { spelling + " Press" }));
                Assert.That(tags["MP4:©cmt"], Is.EqualTo(new[] { "ForbiddenMarker" }));
                Assert.That(fields.Select(field => field.Key), Does.Not.Contain("MP4:©cmt[0]"));
            });
        }

        [TestCase("Frank Herbert Dune George Guidall", "Dune George Guidall")]
        [TestCase("Dune Frank Herbert George Guidall", "Dune George Guidall")]
        [TestCase("Frank Herbert Dune Frank Herbert", "Dune Frank Herbert")]
        public void author_consumption_spends_only_one_occurrence_and_keeps_the_rest_of_the_value(string observed, string expected)
        {
            var tags = new Dictionary<string, List<string>> { ["FIELD"] = new() { observed } };
            var fields = ResidualFields(CreateService(), tags, new[] { "Frank Herbert" });
            Assert.That(fields.Single().ResidualValue, Is.EqualTo(expected));
            Assert.That(tags["FIELD"].Single(), Is.EqualTo(observed));
        }

        [Test]
        public void author_consumption_preserves_independent_self_title_and_narrator_occurrences()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["FIRST"] = new() { "Frank Herbert" },
                ["SECOND"] = new() { "Frank Herbert" },
                ["THIRD"] = new() { "Frank Herbert" }
            };
            var fields = ResidualFields(CreateService(), tags, new[] { "Frank Herbert" });
            Assert.That(fields.Select(field => field.ResidualValue), Is.EqualTo(new[] { "Frank Herbert", "Frank Herbert" }));
            Assert.That(tags.Values.SelectMany(values => values).Count(), Is.EqualTo(3));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void author_consumption_prefers_standalone_name_regardless_of_tag_order_or_label(bool reverseOrder, bool swapLabels)
        {
            var titleField = swapLabels ? "AUTHOR" : "TITLE";
            var creditField = swapLabels ? "TITLE" : "AUTHOR";
            var tags = new Dictionary<string, List<string>>
            {
                [titleField] = new() { "Hugh Howey Free Short Stories" },
                [creditField] = new() { "Hugh Howey" }
            };
            if (reverseOrder)
            {
                tags = tags.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            var fields = ResidualFields(CreateService(), tags, new[] { "Hugh Howey" });
            Assert.That(fields.Select(field => (field.Key, field.ResidualValue)),
                Is.EqualTo(new[] { (titleField + "[0]", "Hugh Howey Free Short Stories") }));
            Assert.That(tags[creditField].Single(), Is.EqualTo("Hugh Howey"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void author_consumption_prefers_the_fewest_unspent_tokens_when_every_value_is_mixed(bool reverseOrder)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["FIRST"] = new() { "Hugh Howey Free Short Stories" },
                ["SECOND"] = new() { "Hugh Howey 2012" }
            };
            if (reverseOrder)
            {
                tags = tags.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            var fields = ResidualFields(CreateService(), tags, new[] { "Hugh Howey" });
            Assert.That(fields.Single(field => field.Key == "FIRST[0]").ResidualValue, Is.EqualTo("Hugh Howey Free Short Stories"));
            Assert.That(fields.Single(field => field.Key == "SECOND[0]").ResidualValue, Is.EqualTo("2012"));
        }

        [TestCase("MP4:©ART", "ARTIST", false)]
        [TestCase("MP4:©ART", "ARTIST", true)]
        [TestCase("author", "authors", false)]
        [TestCase("author", "authors", true)]
        public void author_consumption_prefers_clean_physical_credit_without_spending_its_generated_alias(
            string sourceField, string aliasField, bool reverseOrder)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new() { "Hugh Howey Free Short Stories" },
                [sourceField] = new() { "Hugh Howey" },
                [aliasField] = new() { "Hugh Howey" }
            };
            if (reverseOrder)
            {
                tags = tags.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            var fields = ResidualFields(CreateService(), tags, new[] { "Hugh Howey" });
            Assert.That(fields.Select(field => (field.Key, field.ResidualValue)),
                Is.EqualTo(new[] { ("TITLE[0]", "Hugh Howey Free Short Stories") }));
        }

        [TestCase("creator_aut", false)]
        [TestCase("creator_aut", true)]
        [TestCase("creator_creator", false)]
        [TestCase("creator_creator", true)]
        public void epub_author_views_must_not_duplicate_a_creator_credit(string sourceField, bool reverseOrder)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = new() { "The Richest Man in Babylon" },
                ["author"] = new() { "George S. Clason" },
                ["authors"] = new() { "George S. Clason" },
                ["all_authors"] = new() { "George S. Clason" },
                [sourceField] = new() { "George S. Clason" }
            };
            if (reverseOrder)
            {
                tags = tags.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            var fields = ResidualFields(CreateService(), tags, new[] { "George S. Clason" });
            Assert.That(fields.Select(field => (field.Key, field.ResidualValue)),
                Is.EqualTo(new[] { ("title[0]", "The Richest Man in Babylon") }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void author_alias_suppression_preserves_distinct_values_and_independent_credits(bool sourcePresent)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["authors"] = new() { "Ursula Le Guin", "Other Writer" },
                ["title"] = new() { "Ursula Le Guin" },
                ["narrator"] = new() { "Ursula Le Guin" }
            };
            if (sourcePresent)
            {
                tags["author"] = new() { "Ursula Le Guin" };
            }

            var fields = ResidualFields(CreateService(), tags, new[] { "Ursula Le Guin" });
            Assert.That(fields.Select(field => field.ResidualValue),
                Is.EquivalentTo(new[] { "Other Writer", "Ursula Le Guin", "Ursula Le Guin" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void equally_clean_author_occurrences_have_a_stable_text_order(bool reverseOrder)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["FIRST"] = new() { "Hugh Howey Story" },
                ["SECOND"] = new() { "Hugh Howey Collection" }
            };
            if (reverseOrder)
            {
                tags = tags.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            var fields = ResidualFields(CreateService(), tags, new[] { "Hugh Howey" });
            Assert.That(fields.Single(field => field.Key == "FIRST[0]").ResidualValue, Is.EqualTo("Hugh Howey Story"));
            Assert.That(fields.Single(field => field.Key == "SECOND[0]").ResidualValue, Is.EqualTo("Collection"));
        }

        [Test]
        public void canonical_and_pseudonym_names_spend_only_one_author_occurrence()
        {
            var tags = new Dictionary<string, List<string>> { ["FIELD"] = new() { "Jack Sharp The Martian Andy Weir" } };
            var fields = ResidualFields(CreateService(), tags, new[] { "Andy Weir", "Jack Sharp" });
            Assert.That(fields.Single().ResidualValue, Is.EqualTo("Jack Sharp The Martian"));
        }

        [TestCase("Robert Stevenson", "Robert Louis Stevenson Treasure Island", "Treasure Island")]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson Treasure Island", "Treasure Island")]
        [TestCase("John Q Smith", "John R Smith Story", "John R Smith Story")]
        [TestCase("Габриэль Гарсиа", "Габриэль Гарсиа История", "История")]
        public void consumption_uses_the_shared_author_name_rule(string name, string observed, string expected)
        {
            var fields = ResidualFields(CreateService(), new() { ["FIELD"] = new() { observed } }, new[] { name });
            Assert.That(fields.Single().ResidualValue, Is.EqualTo(expected));
        }

        private static List<EditionFtsFieldQuery> ResidualFields(FileMatchingService service,
            Dictionary<string, List<string>> tags, IReadOnlyList<string> names)
        {
            var consume = typeof(FileMatchingService).GetMethod("ConsumeAuthorEvidence", BindingFlags.Instance | BindingFlags.NonPublic);
            var occurrences = consume.Invoke(service, new object[] { tags, new[] { names }, "fixture", "fixture.m4b" });
            var query = typeof(FileMatchingService).GetMethod("BuildStagedFtsFieldQueries", BindingFlags.Instance | BindingFlags.NonPublic);
            return (List<EditionFtsFieldQuery>)query.Invoke(service,
                new object[] { occurrences, Array.Empty<BookFtsMatch>(), "fixture", "fixture.m4b" });
        }

        private static FileMatchingService CreateService()
        {
            return new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: null,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: null,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: null,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: LogManager.GetCurrentClassLogger());
        }
    }
}
