using System.Collections.Generic;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class AuthorContainmentEvidenceFixture
    {
        private static ContainmentValidator CreateValidator()
        {
            return new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger());
        }

        [TestCase("COMMENT")]
        [TestCase("MP4:©cmt")]
        [TestCase("ID3v2:COMM:eng")]
        [TestCase("XIPH:DESCRIPTION")]
        [TestCase("copyright")]
        public void excluded_fields_should_not_validate_an_author(string field)
        {
            var tags = new Dictionary<string, List<string>>
            {
                [field] = new List<string> { "For readers of Stephen King" }
            };

            Assert.That(CreateValidator().ValidateAuthorInTags("Stephen King", tags), Is.False);
        }

        [Test]
        public void arbitrary_nonexcluded_field_should_validate_an_author()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["CUSTOM_PEOPLE"] = new List<string> { "Written by Ursula K. Le Guin" }
            };

            Assert.That(CreateValidator().ValidateAuthorInTags("Ursula K. Le Guin", tags), Is.True);
        }

        [Test]
        public void real_author_evidence_should_win_without_consulting_a_rival_named_in_comment()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["ODD_FIELD"] = new List<string> { "Ursula K. Le Guin" },
                ["COMMENT"] = new List<string> { "For readers of Stephen King" }
            };

            var validator = CreateValidator();

            Assert.Multiple(() =>
            {
                Assert.That(validator.ValidateAuthorInTags("Ursula K. Le Guin", tags), Is.True);
                Assert.That(validator.ValidateAuthorInTags("Stephen King", tags), Is.False);
            });
        }

        [Test]
        public void canonical_path_author_evidence_should_remain_eligible()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["AUTHOR"] = new List<string> { "J.K. Rowling" }
            };

            Assert.That(CreateValidator().ValidateAuthorInTags("J.K. Rowling", tags), Is.True);
        }

        [TestCase("John Smith", "John Smith The Book Title", true)]
        [TestCase("John Smith", "The Book Title John Smith", true)]
        [TestCase("Frank Herbert", "Frank Herberts The Book Title", true)]
        [TestCase("John Smith", "John Smith - The Book Title", true)]
        [TestCase("Ann Smith", "Mary Ann Smith The Book Title", true)]
        public void known_book_conversion_should_preserve_complete_author_proof_in_a_combined_source_field(string authorName, string observedTitle, bool hasAuthorEvidence)
        {
            var source = new LocalBook
            {
                Path = @"C:\Downloads\The Book Title.mp3".AsOsAgnostic(),
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["TITLE"] = new List<string> { observedTitle }
                    }
                }
            };
            var validator = CreateValidator();
            var options = ConversionTagProposalBuilder.BuildOptions(
                new[] { source }, new Book { Title = "The Book Title" }, new Author { Name = authorName },
                new Edition { Title = "The Book Title" }, validator, ConversionTagModes.Preserve);

            TestContext.Progress.WriteLine($"Known-title preserve proposal: author='{authorName}', observed='{observedTitle}', literalAuthor={validator.ValidateAuthorInTags(authorName, source.RawTags.AllTags)}, artist='{options.Artist ?? "<null>"}', title='{options.Name ?? "<null>"}'.");
            Assert.Multiple(() =>
            {
                Assert.That(options.Name, Is.EqualTo(observedTitle));
                Assert.That(options.Artist, Is.EqualTo(hasAuthorEvidence ? observedTitle : null));
                Assert.That(options.AlbumArtist, Is.EqualTo(hasAuthorEvidence ? observedTitle : null));
            });
        }

        [TestCase("John Smith", "John Smith The Book Title", "The Book Title", true)]
        [TestCase("John Smith", "The Book Title John Smith", "The Book Title", true)]
        [TestCase("Ann Smith", "Mary Ann Smith The Book Title", "The Book Title", true)]
        [TestCase("John Smith", "John Smith Title Book The", "The Book Title", false)]
        [TestCase("John Smith", "John Smith Book Title", "The Book Title", true)]
        [TestCase("John Smith", "John Smith The Book Title", "", false)]
        public void known_title_import_proof_must_keep_name_and_ordered_title_as_separate_signals(string authorName, string observed, string knownTitle, bool hasRequiredIdentity)
        {
            var proof = MatchIdentityProofFactory.FromExpectedIdentity(authorName, new[] { knownTitle },
                new Dictionary<string, List<string>> { ["TITLE"] = new List<string> { observed } }, CreateValidator());

            Assert.Multiple(() =>
            {
                Assert.That(proof.Has(MatchIdentityRole.Author), Is.True);
                Assert.That(MatchIdentityProofMembership.HasRequiredIdentity(proof), Is.EqualTo(hasRequiredIdentity));
            });
        }

        [TestCase("Isaac Asimov", "Asimov, Isaac")]
        [TestCase("Isaac Asimov", "asimov, isaac")]
        [TestCase("Ursula K. Le Guin", "Le Guin, Ursula K.")]
        [TestCase("J. R. R. Tolkien", "Tolkien, J.R.R.")]
        public void comma_inverted_author_tag_should_validate(string authorName, string tagValue)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["ARTIST"] = new List<string> { tagValue }
            };

            Assert.That(CreateValidator().ValidateAuthorInTags(authorName, tags), Is.True);
        }

        [TestCase("Isaac Asimov", "Isaac")]
        [TestCase("Isaac Asimov", "Asimov")]
        [TestCase("Isaac Asimov", "Petrov, Ivan")]
        public void partial_or_wrong_author_tag_should_not_validate(string authorName, string tagValue)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["ARTIST"] = new List<string> { tagValue }
            };

            Assert.That(CreateValidator().ValidateAuthorInTags(authorName, tags), Is.False);
        }

        [TestCase("Pat Smith", "Titre par Pat Smith", true)]
        [TestCase("Pat Smith", "Titel von Pat Smith", true)]
        [TestCase("Pat Smith", "Gelesen von Pat Smith", true)]
        [TestCase("Pat Smith", "Ann und Pat Smith", true)]
        [TestCase("Pat Smith", "Ann et Pat Smith", true)]
        [TestCase("Ann Smith", "Mary Ann Smith", true)]
        [TestCase("Lee", "Stan Lee", true)]
        [TestCase("Jennifer Q. Armentrout", "Armentrout, Jennifer L.", false)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Armentrout", true)]
        [TestCase("Jennifer L. Armentrout", "Armentrout, Jennifer", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout", false)]
        [TestCase("Jennifer L. Armentrout", "Armentrout, Jennifer Q.", false)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout; another contributor", false)]
        // One trailing surname s is compatible in either direction.
        [TestCase("Jennifer L. Armentrout", "Jennifer L. Armentrouts", true)]
        [TestCase("CS Lewis", "C S Lewis", true)]
        [TestCase("C. S. Lewis", "CS Lewis", true)]
        [TestCase("Clive Staples Lewis", "C S Lewis", true)]
        [TestCase("Clive Staples Lewis", "CS Lewis", false)]
        [TestCase("John Smith", "James O H N Smith", false)]
        [TestCase("JOHN SMITH", "James O H N Smith", false)]
        [TestCase("Clive Staples Lewis", "C Q Lewis", false)]
        [TestCase("Ursula Guin", "Le Guin, Ursula K.", false)]
        [TestCase("John Smith", "Pat Smith, John", false)]
        [TestCase("John Smith", "Pat Smith John", false)]
        [TestCase("Jo Rowling", "John O Rowling", false)]
        [TestCase("J K Rowling", "Joanne Rowling", true)]
        [TestCase("Joanne Rowling", "J.K. Rowling", true)]
        [TestCase("J K Rowling", "J Q Rowling", false)]
        [TestCase("John Smith", "Pat Smith, John Doe", false)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q", false)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", true)]
        [TestCase("Robert Louis Stevenson", "Robert Andrew Stevenson", false)]
        [TestCase("John Smith", "Pat Smith; John Doe", false)]
        [TestCase("John Smith", "Pat Smith & John Doe", false)]
        [TestCase("John Smith", "Pat Smith and John Doe", false)]
        [TestCase("John Smith", "Pat Smith John Doe", false)]
        [TestCase("John Smith", "Smith, John; Pat Doe", true)]
        [TestCase("John Smith", "John Smith; Pat Doe", true)]
        // One trailing surname s is compatible in either direction.
        [TestCase("John Smith", "John Smiths", true)]
        [TestCase("John Smith", "JAMES SMITH", false)]
        [TestCase("JOHN SMITH", "James Smith", false)]
        [TestCase("Le Guin, Ursula K.", "Ursula K. Guin", false)]
        [TestCase("Le Guin, Ursula K.", "Ursula Guin", false)]
        [TestCase("Frank Herbert", "Frank Herbert's Dune Messiah", true)]
        [TestCase("Frank Herbert", "Frank Herbert’s Dune Messiah", true)]
        [TestCase("Лев Толстой", "Толстой, Лев", true)]
        [TestCase("Лев Толстой", "Алексей Толстой, Лев Иванов", false)]
        [TestCase("Gabriel Garcia Marquez", "Gabriel Jose de la Concordia Garcia Marquez", true)]
        [TestCase("Gabriel Garcia Marquez", "Marquez, Gabriel Jose de la Concordia Garcia", true)]
        [TestCase("Gabriel Garcia Marquez", "Gabriel Jose de la Concordia Francisco Marquez", false)]
        [TestCase("Лев Толстой", "Лев Николаевич Иванович Петрович Толстой", true)]
        [TestCase("Лев Николаевич Толстой", "Толстой, Лев Александрович", false)]
        [TestCase("Ann Smith", "Ann; Pat Smith", false)]
        [TestCase("Ann Smith", "Ann & Pat Smith", true)]
        [TestCase("Ann Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann Smith", "Ann und Pat Smith", true)]
        [TestCase("Pat Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann and Pat Smith", "Ann und Pat Smith", false)]
        public void author_evidence_should_preserve_order_and_distinguish_omission_from_conflict(string authorName, string tagValue, bool expected)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["ARTIST"] = new List<string> { tagValue }
            };

            Assert.That(CreateValidator().ValidateAuthorInTags(authorName, tags), Is.EqualTo(expected));
        }

        [TestCase("Pat Smith, John Doe", false)]
        [TestCase("Smith, John; Pat Doe", true)]
        [TestCase("John Smith", true)]
        public void import_identity_proof_should_require_one_coherent_author_credit(string credit, bool expected)
        {
            var proof = MatchIdentityProofFactory.FromExpectedIdentity(
                "John Smith",
                new[] { "A Test Book" },
                new Dictionary<string, List<string>>
                {
                    ["ARTIST"] = new List<string> { credit },
                    ["TITLE"] = new List<string> { "A Test Book" }
                },
                CreateValidator());

            Assert.Multiple(() =>
            {
                Assert.That(proof.Has(MatchIdentityRole.Title), Is.True);
                Assert.That(proof.Has(MatchIdentityRole.Author), Is.EqualTo(expected));
                Assert.That(MatchIdentityProofMembership.HasRequiredIdentity(proof), Is.EqualTo(expected));
            });
        }

        [TestCase("Pat Smith, John Doe", "Pat Smith, John Doe", true)]
        [TestCase("Pat Smith, John Doe", "Pat Smith", false)]
        [TestCase("Pat Smith, John Doe", "Pat Doe", false)]
        [TestCase("Pat Smith, John Doe", "John Doe & Pat Smith", true)]
        [TestCase("Pat Smith, John Doe, Unabridged", "Pat Smith, John Doe", false)]
        [TestCase("Pat Smith, John Doe, Full Cast", "Pat Smith, John Doe", false)]
        [TestCase("Pat Smith, John Doe Full Cast", "Pat Smith, Full Cast", false)]
        [TestCase("Pat Smith, John Doe Full Cast", "Pat Smith, John Doe Full Cast", true)]
        [TestCase("Pat Smith, John Doe, Full Cast", "Full Cast; John Doe; Pat Smith", true)]
        [TestCase("C S Lewis", "CS Lewis", true)]
        [TestCase("Dale, Jim", "Jim Dale", true)]
        [TestCase("Dale, Jim", "Peter Dale, Jim Smith", false)]
        [TestCase("Лев Толстой; Иван Петров", "Иван Петров & Лев Толстой", true)]
        public void narrator_title_annotation_should_require_the_complete_credit(string narrators, string credit, bool expected)
        {
            Assert.That(ReleaseNarratorEvidenceExtractor.ContainsCompleteCredit(narrators, credit), Is.EqualTo(expected));
        }

        [TestCase("Frank and Brian Herbert", "Frank Herbert", false)]
        [TestCase("Frank and Brian Herbert", "Brian Herbert", false)]
        [TestCase("Frank and Brian Herbert", "Frank and Brian Herbert", true)]
        [TestCase("Ann and Pat Smith", "Ann Smith", false)]
        [TestCase("Ann and Pat Smith", "Pat Smith", false)]
        [TestCase("Ann and Pat Smith", "Ann and Pat Smith", true)]
        [TestCase("John Smith; Jane Doe", "John Smith", false)]
        [TestCase("John Smith; Jane Doe", "Jane Doe; John Smith", true)]
        [TestCase("Dale, Jim", "Jim Dale", true)]
        public void import_narrator_evidence_should_require_the_whole_credit(string required, string observed, bool expected)
        {
            var evidence = ExactMatchEvidenceBuilder.FindNarratorEvidenceFields(required, "Different Author",
                new Dictionary<string, List<string>> { ["NARRATOR"] = new List<string> { observed } });
            Assert.That(evidence.Count > 0, Is.EqualTo(expected));
        }

        [Test]
        public void one_known_member_of_a_larger_cast_should_classify_a_bracket_without_proving_the_cast()
        {
            var known = new[] { "Pat Smith", "John Doe", "Cara Reader" };
            var tags = new Dictionary<string, List<string>> { ["TITLE"] = new List<string> { "A Test Book" } };
            Assert.That(CreateValidator().GetEditionTitleEvidence("A Test Book (weitere Wörter Pat Smith)", tags, knownNarrators: known), Is.Not.Empty);
            Assert.That(ExactMatchEvidenceBuilder.FindNarratorEvidenceFields("Pat Smith; John Doe; Cara Reader", "Different Author",
                new Dictionary<string, List<string>> { ["NARRATOR"] = new List<string> { "Pat Smith" } }), Is.Empty);
        }

        [Test]
        public void pieces_of_two_unrelated_names_should_not_classify_a_known_narrator_note()
        {
            var tags = new Dictionary<string, List<string>> { ["TITLE"] = new List<string> { "A Test Book" } };
            Assert.That(CreateValidator().GetEditionTitleEvidence("A Test Book (Peter Dale, Jim Smith)", tags, knownNarrators: new[] { "Jim Dale" }), Is.Empty);
        }

        [TestCase("Narrated by Pat Smith")]
        [TestCase("Read by Pat Smith")]
        [TestCase("Lu par Pat Smith")]
        [TestCase("Gelesen von Pat Smith")]
        [TestCase("Pat Smith, arbitrary extra words")]
        public void known_narrator_annotation_must_ignore_credit_grammar(string annotation)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new List<string> { "A Test Book" },
                ["ALBUMARTIST"] = new List<string> { "John Doe, Pat Smith, Cara Reader" }
            };
            var validator = CreateValidator();
            var known = new[] { "John Doe", "Pat Smith", "Cara Reader" };
            foreach (var brackets in new[] { ("(", ")"), ("[", "]"), ("（", "）"), ("［", "］"), ("【", "】") })
            {
                var title = "A Test Book " + brackets.Item1 + annotation + brackets.Item2;
                Assert.Multiple(() =>
                {
                    Assert.That(validator.ValidateEditionInTags(title, tags, known), Is.True);
                    Assert.That(validator.GetEditionTitleEvidence(title, tags, knownNarrators: known), Is.Not.Empty);
                    Assert.That(validator.ValidateEditionInTags(title, tags), Is.False);
                });
            }
        }

        [TestCase("John Smith")]
        [TestCase("a different edition")]
        public void unrelated_tag_cannot_make_unknown_bracket_content_optional(string qualifier)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new List<string> { "A Test Book" },
                ["ALBUMARTIST"] = new List<string> { "Pat Smith" }
            };
            Assert.That(CreateValidator().ValidateEditionInTags("A Test Book [" + qualifier + "]", tags,
                new[] { "Pat Smith" }), Is.False);
        }

        [Test]
        public void comma_inverted_author_in_excluded_field_should_not_validate()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["COMMENT"] = new List<string> { "Asimov, Isaac" }
            };

            Assert.That(CreateValidator().ValidateAuthorInTags("Isaac Asimov", tags), Is.False);
        }

        [TestCase("Ready Player Two", "Ready Player 𝟚", false)]
        [TestCase("Ready Player 2", "Ready Player 𝟚", true)]
        [TestCase("Ready Player Two", "Ready Player ٢", false)]
        [TestCase("Ready Player 2", "Ready Player ٢", true)]
        [TestCase("Ready Player Two", "Ready Player ②", false)]
        [TestCase("Ready Player 2", "Ready Player ②", true)]
        [TestCase("Ready Player Two", "Ready Player Ⅱ", false)]
        [TestCase("Ready Player 2", "Ready Player Ⅱ", false)]
        [TestCase("Ready Player II", "Ready Player Ⅱ", true)]
        [TestCase("Ready Player One", "Ready Player ②", false)]
        [TestCase("The 13½ Lives of Captain Bluebear", "The 13.5 Lives of Captain Bluebear", true)]
        [TestCase("The 13½ Lives of Captain Bluebear", "The 13 5 Lives of Captain Bluebear", false)]
        [TestCase("The 13½ Lives of Captain Bluebear", "The 135 Lives of Captain Bluebear", false)]
        [TestCase("The 7½ Deaths of Evelyn Hardcastle", "The 7 ½ Deaths of Evelyn Hardcastle", true)]
        [TestCase("23 ½ Lies", "23 1/2 Lies", true)]
        [TestCase("23 ½ Lies", "23 ¼ Lies", false)]
        [TestCase("1Q84", "1Q84", true)]
        [TestCase("I, Robot", "1 Robot", false)]
        [TestCase("三体𠀀", "三体𠀁", false)]
        [TestCase("三体𠀀", "三体𠀀", true)]
        public void final_title_proof_should_preserve_numeric_and_supplementary_identity(string editionTitle, string titleTag, bool expected)
        {
            var tags = new Dictionary<string, List<string>> { ["TITLE"] = new List<string> { titleTag } };
            Assert.That(CreateValidator().ValidateEditionInTags(editionTitle, tags), Is.EqualTo(expected));
            Assert.That(CreateValidator().GetEditionTitleEvidence(editionTitle, tags).Count > 0, Is.EqualTo(expected));
        }

        [Test]
        public void malformed_tag_text_should_not_throw_or_supply_missing_author_identity()
        {
            var tags = new Dictionary<string, List<string>> { ["ARTIST"] = new List<string> { "Isaac\uD800Asimov" } };
            Assert.That(CreateValidator().ValidateAuthorInTags("Isaac Asimov", tags), Is.True);
            Assert.That(CreateValidator().ValidateAuthorInTags("𠀀", tags), Is.False);
        }

    }
}
