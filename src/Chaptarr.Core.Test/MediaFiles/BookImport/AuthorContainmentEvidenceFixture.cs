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
    }
}
