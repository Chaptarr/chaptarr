using System.Collections.Generic;
using NUnit.Framework;
using Chaptarr.Api.V1.Books;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookResourceTitleStabilityFixture
    {
        [Test]
        public void to_resource_should_use_selected_edition_title_deterministically()
        {
            var book = new Book
            {
                Title = "Prelude to Foundation",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 2,
                        Title = "Foundation’s Edge",
                        Format = "audiobook",
                        ReadingFormatId = 2,
                        Monitored = true
                    },
                    new Edition
                    {
                        Id = 1,
                        Title = "Prelude to Foundation",
                        Format = "audiobook",
                        ReadingFormatId = 2,
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.Title, Is.EqualTo("Prelude to Foundation"));
        }

        [Test]
        public void to_model_should_not_overwrite_existing_book_title()
        {
            var book = new Book
            {
                Title = "Prelude to Foundation",
                MediaType = BookMediaType.Audiobook
            };

            var resource = new BookResource
            {
                Title = "Foundation’s Edge",
                Monitored = true
            };

            resource.ToModel(book);

            Assert.That(book.Title, Is.EqualTo("Prelude to Foundation"));
        }

        [Test]
        public void to_resource_should_strip_only_series_suffix_that_matches_book_series()
        {
            var book = new Book
            {
                Title = "Guards! Guards! (Discworld, #8)",
                SeriesName = "Discworld",
                SeriesPosition = "8",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Guards! Guards! (Discworld, #8)",
                        Format = "audiobook",
                        ReadingFormatId = 2,
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.Title, Is.EqualTo("Guards! Guards!"));
            Assert.That(resource.SeriesTitle, Is.EqualTo("Discworld #8"));
        }

        [Test]
        public void to_resource_should_not_strip_parenthetical_suffix_without_matching_series_metadata()
        {
            var book = new Book
            {
                Title = "The Manual (Collector's Edition, #1)",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "The Manual (Collector's Edition, #1)",
                        Format = "ebook",
                        ReadingFormatId = 3,
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.Title, Is.EqualTo("The Manual (Collector's Edition, #1)"));
        }

        [Test]
        public void to_resource_should_not_strip_suffix_that_points_to_a_different_series()
        {
            var book = new Book
            {
                Title = "The Last Wish (Witcher, #0.5)",
                SeriesName = "Something Else",
                SeriesPosition = "1",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "The Last Wish (Witcher, #0.5)",
                        Format = "ebook",
                        ReadingFormatId = 3,
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.Title, Is.EqualTo("The Last Wish (Witcher, #0.5)"));
        }
    }
}
