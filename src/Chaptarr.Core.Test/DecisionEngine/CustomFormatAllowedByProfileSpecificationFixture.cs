using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
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

        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        public void unknown_quality_should_enforce_the_resolved_target_profile(BookMediaType mediaType, bool accepted)
        {
            var format = new CustomFormat { Id = 10, Name = "Target preference" };
            var author = CreateAuthor(CreateAudiobookProfile(format, 15, 20), CreateEbookProfile(format, 50, 0));
            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new List<Book> { new Book { MediaType = mediaType } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) },
                CustomFormats = new List<CustomFormat> { format }
            };
            remoteBook.CustomFormatScore = remoteBook.GetQualityProfile().CalculateCustomFormatScore(remoteBook.CustomFormats);

            var decision = new CustomFormatAllowedbyProfileSpecification().IsSatisfiedBy(remoteBook, null);

            Assert.That(remoteBook.CustomFormatScore, Is.EqualTo(mediaType == BookMediaType.Audiobook ? 15 : 50));
            Assert.That(decision.Accepted, Is.EqualTo(accepted));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unknown_quality_without_one_target_media_side_should_not_default_to_ebook(bool mixed)
        {
            var format = new CustomFormat { Id = 10, Name = "Target preference" };
            var remoteBook = new RemoteBook
            {
                Author = CreateAuthor(CreateAudiobookProfile(format, 15, 20), CreateEbookProfile(format, 50, 0)),
                Books = mixed ? new List<Book>
                {
                    new Book { MediaType = BookMediaType.Audiobook },
                    new Book { MediaType = BookMediaType.Ebook }
                } : new List<Book>(),
                Release = new ReleaseInfo { Title = "The Ebook Handbook" },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) }
            };

            Assert.That(remoteBook.GetQualityProfile(), Is.Null);
            Assert.That(new CustomFormatAllowedbyProfileSpecification().IsSatisfiedBy(remoteBook, null).Accepted, Is.False);
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void opposite_quality_should_not_change_a_resolved_target_profile(BookMediaType mediaType)
        {
            var format = new CustomFormat { Id = 10, Name = "Target preference" };
            var author = CreateAuthor(CreateAudiobookProfile(format, 15, 20), CreateEbookProfile(format, 50, 0));
            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new List<Book> { new Book { MediaType = mediaType } },
                ParsedBookInfo = new ParsedBookInfo
                {
                    Quality = new QualityModel(mediaType == BookMediaType.Audiobook ? Quality.EPUB : Quality.M4B)
                }
            };

            Assert.That(remoteBook.GetQualityProfile(), Is.SameAs(author.GetQualityProfileForMediaType(mediaType)));
        }

        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        public void unknown_quality_should_enforce_the_target_quality_allowance(BookMediaType mediaType, bool accepted)
        {
            var format = new CustomFormat { Id = 10, Name = "Target preference" };
            var audio = CreateProfile(ProfileType.Audiobook, Quality.Unknown, format, 15, 0);
            audio.Items[0].Allowed = false;
            var ebook = CreateProfile(ProfileType.Ebook, Quality.Unknown, format, 50, 0);
            var remoteBook = new RemoteBook
            {
                Author = CreateAuthor(audio, ebook),
                Books = new List<Book> { new Book { MediaType = mediaType } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) }
            };

            var decision = new QualityAllowedByProfileSpecification(NLog.LogManager.GetCurrentClassLogger()).IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.EqualTo(accepted));
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

        [TestCase(false)]
        [TestCase(true)]
        public void unresolved_media_should_not_report_a_missing_configured_profile(bool mixed)
        {
            var format = new CustomFormat { Id = 10 };
            var remote = new RemoteBook
            {
                Author = CreateAuthor(CreateAudiobookProfile(format, 0, 0), CreateEbookProfile(format, 0, 0)),
                Books = mixed ? new List<Book> { new Book { MediaType = BookMediaType.Audiobook }, new Book { MediaType = BookMediaType.Ebook } } : new List<Book>(),
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) },
                Release = new ReleaseInfo { Title = "Unknown release" }
            };
            var decision = new QualityAllowedByProfileSpecification(NLog.LogManager.GetCurrentClassLogger()).IsSatisfiedBy(remote, null);
            Assert.That(decision.Reason, Is.EqualTo("Release media type could not be determined"));
            Assert.That(decision.Category, Is.EqualTo("Quality"));
            Assert.That(decision.CanBypass, Is.True);
        }

        [Test]
        public void resolved_media_without_profile_should_keep_its_existing_soft_rejection()
        {
            var remote = new RemoteBook
            {
                Author = new Author(), Books = new List<Book> { new Book { MediaType = BookMediaType.Audiobook } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3) }
            };
            var decision = new QualityAllowedByProfileSpecification(NLog.LogManager.GetCurrentClassLogger()).IsSatisfiedBy(remote, null);
            Assert.That(decision.Reason, Does.StartWith("No quality profile configured"));
            Assert.That(decision.CanBypass, Is.True);
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
