using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Core.Test.Download
{
    public partial class RssAlternativeCopyLifecycleFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public Task automatic_search_requires_author_before_excusing_an_adjacent_number(bool postgres, bool authorCredited)
        {
            return Run(postgres, async scene =>
            {
                scene.Author.Name = "George S. Clason";
                scene.Author.CleanName = "georgesclason";
                var profile = new BasicRepository<QualityProfile>(scene.Database, scene.Events).All()
                    .Single(item => item.ProfileType == ProfileType.Audiobook);
                scene.Author.AudiobookQualityProfileId = profile.Id;
                scene.Author.AudiobookQualityProfile = profile;
                new BasicRepository<Author>(scene.Database, scene.Events).Update(scene.Author);
                var book = scene.Books.Single();
                book.Title = "The Richest Man in Babylon";
                book.CleanTitle = "therichestmaninbabylon";
                scene.BookWriter.Update(book);
                var edition = book.Editions.Single();
                edition.Title = book.Title;
                new BasicRepository<Edition>(scene.Database, scene.Events).Update(edition);

                var report = scene.Release(BookMediaType.Audiobook);
                report.Title = (authorCredited ? "George S. Clason" : "Thievery Corporation") + " - The Richest Man In Babylon-1";
                report.Author = null;
                report.Book = null;
                report.Categories = new List<int> { 3030 };
                report.DownloadProtocol = DownloadProtocol.Usenet;
                var criteria = new BookSearchCriteria
                {
                    Author = scene.Author,
                    Books = scene.Books,
                    AuthorCatalog = scene.Books,
                    InteractiveSearch = false,
                    UserInvokedSearch = false
                };
                var decisions = scene.Maker(new QualityAllowedByProfileSpecification(Logger),
                    new MonitoredMediaTypeSpecification(Logger)).GetSearchDecision(new List<ReleaseInfo> { report }, criteria);
                Assert.That(decisions, Has.Count.EqualTo(1));
                var sent = new List<RemoteBook>();
                var processed = await Process(scene, decisions, sent);
                Assert.Multiple(() =>
                {
                    Assert.That(decisions.Single().Approved, Is.EqualTo(authorCredited));
                    Assert.That(sent, Has.Count.EqualTo(authorCredited ? 1 : 0));
                    Assert.That(processed.Grabbed, Has.Count.EqualTo(authorCredited ? 1 : 0));
                    if (!authorCredited)
                    {
                        Assert.That(decisions.Single().Rejections.Select(rejection => rejection.Category), Does.Contain("Matching"));
                    }
                });
            }, BookMediaType.Audiobook, copies: 1);
        }
    }
}
