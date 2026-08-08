using System.Collections.Generic;
using Chaptarr.Api.V1.Series;
using NzbDrone.Core.Books;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class SeriesResourceMapperFixture
    {
        [Test]
        public void should_map_media_type()
        {
            var series = new Series
            {
                Id = 42,
                Title = "The Housemaid",
                MediaType = BookMediaType.Ebook
            };

            var resource = series.ToResource();

            Assert.That(resource.MediaType, Is.EqualTo("ebook"));
        }

        [Test]
        public void should_map_narrator_fields_for_variants()
        {
            var series = new Series
            {
                Id = 8,
                Title = "Harry Potter",
                MediaType = BookMediaType.Audiobook,
                Narrator = "Jim Dale",
                PreferredNarratorId = 123
            };

            var resource = series.ToResource();

            Assert.That(resource.Title, Is.EqualTo("Harry Potter (Narrated by Jim Dale)"));
            Assert.That(resource.Narrator, Is.EqualTo("Jim Dale"));
        }

        [Test]
        public void should_prefer_goodreads_series_id_as_foreign_series_id()
        {
            var series = new Series
            {
                Id = 99,
                Title = "Example Series",
                GoodreadsSeriesId = "gr:12345",
                AmazonSeriesAsin = "az:B012345678",
                HardcoverSeriesId = "hc:67890",
                OpenLibrarySeriesId = "ol:OL123M",
                MediaType = BookMediaType.Audiobook
            };

            var resource = series.ToResource();

            Assert.That(resource.ForeignSeriesId, Is.EqualTo("gr:12345"));
        }

        [Test]
        public void should_fall_back_to_amazon_series_asin_when_goodreads_missing()
        {
            var series = new Series
            {
                Id = 100,
                Title = "Example Series",
                GoodreadsSeriesId = null,
                AmazonSeriesAsin = "az:B012345678",
                HardcoverSeriesId = "hc:67890",
                MediaType = BookMediaType.Ebook
            };

            var resource = series.ToResource();

            Assert.That(resource.ForeignSeriesId, Is.EqualTo("az:B012345678"));
        }

        [Test]
        public void should_not_expose_local_series_id_as_foreign_series_id()
        {
            var series = new Series
            {
                Id = 100,
                Title = "Local Only Series",
                MediaType = BookMediaType.Ebook
            };

            var resource = series.ToResource();

            Assert.That(resource.LocalSeriesId, Is.EqualTo("100"));
            Assert.That(resource.ForeignSeriesId, Is.Null);
        }

        [Test]
        public void should_not_expose_local_book_ids_as_foreign_book_ids()
        {
            var series = new Series
            {
                Id = 100,
                Title = "Example Series",
                MediaType = BookMediaType.Ebook,
                Books = new List<Book>
                {
                    new Book
                    {
                        Id = 200,
                        Title = "Book With Provider",
                        HardcoverBookId = "hc:123"
                    },
                    new Book
                    {
                        Id = 201,
                        Title = "Local Only Book"
                    }
                }
            };

            var resource = series.ToResource();

            Assert.That(resource.Books[0].ForeignBookId, Is.EqualTo("hc:123"));
            Assert.That(resource.Books[1].ForeignBookId, Is.Null);
        }
    }
}
