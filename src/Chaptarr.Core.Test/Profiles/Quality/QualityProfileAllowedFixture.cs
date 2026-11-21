using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using CoreQuality = NzbDrone.Core.Qualities.Quality;

namespace Chaptarr.Core.Test.Profiles.Quality
{
    [TestFixture]
    public class QualityProfileAllowedFixture
    {
        [TestCase(true, false, true)]
        [TestCase(false, true, false)]
        public void group_permission_controls_member_and_planned_conversion(bool groupAllowed, bool childAllowed, bool expected)
        {
            var profile = new QualityProfile
            {
                ProfileType = ProfileType.Audiobook,
                ConvertToQualityId = CoreQuality.M4B.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new()
                    {
                        Allowed = groupAllowed,
                        Items = new List<QualityProfileQualityItem>
                        {
                            new() { Quality = CoreQuality.M4B, Allowed = childAllowed }
                        }
                    }
                }
            };

            Assert.That(profile.IsQualityAllowed(CoreQuality.M4B.Id), Is.EqualTo(expected));
            Assert.That(QualityConversionHelper.GetPlannedConversionTarget(profile, new QualityModel(CoreQuality.MP3)),
                Is.EqualTo(expected ? CoreQuality.M4B : null));
            Assert.That(profile.IsQualityAllowed(CoreQuality.MP3.Id), Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void standalone_quality_permission_is_respected(bool allowed)
        {
            var profile = new QualityProfile
            {
                Items = new List<QualityProfileQualityItem> { new() { Quality = CoreQuality.EPUB, Allowed = allowed } }
            };

            Assert.That(profile.IsQualityAllowed(CoreQuality.EPUB.Id), Is.EqualTo(allowed));
            Assert.That(profile.IsQualityAllowed(CoreQuality.Unknown.Id), Is.False);
            Assert.That(profile.IsQualityAllowed(int.MaxValue), Is.False);
        }

        [Test]
        public void missing_items_do_not_allow_quality()
        {
            Assert.That(new QualityProfile().IsQualityAllowed(CoreQuality.M4B.Id), Is.False);
        }
    }
}
