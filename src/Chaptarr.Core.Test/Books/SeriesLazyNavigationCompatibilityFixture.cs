using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class SeriesLazyNavigationCompatibilityFixture
    {
        [Test]
        public void public_series_navigation_properties_should_round_trip_through_lazy_slots()
        {
            var link = new SeriesBookLink { Id = 5, BookId = 7, SeriesId = 11 };
            var book = new Book { Id = 13, Title = "Book" };

            var series = new Series
            {
                LinkItems = new List<SeriesBookLink> { link },
                Books = new List<Book> { book }
            };

            Assert.That(series.LazyLinkItems?.Value, Has.Count.EqualTo(1));
            Assert.That(series.LazyBooks?.Value, Has.Count.EqualTo(1));
            Assert.That(series.LinkItems[0], Is.SameAs(link));
            Assert.That(series.Books[0], Is.SameAs(book));
        }

        [Test]
        public void compatibility_accessors_should_resolve_existing_lazy_values()
        {
            var link = new SeriesBookLink { Id = 17, BookId = 19, SeriesId = 23 };
            var book = new Book { Id = 29, Title = "Lazy Book" };

            var series = new Series
            {
                LazyLinkItems = new LazyLoaded<List<SeriesBookLink>>(new List<SeriesBookLink> { link }),
                LazyBooks = new LazyLoaded<List<Book>>(new List<Book> { book })
            };

            Assert.That(series.LinkItems[0], Is.SameAs(link));
            Assert.That(series.Books[0], Is.SameAs(book));
        }
    }
}
