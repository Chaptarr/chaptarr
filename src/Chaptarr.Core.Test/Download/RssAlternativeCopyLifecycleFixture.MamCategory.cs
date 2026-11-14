using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Indexers;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    public partial class RssAlternativeCopyLifecycleFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void native_mam_category_selects_the_correct_monitored_side_and_profile_on_all_search_paths(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var scene = new Scene(database, BookMediaType.Audiobook);
                var audio = scene.Books[0];
                var ebook = scene.Books[1];
                ebook.MediaType = BookMediaType.Ebook;
                ebook.Monitored = ebook.EbookMonitored = true;
                ebook.AudiobookMonitored = false;
                scene.BookWriter.UpdateMany(scene.Books);
                var edition = ebook.Editions.Single();
                edition.ReadingFormatId = 3;
                edition.NarratorNames = new();
                new BasicRepository<Edition>(database, scene.Events).Update(edition);

                var profiles = new BasicRepository<QualityProfile>(database, scene.Events).All();
                var audioProfile = profiles.Single(profile => profile.ProfileType == ProfileType.Audiobook);
                var ebookProfile = profiles.Single(profile => profile.ProfileType == ProfileType.Ebook);
                scene.Author.AudiobookQualityProfileId = audioProfile.Id;
                scene.Author.AudiobookQualityProfile = audioProfile;
                scene.Author.EbookQualityProfileId = ebookProfile.Id;
                scene.Author.EbookQualityProfile = ebookProfile;
                new BasicRepository<Author>(database, scene.Events).Update(scene.Author);

                var cases = new[] { 13, 14 }.SelectMany(category => new[] { null, "", "unknown" }
                    .Select(fileType => (category, fileType, standardCategory: 108, audio: category == 13, quality: category == 13 ? Quality.UnknownAudio : Quality.Unknown)))
                    .Concat(new[] {
                        (13, "epub", 108, false, Quality.EPUB),
                        (999, "", 3030, true, Quality.UnknownAudio),
                        (999, "", 7020, false, Quality.Unknown)
                    });
                foreach (var (category, fileType, standardCategory, isAudio, expectedQuality) in cases)
                foreach (var path in new[] { 0, 1, 2 })
                {
                    var target = isAudio ? audio : ebook;
                    var profile = isAudio ? audioProfile : ebookProfile;
                    var release = MyAnonaMouseCategoryFixture.Report(category,
                        "Test Writer - Paper Lanterns" + (category == 14 ? " [Audiobook]" : ""), fileType);
                    release.Categories = new List<int> { standardCategory };
                    release.Indexer = "MyAnonaMouse";
                    release.IndexerId = 7;
                    var maker = scene.Maker(new QualityAllowedByProfileSpecification(Logger), new MonitoredMediaTypeSpecification(Logger));
                    var reports = new List<ReleaseInfo> { release };
                    var decisions = path == 0 ? maker.GetRssDecision(reports) : maker.GetSearchDecision(reports, new BookSearchCriteria
                    {
                        Author = scene.Author, Books = new() { target }, AuthorCatalog = scene.Books, InteractiveSearch = path == 2
                    });

                    Assert.That(decisions, Has.Count.EqualTo(1), $"category={category}, fileType={fileType}, path={path}");
                    var decision = decisions.Single();
                    Assert.That(decision.Approved, Is.True, string.Join("; ", decision.Rejections.Select(rejection => rejection.Reason)));
                    Assert.That(decision.RemoteBook.Books.Single().Id, Is.EqualTo(target.Id));
                    Assert.That(decision.RemoteBook.GetQualityProfile().Id, Is.EqualTo(profile.Id));
                    Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(expectedQuality));
                    Assert.That(decision.ToResource().FormatType, Is.EqualTo(isAudio ? "audiobook" : "ebook"));
                    Assert.That(release.Categories, Is.EqualTo(new[] { standardCategory }));
                }
            });
        }
    }
}
