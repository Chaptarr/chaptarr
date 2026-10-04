using System.Collections.Generic;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookResourceSeriesTitleFixture
    {
        private static SeriesBookLink Link(int seriesId, string title, string position, bool isPrimary = true, int workCount = 0)
        {
            return new SeriesBookLink
            {
                SeriesId = seriesId,
                Position = position,
                SeriesPosition = int.TryParse(position, out var parsed) ? parsed : 0,
                IsPrimary = isPrimary,
                Series = new Series
                {
                    Id = seriesId,
                    Title = title,
                    PrimaryWorkCount = workCount
                }
            };
        }

        private static Book StoredBook(int id, string title, params SeriesBookLink[] links)
        {
            return new Book
            {
                Id = id,
                Title = title,
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true,
                SeriesLinks = new List<SeriesBookLink>(links ?? new SeriesBookLink[0]),
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = id,
                        Title = title,
                        Format = "audiobook",
                        ReadingFormatId = 2,
                        Monitored = true
                    }
                }
            };
        }

        [Test]
        public void should_use_series_links_instead_of_the_stale_denormalized_pair()
        {
            var book = StoredBook(1, "Dust of Dreams", Link(3447, "Malazan Book of the Fallen", "9", workCount: 10));
            book.SeriesName = "La caduta di Malazan";
            book.SeriesPosition = "28";

            var resource = book.ToResource();

            Assert.That(resource.SeriesTitle, Is.EqualTo("Malazan Book of the Fallen #9"));
        }

        [Test]
        public void should_not_report_a_series_for_a_stored_book_without_links()
        {
            var book = StoredBook(2, "Wuthering Heights");
            book.SeriesName = "Jardín Secreto";
            book.SeriesPosition = "1";

            var resource = book.ToResource();

            Assert.That(resource.SeriesTitle, Is.Null);
        }

        [Test]
        public void should_fall_back_to_the_denormalized_pair_for_lookup_results()
        {
            var book = StoredBook(0, "The Final Empire");
            book.SeriesName = "Mistborn";
            book.SeriesPosition = "1";

            var resource = book.ToResource();

            Assert.That(resource.SeriesTitle, Is.EqualTo("Mistborn #1"));
        }

        [Test]
        public void should_not_emit_the_same_label_for_books_in_a_shared_umbrella_series()
        {
            var cosmere = new Series { Id = 99, Title = "The Cosmere Universe", PrimaryWorkCount = 40 };

            var wayOfKings = StoredBook(3, "The Way of Kings",
                Link(1, "The Stormlight Archive", "1", workCount: 5),
                new SeriesBookLink { SeriesId = 99, Position = "6", SeriesPosition = 6, IsPrimary = true, Series = cosmere });

            var whiteSand = StoredBook(4, "White Sand",
                Link(2, "White Sand", "1", workCount: 3),
                new SeriesBookLink { SeriesId = 99, Position = "11", SeriesPosition = 11, IsPrimary = true, Series = cosmere });

            Assert.Multiple(() =>
            {
                Assert.That(wayOfKings.ToResource().SeriesTitle, Is.EqualTo("The Stormlight Archive #1"));
                Assert.That(whiteSand.ToResource().SeriesTitle, Is.EqualTo("White Sand #1"));
            });
        }

        [Test]
        public void should_strip_a_localized_series_suffix_even_when_the_label_is_canonical()
        {
            var book = StoredBook(6, "Das Spiel der Götter (Das Spiel der Götter, #1)",
                Link(3447, "Malazan Book of the Fallen", "1", workCount: 10));
            book.SeriesName = "Das Spiel der Götter";
            book.SeriesPosition = "1";

            var resource = book.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.Title, Is.EqualTo("Das Spiel der Götter"));
                Assert.That(resource.SeriesTitle, Is.EqualTo("Malazan Book of the Fallen #1"));
            });
        }

        [Test]
        public void should_strip_a_suffix_matching_a_series_the_book_is_in_but_is_not_labelled_with()
        {
            var book = StoredBook(7, "The Way of Kings (The Cosmere Universe, #6)",
                Link(1, "The Stormlight Archive", "1", workCount: 5),
                Link(99, "The Cosmere Universe", "6", workCount: 40));

            var resource = book.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.Title, Is.EqualTo("The Way of Kings"));
                Assert.That(resource.SeriesTitle, Is.EqualTo("The Stormlight Archive #1"));
            });
        }

        [Test]
        public void should_still_strip_a_duplicated_series_suffix_from_the_title()
        {
            var book = StoredBook(5, "Guards! Guards! (Discworld, #8)", Link(1, "Discworld", "8", workCount: 41));

            var resource = book.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.Title, Is.EqualTo("Guards! Guards!"));
                Assert.That(resource.SeriesTitle, Is.EqualTo("Discworld #8"));
            });
        }
    }
}
