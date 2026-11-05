using System.Collections.Generic;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class BookshelfPersistedMonitoringFixture
    {
        [TestCase(BookMediaType.Audiobook, true, false)]
        [TestCase(BookMediaType.Ebook, true, false)]
        [TestCase(BookMediaType.Audiobook, false, false)]
        [TestCase(BookMediaType.Ebook, false, false)]
        [TestCase(BookMediaType.Audiobook, true, true)]
        [TestCase(BookMediaType.Ebook, true, true)]
        [TestCase(BookMediaType.Audiobook, false, true)]
        [TestCase(BookMediaType.Ebook, false, true)]
        public void migrated_release_decisions_should_use_persisted_format_monitoring(BookMediaType mediaType, bool monitored, bool search)
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(service => service.BookshelfMatchCompatibility).Returns(true);
            var subject = new RemoteBook
            {
                Author = new Author { Monitored = true },
                Books = new List<Book>
                {
                    new()
                    {
                        MediaType = mediaType,
                        AudiobookMonitored = mediaType == BookMediaType.Audiobook && monitored,
                        EbookMonitored = mediaType == BookMediaType.Ebook && monitored,
                        // This legacy property has no database column. Hydration leaves it false;
                        // stale in-memory values must not override the persisted format flag either.
                        Monitored = !monitored
                    }
                }
            };
            var specification = new MonitoredBookSpecification(config.Object, LogManager.GetCurrentClassLogger());

            var decision = specification.IsSatisfiedBy(subject, search ? new BookSearchCriteria { MonitoredBooksOnly = true } : null);

            Assert.That(decision.Accepted, Is.EqualTo(monitored));
        }
    }
}
