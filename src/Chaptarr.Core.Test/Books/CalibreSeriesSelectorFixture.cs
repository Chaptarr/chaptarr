using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class CalibreSeriesSelectorFixture
    {
        [Test]
        public void preferred_series_should_override_calibre_fallback_order()
        {
            var book = new Book
            {
                Title = "The Book",
                SeriesLinks = new List<SeriesBookLink>
                {
                    new SeriesBookLink
                    {
                        SeriesId = 1,
                        SeriesPosition = 1,
                        IsPrimary = true,
                        Series = new Series { Id = 1, Title = "Alpha Series", WorkCount = 100 }
                    },
                    new SeriesBookLink
                    {
                        SeriesId = 2,
                        SeriesPosition = 2,
                        IsPrimary = false,
                        Series = new Series { Id = 2, Title = "Série préférée", WorkCount = 1 }
                    }
                }
            };

            var selected = CalibreSeriesSelector.Select(book, "Série préférée");

            Assert.That(selected.Series.Value.Title, Is.EqualTo("Série préférée"));
        }

        [Test]
        public void fallback_ties_should_select_the_same_series_regardless_of_link_order()
        {
            var first = new SeriesBookLink
            {
                SeriesId = 2,
                SeriesPosition = 1,
                Series = new Series { Id = 2, Title = "Beta Series", WorkCount = 4 }
            };
            var second = new SeriesBookLink
            {
                SeriesId = 1,
                SeriesPosition = 1,
                Series = new Series { Id = 1, Title = "Alpha Series", WorkCount = 4 }
            };
            var book = new Book
            {
                Title = "The Book",
                SeriesLinks = new List<SeriesBookLink> { first, second }
            };
            var reversedBook = new Book
            {
                Title = book.Title,
                SeriesLinks = new List<SeriesBookLink> { second, first }
            };

            var selected = CalibreSeriesSelector.Select(book);
            var selectedFromReversedLinks = CalibreSeriesSelector.Select(reversedBook);

            Assert.Multiple(() =>
            {
                Assert.That(selected.Series.Value.Title, Is.EqualTo("Alpha Series"));
                Assert.That(selectedFromReversedLinks.Series.Value.Title, Is.EqualTo("Alpha Series"));
            });
        }
    }
}
