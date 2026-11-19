using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class SplitEditionTitleEvidenceFixture
    {
        [TestCase("TITLE", "mP4:----", false, true)]
        [TestCase("other", "custom", true, true)]
        [TestCase("TITLE", "description", false, false)]
        public void colon_title_requires_both_parts_in_eligible_fields(string headField, string tailField, bool reverse, bool expected)
        {
            var tags = new Dictionary<string, List<string>>
            {
                [headField] = new() { "The Fall" },
                [tailField] = new() { "The Bound and The Broken Novella" }
            };
            if (reverse)
            {
                tags = tags.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            var validator = new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger());
            const string title = "The Fall: The Bound and The Broken Novella";
            var evidence = validator.GetEditionTitleEvidence(title, tags);
            Assert.That(evidence.Count > 0, Is.EqualTo(expected));
            Assert.That(validator.ValidateEditionInTags(title, tags), Is.EqualTo(expected));
            if (expected)
            {
                Assert.That(evidence.Select(item => item.MatchedTitle), Is.EquivalentTo(new[] { "The Fall", "The Bound and The Broken Novella" }));
                Assert.That(evidence.All(item => tags[item.FieldName].Contains(item.FieldValue)), Is.True);
            }
        }

        [TestCase("刺客正傳1：刺客學徒", "刺客正傳1", "刺客學徒", true)]
        [TestCase("刺客正傳1：刺客學徒", "刺客正傳1", null, false)]
        public void full_width_colon_title_splits_like_an_ascii_colon(string title, string head, string tail, bool expected)
        {
            var tags = new Dictionary<string, List<string>> { ["TITLE"] = new() { head } };
            if (tail != null) tags["custom"] = new() { tail };
            var validator = new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger());
            Assert.That(validator.GetEditionTitleEvidence(title, tags).Count > 0, Is.EqualTo(expected));
            Assert.That(validator.ValidateEditionInTags(title, tags), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("The Bound and The Broken")]
        public void absent_or_incomplete_tail_does_not_prove_a_colon_title(string tail)
        {
            var tags = new Dictionary<string, List<string>> { ["TITLE"] = new() { "The Fall" } };
            if (tail != null) tags["custom"] = new() { tail };
            var validator = new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger());
            Assert.That(validator.GetEditionTitleEvidence("The Fall: The Bound and The Broken Novella", tags), Is.Empty);
            Assert.That(validator.ValidateEditionInTags("The Fall: The Bound and The Broken Novella", tags), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void canonical_alias_is_not_another_occurrence(bool hasAlias)
        {
            var tags = new Dictionary<string, List<string>> { ["MP4:©nam"] = new() { "Echo" } };
            if (hasAlias) tags["TITLE"] = new() { "Echo" };
            var validator = new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger());
            Assert.That(validator.GetEditionTitleEvidence("Echo: Echo", tags), Is.Empty);
        }
    }
}
