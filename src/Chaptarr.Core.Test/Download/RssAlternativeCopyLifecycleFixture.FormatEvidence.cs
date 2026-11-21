using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    public partial class RssAlternativeCopyLifecycleFixture
    {
        [TestCase(false, true, false, true, 7000)]
        [TestCase(true, true, false, true, 7000)]
        [TestCase(false, true, true, true, 7000)]
        [TestCase(true, true, true, true, 7000)]
        [TestCase(false, false, true, true, 7000)]
        [TestCase(true, false, true, true, 7000)]
        [TestCase(false, true, false, false, 7000)]
        [TestCase(true, true, false, false, 7000)]
        [TestCase(false, true, true, false, 7000)]
        [TestCase(true, true, true, false, 7000)]
        [TestCase(false, false, true, false, 7000)]
        [TestCase(true, false, true, false, 7000)]
        [TestCase(false, true, true, true, 7020)]
        [TestCase(true, true, true, true, 7020)]
        [TestCase(false, true, true, true, 3030)]
        [TestCase(true, true, true, true, 3030)]
        public void generic_category_hint_fallback_uses_current_rss_monitoring_and_profile_rules(
            bool postgres, bool monitorAudio, bool monitorEbook, bool audioHint, int category)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var scene = new Scene(database, BookMediaType.Audiobook);
                var audio = scene.Books[0];
                var ebook = scene.Books[1];
                audio.Monitored = audio.AudiobookMonitored = monitorAudio;
                ebook.MediaType = BookMediaType.Ebook;
                ebook.Monitored = ebook.EbookMonitored = monitorEbook;
                ebook.AudiobookMonitored = false;
                scene.BookWriter.UpdateMany(scene.Books);

                var edition = ebook.Editions.Single();
                edition.ReadingFormatId = 3;
                edition.NarratorNames = new List<string>();
                new BasicRepository<Edition>(database, scene.Events).Update(edition);

                var profiles = new BasicRepository<QualityProfile>(database, scene.Events).All();
                var audioProfile = profiles.Single(profile => profile.ProfileType == ProfileType.Audiobook);
                var ebookProfile = profiles.Single(profile => profile.ProfileType == ProfileType.Ebook);
                scene.Author.AudiobookQualityProfileId = audioProfile.Id;
                scene.Author.AudiobookQualityProfile = audioProfile;
                scene.Author.EbookQualityProfileId = ebookProfile.Id;
                scene.Author.EbookQualityProfile = ebookProfile;
                new BasicRepository<Author>(database, scene.Events).Update(scene.Author);

                var release = scene.Release(BookMediaType.Audiobook);
                release.Title = audioHint ? "Test Writer - Paper Lanterns [Audiobook]" : "Test Writer - Paper Lanterns";
                release.Author = null;
                release.Book = null;
                release.Categories = new List<int> { category };
                release.DownloadProtocol = DownloadProtocol.Usenet;
                var quality = QualityParser.ParseQuality(release.Title, categories: release.Categories);
                var expectedMedia = category == 7020 ? BookMediaType.Ebook
                    : category == 3030 || audioHint ? BookMediaType.Audiobook : (BookMediaType?)null;
                var expectedQuality = expectedMedia == BookMediaType.Audiobook ? Quality.UnknownAudio : Quality.Unknown;
                Assert.That(quality.Quality, Is.EqualTo(expectedQuality));
                Assert.That(QualityMediaTypeHelper.DetectMediaType(quality.Quality, release), Is.EqualTo(expectedMedia));
                Assert.That(audioProfile.Items.SelectMany(item => item.GetQualities()), Does.Not.Contain(Quality.Unknown));
                Assert.That(audioProfile.Items[0].Quality, Is.EqualTo(Quality.UnknownAudio));
                Assert.That(audioProfile.Items[0].Allowed, Is.True);

                var decisions = scene.Maker(new QualityAllowedByProfileSpecification(Logger),
                    new MonitoredMediaTypeSpecification(Logger)).GetRssDecision(new List<ReleaseInfo> { release });
                if (expectedMedia == BookMediaType.Audiobook && !monitorAudio)
                {
                    Assert.That(decisions, Is.Empty, "An audio hint must not route an unmonitored audiobook to the ebook side.");
                    return;
                }
                if (!expectedMedia.HasValue && monitorAudio && monitorEbook)
                {
                    Assert.That(decisions, Is.Empty, "An unknown format cannot choose between two monitored media sides.");
                    return;
                }

                Assert.That(decisions, Has.Count.EqualTo(1));
                var decision = decisions.Single();
                Assert.That(decision.Approved, Is.True, string.Join("; ", decision.Rejections.Select(rejection => rejection.Reason)));
                var selectedAudio = expectedMedia == BookMediaType.Audiobook || !expectedMedia.HasValue && monitorAudio;
                var expectedTarget = selectedAudio ? audio : ebook;
                var expectedProfile = selectedAudio ? audioProfile : ebookProfile;
                Assert.That(decision.RemoteBook.Books.Single().Id, Is.EqualTo(expectedTarget.Id));
                Assert.That(decision.RemoteBook.GetQualityProfile().Id, Is.EqualTo(expectedProfile.Id));
                Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(expectedQuality));

                if (category == 7000) return;
                var criteria = new BookSearchCriteria
                {
                    Author = scene.Author, Books = new List<Book> { expectedTarget }, AuthorCatalog = scene.Books
                };
                var searched = scene.Maker(new QualityAllowedByProfileSpecification(Logger),
                    new MonitoredMediaTypeSpecification(Logger)).GetSearchDecision(new List<ReleaseInfo> { release }, criteria).Single();
                Assert.That(searched.Approved, Is.True, string.Join("; ", searched.Rejections.Select(rejection => rejection.Reason)));
                Assert.That(searched.RemoteBook.Books.Single().Id, Is.EqualTo(expectedTarget.Id));
                Assert.That(searched.RemoteBook.GetQualityProfile().Id, Is.EqualTo(expectedProfile.Id));
                Assert.That(searched.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(expectedQuality));

                var unmatched = scene.Release(BookMediaType.Audiobook, "unmatched-category-hint");
                unmatched.Title = "Test Writer - Other Work [Audiobook]";
                unmatched.Author = null;
                unmatched.Book = null;
                unmatched.Categories = new List<int> { category };
                unmatched.DownloadProtocol = DownloadProtocol.Usenet;
                var displayed = scene.Maker(new QualityAllowedByProfileSpecification(Logger),
                    new MonitoredMediaTypeSpecification(Logger)).GetSearchDecision(new List<ReleaseInfo> { unmatched }, criteria).Single();
                Assert.That(displayed.Approved, Is.False);
                Assert.That(displayed.RemoteBook.Books.Single().Id, Is.EqualTo(expectedTarget.Id));
                Assert.That(displayed.RemoteBook.GetQualityProfile().Id, Is.EqualTo(expectedProfile.Id));
                Assert.That(displayed.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(expectedQuality),
                    "Unmatched search display must still apply report category before a provisional title hint");
            });
        }
    }
}
