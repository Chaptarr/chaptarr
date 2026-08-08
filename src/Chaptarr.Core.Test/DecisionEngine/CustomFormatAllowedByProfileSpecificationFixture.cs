using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class CustomFormatAllowedByProfileSpecificationFixture
    {
        [Test]
        public void should_reject_release_below_minimum_custom_format_score()
        {
            var format = new CustomFormat { Id = 1, Name = BuiltInCustomFormats.DramatizedAudioName };
            var subject = new CustomFormatAllowedbyProfileSpecification();
            var remoteBook = new RemoteBook
            {
                Author = CreateAuthor(CreateAudiobookProfile(format, -100, 0)),
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) },
                CustomFormats = new List<CustomFormat> { format },
                CustomFormatScore = -100
            };

            var decision = subject.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("below Author profile minimum 0"));
        }

        [Test]
        public void should_not_apply_audiobook_reject_scores_to_ebooks()
        {
            var format = new CustomFormat { Id = 1, Name = BuiltInCustomFormats.DramatizedAudioName };
            var subject = new CustomFormatAllowedbyProfileSpecification();
            var remoteBook = new RemoteBook
            {
                Author = CreateAuthor(CreateAudiobookProfile(format, -100, 0), CreateEbookProfile(format, 0, 0)),
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB) },
                CustomFormats = new List<CustomFormat> { format },
                CustomFormatScore = 0
            };

            var decision = subject.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.True);
        }

        private static Author CreateAuthor(QualityProfile audiobookProfile, QualityProfile ebookProfile = null)
        {
            return new Author
            {
                AudiobookQualityProfileId = audiobookProfile?.Id,
                AudiobookQualityProfile = audiobookProfile == null ? new LazyLoaded<QualityProfile>() : new LazyLoaded<QualityProfile>(audiobookProfile),
                EbookQualityProfileId = ebookProfile?.Id,
                EbookQualityProfile = ebookProfile == null ? new LazyLoaded<QualityProfile>() : new LazyLoaded<QualityProfile>(ebookProfile)
            };
        }

        private static QualityProfile CreateAudiobookProfile(CustomFormat format, int score, int minFormatScore)
        {
            return CreateProfile(ProfileType.Audiobook, Quality.M4B, format, score, minFormatScore);
        }

        private static QualityProfile CreateEbookProfile(CustomFormat format, int score, int minFormatScore)
        {
            return CreateProfile(ProfileType.Ebook, Quality.EPUB, format, score, minFormatScore);
        }

        private static QualityProfile CreateProfile(ProfileType profileType, Quality quality, CustomFormat format, int score, int minFormatScore)
        {
            return new QualityProfile
            {
                Id = (int)profileType,
                Name = $"{profileType} Profile",
                ProfileType = profileType,
                MinFormatScore = minFormatScore,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = quality, Allowed = true }
                },
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem
                    {
                        Format = format,
                        Score = score
                    }
                }
            };
        }
    }
}
