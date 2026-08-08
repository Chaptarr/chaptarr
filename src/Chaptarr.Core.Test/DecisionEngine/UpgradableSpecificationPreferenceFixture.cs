using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class UpgradableSpecificationPreferenceFixture
    {
        private CustomFormat _selectedNarrator;
        private QualityProfile _profile;
        private UpgradableSpecification _subject;

        [SetUp]
        public void SetUp()
        {
            _selectedNarrator = new CustomFormat
            {
                Id = 7,
                Name = "Selected Narrator"
            };
            _profile = new QualityProfile
            {
                Id = 2,
                Name = "Spoken",
                ProfileType = ProfileType.Audiobook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Quality = Quality.MP3, Allowed = true },
                    new() { Quality = Quality.M4B, Allowed = true }
                },
                FormatItems = new List<ProfileFormatItem>
                {
                    new()
                    {
                        Format = _selectedNarrator,
                        Score = 50
                    }
                }
            };
            _subject = new UpgradableSpecification(
                ConfigServiceTestProxy.Create(),
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void preferences_first_should_not_replace_selected_narrator_with_better_source_format()
        {
            _profile.PreferCustomFormatsOverQuality = true;

            var upgradable = _subject.IsUpgradable(
                _profile,
                new QualityModel(Quality.MP3),
                new List<CustomFormat> { _selectedNarrator },
                new QualityModel(Quality.M4B),
                new List<CustomFormat>());

            Assert.That(upgradable, Is.False);
        }

        [Test]
        public void preferences_first_should_upgrade_to_selected_narrator_despite_lower_source_format()
        {
            _profile.PreferCustomFormatsOverQuality = true;

            var upgradable = _subject.IsUpgradable(
                _profile,
                new QualityModel(Quality.M4B),
                new List<CustomFormat>(),
                new QualityModel(Quality.MP3),
                new List<CustomFormat> { _selectedNarrator });

            Assert.That(upgradable, Is.True);
        }

        [Test]
        public void format_first_should_keep_traditional_upgrade_order_without_conversion()
        {
            _profile.PreferCustomFormatsOverQuality = false;

            var upgradable = _subject.IsUpgradable(
                _profile,
                new QualityModel(Quality.MP3),
                new List<CustomFormat> { _selectedNarrator },
                new QualityModel(Quality.M4B),
                new List<CustomFormat>());

            Assert.That(upgradable, Is.True);
        }

        [Test]
        public void converted_candidate_should_compare_against_retained_file_and_not_redownload()
        {
            _profile.PreferCustomFormatsOverQuality = false;
            _profile.ConvertToQualityId = Quality.M4B.Id;

            var replacePreferredNarrator = _subject.IsUpgradable(
                _profile,
                new QualityModel(Quality.M4B),
                new List<CustomFormat> { _selectedNarrator },
                new QualityModel(Quality.MP3),
                new List<CustomFormat>());
            var replaceConvertedWithNative = _subject.IsUpgradable(
                _profile,
                new QualityModel(Quality.M4B),
                new List<CustomFormat>(),
                new QualityModel(Quality.M4B),
                new List<CustomFormat>());

            Assert.Multiple(() =>
            {
                Assert.That(replacePreferredNarrator, Is.False);
                Assert.That(replaceConvertedWithNative, Is.False);
            });
        }

        [Test]
        public void conversion_should_upgrade_a_legacy_stored_mp3_to_the_planned_m4b()
        {
            _profile.ConvertToQualityId = Quality.M4B.Id;

            var upgradable = _subject.IsUpgradable(
                _profile,
                new QualityModel(Quality.MP3),
                new List<CustomFormat>(),
                new QualityModel(Quality.MP3),
                new List<CustomFormat>());

            Assert.That(upgradable, Is.True);
        }

        [Test]
        public void pending_release_comparison_should_prefer_native_source_when_final_format_and_score_tie()
        {
            _profile.ConvertToQualityId = Quality.M4B.Id;

            var upgradable = _subject.IsReleaseUpgradable(
                _profile,
                new QualityModel(Quality.MP3),
                new List<CustomFormat>(),
                new QualityModel(Quality.M4B),
                new List<CustomFormat>());

            Assert.That(upgradable, Is.True);
        }
    }
}
