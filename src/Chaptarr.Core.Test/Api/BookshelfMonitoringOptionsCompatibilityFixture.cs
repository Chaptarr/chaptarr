using Chaptarr.Api.V1.Bookshelf;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookshelfMonitoringOptionsCompatibilityFixture
    {
        [Test]
        public void legacy_monitored_field_should_round_trip_into_monitoring_options()
        {
            var resource = new BookshelfMonitoringOptionsResource
            {
                Monitor = MonitorTypes.All,
                Monitored = true
            };

            var model = resource.ToModel();

            Assert.That(model.Monitored, Is.True);
            Assert.That(model.Monitor, Is.EqualTo(MonitorTypes.All));
        }

        [Test]
        public void missing_legacy_monitor_should_keep_the_bookshelf_default()
        {
            var model = new BookshelfMonitoringOptionsResource().ToModel();

            Assert.That(model.Monitor, Is.EqualTo(MonitorTypes.All));
        }

        [Test]
        public void books_to_monitor_should_select_specific_book_mode_when_monitor_is_omitted()
        {
            var model = new BookshelfMonitoringOptionsResource
            {
                BooksToMonitor = new System.Collections.Generic.List<string> { "123" }
            }.ToModel();

            Assert.That(model.Monitor, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(model.BooksToMonitor, Is.EqualTo(new[] { "123" }));
        }
    }
}
