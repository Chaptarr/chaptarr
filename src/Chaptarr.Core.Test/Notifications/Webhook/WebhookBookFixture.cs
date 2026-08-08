using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Notifications.Webhook;

namespace Chaptarr.Core.Test.Notifications.Webhook
{
    [TestFixture]
    public class WebhookBookFixture
    {
        [Test]
        public void should_not_throw_when_book_editions_not_hydrated()
        {
            var book = new Book
            {
                Id = 1,
                Title = "Test Book"
            };

            Assert.DoesNotThrow(() => _ = new WebhookBook(book));

            var result = new WebhookBook(book);
            Assert.That(result.Edition, Is.Null);
        }

        [Test]
        public void should_use_provided_edition_when_available()
        {
            var book = new Book
            {
                Id = 2,
                Title = "Test Book"
            };

            var edition = new Edition
            {
                Id = 5,
                ForeignEditionId = "gr123",
                Title = "Selected Edition"
            };

            var result = new WebhookBook(book, edition);

            Assert.That(result.Edition, Is.Not.Null);
            Assert.That(result.Edition.GoodreadsId, Is.EqualTo("gr123"));
            Assert.That(result.Edition.Title, Is.EqualTo("Selected Edition"));
        }

        [Test]
        public void should_select_monitored_edition_when_book_has_editions()
        {
            var book = new Book
            {
                Id = 3,
                Title = "Test Book",
                Editions = new List<Edition>
                {
                    new Edition { Id = 2, Monitored = false, ForeignEditionId = "gr-unmonitored", Title = "Unmonitored" },
                    new Edition { Id = 1, Monitored = true, ForeignEditionId = "gr-monitored", Title = "Monitored" }
                }
            };

            var result = new WebhookBook(book);

            Assert.That(result.Edition, Is.Not.Null);
            Assert.That(result.Edition.GoodreadsId, Is.EqualTo("gr-monitored"));
        }

        [Test]
        public void should_fall_back_to_first_edition_when_no_monitored_edition()
        {
            var book = new Book
            {
                Id = 4,
                Title = "Test Book",
                Editions = new List<Edition>
                {
                    new Edition { Id = 2, Monitored = false, ForeignEditionId = "gr-2", Title = "Second" },
                    new Edition { Id = 1, Monitored = false, ForeignEditionId = "gr-1", Title = "First" }
                }
            };

            var result = new WebhookBook(book);

            Assert.That(result.Edition, Is.Not.Null);
            Assert.That(result.Edition.GoodreadsId, Is.EqualTo("gr-1"));
        }
    }
}

