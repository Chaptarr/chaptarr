using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class DownloadDecisionComparerMediaTypeFixture
    {
        [Test]
        public void compare_should_be_antisymmetric_across_audiobook_and_ebook()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var configService = ConfigServiceTestProxy.Create();

            var audiobookProfile = new QualityProfile
            {
                Id = 2,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                }
            };

            var ebookProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebook",
                ProfileType = ProfileType.Ebook,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.PDF, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true }
                }
            };

            var author = new Author
            {
                Id = 123,
                Name = "J.K. Rowling",
                AudiobookQualityProfileId = audiobookProfile.Id,
                EbookQualityProfileId = ebookProfile.Id,
                AudiobookQualityProfile = audiobookProfile,
                EbookQualityProfile = ebookProfile
            };

            DownloadDecision CreateDecision(string title, Quality quality)
            {
                return new DownloadDecision(new RemoteBook
                {
                    Author = author,
                    Release = new ReleaseInfo { Title = title, Size = 0 },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel { Quality = quality } }
                });
            }

            var audiobookDecision = CreateDecision("Audio", Quality.M4B);
            var ebookDecision = CreateDecision("Ebook", Quality.EPUB);

            var comparer = new DownloadDecisionComparer(configService, null, null, logger);

            var audioVsEbook = comparer.Compare(audiobookDecision, ebookDecision);
            var ebookVsAudio = comparer.Compare(ebookDecision, audiobookDecision);

            Assert.That(audioVsEbook, Is.Not.EqualTo(0));
            Assert.That(ebookVsAudio, Is.EqualTo(-audioVsEbook));

            Assert.DoesNotThrow(() =>
                new[] { audiobookDecision, ebookDecision }.OrderByDescending(d => d, comparer).ToList());
        }

        [Test]
        public void should_prefer_native_audiobook_category_without_rejecting_other_audio_categories()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var comparer = new DownloadDecisionComparer(ConfigServiceTestProxy.Create(), null, null, logger);
            var author = new Author { Name = "Mitch Albom" };
            var book = new Book { Author = author, Title = "The Five People You Meet in Heaven", MediaType = BookMediaType.Audiobook };

            DownloadDecision CreateDecision(string title, List<int> categories)
            {
                return new DownloadDecision(new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { book },
                    Release = new ReleaseInfo { Title = title, Size = 0, Categories = categories },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel { Quality = Quality.MP3 } }
                });
            }

            var native = CreateDecision("Native", new List<int> { 3030, 103030 });
            var broadAudio = CreateDecision("Broad", new List<int> { 3010, 103010 });

            Assert.That(comparer.Compare(native, broadAudio), Is.GreaterThan(0));
            Assert.That(native.Rejections, Is.Empty);
            Assert.That(broadAudio.Rejections, Is.Empty);
        }

        [Test]
        public void grouped_qualities_should_tie_so_custom_format_score_can_decide()
        {
            var profile = new QualityProfile
            {
                Id = 2,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem
                    {
                        Id = 1001,
                        Name = "Equivalent audio",
                        Allowed = true,
                        Items = new List<QualityProfileQualityItem>
                        {
                            new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                            new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                        }
                    }
                }
            };

            var author = new Author
            {
                Name = "George Orwell",
                AudiobookQualityProfileId = profile.Id,
                AudiobookQualityProfile = profile
            };

            DownloadDecision CreateDecision(Quality quality, int customFormatScore)
            {
                return new DownloadDecision(new RemoteBook
                {
                    Author = author,
                    Release = new ReleaseInfo { Title = "1984", Size = 0 },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel { Quality = quality } },
                    CustomFormatScore = customFormatScore
                });
            }

            var preferredNarratorMp3 = CreateDecision(Quality.MP3, 50);
            var unscoredM4b = CreateDecision(Quality.M4B, 0);
            var comparer = new DownloadDecisionComparer(ConfigServiceTestProxy.Create(), null, null, LogManager.GetCurrentClassLogger());

            Assert.That(comparer.Compare(preferredNarratorMp3, unscoredM4b), Is.GreaterThan(0));
            Assert.That(comparer.Compare(unscoredM4b, preferredNarratorMp3), Is.LessThan(0));
        }

        [Test]
        public void audiobook_priority_should_compose_preferences_conversion_and_source_format()
        {
            var profile = new QualityProfile
            {
                Id = 2,
                Name = "Spoken",
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Quality = Quality.MP3, Allowed = true },
                    new() { Quality = Quality.M4B, Allowed = true }
                }
            };
            var author = new Author
            {
                Name = "Jim Butcher",
                AudiobookQualityProfileId = profile.Id,
                AudiobookQualityProfile = profile
            };

            DownloadDecision CreateDecision(Quality quality, int customFormatScore)
            {
                return new DownloadDecision(new RemoteBook
                {
                    Author = author,
                    Release = new ReleaseInfo { Title = "Storm Front", Size = 0 },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(quality)
                    },
                    CustomFormatScore = customFormatScore
                });
            }

            var preferredNarratorMp3 = CreateDecision(Quality.MP3, 50);
            var plainM4b = CreateDecision(Quality.M4B, 0);
            var plainMp3 = CreateDecision(Quality.MP3, 0);
            var comparer = new DownloadDecisionComparer(
                ConfigServiceTestProxy.Create(),
                null,
                null,
                LogManager.GetCurrentClassLogger());

            profile.PreferCustomFormatsOverQuality = false;
            profile.ConvertToQualityId = null;
            Assert.That(comparer.Compare(plainM4b, preferredNarratorMp3), Is.GreaterThan(0),
                "Traditional ordering should prefer M4B before the narrator score");

            profile.PreferCustomFormatsOverQuality = true;
            Assert.That(comparer.Compare(preferredNarratorMp3, plainM4b), Is.GreaterThan(0),
                "Preferences-first should let the selected narrator outrank the source container");

            profile.PreferCustomFormatsOverQuality = false;
            profile.ConvertToQualityId = Quality.M4B.Id;
            Assert.That(comparer.Compare(preferredNarratorMp3, plainM4b), Is.GreaterThan(0),
                "When both releases will be kept as M4B, the narrator score should decide");
            Assert.That(comparer.Compare(plainM4b, plainMp3), Is.GreaterThan(0),
                "When final format and score tie, native M4B should avoid unnecessary conversion");
        }

        [Test]
        public void ebook_profiles_should_keep_traditional_quality_first_order()
        {
            var profile = new QualityProfile
            {
                Id = 3,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                PreferCustomFormatsOverQuality = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Quality = Quality.PDF, Allowed = true },
                    new() { Quality = Quality.EPUB, Allowed = true }
                }
            };
            var author = new Author
            {
                Name = "Octavia Butler",
                EbookQualityProfileId = profile.Id,
                EbookQualityProfile = profile
            };

            DownloadDecision CreateDecision(Quality quality, int customFormatScore)
            {
                return new DownloadDecision(new RemoteBook
                {
                    Author = author,
                    Release = new ReleaseInfo { Title = "Kindred", Size = 0 },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(quality) },
                    CustomFormatScore = customFormatScore
                });
            }

            var highScorePdf = CreateDecision(Quality.PDF, 100);
            var plainEpub = CreateDecision(Quality.EPUB, 0);
            var comparer = new DownloadDecisionComparer(ConfigServiceTestProxy.Create(), null, null, LogManager.GetCurrentClassLogger());

            Assert.That(comparer.Compare(plainEpub, highScorePdf), Is.GreaterThan(0));
        }
    }
}
