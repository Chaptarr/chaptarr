using System.Collections.Generic;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class QualityAllowedByProfileCompatibilityFixture
    {
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        public void should_keep_quality_profile_rejections_non_bypassable_for_migrated_bookshelf_installs(
            bool bookshelfMatchCompatibility,
            bool hasQualityProfile,
            bool expectedCanBypass)
        {
            var author = new Author();
            if (hasQualityProfile)
            {
                author.EbookQualityProfileId = 1;
                author.EbookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile
                {
                    Id = 1,
                    Name = "Fixture",
                    Items = new List<QualityProfileQualityItem>
                    {
                        new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = false }
                    }
                });
            }

            var remoteBook = new RemoteBook
            {
                Author = author,
                Release = new ReleaseInfo { Title = "Fixture EPUB", IndexerId = 0 },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB) }
            };
            var subject = new QualityAllowedByProfileSpecification(
                ConfigServiceTestProxy.Create(bookshelfMatchCompatibility: bookshelfMatchCompatibility),
                LogManager.GetCurrentClassLogger());

            var decision = subject.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.EqualTo(expectedCanBypass));
        }
    }
}
