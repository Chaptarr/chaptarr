using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport.Services;

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

        [Test]
        public void comma_inverted_author_in_excluded_field_should_not_validate()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["COMMENT"] = new List<string> { "Asimov, Isaac" }
            };

            Assert.That(CreateValidator().ValidateAuthorInTags("Isaac Asimov", tags), Is.False);
        }

        [TestCase("Ready Player Two", "Ready Player 𝟚", true)]
        [TestCase("Ready Player Two", "Ready Player ٢", true)]
        [TestCase("Ready Player Two", "Ready Player ②", true)]
        [TestCase("Ready Player Two", "Ready Player Ⅱ", true)]
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
